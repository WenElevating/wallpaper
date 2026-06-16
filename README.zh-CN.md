# WallpaperApp — 动态壁纸

> 一款 Windows 桌面应用，将本地视频文件（MP4、WebM、GIF 等）设置为桌面动态壁纸，显示在桌面图标后方。

[**English Version**](README.md)

---

## 功能特性

- **视频 & GIF 壁纸** — 导入本地 MP4、WebM、AVI、MOV、MKV、GIF 文件并设为桌面壁纸。
- **图标后方渲染** — 通过 Win32 WorkerW 嵌入，壁纸显示在桌面图标之下，免疫 Win+D 和 Z 序重排。
- **多显示器支持** — 每台显示器可独立设置不同的壁纸。
- **硬件加速播放** — D3D11VA 硬解 + DXGI Flip-Model 交换链，GPU 零拷贝管线（NV12→RGB 像素着色器，无 CPU 回传）。
- **软件降级** — 无 GPU 加速时自动降级到 FFmpeg 软解 + Direct2D/DXGI CPU 上传渲染。
- **全屏自动暂停** — 检测到全屏无边框应用时自动暂停所有壁纸。
- **电池感知** — 笔记本运行于电池时自动暂停壁纸，延长续航。
- **系统托盘** — 最小化到托盘，右键菜单支持暂停/恢复和语言切换。
- **壁纸库管理** — 浏览、搜索、排序导入的壁纸，支持缩略图预览。
- **国际化** — 支持简体中文和 English，运行时可随时切换。
- **开机自启** — 可选随 Windows 启动。

## 截图

*(即将推出)*

## 快速开始

### 系统要求

- Windows 10 或更高版本（64 位）
- [.NET 8 运行时](https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0)
- 支持 Direct3D 11 的 GPU（硬件加速需要）

### 安装

1. 从 [Releases](https://github.com/yourname/WallpaperApp/releases) 页面下载最新版本。
2. 解压并运行 `WallpaperApp.exe`。

### 从源码构建

```bash
# 克隆仓库
git clone https://github.com/yourname/WallpaperApp.git
cd WallpaperApp

# 构建
dotnet build WallpaperApp.sln

# 运行
dotnet run --project src/WallpaperApp

# 运行测试
dotnet test tests/WallpaperApp.Tests
```

> **注意：** `lib/ffmpeg/` 包含预编译的 FFmpeg 7.x DLL。smoke test 还需要将 `ffmpeg.exe` 添加到 PATH 环境变量中以进行交叉验证。

## 技术栈

| 层 | 技术 |
|---|---|
| UI 框架 | WPF（.NET 8，仅 Windows） |
| 桌面集成 | Win32 P/Invoke（WorkerW、Shell_NotifyIcon） |
| 视频解码 | FFmpeg 7.x 通过 P/Invoke（可选 D3D11VA 硬解） |
| 渲染 | Direct3D 11 / DXGI Flip-Model 交换链（主要），Direct2D（降级） |
| 着色器编译 | `d3dcompiler_47.dll` 运行时 HLSL 编译 |
| 数据库 | SQLite via Entity Framework Core |
| 本地化 | .NET 资源文件 (.resx) |
| 依赖注入 | Microsoft.Extensions.DependencyInjection |
| 测试 | xUnit、Moq |

## 项目结构

```
WallpaperApp/
├── src/WallpaperApp/        # 主应用
│   ├── App.xaml.cs          # 启动、DI 注册、异常处理
│   ├── Data/                # EF Core SQLite 上下文
│   ├── Interop/             # Win32 和 FFmpeg P/Invoke 声明
│   ├── Localization/        # 字符串资源、语言切换
│   ├── Models/              # 数据模型（AppSettings、WallpaperItem）
│   ├── Services/
│   │   ├── Desktop/         # WorkerW 嵌入、壁纸窗口宿主
│   │   ├── Library/         # 文件导入、缩略图、元数据
│   │   ├── Logging/         # 基于文件的每日日志轮转
│   │   ├── Monitor/         # 显示器枚举、全屏检测、电池感知
│   │   ├── Playback/        # 播放管理器、FFmpeg 后端、DXGI/D2D 渲染器
│   │   └── Settings/        # JSON 设置持久化、开机自启注册表
│   └── UI/
│       ├── Controls/        # 自定义 WPF 控件（VideoFrameView、WallpaperCard）
│       ├── ViewModels/      # MainViewModel
│       ├── Views/           # 库、详情、设置、显示器选择窗口
│       └── TrayIcon.cs      # 手工实现的系统托盘图标（Win32 NOTIFYICONDATA）
├── tests/
│   ├── WallpaperApp.Tests/           # xUnit 单元测试
│   ├── WallpaperApp.FfmpegProbe/     # 独立 FFmpeg 解码探针（测试工具）
│   ├── WallpaperApp.HwDecodeProbe/   # D3D11VA 硬解探针
│   └── WallpaperApp.RenderProbe/     # DXGI/D2D 渲染测试探针
├── lib/ffmpeg/              # FFmpeg 7.x 原生 DLL
└── docs/                    # 设计文档与规格说明
```

## 架构亮点

### GPU 零拷贝渲染管线

共享的 `GpuDevice` 单例实现了真正的零拷贝渲染路径：

```
FFmpeg D3D11VA 硬解 ──► NV12 纹理 (GPU) ──► NV12→RGB 像素着色器 ──► DXGI Flip-Model 交换链
                              │
                        (无 CPU 回传)
```

当共享设备不可用时，解码降级到软件路径，渲染使用 CPU 上传路径（BGRA 缓冲 → staging 纹理 → 后备缓冲）。

### 渲染线程隔离

每台显示器拥有独立的专用渲染线程，该线程同时拥有 Win32 壁纸窗口和 D2D/DXGI 渲染目标。D2D 单线程工厂要求如此，违反此约定会导致 `EndDraw()` 返回 `S_OK` 但屏幕上没有任何显示。

### 暂停原因计数机制

多个暂停源（用户、全屏、电池）通过 `HashSet<PauseReason>` 独立追踪。会话在暂停数从空→非空时暂停，从最后一个原因清除时恢复，确保自动暂停不会覆盖手动暂停，反之亦然。

## 许可协议

*(在此指定您的许可协议)*
