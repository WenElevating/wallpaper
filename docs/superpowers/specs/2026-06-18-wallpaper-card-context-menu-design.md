# 壁纸库卡片右键菜单（Context Menu）设计

- **日期：** 2026-06-18
- **状态：** 待实现
- **作者：** brainstorming 会话产出

## 1. 背景与目标

壁纸库（`LibraryView`）目前只能通过左键点击卡片进入详情页，**缺少删除等管理操作**。用户希望增加右键菜单，承载一组壁纸管理功能。

**核心目标：**
- 给 `WallpaperCard` 增加右键菜单，提供常用的壁纸管理操作入口
- 优先满足核心诉求（删除），同时补齐业界惯例功能
- 复用现有架构（`RelayCommand` / 自定义窗口范式 / toast / 本地化），不引入新模式
- 样式贴合现有 Catppuccin Mocha 暗色玻璃主题

## 2. 业界调研结论

| 功能 | Wallpaper Engine | Lively | 通用媒体库 |
|------|:--:|:--:|:--:|
| 设为壁纸 | ✅ | ✅ | — |
| 打开详情 | ✅ | — | — |
| 打开文件位置 | ✅ | — | ✅ |
| 删除 | ✅(取消订阅) | — | ✅ |
| 重命名 | — | — | ✅ |
| 加入播放列表/相册 | — | — | ✅ |
| 复制文件 | — | — | ✅ |
| 收藏/隐藏 | ✅ | — | ✅ |

**本项目取舍：** 收藏/隐藏需要加 DB 字段、改过滤/排序逻辑，范围偏大且与"播放列表"功能定位重叠，本期**不做**。

## 3. 功能范围（本期）

经确认，本期实现以下 7 个右键菜单项，按分组排列：

| 分组 | 菜单项 | 命令 |
|------|--------|------|
| 查看 | 设为壁纸 | `SetAsWallpaperCommand`（复用+改造） |
| 查看 | 打开详情 | `OpenDetailCommand`（复用） |
| 文件操作 | 在文件夹中打开 | `OpenFileLocationCommand`（新增） |
| 文件操作 | 重命名… | `RenameWallpaperCommand`（新增） |
| 文件操作 | 加入播放列表… | `AddToPlaylistCommand`（新增，弹选择窗口） |
| 文件操作 | 复制到… | `CopyToFolderCommand`（新增） |
| 破坏性 | 删除 | `DeleteWallpaperCommand`（新增，红色） |

分组依据：误触概率从上到下递增，破坏性操作放最底并用分隔线隔开 + 红色。

## 4. 架构设计

### 4.1 命令承载方式

采用**纯 XAML ContextMenu**（用户决策）。命令通过一个聚合对象 `WallpaperCommands` 下发，避免给 `WallpaperCard` 加 7 个 DependencyProperty。

```csharp
// UI/Controls/WallpaperCommands.cs
public sealed class WallpaperCommands
{
    public ICommand SetAsWallpaper { get; init; } = null!;
    public ICommand OpenDetail { get; init; } = null!;
    public ICommand OpenFileLocation { get; init; } = null!;
    public ICommand Rename { get; init; } = null!;
    public ICommand AddToPlaylist { get; init; } = null!;
    public ICommand CopyToFolder { get; init; } = null!;
    public ICommand Delete { get; init; } = null!;
}
```

`WallpaperCard` 新增一个 DP `Commands`，绑定整个聚合对象。

### 4.2 ContextMenu 绑定（关键：脱离可视树）

`ContextMenu` 不在 WPF 可视树中，无法用常规 `RelativeSource AncestorType` 找到 ViewModel。用 `PlacementTarget`（即承载 ContextMenu 的 `WallpaperCard`）桥接：

```xml
<Border.ContextMenu>
    <ContextMenu>
        <MenuItem Header="{loc:Loc MenuSetAsWallpaper}"
                  Command="{Binding PlacementTarget.Commands.SetAsWallpaper,
                            RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                  CommandParameter="{Binding PlacementTarget.DataContext,
                            RelativeSource={RelativeSource AncestorType=ContextMenu}}"/>
        <!-- 其余 MenuItem 同理 -->
    </ContextMenu>
</Border.ContextMenu>
```

`CommandParameter` 始终是卡片的 `DataContext`（即 `WallpaperItem`）。

### 4.3 命令组装位置

