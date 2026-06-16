using System.Runtime.InteropServices;
using System.Text;
using WallpaperApp.Services.Logging;

namespace WallpaperApp.Services.Playback;

// Compiles the NV12 -> RGB shaders at runtime via d3dcompiler_47.dll (a system
// DLL on Windows 8.1+, no extra dependency). Bytecode is small and the compile
// runs once; failures return null so the renderer can fall back to CPU upload.
//
// We avoid COM-interop for ID3DBlob (which trips an RCW marshalling error) and
// instead take the blob back as a raw IntPtr, then read its vtable directly:
// ID3DBlob = IUnknown(3) + GetBufferPointer(slot 3) + GetBufferSize(slot 4).
public static class Nv12Shader
{
    // Fullscreen triangle from SV_VertexID — no vertex buffer or input layout.
    private const string VsSrc = @"
        struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
        VSOut main(uint id : SV_VertexID) {
            VSOut o;
            o.uv  = float2((id << 1) & 2, id & 2);
            o.pos = float4(o.uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return o;
        }";

    // BT.709 limited-range YUV -> RGB (matches sws_setColorspaceDetails for the
    // 4K/high-def case). luma = R8 (plane 0), chroma = R8G8 (plane 1: .x=U,.y=V).
    private const string PsSrc = @"
        Texture2D<float>   lumaTx  : register(t0);
        Texture2D<float2>  chromaTx: register(t1);
        SamplerState       samp    : register(s0);
        struct VSOut { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };
        float4 main(VSOut i) : SV_TARGET {
            float y   = lumaTx.Sample(samp, i.uv);
            float2 uv = chromaTx.Sample(samp, i.uv);
            y         = (y - 16.0/255.0)  * (255.0/219.0);
            float cb  = (uv.x - 128.0/255.0) * (255.0/224.0);
            float cr  = (uv.y - 128.0/255.0) * (255.0/224.0);
            float r = y + 1.5748 * cr;
            float g = y - 0.1873 * cb - 0.4681 * cr;
            float b = y + 1.8556 * cb;
            return float4(saturate(r), saturate(g), saturate(b), 1.0);
        }";

    public static byte[]? CompileVertexShader() => Compile(VsSrc, "vs_5_0");
    public static byte[]? CompilePixelShader() => Compile(PsSrc, "ps_5_0");

    public static FileLogger? Logger { get; set; }

    private static byte[]? Compile(string source, string profile)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(source);
            var hr = D3DCompile(bytes, (nuint)bytes.Length, "nv12", IntPtr.Zero, IntPtr.Zero,
                "main", profile, 0u, 0u, out var blobPtr, out var errPtr);
            if (hr < 0 || blobPtr == IntPtr.Zero)
            {
                string msg = errPtr != IntPtr.Zero ? BlobToString(errPtr) : "(no error blob)";
                Logger?.Warn($"D3DCompile({profile}) failed hr=0x{hr:X8}: {msg}");
                if (errPtr != IntPtr.Zero) ReleaseBlob(errPtr);
                return null;
            }
            var result = BlobToBytes(blobPtr);
            ReleaseBlob(blobPtr);
            return result;
        }
        catch (Exception ex)
        {
            Logger?.Warn($"D3DCompile({profile}) exception: {ex.Message}");
            return null;
        }
    }

    // Read the ID3DBlob's buffer into a byte[] via its vtable.
    private static byte[] BlobToBytes(IntPtr blob)
    {
        var vtable = Marshal.ReadIntPtr(blob);
        var getBuf = Marshal.GetDelegateForFunctionPointer<GetBufferPtrFn>(Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size));
        var getSize = Marshal.GetDelegateForFunctionPointer<GetBufferSizeFn>(Marshal.ReadIntPtr(vtable, 4 * IntPtr.Size));
        var ptr = getBuf(blob);
        var size = (int)getSize(blob);
        var result = new byte[size];
        Marshal.Copy(ptr, result, 0, size);
        return result;
    }

    private static unsafe string BlobToString(IntPtr blob)
    {
        var bytes = BlobToBytes(blob);
        fixed (byte* p = bytes) return Encoding.UTF8.GetString(p, bytes.Length);
    }

    private static void ReleaseBlob(IntPtr blob)
    {
        var vtable = Marshal.ReadIntPtr(blob);
        var release = Marshal.GetDelegateForFunctionPointer<ReleaseFn>(Marshal.ReadIntPtr(vtable, 2 * IntPtr.Size));
        release(blob);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr GetBufferPtrFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate nuint GetBufferSizeFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate uint ReleaseFn(IntPtr self);

    [DllImport("d3dcompiler_47.dll", EntryPoint = "D3DCompile")]
    private static extern int D3DCompile(
        byte[] srcData, nuint srcDataSize,
        [MarshalAs(UnmanagedType.LPStr)] string? sourceName,
        IntPtr pDefines, IntPtr pInclude,
        [MarshalAs(UnmanagedType.LPStr)] string entryPoint,
        [MarshalAs(UnmanagedType.LPStr)] string target,
        uint flags1, uint flags2,
        out IntPtr code, out IntPtr errorMsgs);
}
