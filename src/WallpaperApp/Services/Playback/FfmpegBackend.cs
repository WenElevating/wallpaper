using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

public sealed class FfmpegBackend : IPlaybackBackend
{
    private readonly FileLogger _logger;
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private Process? _ffmpegProcess;
    private string? _filePath;
    private bool _isPlaying;
    private bool _isPaused;
    private int _width;
    private int _height;
    private int _frameSize;
    private long _frameCount;
    private double _fps;
    private TimeSpan _duration;
    private TimeSpan _position;
    private CancellationTokenSource? _decodeCts;

    public bool IsPlaying => _isPlaying;
    public bool IsPaused => _isPaused;
    public TimeSpan Duration => _duration;
    public TimeSpan Position => _position;
    public event EventHandler? EndOfStream;

    public FfmpegBackend(FileLogger logger)
    {
        _logger = logger;
        _ffmpegPath = ResolveToolPath("ffmpeg.exe");
        _ffprobePath = ResolveToolPath("ffprobe.exe");
    }

    private static string ResolveToolPath(string toolName)
    {
        // Check bundled path first
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        var bundled = Path.Combine(baseDir, "ffmpeg", toolName);
        if (File.Exists(bundled))
            return bundled;

        // Fall back to PATH
        return toolName;
    }

    public async Task<bool> OpenAsync(string filePath, CancellationToken ct = default)
    {
        try
        {
            _filePath = filePath;
            _frameCount = 0;
            _position = TimeSpan.Zero;
            var info = await ProbeVideoInfoAsync(filePath, ct);
            if (info == null) { _logger.Error($"Failed to probe: {filePath}"); return false; }
            (_width, _height, _fps, _duration) = info.Value;
            _frameSize = _width * _height * 4;
            _logger.Info($"Opened: {filePath} ({_width}x{_height} @ {_fps:F1}fps)");
            return true;
        }
        catch (Exception ex) { _logger.Error($"Failed to open: {filePath}", ex); return false; }
    }

    public Task PlayAsync(CancellationToken ct = default)
    { _isPlaying = true; _isPaused = false; StartDecoding(); return Task.CompletedTask; }
    public Task PauseAsync(CancellationToken ct = default)
    { _isPaused = true; return Task.CompletedTask; }
    public Task ResumeAsync(CancellationToken ct = default)
    { _isPaused = false; return Task.CompletedTask; }
    public Task StopAsync(CancellationToken ct = default)
    { _isPlaying = false; _isPaused = false; _position = TimeSpan.Zero; _frameCount = 0; StopDecoding(); return Task.CompletedTask; }
    public Task SeekAsync(TimeSpan position, CancellationToken ct = default)
    { _position = position; _frameCount = (long)(position.TotalSeconds * _fps); StopDecoding(); if (_isPlaying) StartDecoding(); return Task.CompletedTask; }

    public async Task<FrameData?> NextFrameAsync(CancellationToken ct = default)
    {
        if (_ffmpegProcess?.StandardOutput.BaseStream == null) return null;
        try
        {
            var managed = new byte[_frameSize];
            var read = await _ffmpegProcess.StandardOutput.BaseStream.ReadAsync(managed, ct);
            if (read < _frameSize) { EndOfStream?.Invoke(this, EventArgs.Empty); return null; }
            var buffer = Marshal.AllocHGlobal(_frameSize);
            Marshal.Copy(managed, 0, buffer, _frameSize);
            _frameCount++;
            _position = TimeSpan.FromSeconds(_frameCount / _fps);
            return new FrameData(buffer, _width, _height, _width * 4, (long)(_position.TotalSeconds * 1_000_000));
        }
        catch { return null; }
    }

    private void StartDecoding()
    {
        StopDecoding();
        _decodeCts = new CancellationTokenSource();
        var seekArg = _position.TotalSeconds > 0 ? $"-ss {_position.TotalSeconds:F3}" : "";
        _ffmpegProcess = new Process { StartInfo = new ProcessStartInfo {
            FileName = _ffmpegPath, Arguments = $"{seekArg} -i \"{_filePath}\" -f rawvideo -pix_fmt bgra -v error -",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }};
        _ffmpegProcess.Start();
    }

    private void StopDecoding()
    {
        _decodeCts?.Cancel();
        if (_ffmpegProcess != null) { try { _ffmpegProcess.Kill(); } catch { } _ffmpegProcess.Dispose(); _ffmpegProcess = null; }
    }

    private async Task<(int width, int height, double fps, TimeSpan duration)?> ProbeVideoInfoAsync(string filePath, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = _ffprobePath,
                Arguments = $"-v error -select_streams v:0 -show_entries stream=width,height,r_frame_rate -show_entries format=duration -of json \"{filePath}\"",
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var process = Process.Start(psi);
            if (process == null) return null;
            var output = await process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            var width = ExtractJsonInt(output, "width");
            var height = ExtractJsonInt(output, "height");
            var fpsStr = ExtractJsonString(output, "r_frame_rate");
            var durationSec = ExtractJsonDouble(output, "duration");
            if (width == 0 || height == 0) return null;
            double fps = 30;
            if (fpsStr.Contains('/')) { var p = fpsStr.Split('/'); if (double.TryParse(p[0], out var n) && double.TryParse(p[1], out var d) && d > 0) fps = n / d; }
            else double.TryParse(fpsStr, out fps);
            return (width, height, fps, TimeSpan.FromSeconds(durationSec > 0 ? durationSec : 10));
        }
        catch (Exception ex) { _logger.Error($"ffprobe failed: {filePath}", ex); return null; }
    }

    private static int ExtractJsonInt(string json, string key) { var s = $"\"{key}\":"; var i = json.IndexOf(s); if (i < 0) return 0; i += s.Length; while (i < json.Length && !char.IsDigit(json[i]) && json[i] != '-') i++; var st = i; while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '.' || json[i] == '-')) i++; return int.TryParse(json[st..i], out var v) ? v : 0; }
    private static string ExtractJsonString(string json, string key) { var s = $"\"{key}\":\""; var i = json.IndexOf(s); if (i < 0) return string.Empty; i += s.Length; var e = json.IndexOf('"', i); return e > i ? json[i..e] : string.Empty; }
    private static double ExtractJsonDouble(string json, string key) { var s = $"\"{key}\":"; var i = json.IndexOf(s); if (i < 0) return 0; i += s.Length; while (i < json.Length && json[i] == ' ') i++; var st = i; while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '.' || json[i] == '-')) i++; return double.TryParse(json[st..i], out var v) ? v : 0; }

    public void Dispose() { StopDecoding(); _decodeCts?.Dispose(); }
}