`MainViewModel` 构造函数末尾组装 `WallpaperCommands`：

```csharp
public WallpaperCommands Commands { get; }

// 构造函数末尾
Commands = new WallpaperCommands
{
    SetAsWallpaper = SetAsWallpaperCommand,
    OpenDetail = OpenDetailCommand,
    OpenFileLocation = new RelayCommand(p => OpenFileLocation(p as WallpaperItem)),
    Rename = new RelayCommand(p => _ = RenameAsync(p as WallpaperItem)),
    AddToPlaylist = new RelayCommand(p => _ = AddToPlaylistFromCardAsync(p as WallpaperItem), _ => Playlists.Count > 0),
    CopyToFolder = new RelayCommand(p => _ = CopyToFolderAsync(p as WallpaperItem)),
    Delete = new RelayCommand(p => _ = DeleteWallpaperAsync(p as WallpaperItem)),
};
```

`LibraryView.xaml` 的卡片模板绑定：
```xml
<controls:WallpaperCard Margin="10"
    OpenCommand="{Binding DataContext.OpenDetailCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
    Commands="{Binding DataContext.Commands, RelativeSource={RelativeSource AncestorType=ItemsControl}}"/>
```

## 5. 各操作详细设计

### 5.1 删除（最复杂，唯一不可逆）

**确认方式：** 自定义模态窗口 `ConfirmDeleteWindow`（不用系统 `MessageBox`，沿用项目 `MonitorPickerWindow` 范式）。窗口内动态展示影响项，让用户知情。

**执行顺序（先清理依赖、后删本体，保证文件句柄释放）：**

```
1. 统计影响：
   - playingMonitors = Monitors 中 GetActiveWallpaperId == wallpaper.Id 的显示器
   - referencedPlaylists = Playlists 中 Members 含此 wallpaperId 的列表
2. 弹 ConfirmDeleteWindow(wallpaper, playingMonitors.Count, referencedPlaylists.Count)
   用户取消 → 返回
3. 对每个 playingMonitor：await _playback.RemoveWallpaperAsync(monitorId)
   （先释放播放器对文件的句柄，否则 Windows 上文件可能删不掉）
4. 对每个 referencedPlaylist：await _playlistService.RemoveMemberAsync(playlistId, wallpaperId)
   （幂等，级联清理播放列表引用）
5. await _library.DeleteAsync(wallpaper.Id)
   （删 DB 记录 + 删源文件 + 删缩略图，已实现）
6. Wallpapers.Remove(wallpaper)  ← 同步内存集合
7. ShowToast("已删除 'xxx'")
```

**ConfirmDeleteWindow 窗口内容：**
```
删除壁纸
─────────────────────
确定要删除 "xxx.mp4" 吗？
此操作将永久删除源文件，无法恢复。

• 正在播放：是          （仅 playingMonitors.Count > 0 时显示）
• 播放列表引用：3 个     （仅 referencedPlaylists.Count > 0 时显示）

[ 取消 ]   [ 删除（DangerButtonStyle 红色主按钮） ]
```

### 5.2 重命名

**交互：** 自定义 `RenameWindow`，预填当前 `DisplayName`，全选文本聚焦。校验：去空格后非空且与原值不同才启用确认按钮。

**后端新增：**
```csharp
// LibraryService.cs
public async Task<bool> RenameAsync(Guid id, string newName, CancellationToken ct = default)
{
    await using var db = CreateDbContext();
    var item = await db.WallpaperItems.FindAsync(new object[] { id }, ct);
    if (item == null) return false;
    var trimmed = newName.Trim();
    if (string.IsNullOrEmpty(trimmed)) return false;
    item.DisplayName = trimmed;
    await db.SaveChangesAsync(ct);
    _logger.Info($"Renamed {id} -> '{trimmed}'");
    return true;
}
```

**内存同步：** `WallpaperItem` 无 `INotifyPropertyChanged`，改属性后 UI 不自动刷新。方案：直接改内存对象属性 + `WallpapersView.Refresh()`（让排序/过滤重算）。

```csharp
wallpaper.DisplayName = newName;
WallpapersView.Refresh();
```

### 5.3 加入播放列表

**交互：** 弹 `PlaylistPickerWindow`（用户决策：弹选择窗口，不做子菜单）。列出所有 `Playlists`，点击即加入。无播放列表时该菜单项禁用。

