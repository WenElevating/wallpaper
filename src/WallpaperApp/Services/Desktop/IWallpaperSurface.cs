namespace WallpaperApp.Services.Desktop;

public interface IWallpaperSurface : IDisposable
{
    IntPtr Handle { get; }
    int Width { get; }
    int Height { get; }
}
