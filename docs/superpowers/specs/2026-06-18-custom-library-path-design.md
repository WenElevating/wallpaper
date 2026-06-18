# 自定义壁纸存储路径设计

- **日期：** 2026-06-18
- **状态：** 待实现

## 1. 背景与目标

壁纸视频文件目前硬编码存储在 `LocalApplicationData/WallpaperApp/library`，海报缓存在 `.../posters`。用户希望可以**自定义存储路径**（比如放到空间更大的非系统盘）。

**目标：**
- 设置页可选择任意文件夹作为存储根目录
- 切换路径时**自动迁移现有文件**（视频 + 海报）
- 迁移安全：失败不丢数据

## 2. 关键决策（已与用户确认）

| 决策点 | 选择 |
|---|---|
| 现有文件处理 | 自动迁移 |
| 路径选择方式 | 文件夹选择对话框 |
| 复制失败处理 | 逐个迁移，失败跳过（失败的留原路径继续可用） |
| 复制 vs 移动 | 先全部复制成功，才删原文件（中途崩溃不丢数据） |
| 海报缓存 | 一并迁移到新路径下的 posters 子目录 |

## 3. 目录结构

存储根目录 `LibraryRoot`（空 = 默认 `LocalAppData/WallpaperApp`）：
```
<LibraryRoot>/
  library/       <- 视频文件 (<hash>.<ext>)
  posters/       <- 海报缓存 (<hash>.jpg)
```

## 4. 迁移流程

```
ChangeLibraryRootCommand(newRoot):
  1. 校验 newRoot：非空、与当前不同、可写（试写临时文件验证）
  2. PauseAllAsync()  ← 释放文件句柄，避免复制失败
  3. 创建 newRoot/library 和 newRoot/posters
  4. 对库中每个 WallpaperItem（逐个，失败跳过）：
       a. 复制 ManagedFilePath(视频) → newRoot/library/<hash>.<ext>
       b. 若 ThumbnailPath 存在，复制 → newRoot/posters/<hash>.jpg
       c. 任一复制失败 → 标记该项失败，继续下一个
  5. 全部尝试完后，对【成功复制】的项：
       a. 更新 DB 的 ManagedFilePath / ThumbnailPath 指向新路径
       b. 删除原文件
     【失败】的项：DB 和文件保持原路径不动
  6. 切换 LibraryService._libraryDir 和 PosterCache.CacheRoot 到新路径
  7. 持久化 settings.LibraryRoot
  8. ResumeAllAsync()（若之前在播放）
  9. toast: "已迁移 N 个，M 个失败"（M>0 时）
```

**安全性保证：** "先复制成功才删原"确保迁移中途崩溃/断电时原文件不丢。重启后 DB 仍指向旧路径（事务未提交），用户可重试迁移。

## 5. 数据结构

**`AppSettings`** 新增：
```csharp
/// <summary>Library storage root. Empty = default (LocalAppData/WallpaperApp).
/// Videos go in <root>/library, posters in <root>/posters.</summary>
public string LibraryRoot { get; init; } = "";
```

## 6. 改造点

### `LibraryService`
- `_libraryDir` 改为可赋值（非 readonly）
- 新增 `UseRoot(string root)`：`_libraryDir = ResolveLibraryDir(root)`（root 空→默认）
- 新增 `MigrateToAsync(newRoot, ct)`：执行复制+更新DB+删原（返回 `(success, failed)` 计数）
- 新增静态 `ResolveLibraryDir(root)`：`root` 空→默认 `LocalAppData/WallpaperApp/library`，否则 `<root>/library`
- App 启动时（settings 加载后）调 `UseRoot(settings.LibraryRoot)` 设初始路径

### `PosterCache`
- 当前 `static readonly CacheDir` 硬编码 → 改为 `static string CacheDir { get; set; }`
- 加 `SetCacheRoot(string root)`：`CacheDir = root 空 ? 默认 : Path.Combine(root, "posters")`
- App 启动时设置

### `App.xaml.cs`
- 启动流程 line 86 加载 settings 后、line 94 LoadAsync 前：
  - `libraryService.UseRoot(appSettings.LibraryRoot)`
  - `PosterCache.SetCacheRoot(appSettings.LibraryRoot)`
- DI 注册不变（LibraryService 仍是无参 singleton，路径是运行时状态）

### `MainViewModel`
- 新增 `LibraryRoot`（只读展示当前路径）、`ChangeLibraryRootCommand`
- 编排迁移（PauseAll → LibraryService.MigrateToAsync → 持久化 → toast）

### `SettingsView.xaml`
- 新增"存储位置"区块：路径文本 + "更改…"按钮

## 7. 本地化键

| Key | en | zh-CN |
|---|---|---|
| `SettingsStorageLocation` | Storage location | 存储位置 |
| `SettingsStorageChange` | Change… | 更改… |
| `DlgPickLibraryFolder` | Choose wallpaper storage folder | 选择壁纸存储文件夹 |
| `MsgLibraryMigrated` | Migrated {0} wallpaper(s){1} | 已迁移 {0} 个壁纸{1} |
| `MsgLibraryMigrateFailed` | Migration failed: {0} | 迁移失败：{0} |
| `MsgLibraryMigratePartial` | ，{0} failed | ，{0} 个失败 |

## 8. 风险

- **播放中文件占用**：先 PauseAll；仍失败的走跳过路径
- **路径不可写**：迁移前试写临时文件验证，失败直接 toast 报错、不切换
- **迁移中途崩溃**："先复制成功才删原"保证原文件不丢
- **LibraryService 单例 factory 改造**：当前是无参 singleton，改成读 settings 的 factory；需确认 settings 在 DI 构造 LibraryService 时已加载
