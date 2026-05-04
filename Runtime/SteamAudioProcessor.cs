using System.Numerics;
using IPL = SteamAudio.IPL;

namespace Engine;

/// <summary>
/// <see cref="ISpatialAudioProcessor"/> backed by Valve's
/// <a href="https://valvesoftware.github.io/steam-audio/">Steam Audio</a> SDK
/// (a.k.a. <c>phonon</c>) via the <c>SteamAudio.NET</c> managed bindings. v2 supplies
/// physically-grounded distance attenuation, dipole directivity and a stereo pan
/// derived from the source position projected into the listener's local frame.
/// Air absorption / occlusion / HRTF are available from the underlying SDK and are
/// reserved for follow-up work as <see cref="SpatialContext"/> grows fields to feed them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Math:</b>
/// <list type="bullet">
///   <item><description><c>iplDistanceAttenuationCalculate</c> with a configurable
///   <see cref="MinDistance"/> (SDK default model = inverse-distance fall-off).</description></item>
///   <item><description><c>iplDirectivityCalculate</c> driven by the source's
///   <see cref="SpatialContext.SourceForward"/> / <see cref="SpatialContext.SourceUp"/>
///   plus <see cref="SpatialContext.DipoleWeight"/> /
///   <see cref="SpatialContext.DipolePower"/>.</description></item>
///   <item><description><c>iplCalculateRelativeDirection</c> projects the source into
///   the listener's local frame; the <c>X</c> (right) component is forwarded as
///   <see cref="SpatialResult.Pan"/> in <c>[-1, +1]</c>.</description></item>
/// </list>
/// All gains are folded into <see cref="SpatialResult.VolumeAttenuation"/>;
/// <see cref="AudioServer"/> multiplies them by the voice's authored
/// <see cref="AudioVoiceParams.Volume"/> and forwards the pan to the backend.
/// </para>
/// <para>
/// <b>Failure mode:</b> if the <c>phonon</c> native library is missing the processor
/// logs a warning, leaves <see cref="IsInitialized"/>-style state off, and returns
/// <see cref="SpatialResult.Pass"/> from every <see cref="Compute"/> call. Mirrors
/// <see cref="SdlAudioBackend"/>'s "fail-soft" pattern.
/// </para>
/// </remarks>
/// <seealso cref="SteamAudioPlugin"/>
/// <seealso cref="ISpatialAudioProcessor"/>
public sealed class SteamAudioProcessor : ISpatialAudioProcessor
{
    private static readonly ILogger Logger = Log.Category("Engine.Sound.SteamAudio");

    // SteamAudio.NET 4.6.1 ships against phonon 4.6 (STEAMAUDIO_VERSION_MAJOR=4,
    // STEAMAUDIO_VERSION_MINOR=6, STEAMAUDIO_VERSION_PATCH=1). The phonon ABI
    // encodes (major << 16) | (minor << 8) | patch.
    private const uint PhononVersion = (4u << 16) | (6u << 8) | 1u;

    private readonly object _lock = new();
    private IPL.Context _context;
    private IPL.DistanceAttenuationModel _distanceModel;
    private float _minDistance = 1f;
    private bool _initialized;
    private bool _initFailed;

    /// <inheritdoc />
    public string ProcessorId => "steamaudio";

    /// <summary>True once <see cref="Initialize"/> has wired the native context.</summary>
    public bool IsInitialized => _initialized;

    /// <summary>
    /// Reference distance (metres) at which a source plays at unity gain. Distances
    /// below this are clamped to <c>1.0</c> by the SDK; greater distances fall off
    /// according to the configured <see cref="IPL.DistanceAttenuationModelType"/>
    /// (currently <see cref="IPL.DistanceAttenuationModelType.Default"/>).
    /// Defaults to <c>1.0</c>. Setting marks the model dirty so the SDK recomputes
    /// internal coefficients on the next <see cref="Compute"/>.
    /// </summary>
    public float MinDistance
    {
        get { lock (_lock) return _minDistance; }
        set
        {
            if (value <= 0f || float.IsNaN(value)) value = 1f;
            lock (_lock)
            {
                if (Math.Abs(_minDistance - value) < 1e-6f) return;
                _minDistance = value;
                _distanceModel.MinDistance = value;
                _distanceModel.Dirty = true;
            }
        }
    }

