namespace WallpaperApp.Services.Playback;

public sealed class NullRenderer : IFrameRenderer
{
    public bool Present(FrameData frame) => true;

    public void Resize(int width, int height) { }

    public void Dispose() { }
}
