using System.Numerics;
using FluentAssertions;

namespace Engine.Tests.Audio.SteamAudio;

/// <summary>
/// Tests for the Steam Audio spatial-processor wiring. Like the SDL3 audio backend, this
/// module is allowed to fail soft when the native <c>phonon</c> library is missing
/// from the host: <see cref="SteamAudioProcessor"/> installs but every
/// <see cref="ISpatialAudioProcessor.Compute"/> call returns <see cref="SpatialResult.Pass"/>.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Backend", "SteamAudio")]
public class SteamAudioProcessorTests
{
    [Fact]
    public void ProcessorId_Is_Stable()
    {
        var p = new SteamAudioProcessor();
        p.ProcessorId.Should().Be("steamaudio");
    }

    [Fact]
    public void Initialize_Is_Idempotent_And_NeverThrows_Without_Native_Lib()
    {
        var p = new SteamAudioProcessor();
        var act = () => { p.Initialize(); p.Initialize(); };
        act.Should().NotThrow();
        p.Dispose();
    }

    [Fact]
    public void Compute_Returns_Pass_When_Native_Init_Failed()
    {
        var p = new SteamAudioProcessor();
        p.Initialize();
        if (p.IsInitialized) return; // skip on hosts where phonon is installed

        var result = p.Compute(voiceId: 1, sourcePosition: new Vector3(10, 0, 0),
            listenerPosition: Vector3.Zero, context: SpatialContext.Empty);

        result.VolumeAttenuation.Should().Be(1f, "pass-through gain when the native processor isn't running");
    }

    [Fact]
    public void Compute_Returns_Bounded_Gain_When_Native_Available()
    {
        var p = new SteamAudioProcessor();
        p.Initialize();
        if (!p.IsInitialized) return; // skip when phonon isn't installed

        // Distance attenuation must always be in [0, 1].
        var near = p.Compute(1, new Vector3(0.5f, 0, 0), Vector3.Zero, SpatialContext.Empty);
        var far  = p.Compute(1, new Vector3(100f, 0, 0), Vector3.Zero, SpatialContext.Empty);

        near.VolumeAttenuation.Should().BeInRange(0f, 1f);
        far.VolumeAttenuation.Should().BeInRange(0f, 1f);
        far.VolumeAttenuation.Should().BeLessThanOrEqualTo(near.VolumeAttenuation,
            "the SDK's default model is monotonically attenuating with distance");

        p.Dispose();
    }

    [Fact]
    public void Compute_Pan_Tracks_Source_Side_Of_Listener()
    {
        var p = new SteamAudioProcessor();
        p.Initialize();
        if (!p.IsInitialized) return;

        // Listener at origin facing -Z (engine convention), up = +Y. With a right-handed
        // listener basis, +X world is the listener's right and -X is the left, so a
        // source on +X must produce a positive pan and -X a negative one.
        var ctx = SpatialContext.Empty;

        var right = p.Compute(1, sourcePosition: new Vector3(+5, 0, 0), Vector3.Zero, ctx);
        var left  = p.Compute(2, sourcePosition: new Vector3(-5, 0, 0), Vector3.Zero, ctx);
        var front = p.Compute(3, sourcePosition: new Vector3(0, 0, -5), Vector3.Zero, ctx);

        right.Pan.Should().BeGreaterThan(0f, "source on the listener's right hand side");
        left.Pan.Should().BeLessThan(0f,    "source on the listener's left hand side");
        front.Pan.Should().BeApproximately(0f, 0.05f, "source dead-ahead is centred");

        right.Pan.Should().BeInRange(-1f, +1f);
        left.Pan.Should().BeInRange(-1f, +1f);

        p.Dispose();
    }

    [Fact]
    public void Compute_Directivity_Attenuates_Behind_A_Cardioid_Source()
    {
        var p = new SteamAudioProcessor();
        p.Initialize();
        if (!p.IsInitialized) return;

        // Source at the origin, "front" pointing -Z. Listener placed in front (-Z) and
        // behind (+Z) at the same distance, so distance attenuation cancels out and any
        // gain difference must come from the dipole directivity term.
        const float dipoleWeight = 1f;   // pure dipole (silent rear).
        const float dipolePower  = 2f;   // sharper front lobe.
        var spatial = new SpatialContext
        {
            SourceForward   = -Vector3.UnitZ,
            SourceUp        =  Vector3.UnitY,
            ListenerForward = -Vector3.UnitZ,
            ListenerUp      =  Vector3.UnitY,
            DipoleWeight    = dipoleWeight,
            DipolePower     = dipolePower,
        };

        var inFront = p.Compute(1, sourcePosition: Vector3.Zero, listenerPosition: new Vector3(0, 0, -5), spatial);
        var behind  = p.Compute(2, sourcePosition: Vector3.Zero, listenerPosition: new Vector3(0, 0, +5), spatial);

        inFront.DirectivityAttenuation.Should().BeInRange(0f, 1f);
        behind.DirectivityAttenuation.Should().BeInRange(0f, 1f);
        inFront.DirectivityAttenuation.Should().BeGreaterThan(behind.DirectivityAttenuation,
            "a forward-pointing dipole must be louder in front than behind");
        behind.DirectivityAttenuation.Should().BeLessThan(0.1f,
            "a pure dipole at the rear is essentially silent");

        // Sanity: directivity is folded into the combined volume.
        inFront.VolumeAttenuation.Should()
            .BeApproximately(inFront.DistanceAttenuation * inFront.DirectivityAttenuation, 1e-4f);

        p.Dispose();
    }

    [Fact]
    public void Compute_Directivity_Is_NoOp_When_DipoleWeight_Is_Zero()
    {
        var p = new SteamAudioProcessor();
        p.Initialize();
        if (!p.IsInitialized) return;

        var omni = p.Compute(1, Vector3.Zero, new Vector3(0, 0, +5), SpatialContext.Empty);
        omni.DirectivityAttenuation.Should().Be(1f, "DipoleWeight = 0 means omni-directional source");

        p.Dispose();
    }

    [Fact]
    public void MinDistance_Setter_Increases_Far_Field_Gain()
    {
        var p = new SteamAudioProcessor();
        p.Initialize();
        if (!p.IsInitialized) return;

        var src = new Vector3(20f, 0, 0);
        p.MinDistance = 1f;
        var tightGain = p.Compute(1, src, Vector3.Zero, SpatialContext.Empty).DistanceAttenuation;

        p.MinDistance = 10f;
        var loosenedGain = p.Compute(2, src, Vector3.Zero, SpatialContext.Empty).DistanceAttenuation;

        loosenedGain.Should().BeGreaterThan(tightGain,
            "a larger reference distance pushes the inverse-distance roll-off further out, " +
            "so the same far-field point is attenuated less");
        loosenedGain.Should().BeInRange(0f, 1f);

        // Invalid values are clamped to the safe default.
        p.MinDistance = -5f;
        p.MinDistance.Should().Be(1f);

        p.Dispose();
    }

    [Fact]
    public void SteamAudioPlugin_Installs_Processor_On_AudioServer()
    {
        using var server = new AudioServer();
        server.Spatial.Should().BeNull();

        // Mirror what SteamAudioPlugin.Build does without spinning up a full App.
        server.SetSpatialProcessor(new SteamAudioProcessor());

        server.Spatial.Should().NotBeNull();
        server.Spatial!.ProcessorId.Should().Be("steamaudio");
    }
}