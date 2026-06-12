using System.Runtime.InteropServices;

namespace WallpaperApp.Interop;

internal static partial class D2D1
{
    private const string DllName = "d2d1.dll";

    internal const int D2D1_FACTORY_TYPE_SINGLE_THREADED = 0;
    internal const int D2D1_RENDER_TARGET_TYPE_DEFAULT  = 2;
    internal const int D2D1_PRESENT_OPTIONS_NONE        = 0;
    internal const int D2D1_FEATURE_LEVEL_DEFAULT       = 0;
    internal const int D2D1_ALPHA_MODE_IGNORE           = 2;
    internal const int D2D1_BITMAP_OPTIONS_NONE         = 0;

    internal const int S_OK = 0;
    internal const int D2DERR_RECREATE_TARGET = unchecked((int)0x8899000C);

    private static readonly Guid IID_ID2D1Factory = new("06152247-04f6-4698-8b2d-6d1b2d0b7b1a");

    [DllImport(DllName)]
    internal static extern int D2D1CreateFactory(
        int factoryType,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        out IntPtr ppFactory);

    // ── ID2D1Factory ──
    // vtable: [0]QueryInterface [1]AddRef [2]Release [3]ReloadSystemResources
    //         [4]GetDesktopDpi [5-13] geometry methods
    //         [14]CreateHwndRenderTarget

