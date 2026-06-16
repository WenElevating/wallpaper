using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Playback;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using System.Runtime.InteropServices;
using static Vortice.Direct3D11.D3D11;
using DxgiFormat = Vortice.DXGI.Format;

// End-to-end validation of the zero-copy GPU pipeline:
//   1. NV12 shaders compile.
//   2. GpuDevice (shared device) created.
//   3. Reference BGRA: frame 0 via the COPY path (transfer + sws) -> CPU bytes.
//   4. Zero-copy: frame 0 on the shared device -> GPU NV12 texture.
//   5. Offscreen render the NV12 texture through the NV12->RGB shader, read
//      back the BGRA, and compare to the reference. A small per-channel
//      difference is expected (shader vs sws rounding); a large one means the
//      NV12 copy / SRV plane / shader math is wrong.
if (args.Length != 1) { Console.Error.WriteLine("usage: HwDecodeProbe <video.mp4>"); return 64; }

var logDir = Path.Combine(Path.GetTempPath(), "WallpaperAppHwProbe", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(logDir);
using var logger = new FileLogger(logDir);
HwDecodeDevice.Logger = logger;
Nv12Shader.Logger = logger;
Console.WriteLine($"file: {args[0]}");

var vsbc = Nv12Shader.CompileVertexShader();
var psbc = Nv12Shader.CompilePixelShader();
Console.WriteLine($"shaders: vs={vsbc is not null} ps={psbc is not null}");
if (vsbc is null || psbc is null) return 5;

using var gpu = new GpuDevice(logger);
Console.WriteLine($"gpu: available={gpu.IsAvailable} video={gpu.SupportsVideo}");
if (!gpu.SupportsVideo) return 6;

// --- reference: copy-path BGRA of frame 0 ---
byte[] refBgra; int refStride, w, h;
{
    using var b = new FfmpegBackend(logger, HwDecodeDevice.CreateNew);
    if (!await b.OpenAsync(args[0])) return 2;
    await b.PlayAsync();
    using var f = await b.NextFrameAsync();
    if (f is null || f.IsGpu) { Console.WriteLine("reference decode not CPU"); return 3; }
    w = f.Width; h = f.Height; refStride = f.Stride;
    refBgra = new byte[h * refStride];
    Marshal.Copy(f.Buffer, refBgra, 0, refBgra.Length);
}
Console.WriteLine($"reference BGRA: {w}x{h} stride={refStride}");

// --- zero-copy frame 0 on the shared device (keep backend alive across the copy) ---
var zb = new FfmpegBackend(logger, () => HwDecodeDevice.CreateForDevice(gpu.DevicePointer));
zb.PreferZeroCopy = true;
if (!await zb.OpenAsync(args[0])) return 2;
await zb.PlayAsync();
var zf = await zb.NextFrameAsync();
if (zf is null || !zf.IsGpu) { Console.WriteLine($"zero-copy frame not GPU (hw={zb.IsHardwareDecoding})"); return 4; }
Console.WriteLine($"zero-copy GPU frame: {zf.Width}x{zf.Height} texIndex={zf.TextureIndex}");

var dev = gpu.Device; var ctx = gpu.Context;

// NV12 intermediate + SRVs (luma R8 / chroma R8G8).
var nv12 = dev.CreateTexture2D(new Texture2DDescription
{
    Width = zf.Width, Height = zf.Height, MipLevels = 1, ArraySize = 1, Format = DxgiFormat.NV12,
    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource,
});
var srvLuma = dev.CreateShaderResourceView(nv12, new ShaderResourceViewDescription
{
    Format = DxgiFormat.R8_UNorm, ViewDimension = ShaderResourceViewDimension.Texture2D,
    Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 },
});
var srvChroma = dev.CreateShaderResourceView(nv12, new ShaderResourceViewDescription
{
    Format = DxgiFormat.R8G8_UNorm, ViewDimension = ShaderResourceViewDimension.Texture2D,
    Texture2D = new Texture2DShaderResourceView { MostDetailedMip = 0, MipLevels = 1 },
});

// Borrow the decoded texture, copy the whole planar surface (array slice -> subresource).
Marshal.AddRef(zf.Texture);
using (var decoded = MarshallingHelpers.FromPointer<ID3D11Texture2D>(zf.Texture))
{
    ctx.CopySubresourceRegion(nv12, 0, 0, 0, 0, decoded, zf.TextureIndex, null);
}

// Offscreen BGRA render target + render through the shader.
var rt = dev.CreateTexture2D(new Texture2DDescription
{
    Width = zf.Width, Height = zf.Height, MipLevels = 1, ArraySize = 1, Format = DxgiFormat.B8G8R8A8_UNorm,
    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Default,
    BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
});
var rtv = dev.CreateRenderTargetView(rt);
var vs = dev.CreateVertexShader(vsbc);
var ps = dev.CreatePixelShader(psbc);
var samp = dev.CreateSamplerState(new SamplerDescription
{
    Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp,
    AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
    MinLOD = 0, MaxLOD = float.MaxValue,
});
ctx.OMSetRenderTargets(rtv, null);
ctx.RSSetViewports(new[] { new Viewport(0, 0, zf.Width, zf.Height) });
ctx.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
ctx.VSSetShader(vs);
ctx.PSSetShader(ps);
ctx.PSSetShaderResources(0, 2, new[] { srvLuma, srvChroma });
ctx.PSSetSamplers(0, 1, new[] { samp });
ctx.Draw(3, 0);

// Read back and compare to the reference.
var staging = dev.CreateTexture2D(new Texture2DDescription
{
    Width = zf.Width, Height = zf.Height, MipLevels = 1, ArraySize = 1, Format = DxgiFormat.B8G8R8A8_UNorm,
    SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Staging,
    BindFlags = BindFlags.None, CPUAccessFlags = CpuAccessFlags.Read,
});
ctx.CopyResource(staging, rt);
var mapped = ctx.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

int maxDiff = 0; long sumDiff = 0; long samples = 0;
unsafe
{
    byte* dst = (byte*)mapped.DataPointer;
    int dstStride = (int)mapped.RowPitch;
    int stepX = Math.Max(1, zf.Width / 40);
    int stepY = Math.Max(1, zf.Height / 24);
    fixed (byte* refPtr = refBgra)
    {
        for (int y = 0; y < zf.Height; y += stepY)
        {
            for (int x = 0; x < zf.Width; x += stepX)
            {
                byte* a = dst + y * dstStride + x * 4;        // shader output (B,G,R,A)
                byte* b = refPtr + y * refStride + x * 4;     // sws reference
                for (int c = 0; c < 3; c++)
                {
                    int d = Math.Abs(a[c] - b[c]);
                    if (d > maxDiff) maxDiff = d;
                    sumDiff += d;
                    samples++;
                }
            }
        }
    }
}
ctx.Unmap(staging, 0);

double meanDiff = samples > 0 ? (double)sumDiff / samples : -1;
Console.WriteLine($"zero-copy vs sws: maxDiff={maxDiff} meanDiff={meanDiff:F2} (samples={samples})");
Console.WriteLine(samples > 0 && maxDiff <= 16
    ? "PASS — zero-copy colors match the sws reference within tolerance."
    : "FAIL — colors diverge; check NV12 subresource copy / SRV plane / shader matrix.");

foreach (var d in new IDisposable[] { staging, rt, rtv, vs, ps, samp, srvLuma, srvChroma, nv12 }) d.Dispose();
zf.Dispose(); zb.Dispose();
Console.WriteLine($"logs: {logDir}");
return (samples > 0 && maxDiff <= 16) ? 0 : 7;