    /// <inheritdoc />
    public void Initialize()
    {
        if (_initialized || _initFailed) return;
        lock (_lock)
        {
            if (_initialized || _initFailed) return;
            try
            {
                var settings = new IPL.ContextSettings
                {
                    Version = PhononVersion,
                    SimdLevel = IPL.SimdLevel.Sse2,
                    Flags = 0,
                };
                var err = IPL.ContextCreate(in settings, out _context);
                if (err != IPL.Error.Success)
                {
                    Logger.Warn($"SteamAudioProcessor: iplContextCreate failed (error={err}). Spatial processor disabled.");
                    _initFailed = true;
                    return;
                }
                _distanceModel = new IPL.DistanceAttenuationModel
                {
                    Type = IPL.DistanceAttenuationModelType.Default,
                    MinDistance = _minDistance,
                    Dirty = false,
                };
                _initialized = true;
                Logger.Info($"SteamAudioProcessor: Steam Audio initialised (phonon v{PhononVersion >> 16}.{(PhononVersion >> 8) & 0xFF}.{PhononVersion & 0xFF}).");
            }
            catch (DllNotFoundException ex)
            {
                Logger.Warn($"SteamAudioProcessor: native 'phonon' library not found ({ex.Message}). Processor disabled.");
                _initFailed = true;
            }
            catch (Exception ex)
            {
                Logger.Warn($"SteamAudioProcessor: initialisation failed ({ex.GetType().Name}: {ex.Message}). Processor disabled.");
                _initFailed = true;
            }
        }
    }

    /// <inheritdoc />
    public SpatialResult Compute(int voiceId, Vector3 sourcePosition, Vector3 listenerPosition, in SpatialContext context)
    {
        if (!_initialized) return SpatialResult.Pass;
        try
        {
            var src = ToIpl(sourcePosition);
            var lst = ToIpl(listenerPosition);

            // 1. Distance attenuation (inverse-distance with configurable MinDistance).
            float distance = IPL.DistanceAttenuationCalculate(_context, src, lst, in _distanceModel);
            distance = Saturate(distance);

            // 2. Directivity (only when the caller actually configured a non-omni pattern).
            float directivity = 1f;
            if (context.DipoleWeight > 0f)
            {
                var ahead = SafeNormalize(context.SourceForward, -Vector3.UnitZ);
                var up    = SafeNormalize(context.SourceUp,       Vector3.UnitY);
                var right = SafeNormalize(Vector3.Cross(ahead, up), Vector3.UnitX);
                // Re-orthogonalise up so the basis is a clean right-handed frame.
                up = Vector3.Normalize(Vector3.Cross(right, ahead));

                var space = new IPL.CoordinateSpace3
                {
                    Right  = ToIpl(right),
                    Up     = ToIpl(up),
                    Ahead  = ToIpl(ahead),
                    Origin = src,
                };
                var dir = new IPL.Directivity
                {
                    DipoleWeight = Math.Clamp(context.DipoleWeight, 0f, 1f),
                    DipolePower  = context.DipolePower <= 0f ? 1f : context.DipolePower,
                    // Callback / UserData left as default → SDK uses the built-in dipole formula.
                };
                directivity = Saturate(IPL.DirectivityCalculate(_context, space, lst, in dir));
            }

            // 3. Stereo pan from relative direction in listener space.
            var listenerAhead = ToIpl(SafeNormalize(context.ListenerForward, -Vector3.UnitZ));
            var listenerUp    = ToIpl(SafeNormalize(context.ListenerUp,       Vector3.UnitY));
            var rel = IPL.CalculateRelativeDirection(_context, src, lst, listenerAhead, listenerUp);
            // CalculateRelativeDirection returns a unit vector in the listener's local
            // frame: +X = right, +Y = up, +Z = forward. The X component is the natural
            // pan signal; it's already in [-1, +1] (clamp defensively for NaN safety).
            float pan = float.IsNaN(rel.X) ? 0f : Math.Clamp(rel.X, -1f, 1f);

            float volume = distance * directivity;
            return new SpatialResult
            {
                VolumeAttenuation      = volume,
                DistanceAttenuation    = distance,
                DirectivityAttenuation = directivity,
                Pan                    = pan,
            };
        }
        catch (Exception ex)
        {
            // Don't let a transient native error tear down a frame: degrade to pass-through.
            Logger.Debug($"SteamAudioProcessor: Compute failed ({ex.GetType().Name}: {ex.Message}). Returning pass-through.");
            return SpatialResult.Pass;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_lock)
        {
            if (_initialized)
            {
                try { var ctx = _context; IPL.ContextRelease(ref ctx); _context = ctx; }
                catch (Exception ex) { Logger.Debug($"SteamAudioProcessor: ContextRelease threw ({ex.Message}); ignoring."); }
                _initialized = false;
            }
        }
    }

    private static IPL.Vector3 ToIpl(Vector3 v) => new() { X = v.X, Y = v.Y, Z = v.Z };

    private static Vector3 SafeNormalize(Vector3 v, Vector3 fallback)
    {
        float lenSq = v.LengthSquared();
        if (lenSq < 1e-12f || float.IsNaN(lenSq)) return fallback;
        return v / MathF.Sqrt(lenSq);
    }

    private static float Saturate(float x)
    {
        if (float.IsNaN(x) || x < 0f) return 0f;
        return x > 1f ? 1f : x;
    }
}