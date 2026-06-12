namespace WallpaperApp.Services.Playback;

public interface IFrameRenderer : IDisposable
{
    bool Present(FrameData frame);
    void Resize(int width, int height);
}