    internal static int CreateHwndRenderTarget(
        IntPtr factory,
        ref D2D1_RENDER_TARGET_PROPERTIES renderTargetProps,
        ref D2D1_HWND_RENDER_TARGET_PROPERTIES hwndProps,
        out IntPtr renderTarget)
    {
        var vtable = Marshal.ReadIntPtr(factory);
        var methodPtr = Marshal.ReadIntPtr(vtable, 14 * IntPtr.Size);
        var fn = Marshal.GetDelegateForFunctionPointer<CreateHwndRenderTargetFn>(methodPtr);
        return fn(factory, ref renderTargetProps, ref hwndProps, out renderTarget);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateHwndRenderTargetFn(IntPtr factory,
        ref D2D1_RENDER_TARGET_PROPERTIES renderTargetProps,
        ref D2D1_HWND_RENDER_TARGET_PROPERTIES hwndProps,
        out IntPtr renderTarget);

    // ── ID2D1RenderTarget ──
    // vtable: [0]QueryInterface [1]AddRef [2]Release [3]GetFactory
    //         [4]CreateBitmap [5]BeginDraw [6]EndDraw [7]Clear
    //         [8]SetTransform [9-12] ... [13]DrawBitmap [14-18] ...
    //         [19]CreateBitmap (the actual method we need)

    internal static int BeginDraw(IntPtr renderTarget)
    {
        var vtable = Marshal.ReadIntPtr(renderTarget);
        var fn = GetFn<BeginDrawFn>(vtable, 5);
        return fn(renderTarget);
    }

    internal static int EndDraw(IntPtr renderTarget)
    {
        var vtable = Marshal.ReadIntPtr(renderTarget);
        var fn = GetFn<EndDrawFn>(vtable, 6);
        return fn(renderTarget);
    }

    internal static int DrawBitmap(
        IntPtr renderTarget, IntPtr bitmap,
        ref D2D1_RECT_F destinationRect,
        float opacity, int interpolationMode,
        ref D2D1_RECT_F sourceRect)
    {
        var vtable = Marshal.ReadIntPtr(renderTarget);
        var fn = GetFn<DrawBitmapFn>(vtable, 13);
        return fn(renderTarget, bitmap, ref destinationRect, opacity, interpolationMode, ref sourceRect);
    }

    internal static int CreateBitmap(
        IntPtr renderTarget,
        D2D1_SIZE_U size,
        IntPtr srcData,
        int pitch,
        ref D2D1_BITMAP_PROPERTIES bitmapProps,
        out IntPtr bitmap)
    {
        var vtable = Marshal.ReadIntPtr(renderTarget);
        var fn = GetFn<CreateBitmapFn>(vtable, 19);
        return fn(renderTarget, size, srcData, pitch, ref bitmapProps, out bitmap);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int BeginDrawFn(IntPtr renderTarget);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int EndDrawFn(IntPtr renderTarget);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int DrawBitmapFn(IntPtr renderTarget, IntPtr bitmap,
        ref D2D1_RECT_F dstRect, float opacity, int interpolation,
        ref D2D1_RECT_F srcRect);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateBitmapFn(IntPtr renderTarget,
        D2D1_SIZE_U size, IntPtr srcData, int pitch,
        ref D2D1_BITMAP_PROPERTIES props, out IntPtr bitmap);

    // ── ID2D1Bitmap ──
    // vtable: [0]QueryInterface [1]AddRef [2]Release [3]GetSize
    //         [4]GetPixelSize [5]GetPixelFormat [6]GetDpi
    //         [7]CopyFromMemory [8]CopyFromBitmap [9]CopyFromRenderTarget

    internal static int CopyFromMemory(
        IntPtr bitmap, IntPtr dstRect, IntPtr srcData, int pitch)
    {
        var vtable = Marshal.ReadIntPtr(bitmap);
        var fn = GetFn<CopyFromMemoryFn>(vtable, 7);
        return fn(bitmap, dstRect, srcData, pitch);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CopyFromMemoryFn(IntPtr bitmap, IntPtr dstRect, IntPtr srcData, int pitch);

    // ── COM helpers ──

    internal static int Release(IntPtr comPtr)
    {
        if (comPtr == IntPtr.Zero) return 0;
        var vtable = Marshal.ReadIntPtr(comPtr);
        var fn = GetFn<ReleaseFn>(vtable, 2);
        return fn(comPtr);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReleaseFn(IntPtr comPtr);

    private static T GetFn<T>(IntPtr vtable, int slot) where T : class
    {
        var methodPtr = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
        return Marshal.GetDelegateForFunctionPointer<T>(methodPtr);
    }

    // ── Structs ──

    [StructLayout(LayoutKind.Sequential)]
    internal struct D2D1_SIZE_U
    {
        public uint Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D2D1_RECT_F
    {
        public float Left, Top, Right, Bottom;
        public static D2D1_RECT_F Full(float w, float h) =>
            new() { Right = w, Bottom = h };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D2D1_PIXEL_FORMAT
    {
        public int Format;    // DXGI_FORMAT (0 = UNKNOWN)
        public int AlphaMode; // D2D1_ALPHA_MODE
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D2D1_RENDER_TARGET_PROPERTIES
    {
        public int Type;
        public D2D1_PIXEL_FORMAT PixelFormat;
        public float DpiX, DpiY;
        public int Usage;
        public int MinLevel;

        public static D2D1_RENDER_TARGET_PROPERTIES Default() => new()
        {
            Type = D2D1_RENDER_TARGET_TYPE_DEFAULT,
            PixelFormat = new D2D1_PIXEL_FORMAT
            {
                Format = 0,
                AlphaMode = D2D1_ALPHA_MODE_IGNORE
            },
            DpiX = 96, DpiY = 96,
            Usage = 0,
            MinLevel = D2D1_FEATURE_LEVEL_DEFAULT
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D2D1_HWND_RENDER_TARGET_PROPERTIES
    {
        public IntPtr Hwnd;
        public D2D1_SIZE_U PixelSize;
        public int PresentOptions;

        public static D2D1_HWND_RENDER_TARGET_PROPERTIES ForHwnd(IntPtr hwnd, uint w, uint h) =>
            new()
            {
                Hwnd = hwnd,
                PixelSize = new D2D1_SIZE_U { Width = w, Height = h },
                PresentOptions = D2D1_PRESENT_OPTIONS_NONE
            };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct D2D1_BITMAP_PROPERTIES
    {
        public D2D1_PIXEL_FORMAT PixelFormat;
        public float DpiX, DpiY;

        public static D2D1_BITMAP_PROPERTIES Default() => new()
        {
            PixelFormat = new D2D1_PIXEL_FORMAT
            {
                Format = 0,
                AlphaMode = D2D1_ALPHA_MODE_IGNORE
            },
            DpiX = 96, DpiY = 96
        };
    }
}
