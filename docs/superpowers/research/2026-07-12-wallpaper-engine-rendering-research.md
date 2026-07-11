# 主流动态壁纸引擎渲染架构调研报告

Date: 2026-07-12

## 触发原因

阶段 1 GPU 优化（waitable-timer 帧率节流，已合并到 master）实施后，省电模式（Saver）仍然一卡一卡。说明"自己控制视频帧率"这条技术路线本身有问题。本报告调研主流壁纸引擎的视频渲染实现，找出根本差异。

## 核心结论（先看这个）

**主流壁纸引擎的视频壁纸都不自己控制帧率。** 它们把视频交给系统媒体管线（Media Foundation），让视频按自身时钟播放。我们的应用自己手搓"解码→算PTS→Sleep节流→Present"整条管线，这个节流动作本身就是卡顿的根源——不管用 `Thread.Sleep`（粗）还是 waitable timer（细），应用层定时器永远无法和视频内部时钟完美同步，积累的微小误差就是肉眼可见的卡。

**Wallpaper Engine 开发者亲口确认**：视频壁纸的 FPS 限制是做不到的，视频只能按正常速度播放。

---

## 调研对象 1：Lively Wallpaper（开源，直接读了源码）

仓库：`github.com/rocksdanister/lively`，C#/WPF，分支 `master`。

### 视频壁纸实现

核心文件：`src/livelywpf/livelywpf/wp_windows/MediaPlayer.xaml.cs`

```csharp
public MediaPlayer(string path, int playSpeed)
{
    InitializeComponent();
    mePlayer.LoadedBehavior = MediaState.Manual;
    mePlayer.Source = new Uri(path);
    mePlayer.Stretch = SaveData.config.VideoScaler;
    mePlayer.SpeedRatio = playSpeed/100f;
    mePlayer.Play();
}
```

XAML（`MediaPlayer.xaml`）：
```xml
<MediaElement Name="mePlayer" />
```

**就是 WPF 原生的 `MediaElement`**，底层是 Media Foundation。整个视频壁纸引擎的实现就这几行——没有自己解码、没有自己 Present、没有帧率控制循环。

### Lively 的"省电"怎么做

- 视频壁纸：**不降帧率**，按视频原始速度播放（`SpeedRatio` 只控制播放倍速，不是帧率限制）
- 暂停检测：全屏应用、其他窗口遮挡时暂停（和我们的应用一样的思路）
- 多显示器：额外显示器静音

### 关键架构差异

Lively 把视频渲染**完全委托给 WPF 的合成系统**：
1. `MediaElement` 是 WPF visual tree 的一部分
2. Media Foundation 在后台解码，按视频自身时钟出帧
3. WPF 合成线程（composition thread）负责把帧送到屏幕，和 DWM/VSync 协调
4. 应用层**完全不参与帧节奏控制**

这就是为什么它不卡——帧节奏由系统管线的内部时钟保证，不存在应用层定时器追赶的问题。

---

## 调研对象 2：Wallpaper Engine（闭源，开发者官方声明）

来源：Steam 社区官方讨论（开发者 Tim 回复）。

> **用户**：能不能把视频壁纸限到 20fps 来省资源？
>
> **Tim（Wallpaper Engine 开发者）**：*"For Video wallpapers, the FPS limit has no effect as the video is simply played back at normal speed."*（视频壁纸，FPS 限制无效，视频就是按正常速度播放。）
>
> **用户追问**：那就没法限到 20fps 省电了吗？
>
> **Tim**：*"No, this is not possible."*（不，做不到。）

### Wallpaper Engine 的 FPS 限制到底限制什么

