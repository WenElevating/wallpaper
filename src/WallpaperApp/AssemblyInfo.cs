using System.Runtime.CompilerServices;
using System.Windows;

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]

// 允许测试程序集访问 internal 类型(如 NativeMethods 的 P/Invoke struct),
// 以便对 Win32 struct 大小做断言,锁住 marshal 布局正确性。
[assembly: InternalsVisibleTo("WallpaperApp.Tests")]