**为何不复用现有 `AddToPlaylistCommand`：** 现有命令用 `_selectedPlaylist`（播放列表管理页选中的那个），而卡片场景下用户应能选任意播放列表。故新增独立命令 `AddToPlaylistFromCardAsync`：弹窗 → 用户选 → `AddMemberAsync` → 刷新。

### 5.4 复制到…

**交互：** `Microsoft.Win32.SaveFileDialog`（项目已用同命名空间的 `OpenFileDialog`），默认文件名 = `OriginalFileName`，`SaveFileDialog` 本身处理"目标已存在"的覆盖确认。

```csharp
var dialog = new SaveFileDialog
{
    FileName = wallpaper.OriginalFileName,
    Filter = $"{Strings.DlgImportFilterAll}|*.*",
    Title = Strings.DlgCopyTitle
};
if (dialog.ShowDialog() == true)
{
    try { File.Copy(wallpaper.ManagedFilePath, dialog.FileName, overwrite: true); ShowToast(Strings.MsgCopySuccess); }
    catch (Exception ex) { _logger.Warn($"Copy failed: {ex.Message}"); ShowToast(Strings.MsgCopyFailed, error: true); }
}
```

### 5.5 在文件夹中打开

```csharp
Process.Start("explorer.exe", $"/select,\"{wallpaper.ManagedFilePath}\"");
```

### 5.6 设为壁纸（复用 + 改造）

现有 `SetAsWallpaperCommand` 读 `ActiveWallpaper`。改造为兼容参数：`parameter as WallpaperItem ?? ActiveWallpaper`。详情页无参调用（用 `ActiveWallpaper`），卡片菜单带参调用（用具体 `WallpaperItem`）。

## 6. 样式（贴合现有主题）

主题：Catppuccin Mocha 暗色玻璃（见 `App.xaml`）。新窗口/菜单复用现有资源：
- 背景 `#1E1E2E`（窗口）/ `SurfaceBrush`（控件）
- 文字 `TextBrush` / `MutedTextBrush`
- 主按钮 `PrimaryButtonStyle`
- 窗口骨架参照 `MonitorPickerWindow.xaml`

**App.xaml 需新增 2 个资源：**

1. **`DangerButtonStyle`**（删除确认按钮用，红色变体，复刻 `PrimaryButtonStyle` 结构但渐变改红色）：
   - 渐变 `#F38BA8` → `#EBA0AC`
   - 发光 `#F38BA8`

2. **`ContextMenu` / `MenuItem` 样式**（App.xaml 目前没有，默认白底会跳主题）：
   - `ContextMenu` 背景 `SurfaceBrush`，边框 `BorderBrush`，圆角 8，带 `CardShadowEffect`
   - `MenuItem` hover 时背景 `SurfaceHoverBrush`，文字 `TextBrush`，圆角 4
   - `MenuItem` separator 用 `BorderBrush`

## 7. 本地化

`Strings.cs` + `Resources/Strings.resx`(en) + `Resources/Strings.zh-CN.resx`(zh) 各加约 20 个键：

| Key | en | zh-CN |
|---|---|---|
| `MenuSetAsWallpaper` | Set as wallpaper | 设为壁纸 |
| `MenuOpenDetail` | Open details | 打开详情 |
| `MenuOpenFileLocation` | Open file location | 在文件夹中打开 |
| `MenuRename` | Rename… | 重命名… |
| `MenuAddToPlaylist` | Add to playlist… | 加入播放列表… |
| `MenuCopyToFolder` | Copy to… | 复制到… |
| `MenuDelete` | Delete | 删除 |
| `DlgDeleteTitle` | Delete wallpaper | 删除壁纸 |
| `DlgDeletePrompt` | Are you sure you want to delete "{0}"? The source file will be permanently removed. | 确定要删除"{0}"吗？此操作将永久删除源文件，无法恢复。 |
| `DlgDeletePlaying` | Currently playing: yes | 正在播放：是 |
| `DlgDeletePlaylistRefs` | Playlist references: {0} | 播放列表引用：{0} 个 |
| `DlgDeleteConfirm` | Delete | 删除 |
| `DlgCancel` | Cancel | 取消 |
| `DlgRenameTitle` | Rename | 重命名 |
| `DlgRenamePrompt` | New name | 新名称 |
| `DlgPickPlaylistTitle` | Add to playlist | 加入播放列表 |
| `DlgPlaylistEmpty` | No playlists. Create one in the Playlists tab first. | 没有播放列表，请先在"播放列表"页创建。 |
| `DlgCopyTitle` | Copy to… | 复制到… |
| `MsgDeleted` | Deleted "{0}" | 已删除"{0}" |
| `MsgRenamed` | Renamed | 已重命名 |
| `MsgAddedToPlaylist` | Added to "{0}" | 已加入"{0}" |
| `MsgCopySuccess` | Copied | 已复制 |
| `MsgCopyFailed` | Copy failed | 复制失败 |
| `MsgNameEmpty` | Name cannot be empty | 名称不能为空 |

