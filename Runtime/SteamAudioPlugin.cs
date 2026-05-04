namespace Engine;

/// <summary>
/// Plugin that brings up the Steam Audio spatial processor for the audio system.
/// Installs <see cref="SteamAudioProcessor"/> on the <see cref="AudioServer"/>'s
/// <see cref="ISpatialAudioProcessor"/> slot.
/// </summary>
/// <remarks>
/// <para>
/// Pulled in automatically by <see cref="SoundsPlugin"/> when this module is part of
/// the build (probed by full type name). Composes cleanly with any
/// <see cref="IAudioBackend"/>: the backend handles voice playback, the processor
/// supplies an extra distance / occlusion gain that <see cref="AudioServer.Tick"/>
/// folds into the per-voice volume each frame.
/// </para>
/// <para>
/// <b>Failure mode:</b> if the <c>phonon</c> native library is missing the processor
/// installs but every <see cref="ISpatialAudioProcessor.Compute"/> call returns
/// <see cref="SpatialResult.Pass"/>, leaving voices at their authored gain.
/// </para>
/// </remarks>
/// <seealso cref="SoundsPlugin"/>
/// <seealso cref="SteamAudioProcessor"/>
public sealed class SteamAudioPlugin : IPlugin
{
    private static readonly ILogger Logger = Log.Category("Engine.Sound.SteamAudio");

    /// <inheritdoc />
    public void Build(App app)
    {
        Logger.Info("SteamAudioPlugin: Initialising Steam Audio spatial processor...");

        if (!app.World.TryGetResource<AudioServer>(out var server))
        {
            Logger.Warn("SteamAudioPlugin: AudioServer was missing - did you forget SoundsPlugin? Skipping.");
            return;
        }

        var processor = new SteamAudioProcessor();
        server.SetSpatialProcessor(processor);

        Logger.Info(
            processor.IsInitialized
                ? "SteamAudioPlugin: Steam Audio spatial processor ready."
                : "SteamAudioPlugin: Steam Audio processor installed but native init failed - " +
                  "spatial pipeline will pass through unchanged.");
    }
}