namespace WallpaperApp.Services.Playback;

// Minimal pause/resume seam so monitoring controllers can be unit-tested without
// constructing the heavy PlaybackManager (which owns HWNDs and GPU devices and is
// sealed). Currently consumed by RemoteSessionDetector; the other controllers
// (PowerAwareController, fullscreen/visibility handlers) still reference the
// concrete PlaybackManager directly and could be migrated to this seam later.
public interface IPlaybackPauseController
{
    Task PauseAllAsync(PauseReason reason, CancellationToken ct = default);
    Task ResumeAllAsync(PauseReason reason, CancellationToken ct = default);
}