## 8. 改动文件清单

| 文件 | 改动类型 | 说明 |
|---|---|---|
| `Services/Library/LibraryService.cs` | 修改 | +`RenameAsync` |
| `UI/ViewModels/MainViewModel.cs` | 修改 | +6 命令逻辑、`Commands` 属性、删除/重命名/复制 handler |
| `UI/Controls/WallpaperCard.xaml` | 修改 | +`ContextMenu` |
| `UI/Controls/WallpaperCard.xaml.cs` | 修改 | +`Commands` DP |
| `UI/Controls/WallpaperCommands.cs` | 新增 | 聚合命令对象 |
| `UI/Views/LibraryView.xaml` | 修改 | 卡片绑定 `Commands` |
| `UI/Views/ConfirmDeleteWindow.xaml(.cs)` | 新增 | 删除确认窗口 |
| `UI/Views/RenameWindow.xaml(.cs)` | 新增 | 重命名窗口 |
| `UI/Views/PlaylistPickerWindow.xaml(.cs)` | 新增 | 播放列表选择窗口 |
| `App.xaml` | 修改 | +`DangerButtonStyle`、`ContextMenu`/`MenuItem` 样式 |
| `Localization/Strings.cs` | 修改 | +约 20 个属性 |
| `Resources/Strings.resx` | 修改 | +en 文案 |
| `Resources/Strings.zh-CN.resx` | 修改 | +zh-CN 文案 |
| `tests/.../LibraryServiceTests.cs` | 修改 | +`RenameAsync` 单测 |
| `tests/.../MainViewModelWallpaperCommandsTests.cs` | 新增 | 删除/重命名/加列表命令测试 |

## 9. 测试策略

沿用现有 `tests/WallpaperApp.Tests/` 模式（xUnit + 内存 SQLite，见 `LibraryServiceTests` / `MainViewModelPlaylistTests`）：

**可测（业务逻辑）：**
- `LibraryService.RenameAsync`：正常改名、空名拒绝、不存在 id 返回 false
- `DeleteWallpaperCommand`：
  - 正在播放时先调 `RemoveWallpaperAsync`（用 mock/fake PlaybackManager）
  - 有播放列表引用时清理成员
  - 最后调 `DeleteAsync`
  - `Wallpapers` 集合移除该项
- `RenameWallpaperCommand`：调 `RenameAsync` + 内存对象更新 + `WallpapersView` 刷新
- `AddToPlaylistFromCardAsync`：选了播放列表 → `AddMemberAsync`
- `SetAsWallpaperCommand` 改造：带参用参数、无参用 `ActiveWallpaper`

**不测（系统交互薄壳）：**
- `OpenFileLocation`（启 explorer 进程）
- `CopyToFolder`（系统 SaveFileDialog）
- 自定义窗口的渲染（XAML 视觉）

## 10. 风险与权衡

- **`SetAsWallpaperCommand` 改造影响现有调用方：** 详情页 `WallpaperDetailView.xaml` 用 `SetAsWallpaperCommand` 无参绑定。改造为"参数优先、回退 ActiveWallpaper"后，详情页行为不变。需回归验证。
- **删除正在播放的壁纸：** 第 3 步先 `RemoveWallpaperAsync` 释放句柄，再删文件。若 `RemoveWallpaperAsync` 内部异步释放不完全（极少见），`File.Delete` 可能失败——`LibraryService.DeleteAsync` 已有 try/catch 记录警告，不会崩。
- **`WallpaperItem` 无 INPC：** 重命名靠 `WallpapersView.Refresh()` 刷新，轻量但会触发整个视图重算。当前库规模下无性能问题。
- **ContextMenu 不在可视树：** 必须用 `PlacementTarget` 桥接，已在 4.2 说明。这是 WPF 已知模式，稳定。