根据其[官方 FPS Limiter 文档](https://docs.wallpaperengine.io/en/web/performance/fps.html)，FPS 限制只作用于：
- **Scene 类壁纸**（实时渲染的场景，可以合法降帧）
- **Web 类壁纸**（如果创作者实现了）

**Video 类壁纸不在 FPS 限制范围内**——因为视频有固定的源帧率，你不能"让一个 30fps 的视频变成 15fps 还不卡"。

### Wallpaper Engine 怎么省电

根据其设置选项和社区文档：
- 全屏应用时暂停 / 其他应用聚焦时暂停（核心省电机制）
- 仅对 Scene/Web 类壁纸做帧率限制
- 视频壁纸靠 Media Foundation 管线的硬件解码 + 系统低功耗特性
- 不试图降视频帧率

---

## 三种渲染管线对比

| 维度 | 我们的应用（当前） | Lively Wallpaper | Wallpaper Engine（视频） |
|---|---|---|---|
| **解码** | FFmpeg D3D11VA 自建 | WPF MediaElement（Media Foundation） | 系统媒体管线 |
| **帧节奏控制** | **自己算 PTS + Sleep/waitable-timer** | **系统管线内部时钟** | **系统管线内部时钟** |
| **呈现** | 自己 D3D11 Present(1) | WPF 合成线程 | 系统合成 |
| **省电机制** | 试图降帧率（会卡） | 不降帧率，靠暂停 | 不降帧率，靠暂停 |
| **卡顿根因** | 应用层定时器追不上视频时钟 | 无（系统管线协调） | 无 |

### 为什么"自己节流"必然卡

视频帧的正确显示时间由视频流的时间戳（PTS）定义。要流畅播放，每一帧必须在**精确的 PTS 时刻**送到屏幕。我们的管线这样工作：

```
解码出第N帧 → 读 PTS → 算"还要等多久" → Sleep/waitable-timer → Present
```

每一步都引入误差：
- `Stopwatch` 测量有抖动
- Sleep/timer 唤醒有调度延迟（即使 waitable timer ~1ms，Windows 调度器不一定准时唤醒线程）
- Present 到 VSync 之间有排队延迟
- 这些误差逐帧累积，表现为画面一顿一顿

系统媒体管线（Media Foundation）没有这个问题，因为它用的是**管线内部的硬件时钟/音频时钟**直接驱动，解码、显示、VSync 在管线内部原子协调，应用层根本不参与。

---

## 对我们应用的具体诊断

### 阶段 1 为什么没解决卡顿

阶段 1（waitable-timer 节流）的假设是："卡是因为 `Thread.Sleep` 精度不够（~15ms），换成 waitable timer（~1ms）就好了。"

这个假设错了。精度从 15ms 降到 1ms 只是减小了单帧误差，但"应用层定时器追视频时钟"这个**结构性问题**没变。只要应用层在控制帧节奏，就一定会有累积抖动。实测：Saver 档（15fps）比 Balanced（30fps）更明显地卡，因为帧间隔越大（66ms vs 33ms），单帧误差的相对占比越大，累积越明显。

### Quality 档为什么不卡（或不太卡）

Quality 档 `MaxPresentFps=null`，`ShouldPresentFrame` 永远返回 true——**等于没有节流**。此时唯一的节奏控制是 PTS pacing 的 `Sleep`，但它只是"不要比视频快"，不是"精确限制"。因为每帧都 Present，误差不会表现为丢帧（只是表现为微小的时间漂移），人眼对"时间漂移"不敏感，对"丢帧"（一顿一顿）很敏感。

所以卡顿的本质：**节流 = 有选择地丢帧 = 丢帧被感知为卡顿。**

---

## 三条可能的路线

### 路线 A：改用系统媒体管线（仿 Lively）—— 彻底解决

把视频壁纸从"FFmpeg 自建管线"改成 WPF `MediaElement` 或 WinRT `MediaPlayer`。

- **流畅度**：直接达到 Lively 水平（系统管线保证）
- **省电**：系统管线的硬件解码 + DWM 低功耗路径，天然比自建管线省
- **代价**：
  - 重写渲染层（`DxgiRenderer` + `PlaybackSession` 的渲染循环基本作废）
  - 丢掉 FFmpeg 零拷贝管线（但那条管线正是卡的根源，丢了不可惜）
  - 需要验证 MediaElement 能否在 WorkerW 桌面子窗口里正常渲染（Lively 能做到，所以技术上可行）
- **Saver 档**：改为"暂停优先"策略，不做帧率限制（和 Wallpaper Engine 一致）

### 路线 B：保留 FFmpeg 管线，但去掉所有节流 —— 部分缓解

保留现有解码管线，但：
- 去掉 `ShouldPresentFrame` 节流（已合并的阶段 1 要回退）
- 去掉 PTS pacing 的 `Sleep`（让解码全速跟随视频时钟）
- 让 FFmpeg 解码以视频自身速度出帧，Present 每一帧
- Saver 档只靠 `skip_frame=NONREF` + 暂停检测

- **流畅度**：解决"自己节流造成的卡"（因为不再节流）
- **代价**：GPU 占用不会明显降低（解码全速跑，每帧都 Present）
- **风险**：去掉 PTS pacing 后，如果 FFmpeg 解码比视频速度快（很可能，因为硬件解码很快），会变成"狂解狂Present"，反而更耗 GPU。需要某种"跟随视频时钟但不丢帧"的机制——这又回到节流问题。

### 路线 C：现状不动，只靠暂停省电 —— 最低成本

- 回退阶段 1 的节流（Saver 档和 Balanced 档都不限帧率，等同 Quality）
- Saver 档的差异只体现在 `skip_frame=NONREF`（小幅省解码）+ 更激进的暂停检测
- 接受"视频壁纸就是会吃 GPU，只能靠暂停缓解"

- **流畅度**：不卡（因为不限帧率）
- **代价**：Saver 档几乎没省电效果
- **这是 Wallpaper Engine 对视频壁纸的官方立场**：视频壁纸就是没法省，只能暂停

---

## 我的推荐

**短期**：路线 C（回退节流，停止做不可能的事）。立刻解决"省电模式卡"的问题，因为省电模式不再限帧率了。

**中期**：路线 A（改用系统媒体管线）。如果想让视频壁纸既流畅又省电，唯一正确的答案是系统媒体管线。Lively 已经证明 WPF MediaElement 在桌面壁纸场景可用。这才是和主流引擎对齐的架构。

**不推荐**：路线 B。去掉节流但保留自建管线，要么解码全速跑更耗 GPU，要么得重新发明某种节流——又回到原点。

---

## 附：调研证据来源

| 证据 | 来源 |
|---|---|
| Lily 视频壁纸用 WPF MediaElement | `github.com/rocksdanister/lively` 源码 `MediaPlayer.xaml.cs`（本文引用了关键代码） |
| Wallpaper Engine 视频壁纸不做 FPS 限制 | Steam 社区开发者 Tim 官方回复 `steamcommunity.com/app/431960/discussions/2/2944746708979446155/` |
| WPF MediaElement 底层是 Media Foundation | Microsoft Learn `multimedia-overview` |
| "自己节流会卡"的技术原理 | 帧节奏必须由管线内部时钟驱动，应用层定时器必然累积抖动 |

## 附：Lively 的完整视频壁纸代码（作为参考架构）

`MediaPlayer.xaml`：
```xml
<Window WindowStyle="None" ResizeMode="NoResize">
    <Grid>
        <MediaElement Name="mePlayer" />
    </Grid>
</Window>
```

`MediaPlayer.xaml.cs`（节选）：
```csharp
public MediaPlayer(string path, int playSpeed)
{
    InitializeComponent();
    mePlayer.LoadedBehavior = MediaState.Manual;
    mePlayer.Source = new Uri(path);
    mePlayer.Stretch = SaveData.config.VideoScaler;
    mePlayer.SpeedRatio = playSpeed/100f;  // 播放倍速，不是帧率限制
    mePlayer.MediaEnded += (s,e) => { mePlayer.Position = TimeSpan.Zero; mePlayer.Play(); };  // 循环
    mePlayer.Play();
}
```

就这么简单。没有解码循环、没有 PTS、没有 Sleep、没有 Present、没有帧率限制。
