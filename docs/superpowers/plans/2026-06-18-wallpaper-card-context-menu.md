# Wallpaper Card Context Menu Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a right-click context menu to wallpaper library cards with 7 actions (set as wallpaper, open details, open file location, rename, add to playlist, copy to, delete), all styled to match the existing Catppuccin Mocha dark theme.

**Architecture:** A `WallpaperCommands` aggregate object holds 7 `ICommand`s, assembled in `MainViewModel` and bound down to each `WallpaperCard` via a new `Commands` DependencyProperty. The `ContextMenu` (pure XAML) bridges the out-of-visual-tree problem with `PlacementTarget`. Three small modal windows (`ConfirmDeleteWindow`, `RenameWindow`, `PlaylistPickerWindow`) follow the existing `MonitorPickerWindow` pattern. Backend gains `LibraryService.RenameAsync`.

**Tech Stack:** WPF (.NET), C#, EF Core + SQLite, xUnit. Localization via RESX + `Strings.cs` code accessor.

**Spec:** `docs/superpowers/specs/2026-06-18-wallpaper-card-context-menu-design.md`

---

## Prerequisite: Fix broken test project

The test project currently does NOT compile (pre-existing F5 regression): `MainViewModelPlaylistTests.cs:263` calls the 7-arg `MainViewModel` constructor but the signature now requires an 8th `RandomWallpaperSwitcher shuffler`. Must fix before adding any new tests.

---

## File Structure

**Create:**
- `src/WallpaperApp/UI/Controls/WallpaperCommands.cs` — aggregate of 7 ICommands, bound to each card
- `src/WallpaperApp/UI/Views/ConfirmDeleteWindow.xaml` + `.cs` — modal delete confirmation
- `src/WallpaperApp/UI/Views/RenameWindow.xaml` + `.cs` — modal rename input
- `src/WallpaperApp/UI/Views/PlaylistPickerWindow.xaml` + `.cs` — modal playlist picker
- `tests/WallpaperApp.Tests/UI/MainViewModelWallpaperCommandsTests.cs` — VM command tests

**Modify:**
- `src/WallpaperApp/Services/Library/LibraryService.cs` — +`RenameAsync`
- `src/WallpaperApp/UI/ViewModels/MainViewModel.cs` — +6 commands, `Commands` property, handlers, refactor `SetAsWallpaperCommand` to accept param
- `src/WallpaperApp/UI/Controls/WallpaperCard.xaml` — +`ContextMenu`
- `src/WallpaperApp/UI/Controls/WallpaperCard.xaml.cs` — +`Commands` DP
- `src/WallpaperApp/UI/Views/LibraryView.xaml` — bind `Commands`
- `src/WallpaperApp/App.xaml` — +`DangerButtonStyle`, +`ContextMenu`/`MenuItem` styles
- `src/WallpaperApp/Localization/Strings.cs` — +~24 properties
- `src/WallpaperApp/Resources/Strings.resx` — +en strings
- `src/WallpaperApp/Resources/Strings.zh-CN.resx` — +zh-CN strings
- `tests/WallpaperApp.Tests/Services/LibraryServiceTests.cs` — +`RenameAsync` tests
- `tests/WallpaperApp.Tests/UI/MainViewModelPlaylistTests.cs` — fix `shuffler` arg

---

### Task 0: Fix pre-existing broken test build

**Files:**
- Modify: `tests/WallpaperApp.Tests/UI/MainViewModelPlaylistTests.cs:253-264`

- [ ] **Step 1: Inspect the broken factory method**

The `CreateViewModel` helper at line 253 builds a `MainViewModel` but omits the `RandomWallpaperSwitcher shuffler` arg required by the current constructor signature `MainViewModel(LibraryService, PlaybackManager, MonitorManager, SettingsService, FileLogger, GlobalHotkeyService, PlaylistService, RandomWallpaperSwitcher)`.

- [ ] **Step 2: Add the shuffler to the factory**

In `MainViewModelPlaylistTests.cs`, edit the `CreateViewModel` method. Add `RandomWallpaperSwitcher` to the using block at top of file if missing, then construct it and pass it:

```csharp
private MainViewModel CreateViewModel(PlaylistService playlistService)
{
    var library = new LibraryService(_logger, _provider, Path.Combine(_tempDir, "library"));
    var desktopHost = new DesktopHost(_logger);
    var playback = new PlaybackManager(_logger, desktopHost);
    var monitors = new MonitorManager(_logger);
    var settingsPath = Path.Combine(_tempDir, "settings.json");
    var settings = new SettingsService(settingsPath);
    var hotkeys = new GlobalHotkeyService(_logger);
    var shuffler = new RandomWallpaperSwitcher(_logger);

    return new MainViewModel(library, playback, monitors, settings, _logger, hotkeys, playlistService, shuffler);
}
```

Add to the `using` block at the top of the file (if not present):
```csharp
using WallpaperApp.Services.Playback;
```
(`RandomWallpaperSwitcher` is in namespace `WallpaperApp.Services.Playback`.)

- [ ] **Step 3: Build the test project to confirm it compiles**

Run: `dotnet build "D:\AiProject\wallpaper\tests\WallpaperApp.Tests\WallpaperApp.Tests.csproj"`
Expected: Build succeeds, no CS7036 error.

- [ ] **Step 4: Run the playlist tests to confirm green baseline**

Run: `dotnet test "D:\AiProject\wallpaper\tests\WallpaperApp.Tests\WallpaperApp.Tests.csproj" --filter "FullyQualifiedName~MainViewModelPlaylistTests"`
Expected: all tests PASS.

- [ ] **Step 5: Commit**

```bash
git add tests/WallpaperApp.Tests/UI/MainViewModelPlaylistTests.cs
git commit -m "test: fix MainViewModelPlaylistTests shuffler constructor arg"
```

---

### Task 1: Add `RenameAsync` to LibraryService (TDD)

**Files:**
- Modify: `src/WallpaperApp/Services/Library/LibraryService.cs`
- Test: `tests/WallpaperApp.Tests/Services/LibraryServiceTests.cs`

- [ ] **Step 1: Write failing tests for RenameAsync**

In `tests/WallpaperApp.Tests/Services/LibraryServiceTests.cs`, add these three tests (append inside the `LibraryServiceTests` class, before the closing brace). First add a helper to seed a wallpaper:

```csharp
private async Task<WallpaperItem> SeedWallpaperAsync(LibraryService service, string name)
{
    var tempFile = Path.Combine(_testLibDir, name + ".mp4");
    await File.WriteAllBytesAsync(tempFile, new byte[] { 0x00 });
    return (await service.ImportAsync(tempFile))!;
}
```

Then add the tests:

```csharp
[Fact]
public async Task RenameAsync_UpdatesDisplayName()
{
    var service = CreateService();
    var item = await SeedWallpaperAsync(service, "clip");

    var result = await service.RenameAsync(item.Id, "renamed clip");

    Assert.True(result);
    var refreshed = await service.GetByIdAsync(item.Id);
    Assert.Equal("renamed clip", refreshed!.DisplayName);
}

[Fact]
public async Task RenameAsync_EmptyName_ReturnsFalseAndKeepsOriginal()
{
    var service = CreateService();
    var item = await SeedWallpaperAsync(service, "clip");
    var original = item.DisplayName;

    var result = await service.RenameAsync(item.Id, "   ");

    Assert.False(result);
    var refreshed = await service.GetByIdAsync(item.Id);
    Assert.Equal(original, refreshed!.DisplayName);
}

[Fact]
public async Task RenameAsync_NonexistentId_ReturnsFalse()
{
    var service = CreateService();
    var result = await service.RenameAsync(Guid.NewGuid(), "anything");
    Assert.False(result);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test "D:\AiProject\wallpaper\tests\WallpaperApp.Tests\WallpaperApp.Tests.csproj" --filter "FullyQualifiedName~RenameAsync"`
Expected: FAIL with compile error `'LibraryService' does not contain a definition for 'RenameAsync'`.

- [ ] **Step 3: Implement RenameAsync**

In `src/WallpaperApp/Services/Library/LibraryService.cs`, add this method after `UpdateMetadataAsync` (before `DeleteAsync`):

```csharp
public async Task<bool> RenameAsync(Guid id, string newName, CancellationToken ct = default)
{
    var trimmed = newName?.Trim();
    if (string.IsNullOrEmpty(trimmed)) return false;

    await using var db = CreateDbContext();
    var item = await db.WallpaperItems.FindAsync(new object[] { id }, ct);
    if (item == null) return false;

    item.DisplayName = trimmed;
    await db.SaveChangesAsync(ct);
    _logger.Info($"Renamed wallpaper {id} -> '{trimmed}'");
    return true;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test "D:\AiProject\wallpaper\tests\WallpaperApp.Tests\WallpaperApp.Tests.csproj" --filter "FullyQualifiedName~RenameAsync"`
Expected: 3 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WallpaperApp/Services/Library/LibraryService.cs tests/WallpaperApp.Tests/Services/LibraryServiceTests.cs
git commit -m "feat(library): add RenameAsync to LibraryService"
```

---

### Task 2: Add localization keys

All UI-visible strings go through the RESX + `Strings.cs` accessor. Do this before XAML so the `{loc:Loc Key}` bindings resolve.

**Files:**
- Modify: `src/WallpaperApp/Resources/Strings.resx` (en, the neutral/fallback)
- Modify: `src/WallpaperApp/Resources/Strings.zh-CN.resx` (zh-CN satellite)
- Modify: `src/WallpaperApp/Localization/Strings.cs`

- [ ] **Step 1: Add en strings to `Resources/Strings.resx`**

In `src/WallpaperApp/Resources/Strings.resx`, insert these `<data>` elements before the closing `</root>` tag (after the `MsgShuffleNoLibrary` element). Each follows the existing format:

```xml
  <data name="MenuSetAsWallpaper" xml:space="preserve">
    <value>Set as wallpaper</value>
  </data>
  <data name="MenuOpenDetail" xml:space="preserve">
    <value>Open details</value>
  </data>
  <data name="MenuOpenFileLocation" xml:space="preserve">
    <value>Open file location</value>
  </data>
  <data name="MenuRename" xml:space="preserve">
    <value>Rename…</value>
  </data>
  <data name="MenuAddToPlaylist" xml:space="preserve">
    <value>Add to playlist…</value>
  </data>
  <data name="MenuCopyToFolder" xml:space="preserve">
    <value>Copy to…</value>
  </data>
  <data name="MenuDelete" xml:space="preserve">
    <value>Delete</value>
  </data>
  <data name="DlgCancel" xml:space="preserve">
    <value>Cancel</value>
  </data>
  <data name="DlgDeleteTitle" xml:space="preserve">
    <value>Delete wallpaper</value>
  </data>
  <data name="DlgDeletePrompt" xml:space="preserve">
    <value>Are you sure you want to delete "{0}"? The source file will be permanently removed.</value>
  </data>
  <data name="DlgDeletePlaying" xml:space="preserve">
    <value>Currently playing on a monitor</value>
  </data>
  <data name="DlgDeletePlaylistRefs" xml:space="preserve">
    <value>Referenced by {0} playlist(s)</value>
  </data>
  <data name="DlgDeleteConfirm" xml:space="preserve">
    <value>Delete</value>
  </data>
  <data name="DlgRenameTitle" xml:space="preserve">
    <value>Rename wallpaper</value>
  </data>
  <data name="DlgPickPlaylistTitle" xml:space="preserve">
    <value>Add to playlist</value>
  </data>
  <data name="DlgPlaylistEmpty" xml:space="preserve">
    <value>No playlists yet. Create one in the Playlists tab first.</value>
  </data>
  <data name="DlgCopyTitle" xml:space="preserve">
    <value>Copy wallpaper to…</value>
  </data>
  <data name="MsgDeleted" xml:space="preserve">
    <value>Deleted "{0}"</value>
  </data>
  <data name="MsgRenamed" xml:space="preserve">
    <value>Renamed</value>
  </data>
  <data name="MsgAddedToPlaylist" xml:space="preserve">
    <value>Added to "{0}"</value>
  </data>
  <data name="MsgCopySuccess" xml:space="preserve">
    <value>Copied</value>
  </data>
  <data name="MsgCopyFailed" xml:space="preserve">
    <value>Copy failed</value>
  </data>
```

- [ ] **Step 2: Add zh-CN strings to `Resources/Strings.zh-CN.resx`**

Same 23 keys, Chinese values. Insert before `</root>`:

```xml
  <data name="MenuSetAsWallpaper" xml:space="preserve">
    <value>设为壁纸</value>
  </data>
  <data name="MenuOpenDetail" xml:space="preserve">
    <value>打开详情</value>
  </data>
  <data name="MenuOpenFileLocation" xml:space="preserve">
    <value>在文件夹中打开</value>
  </data>
  <data name="MenuRename" xml:space="preserve">
    <value>重命名…</value>
  </data>
  <data name="MenuAddToPlaylist" xml:space="preserve">
    <value>加入播放列表…</value>
  </data>
  <data name="MenuCopyToFolder" xml:space="preserve">
    <value>复制到…</value>
  </data>
  <data name="MenuDelete" xml:space="preserve">
    <value>删除</value>
  </data>
  <data name="DlgCancel" xml:space="preserve">
    <value>取消</value>
  </data>
  <data name="DlgDeleteTitle" xml:space="preserve">
    <value>删除壁纸</value>
  </data>
  <data name="DlgDeletePrompt" xml:space="preserve">
    <value>确定要删除"{0}"吗？此操作将永久删除源文件，无法恢复。</value>
  </data>
  <data name="DlgDeletePlaying" xml:space="preserve">
    <value>正在某显示器上播放</value>
  </data>
  <data name="DlgDeletePlaylistRefs" xml:space="preserve">
    <value>被 {0} 个播放列表引用</value>
  </data>
  <data name="DlgDeleteConfirm" xml:space="preserve">
    <value>删除</value>
  </data>
  <data name="DlgRenameTitle" xml:space="preserve">
    <value>重命名壁纸</value>
  </data>
  <data name="DlgPickPlaylistTitle" xml:space="preserve">
    <value>加入播放列表</value>
  </data>
  <data name="DlgPlaylistEmpty" xml:space="preserve">
    <value>没有播放列表，请先在"播放列表"页创建。</value>
  </data>
  <data name="DlgCopyTitle" xml:space="preserve">
    <value>复制壁纸到…</value>
  </data>
  <data name="MsgDeleted" xml:space="preserve">
    <value>已删除"{0}"</value>
  </data>
  <data name="MsgRenamed" xml:space="preserve">
    <value>已重命名</value>
  </data>
  <data name="MsgAddedToPlaylist" xml:space="preserve">
    <value>已加入"{0}"</value>
  </data>
  <data name="MsgCopySuccess" xml:space="preserve">
    <value>已复制</value>
  </data>
  <data name="MsgCopyFailed" xml:space="preserve">
    <value>复制失败</value>
  </data>
```

- [ ] **Step 3: Add C# accessors to `Localization/Strings.cs`**

In `src/WallpaperApp/Localization/Strings.cs`, add these properties before the final closing brace of the `Strings` class (after the `MsgShuffleNoLibrary` property):

```csharp
    public static string MenuSetAsWallpaper => Get(nameof(MenuSetAsWallpaper));
    public static string MenuOpenDetail => Get(nameof(MenuOpenDetail));
    public static string MenuOpenFileLocation => Get(nameof(MenuOpenFileLocation));
    public static string MenuRename => Get(nameof(MenuRename));
    public static string MenuAddToPlaylist => Get(nameof(MenuAddToPlaylist));
    public static string MenuCopyToFolder => Get(nameof(MenuCopyToFolder));
    public static string MenuDelete => Get(nameof(MenuDelete));
    public static string DlgCancel => Get(nameof(DlgCancel));
    public static string DlgDeleteTitle => Get(nameof(DlgDeleteTitle));
    public static string DlgDeletePrompt => Get(nameof(DlgDeletePrompt));
    public static string DlgDeletePlaying => Get(nameof(DlgDeletePlaying));
    public static string DlgDeletePlaylistRefs => Get(nameof(DlgDeletePlaylistRefs));
    public static string DlgDeleteConfirm => Get(nameof(DlgDeleteConfirm));
    public static string DlgRenameTitle => Get(nameof(DlgRenameTitle));
    public static string DlgPickPlaylistTitle => Get(nameof(DlgPickPlaylistTitle));
    public static string DlgPlaylistEmpty => Get(nameof(DlgPlaylistEmpty));
    public static string DlgCopyTitle => Get(nameof(DlgCopyTitle));
    public static string MsgDeleted => Get(nameof(MsgDeleted));
    public static string MsgRenamed => Get(nameof(MsgRenamed));
    public static string MsgAddedToPlaylist => Get(nameof(MsgAddedToPlaylist));
    public static string MsgCopySuccess => Get(nameof(MsgCopySuccess));
    public static string MsgCopyFailed => Get(nameof(MsgCopyFailed));
```

- [ ] **Step 4: Build to confirm RESX + accessor compile**

Run: `dotnet build "D:\AiProject\wallpaper\src\WallpaperApp\WallpaperApp.csproj"`
Expected: Build succeeds.

- [ ] **Step 5: Commit**

```bash
git add src/WallpaperApp/Resources/Strings.resx src/WallpaperApp/Resources/Strings.zh-CN.resx src/WallpaperApp/Localization/Strings.cs
git commit -m "feat(i18n): add context menu + dialog localization keys"
```

---

### Task 3: Add `DangerButtonStyle` + ContextMenu/MenuItem styles to App.xaml

The app currently has no ContextMenu style (default would render white, clashing with the dark theme). The delete-confirm button needs a red variant of `PrimaryButtonStyle`.

**Files:**
- Modify: `src/WallpaperApp/App.xaml`

- [ ] **Step 1: Add DangerButtonStyle + ContextMenu/MenuItem styles**

In `src/WallpaperApp/App.xaml`, insert these three style blocks inside `<Application.Resources>`, right after the `PrimaryButtonStyle` block (after its closing `</Style>` at the line before `<!-- ===== Dark ComboBox`):

```xml
        <!-- Red "danger" button variant of PrimaryButtonStyle (used by delete confirm). -->
        <Style x:Key="DangerButtonStyle" TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
            <Setter Property="Foreground" Value="#0B0B14"/>
            <Setter Property="FontWeight" Value="SemiBold"/>
            <Setter Property="Padding" Value="20,10"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="bd" CornerRadius="8" Padding="{TemplateBinding Padding}">
                            <Border.Background>
                                <LinearGradientBrush StartPoint="0,0" EndPoint="1,1">
                                    <GradientStop Color="#F38BA8" Offset="0"/>
                                    <GradientStop Color="#EBA0AC" Offset="1"/>
                                </LinearGradientBrush>
                            </Border.Background>
                            <Border.Effect>
                                <DropShadowEffect Color="#F38BA8" Opacity="0.35" BlurRadius="14" ShadowDepth="0"/>
                            </Border.Effect>
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Trigger.Setters>
                                    <Setter TargetName="bd" Property="Effect">
                                        <Setter.Value>
                                            <DropShadowEffect Color="#F38BA8" Opacity="0.7" BlurRadius="24" ShadowDepth="0"/>
                                        </Setter.Value>
                                    </Setter>
                                </Trigger.Setters>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter Property="Opacity" Value="0.4"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <!-- ===== Dark ContextMenu (card right-click menu) ===== -->
        <Style TargetType="ContextMenu">
            <Setter Property="Background" Value="{StaticResource SurfaceBrush}"/>
            <Setter Property="BorderBrush" Value="{StaticResource BorderBrush}"/>
            <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="Padding" Value="4"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ContextMenu">
                        <Border Background="{TemplateBinding Background}"
                                BorderBrush="{TemplateBinding BorderBrush}"
                                BorderThickness="{TemplateBinding BorderThickness}"
                                CornerRadius="8" Padding="{TemplateBinding Padding}">
                            <Border.Effect>
                                <DropShadowEffect Color="#000000" Opacity="0.45" BlurRadius="18" ShadowDepth="3" Direction="270"/>
                            </Border.Effect>
                            <StackPanel>
                                <ContentPresenter/>
                            </StackPanel>
                        </Border>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style TargetType="MenuItem">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
            <Setter Property="Padding" Value="14,7"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="MenuItem">
                        <Border x:Name="bd" Background="{TemplateBinding Background}"
                                CornerRadius="4" Padding="{TemplateBinding Padding}">
                            <ContentPresenter ContentSource="Header" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsHighlighted" Value="True">
                                <Setter TargetName="bd" Property="Background" Value="{StaticResource SurfaceHoverBrush}"/>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter Property="Opacity" Value="0.45"/>
                                <Setter Property="Foreground" Value="{StaticResource MutedTextBrush}"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
```

- [ ] **Step 2: Build to confirm App.xaml parses**

Run: `dotnet build "D:\AiProject\wallpaper\src\WallpaperApp\WallpaperApp.csproj"`
Expected: Build succeeds (XAML compiles).

- [ ] **Step 3: Commit**

```bash
git add src/WallpaperApp/App.xaml
git commit -m "style: add DangerButtonStyle + dark ContextMenu/MenuItem styles"
```

---

### Task 4: Create `WallpaperCommands` aggregate

**Files:**
- Create: `src/WallpaperApp/UI/Controls/WallpaperCommands.cs`

- [ ] **Step 1: Create the aggregate type**

Create `src/WallpaperApp/UI/Controls/WallpaperCommands.cs`:

```csharp
using System.Windows.Input;

namespace WallpaperApp.UI.Controls;

// Aggregate of the 7 commands surfaced on each wallpaper card's context menu.
// Assembled once in MainViewModel and bound down to every WallpaperCard via its
// Commands dependency property, so we avoid adding 7 separate DPs to the card.
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

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build "D:\AiProject\wallpaper\src\WallpaperApp\WallpaperApp.csproj"`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/WallpaperApp/UI/Controls/WallpaperCommands.cs
git commit -m "feat(ui): add WallpaperCommands aggregate for card context menu"
```

---

### Task 5: Add `Commands` DependencyProperty to WallpaperCard

**Files:**
- Modify: `src/WallpaperApp/UI/Controls/WallpaperCard.xaml.cs`

- [ ] **Step 1: Add the Commands DP**

In `src/WallpaperApp/UI/Controls/WallpaperCard.xaml.cs`, add this DP right after the existing `OpenCommandProperty` / `OpenCommand` block (after line 25, the `set => SetValue(...)` for `OpenCommand`):

```csharp
    // Aggregate of context-menu commands, bound from MainViewModel. Each MenuItem
    // in the ContextMenu binds PlacementTarget.Commands.Xxx to reach these.
    public static readonly DependencyProperty CommandsProperty = DependencyProperty.Register(
        nameof(Commands), typeof(WallpaperCommands), typeof(WallpaperCard), new PropertyMetadata(null));

    public WallpaperCommands? Commands
    {
        get => (WallpaperCommands?)GetValue(CommandsProperty);
        set => SetValue(CommandsProperty, value);
    }
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build "D:\AiProject\wallpaper\src\WallpaperApp\WallpaperApp.csproj"`
Expected: Build succeeds.

- [ ] **Step 3: Commit**

```bash
git add src/WallpaperApp/UI/Controls/WallpaperCard.xaml.cs
git commit -m "feat(ui): add Commands DependencyProperty to WallpaperCard"
```

---

### Task 6: Build the ViewModel command logic (TDD)

The commands live in `MainViewModel`. Test the business-logic-heavy ones (delete orchestration, rename memory-sync, add-to-playlist, set-as-wallpaper param fallback) in a new test file. System-dialog/shell commands (open file location, copy to) are thin shells and tested via build + manual.

**Files:**
- Modify: `src/WallpaperApp/UI/ViewModels/MainViewModel.cs`
- Create: `tests/WallpaperApp.Tests/UI/MainViewModelWallpaperCommandsTests.cs`

- [ ] **Step 1: Write failing tests first**

Create `tests/WallpaperApp.Tests/UI/MainViewModelWallpaperCommandsTests.cs`. This mirrors the setup in `MainViewModelPlaylistTests` (same DI/fixtures). Note: the dialogs (`ConfirmDeleteWindow`, `RenameWindow`, `PlaylistPickerWindow`) open real `Window`s, which can't run headless in tests — so the tests call the **private async handler methods** indirectly via a small test seam. Add a `internal` test hook on the VM for the pure-logic delete path.

```csharp
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WallpaperApp.Data;
using WallpaperApp.Models;
using WallpaperApp.Services.Desktop;
using WallpaperApp.Services.Input;
using WallpaperApp.Services.Library;
using WallpaperApp.Services.Logging;
using WallpaperApp.Services.Monitor;
using WallpaperApp.Services.Playback;
using WallpaperApp.Services.Playlists;
using WallpaperApp.Services.Settings;
using WallpaperApp.UI.ViewModels;

namespace WallpaperApp.Tests.UI;

public sealed class MainViewModelWallpaperCommandsTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ServiceProvider _provider;
    private readonly FileLogger _logger;
    private readonly string _tempDir;

    public MainViewModelWallpaperCommandsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WallpaperCmdTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddSingleton(_db);
        _provider = services.BuildServiceProvider();
        _logger = new FileLogger(_tempDir);
    }

    [Fact]
    public void Commands_ArePopulatedAfterConstruction()
    {
        var vm = CreateViewModel();
        Assert.NotNull(vm.Commands);
        Assert.NotNull(vm.Commands.SetAsWallpaper);
        Assert.NotNull(vm.Commands.OpenDetail);
        Assert.NotNull(vm.Commands.OpenFileLocation);
        Assert.NotNull(vm.Commands.Rename);
        Assert.NotNull(vm.Commands.AddToPlaylist);
        Assert.NotNull(vm.Commands.CopyToFolder);
        Assert.NotNull(vm.Commands.Delete);
    }

    [Fact]
    public async Task RenameWallpaperAsync_UpdatesMemoryObjectAndPersists()
    {
        var library = new LibraryService(_logger, _provider, Path.Combine(_tempDir, "lib"));
        var vm = CreateViewModel(library);
        var item = await SeedWallpaperAsync(library);
        vm.Wallpapers.Add(item);

        await vm.ApplyRenameAsync(item, "New Name");

        Assert.Equal("New Name", item.DisplayName);
        var persisted = await library.GetByIdAsync(item.Id);
        Assert.Equal("New Name", persisted!.DisplayName);
    }

    [Fact]
    public async Task DeleteWallpaperAsync_StopsPlayback_RemovesFromListAndPlaylists()
    {
        var library = new LibraryService(_logger, _provider, Path.Combine(_tempDir, "lib"));
        var playlistService = new PlaylistService(_logger, _db);
        var vm = CreateViewModel(library, playlistService);
        var item = await SeedWallpaperAsync(library);
        vm.Wallpapers.Add(item);

        // Put it in a playlist so we can verify cascade cleanup.
        var playlistId = await playlistService.CreateAsync("P1");
        await playlistService.AddMemberAsync(playlistId, item.Id);
        await vm.LoadPlaylistsAsync();

        // Pretend it's playing on a monitor so we verify playback release.
        var monitorId = Guid.NewGuid();
        var monitor = new MonitorInfo { DeviceName = "D1", MonitorKey = monitorId.ToString(), Width = 1920, Height = 1080 };
        vm.Monitors.Add(monitor);
        var playback = (TestablePlaybackManager)vm.PlaybackForTests;
        playback.SetActive(monitorId, item.Id);

        await vm.DeleteWallpaperAsync(item);

        Assert.Empty(vm.Wallpapers);
        Assert.True(playback.WasRemoved(monitorId));
        var persisted = await library.GetByIdAsync(item.Id);
        Assert.Null(persisted);
        var pl = await playlistService.GetByIdAsync(playlistId);
        Assert.Empty(pl!.Members);
    }

    [Fact]
    public async Task SetAsWallpaperCommand_WithParameter_UsesParameterNotActiveWallpaper()
    {
        var library = new LibraryService(_logger, _provider, Path.Combine(_tempDir, "lib"));
        var vm = CreateViewModel(library);
        var a = await SeedWallpaperAsync(library, "A");
        var b = await SeedWallpaperAsync(library, "B");
        vm.Wallpapers.Add(a);
        vm.Wallpapers.Add(b);
        vm.ActiveWallpaper = a;

        var monitorId = Guid.NewGuid();
        vm.Monitors.Add(new MonitorInfo { DeviceName = "D1", MonitorKey = monitorId.ToString(), Width = 1920, Height = 1080 });
        var playback = (TestablePlaybackManager)vm.PlaybackForTests;

        bool ok = await vm.AssignWallpaperFromCommandAsync(b);

        Assert.True(ok);
        Assert.Equal(b.Id, playback.AssignedId(monitorId));
    }

    private async Task<WallpaperItem> SeedWallpaperAsync(LibraryService library, string name = "clip")
    {
        var path = Path.Combine(_tempDir, name + ".mp4");
        await File.WriteAllBytesAsync(path, new byte[] { 0x00 });
        return (await library.ImportAsync(path))!;
    }

    private MainViewModel CreateViewModel(LibraryService? library = null, PlaylistService? playlistService = null)
    {
        library ??= new LibraryService(_logger, _provider, Path.Combine(_tempDir, "lib"));
        playlistService ??= new PlaylistService(_logger, _db);
        var desktopHost = new DesktopHost(_logger);
        var playback = new TestablePlaybackManager(_logger, desktopHost);
        var monitors = new MonitorManager(_logger);
        var settings = new SettingsService(Path.Combine(_tempDir, "settings.json"));
        var hotkeys = new GlobalHotkeyService(_logger);
        var shuffler = new RandomWallpaperSwitcher(_logger);
        return new MainViewModel(library, playback, monitors, settings, _logger, hotkeys, playlistService, shuffler);
    }

    public void Dispose()
    {
        _provider.Dispose();
        _db.Dispose();
        _connection.Dispose();
        _logger.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}

// Minimal PlaybackManager subclass that records calls instead of rendering, so
// tests can assert stop/assign without a real D2D surface. Exposes the hooks the
// VM uses (RemoveWallpaperAsync / SetWallpaperAsync / GetActiveWallpaperId) by
// overriding the virtual members added in Task 6 Step 2.
internal sealed class TestablePlaybackManager : PlaybackManager
{
    private readonly Dictionary<Guid, Guid> _active = new();
    private readonly HashSet<Guid> _removed = new();
    private readonly Dictionary<Guid, Guid> _assigned = new();

    public TestablePlaybackManager(FileLogger logger, DesktopHost host) : base(logger, host) { }

    public void SetActive(Guid monitorId, Guid wallpaperId) => _active[monitorId] = wallpaperId;
    public bool WasRemoved(Guid monitorId) => _removed.Contains(monitorId);
    public Guid? AssignedId(Guid monitorId) => _assigned.TryGetValue(monitorId, out var id) ? id : null;

    public override Guid? GetActiveWallpaperId(Guid monitorId)
        => _active.TryGetValue(monitorId, out var id) ? id : null;

    public override Task RemoveWallpaperAsync(Guid monitorId, CancellationToken ct = default)
    {
        _active.Remove(monitorId);
        _removed.Add(monitorId);
        return Task.CompletedTask;
    }

    public override Task<bool> SetWallpaperAsync(Guid monitorId, Guid wallpaperId, string filePath,
        int x, int y, int w, int h, CancellationToken ct = default)
    {
        _active[monitorId] = wallpaperId;
        _assigned[monitorId] = wallpaperId;
        return Task.FromResult(true);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test "D:\AiProject\wallpaper\tests\WallpaperApp.Tests\WallpaperApp.Tests.csproj" --filter "FullyQualifiedName~MainViewModelWallpaperCommandsTests"`
Expected: FAIL (compile errors: `Commands`, `RenameWallpaperAsync`, `DeleteWallpaperAsync`, `AssignWallpaperFromCommandAsync`, `PlaybackForTests` don't exist; `GetActiveWallpaperId`/`RemoveWallpaperAsync`/`SetWallpaperAsync` not virtual).

- [ ] **Step 3: Make PlaybackManager members virtual for testability**

In `src/WallpaperApp/Services/Playback/PlaybackManager.cs`, mark three members `virtual`:
- `public Guid? GetActiveWallpaperId(Guid monitorId)` → `public virtual Guid? GetActiveWallpaperId(Guid monitorId)`
- `public async Task RemoveWallpaperAsync(...)` → `public virtual async Task RemoveWallpaperAsync(...)` (keep the body)
- `public async Task<bool> SetWallpaperAsync(...)` → `public virtual async Task<bool> SetWallpaperAsync(...)` (keep the body)

(These are already `public`; just add `virtual` so `TestablePlaybackManager` can override.)

- [ ] **Step 4: Add the command logic + test hooks to MainViewModel**

In `src/WallpaperApp/UI/ViewModels/MainViewModel.cs`:

(a) Add `using System.Diagnostics;` at the top (for `Process.Start` in open-file-location).

(b) Add a `Commands` property and the aggregate assembly. After the existing command-field declarations (after `public ICommand AssignPlaylistMonitorCommand { get; }` around line 65), add:

```csharp
    public WallpaperCommands Commands { get; }
```

(c) Refactor `SetAsWallpaperCommand`. Currently (line 270):
```csharp
    SetAsWallpaperCommand = new RelayCommand(_ => _ = SetAsWallpaperAsync());
```
Replace with (accept param, fall back to ActiveWallpaper):
```csharp
    SetAsWallpaperCommand = new RelayCommand(p => _ = SetAsWallpaperAsync(p as WallpaperItem));
```

(d) Refactor `SetAsWallpaperAsync` to accept a param. Currently (line 650-651):
```csharp
    private async Task SetAsWallpaperAsync()
    {
        var wallpaper = ActiveWallpaper;
        if (wallpaper == null) return;
```
Replace the signature + first lines with:
```csharp
    private async Task SetAsWallpaperAsync()
        => await SetAsWallpaperAsync(ActiveWallpaper);

    public async Task<bool> AssignWallpaperFromCommandAsync(WallpaperItem? wallpaper)
        => await SetAsWallpaperAsync(wallpaper);

    private async Task SetAsWallpaperAsync(WallpaperItem? wallpaper)
    {
        wallpaper ??= ActiveWallpaper;
        if (wallpaper == null) return;
```
(The body below that uses `wallpaper` unchanged — the variable now comes from the param.)

(e) At the end of the constructor (after `AssignPlaylistMonitorCommand = ...`), assemble the `Commands` aggregate:

```csharp
        Commands = new WallpaperCommands
        {
            SetAsWallpaper = SetAsWallpaperCommand,
            OpenDetail = OpenDetailCommand,
            OpenFileLocation = new RelayCommand(p => OpenFileLocation(p as WallpaperItem)),
            Rename = new RelayCommand(p => _ = RenameWallpaperAsync(p as WallpaperItem)),
            AddToPlaylist = new RelayCommand(p => _ = AddToPlaylistFromCardAsync(p as WallpaperItem), _ => Playlists.Count > 0),
            CopyToFolder = new RelayCommand(p => _ = CopyToFolderAsync(p as WallpaperItem)),
            Delete = new RelayCommand(p => _ = DeleteWallpaperAsync(p as WallpaperItem)),
        };
```

(f) Add the handler methods. Place these near the other private handlers (e.g., after `TogglePauseAsync`). Add the `using WallpaperApp.UI.Controls;` import at top (for `WallpaperCommands`):

```csharp
    // Test seam: exposes the playback manager so tests can substitute a fake.
    internal PlaybackManager PlaybackForTests => _playback;

    private void OpenFileLocation(WallpaperItem? wallpaper)
    {
        if (wallpaper == null || string.IsNullOrEmpty(wallpaper.ManagedFilePath)) return;
        try { Process.Start("explorer.exe", $"/select,\"{wallpaper.ManagedFilePath}\""); }
        catch (Exception ex) { _logger.Warn($"Open file location failed: {ex.Message}"); }
    }

    public async Task RenameWallpaperAsync(WallpaperItem? wallpaper)
    {
        if (wallpaper == null) return;
        var dialog = new RenameWindow(wallpaper.DisplayName) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        await ApplyRenameAsync(wallpaper, dialog.NewName);
    }

    // Core rename logic, separated so tests can exercise it headless (no Window).
    internal async Task ApplyRenameAsync(WallpaperItem wallpaper, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) { ShowToast(Strings.MsgNameEmpty, error: true); return; }

        if (await _library.RenameAsync(wallpaper.Id, newName))
        {
            wallpaper.DisplayName = newName.Trim();
            WallpapersView.Refresh();
            ShowToast(Strings.MsgRenamed);
        }
    }
```

(Add `using WallpaperApp.UI.Views;` at top if not present — `RenameWindow` lives there.)

(g) Add the delete orchestrator:

```csharp
    public async Task DeleteWallpaperAsync(WallpaperItem? wallpaper)
    {
        if (wallpaper == null) return;

        // 1. Tally impact.
        var playingMonitors = new List<Guid>();
        foreach (var m in Monitors)
        {
            if (Guid.TryParse(m.MonitorKey, out var mid) &&
                _playback.GetActiveWallpaperId(mid) == wallpaper.Id)
                playingMonitors.Add(mid);
        }
        var referencedPlaylists = Playlists
            .Where(p => p.Members.Any(mem => mem.WallpaperId == wallpaper.Id))
            .ToList();

        // 2. Confirm.
        var dialog = new ConfirmDeleteWindow(
            wallpaper.DisplayName,
            playingMonitors.Count > 0,
            referencedPlaylists.Count)
        { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;

        // 3. Stop playback (release file handle before delete).
        foreach (var mid in playingMonitors)
            await _playback.RemoveWallpaperAsync(mid);

        // 4. Cascade-clean playlist member references (idempotent).
        foreach (var pl in referencedPlaylists)
            await _playlistService.RemoveMemberAsync(pl.Id, wallpaper.Id);

        // 5. Delete DB record + source file + thumbnail.
        await _library.DeleteAsync(wallpaper.Id);

        // 6. Sync in-memory list.
        Wallpapers.Remove(wallpaper);

        // 7. Toast.
        ShowToast(string.Format(Strings.MsgDeleted, wallpaper.DisplayName));
    }
```

(h) Add the add-to-playlist-from-card handler:

```csharp
    private async Task AddToPlaylistFromCardAsync(WallpaperItem? wallpaper)
    {
        if (wallpaper == null) return;
        if (Playlists.Count == 0) { ShowToast(Strings.DlgPlaylistEmpty, error: true); return; }

        var dialog = new PlaylistPickerWindow(Playlists.ToList())
        { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.Selected == null) return;

        await _playlistService.AddMemberAsync(dialog.Selected.Id, wallpaper.Id);
        await ReloadPlaylistsKeepingSelectionAsync();
        ShowToast(string.Format(Strings.MsgAddedToPlaylist, dialog.Selected.Name));
    }
```

(i) Add the copy-to handler:

```csharp
    private async Task CopyToFolderAsync(WallpaperItem? wallpaper)
    {
        if (wallpaper == null || string.IsNullOrEmpty(wallpaper.ManagedFilePath)) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = string.IsNullOrEmpty(wallpaper.OriginalFileName) ? wallpaper.DisplayName : wallpaper.OriginalFileName,
            Filter = $"{Strings.DlgImportFilterAll}|*.*",
            Title = Strings.DlgCopyTitle
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            await Task.Run(() => File.Copy(wallpaper.ManagedFilePath, dialog.FileName, overwrite: true));
            ShowToast(Strings.MsgCopySuccess);
        }
        catch (Exception ex)
        {
            _logger.Warn($"Copy failed: {ex.Message}");
            ShowToast(Strings.MsgCopyFailed, error: true);
        }
    }
```

(Add `using System.IO;` at top if not present — for `File.Copy`.)

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test "D:\AiProject\wallpaper\tests\WallpaperApp.Tests\WallpaperApp.Tests.csproj" --filter "FullyQualifiedName~MainViewModelWallpaperCommandsTests"`
Expected: 4 tests PASS.

- [ ] **Step 6: Run the full test suite to confirm no regression**

Run: `dotnet test "D:\AiProject\wallpaper\tests\WallpaperApp.Tests\WallpaperApp.Tests.csproj"`
Expected: all PASS.

- [ ] **Step 7: Commit**

```bash
git add src/WallpaperApp/UI/ViewModels/MainViewModel.cs src/WallpaperApp/Services/Playback/PlaybackManager.cs tests/WallpaperApp.Tests/UI/MainViewModelWallpaperCommandsTests.cs
git commit -m "feat(vm): add wallpaper context menu command logic + tests"
```

---

### Task 7: Create the three modal windows

**Files:**
- Create: `src/WallpaperApp/UI/Views/ConfirmDeleteWindow.xaml` + `.cs`
- Create: `src/WallpaperApp/UI/Views/RenameWindow.xaml` + `.cs`
- Create: `src/WallpaperApp/UI/Views/PlaylistPickerWindow.xaml` + `.cs`

- [ ] **Step 1: Create ConfirmDeleteWindow.xaml**

Create `src/WallpaperApp/UI/Views/ConfirmDeleteWindow.xaml`:

```xml
<Window x:Class="WallpaperApp.UI.Views.ConfirmDeleteWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:loc="clr-namespace:WallpaperApp.Localization"
        Title="{loc:Loc DlgDeleteTitle}" Width="440" SizeToContent="Height"
        ResizeMode="NoResize" WindowStartupLocation="CenterOwner"
        Background="#1E1E2E" Foreground="{StaticResource TextBrush}"
        ShowInTaskbar="False">
    <StackPanel Margin="24">
        <TextBlock Text="{loc:Loc DlgDeleteTitle}" FontSize="20" FontWeight="Bold" Margin="0,0,0,14"/>

        <TextBlock x:Name="PromptText" TextWrapping="Wrap" Margin="0,0,0,14"/>

        <StackPanel x:Name="ImpactPanel" Margin="0,0,0,18"/>

        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
            <Button Content="{loc:Loc DlgCancel}" Click="Cancel_Click" Padding="20,10" Margin="0,0,10,0"/>
            <Button Style="{StaticResource DangerButtonStyle}" Content="{loc:Loc DlgDeleteConfirm}" Click="Delete_Click"/>
        </StackPanel>
    </StackPanel>
</Window>
```

- [ ] **Step 2: Create ConfirmDeleteWindow.xaml.cs**

Create `src/WallpaperApp/UI/Views/ConfirmDeleteWindow.xaml.cs`:

```csharp
using System.Windows;
using WallpaperApp.Localization;

namespace WallpaperApp.UI.Views;

// Modal delete confirmation. Shows the wallpaper name plus, when relevant,
// the live-impact rows (currently playing / referenced by N playlists) so the
// user knows what else gets torn down by the delete.
public partial class ConfirmDeleteWindow : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmDeleteWindow(string displayName, bool isPlaying, int playlistRefCount)
    {
        InitializeComponent();
        PromptText.Text = string.Format(Strings.DlgDeletePrompt, displayName);

        if (isPlaying)
            ImpactPanel.Children.Add(MakeImpactRow("⚠ " + Strings.DlgDeletePlaying));
        if (playlistRefCount > 0)
            ImpactPanel.Children.Add(MakeImpactRow("📋 " + string.Format(Strings.DlgDeletePlaylistRefs, playlistRefCount)));
    }

    private static TextBlock MakeImpactRow(string text)
        => new() { Text = text, Foreground = FindResource("MutedTextBrush") as System.Windows.Media.Brush,
                   Margin = new Thickness(0, 0, 0, 6), FontSize = 13 };

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        DialogResult = false;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
    }
}
```

- [ ] **Step 3: Create RenameWindow.xaml**

Create `src/WallpaperApp/UI/Views/RenameWindow.xaml`:

```xml
<Window x:Class="WallpaperApp.UI.Views.RenameWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:loc="clr-namespace:WallpaperApp.Localization"
        Title="{loc:Loc DlgRenameTitle}" Width="420" SizeToContent="Height"
        ResizeMode="NoResize" WindowStartupLocation="CenterOwner"
        Background="#1E1E2E" Foreground="{StaticResource TextBrush}"
        ShowInTaskbar="False">
    <StackPanel Margin="24">
        <TextBlock Text="{loc:Loc DlgRenameTitle}" FontSize="20" FontWeight="Bold" Margin="0,0,0,14"/>
        <TextBlock Text="{loc:Loc DlgRenamePrompt}" Foreground="{StaticResource MutedTextBrush}" Margin="0,0,0,6"/>
        <TextBox x:Name="NameBox" KeyDown="NameBox_KeyDown" Margin="0,0,0,18"/>
        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
            <Button Content="{loc:Loc DlgCancel}" Click="Cancel_Click" Padding="20,10" Margin="0,0,10,0"/>
            <Button x:Name="ConfirmButton" Style="{StaticResource PrimaryButtonStyle}"
                    Content="{loc:Loc DlgRenameTitle}" Click="Confirm_Click"/>
        </StackPanel>
    </StackPanel>
</Window>
```

- [ ] **Step 4: Create RenameWindow.xaml.cs**

Create `src/WallpaperApp/UI/Views/RenameWindow.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WallpaperApp.UI.Views;

// Modal rename input. Pre-fills the current name, selects all, focuses. The
// confirm button stays disabled until the trimmed value differs from the original.
public partial class RenameWindow : Window
{
    private readonly string _original;

    public string NewName { get; private set; } = "";

    public RenameWindow(string currentName)
    {
        InitializeComponent();
        _original = currentName;
        NameBox.Text = currentName;
        NameBox.SelectAll();
        NameBox.Focus();
        NameBox.TextChanged += (_, _) => UpdateConfirmEnabled();
        UpdateConfirmEnabled();
    }

    private void UpdateConfirmEnabled()
    {
        ConfirmButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text)
                                  && NameBox.Text.Trim() != _original.Trim();
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && ConfirmButton.IsEnabled) Confirm_Click(sender, e);
        if (e.Key == Key.Escape) Cancel_Click(sender, e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        NewName = "";
        DialogResult = false;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        NewName = NameBox.Text;
        DialogResult = true;
    }
}
```

- [ ] **Step 5: Create PlaylistPickerWindow.xaml**

Create `src/WallpaperApp/UI/Views/PlaylistPickerWindow.xaml`:

```xml
<Window x:Class="WallpaperApp.UI.Views.PlaylistPickerWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:loc="clr-namespace:WallpaperApp.Localization"
        Title="{loc:Loc DlgPickPlaylistTitle}" Width="420" Height="440"
        ResizeMode="NoResize" WindowStartupLocation="CenterOwner"
        Background="#1E1E2E" Foreground="{StaticResource TextBrush}"
        ShowInTaskbar="False">
    <Grid Margin="24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Text="{loc:Loc DlgPickPlaylistTitle}" FontSize="20" FontWeight="Bold" Margin="0,0,0,14"/>

        <ListBox x:Name="PlaylistList" Grid.Row="1" Background="Transparent" BorderThickness="0"
                 Foreground="{StaticResource TextBrush}" ScrollViewer.HorizontalScrollBarVisibility="Disabled">
            <ListBox.ItemContainerStyle>
                <Style TargetType="ListBoxItem">
                    <Setter Property="Padding" Value="0"/>
                    <Setter Property="Margin" Value="0,0,0,8"/>
                    <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                    <Setter Property="Template">
                        <Setter.Value>
                            <ControlTemplate TargetType="ListBoxItem">
                                <Button Click="Playlist_Click" Tag="{Binding}" HorizontalAlignment="Stretch"
                                        HorizontalContentAlignment="Left" Padding="14,10">
                                    <Button.Template>
                                        <ControlTemplate TargetType="Button">
                                            <Border x:Name="bd" Background="{StaticResource CardSurfaceBrush}"
                                                    BorderBrush="{StaticResource CardBorderBrush}" BorderThickness="1"
                                                    CornerRadius="8" Padding="{TemplateBinding Padding}">
                                                <ContentPresenter/>
                                            </Border>
                                            <ControlTemplate.Triggers>
                                                <Trigger Property="IsMouseOver" Value="True">
                                                    <Setter TargetName="bd" Property="Background" Value="{StaticResource SurfaceHoverBrush}"/>
                                                </Trigger>
                                            </ControlTemplate.Triggers>
                                        </ControlTemplate>
                                    </Button.Template>
                                    <TextBlock Text="{Binding Name}"/>
                                </Button>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
            </ListBox.ItemContainerStyle>
        </ListBox>
    </Grid>
</Window>
```

- [ ] **Step 6: Create PlaylistPickerWindow.xaml.cs**

Create `src/WallpaperApp/UI/Views/PlaylistPickerWindow.xaml.cs`:

```csharp
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using WallpaperApp.Models;

namespace WallpaperApp.UI.Views;

// Modal playlist picker for "Add to playlist" from a card. Returns the chosen
// playlist via Selected (null if cancelled). Mirrors MonitorPickerWindow's shape.
public partial class PlaylistPickerWindow : Window
{
    public Playlist? Selected { get; private set; }

    public PlaylistPickerWindow(IList<Playlist> playlists)
    {
        InitializeComponent();
        PlaylistList.ItemsSource = playlists;
    }

    private void Playlist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is Playlist p)
        {
            Selected = p;
            DialogResult = true;
        }
    }
}
```

- [ ] **Step 7: Build to confirm all windows compile**

Run: `dotnet build "D:\AiProject\wallpaper\src\WallpaperApp\WallpaperApp.csproj"`
Expected: Build succeeds.

- [ ] **Step 8: Commit**

```bash
git add src/WallpaperApp/UI/Views/ConfirmDeleteWindow.xaml src/WallpaperApp/UI/Views/ConfirmDeleteWindow.xaml.cs src/WallpaperApp/UI/Views/RenameWindow.xaml src/WallpaperApp/UI/Views/RenameWindow.xaml.cs src/WallpaperApp/UI/Views/PlaylistPickerWindow.xaml src/WallpaperApp/UI/Views/PlaylistPickerWindow.xaml.cs
git commit -m "feat(ui): add ConfirmDelete, Rename, PlaylistPicker modal windows"
```

---

### Task 8: Wire the ContextMenu into WallpaperCard + LibraryView

**Files:**
- Modify: `src/WallpaperApp/UI/Controls/WallpaperCard.xaml`
- Modify: `src/WallpaperApp/UI/Views/LibraryView.xaml`

- [ ] **Step 1: Add ContextMenu to WallpaperCard.xaml**

In `src/WallpaperApp/UI/Controls/WallpaperCard.xaml`, add a `ContextMenu` to the `Border` named `Card`. Add this as a direct child of the `<Border x:Name="Card" ...>` element — i.e. inside the Border, before the `<Grid x:Name="CardContent">`. (In WPF, `ContextMenu`, like `Effect`/`RenderTransform`, is a property-element assigned inside the element.) Add the loc namespace is already present.

Actually `ContextMenu` is set via `Border.ContextMenu`. Insert this between the opening `<Border x:Name="Card" ...>` tag and its `<Border.RenderTransform>` block:

```xml
        <Border.ContextMenu>
            <ContextMenu>
                <MenuItem Header="{loc:Loc MenuSetAsWallpaper}"
                          Command="{Binding PlacementTarget.Commands.SetAsWallpaper,
                                    RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                          CommandParameter="{Binding PlacementTarget.DataContext,
                                    RelativeSource={RelativeSource AncestorType=ContextMenu}}"/>
                <MenuItem Header="{loc:Loc MenuOpenDetail}"
                          Command="{Binding PlacementTarget.Commands.OpenDetail,
                                    RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                          CommandParameter="{Binding PlacementTarget.DataContext,
                                    RelativeSource={RelativeSource AncestorType=ContextMenu}}"/>
                <Separator/>
                <MenuItem Header="{loc:Loc MenuOpenFileLocation}"
                          Command="{Binding PlacementTarget.Commands.OpenFileLocation,
                                    RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                          CommandParameter="{Binding PlacementTarget.DataContext,
                                    RelativeSource={RelativeSource AncestorType=ContextMenu}}"/>
                <MenuItem Header="{loc:Loc MenuRename}"
                          Command="{Binding PlacementTarget.Commands.Rename,
                                    RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                          CommandParameter="{Binding PlacementTarget.DataContext,
                                    RelativeSource={RelativeSource AncestorType=ContextMenu}}"/>
                <MenuItem Header="{loc:Loc MenuAddToPlaylist}"
                          Command="{Binding PlacementTarget.Commands.AddToPlaylist,
                                    RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                          CommandParameter="{Binding PlacementTarget.DataContext,
                                    RelativeSource={RelativeSource AncestorType=ContextMenu}}"/>
                <MenuItem Header="{loc:Loc MenuCopyToFolder}"
                          Command="{Binding PlacementTarget.Commands.CopyToFolder,
                                    RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                          CommandParameter="{Binding PlacementTarget.DataContext,
                                    RelativeSource={RelativeSource AncestorType=ContextMenu}}"/>
                <Separator/>
                <MenuItem Header="{loc:Loc MenuDelete}" Foreground="#F38BA8"
                          Command="{Binding PlacementTarget.Commands.Delete,
                                    RelativeSource={RelativeSource AncestorType=ContextMenu}}"
                          CommandParameter="{Binding PlacementTarget.DataContext,
                                    RelativeSource={RelativeSource AncestorType=ContextMenu}}"/>
            </ContextMenu>
        </Border.ContextMenu>
```

- [ ] **Step 2: Bind Commands in LibraryView.xaml**

In `src/WallpaperApp/UI/Views/LibraryView.xaml`, the `WallpaperCard` template (around line 46) currently binds only `OpenCommand`. Add the `Commands` binding:

```xml
                    <controls:WallpaperCard Margin="10"
                        OpenCommand="{Binding DataContext.OpenDetailCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                        Commands="{Binding DataContext.Commands, RelativeSource={RelativeSource AncestorType=ItemsControl}}"/>
```

- [ ] **Step 3: Build to confirm XAML compiles**

Run: `dotnet build "D:\AiProject\wallpaper\src\WallpaperApp\WallpaperApp.csproj"`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add src/WallpaperApp/UI/Controls/WallpaperCard.xaml src/WallpaperApp/UI/Views/LibraryView.xaml
git commit -m "feat(ui): wire context menu into WallpaperCard"
```

---

### Task 9: Build the whole solution + run full test suite

- [ ] **Step 1: Build the entire solution**

Run: `dotnet build "D:\AiProject\wallpaper\WallpaperApp.sln"`
Expected: Build succeeds, 0 errors.

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test "D:\AiProject\wallpaper\WallpaperApp.sln"`
Expected: all tests PASS, 0 failures.

- [ ] **Step 3: Manual smoke test checklist**

Launch the app (from the built exe or `dotnet run --project src/WallpaperApp`). Verify each menu item:

- [ ] Right-click a card → menu appears, dark themed, matches window style
- [ ] "设为壁纸"/"Set as wallpaper" → single monitor sets silently; multi-monitor opens picker
- [ ] "打开详情"/"Open details" → navigates to detail view
- [ ] "在文件夹中打开"/"Open file location" → Explorer opens with the file selected
- [ ] "重命名…"/"Rename…" → dialog pre-fills name, Enter confirms, list updates; Esc cancels
- [ ] "加入播放列表…"/"Add to playlist…" → picker shows playlists, click adds, toast confirms
- [ ] "复制到…"/"Copy to…" → SaveFileDialog, copy succeeds, toast confirms
- [ ] "删除"/"Delete" → confirm dialog shows impact rows; delete removes card, stops playback, cleans playlist refs; Cancel does nothing
- [ ] Switch language (Settings) → menu items + dialogs update to the other language

- [ ] **Step 4: Commit any smoke-test fixes**

If the smoke test surfaces bugs, fix them and commit each fix. If clean, no commit needed.

---

## Self-Review Notes

**Spec coverage check:**
- ✅ 7 menu items (spec §3) → Task 8 wires all 7; Task 6 defines the 7 handlers
- ✅ `WallpaperCommands` aggregate (spec §4.1) → Task 4
- ✅ ContextMenu PlacementTarget bridging (spec §4.2) → Task 8 Step 1
- ✅ `Commands` assembled in `MainViewModel` (spec §4.3) → Task 6 Step 4(e)
- ✅ Delete flow 7 steps (spec §5.1) → Task 6 Step 4(g), with stop-playback-before-delete order
- ✅ `RenameAsync` (spec §5.2) → Task 1
- ✅ Add-to-playlist via picker (spec §5.3) → Task 6 Step 4(h) + Task 7 (window)
- ✅ Copy-to via SaveFileDialog (spec §5.4) → Task 6 Step 4(i)
- ✅ Open file location (spec §5.5) → Task 6 Step 4(f)
- ✅ SetAsWallpaper param refactor (spec §5.6) → Task 6 Step 4(c)(d)
- ✅ DangerButtonStyle + ContextMenu/MenuItem styles (spec §6) → Task 3
- ✅ ~22 localization keys (spec §7) → Task 2
- ✅ Test strategy (spec §9) → Task 1 + Task 6 tests; system-shell commands documented as build+manual (Task 9)

**Type/name consistency check:**
- `WallpaperCommands` properties: `SetAsWallpaper`, `OpenDetail`, `OpenFileLocation`, `Rename`, `AddToPlaylist`, `CopyToFolder`, `Delete` — consistent across Task 4 (def), Task 6 Step 4(e) (assembly), Task 8 Step 1 (XAML bindings). ✅
- `RenameAsync(Guid, string, CancellationToken)` — Task 1 def, Task 6 Step 4(g) call uses `_library.RenameAsync(wallpaper.Id, newName)`. ✅
- `DeleteWallpaperAsync(WallpaperItem?)` public test hook — Task 6 Step 4(g) def, Task 6 Step 1 test call `vm.DeleteWallpaperAsync(item)`. ✅
- `RenameWallpaperAsync(WallpaperItem?)` public — Task 6 Step 4(f), test call `vm.RenameWallpaperAsync(item, "New Name")`. Wait: test calls it with TWO args but the method signature has ONE. **FIX NEEDED.**

**Issue found + fix:** The test `RenameWallpaperAsync_UpdatesMemoryObjectAndPersists` calls `vm.RenameWallpaperAsync(item, "New Name")` to bypass the dialog, but the handler signature is `RenameWallpaperAsync(WallpaperItem?)` which opens the dialog. To keep the test headless, the test needs a separate overload or the handler needs to be split. Correct fix: make `RenameWallpaperAsync(WallpaperItem?)` open the dialog AND call an internal core `ApplyRenameAsync(WallpaperItem, string)`; the test calls `ApplyRenameAsync`. 

Applying this fix inline to Task 6:

In Task 6 Step 4(f), replace the `RenameWallpaperAsync` method with TWO methods:

```csharp
    public async Task RenameWallpaperAsync(WallpaperItem? wallpaper)
    {
        if (wallpaper == null) return;
        var dialog = new RenameWindow(wallpaper.DisplayName) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        await ApplyRenameAsync(wallpaper, dialog.NewName);
    }

    // Core logic, separated so tests can exercise it without opening a Window.
    internal async Task ApplyRenameAsync(WallpaperItem wallpaper, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) { ShowToast(Strings.MsgNameEmpty, error: true); return; }
        if (await _library.RenameAsync(wallpaper.Id, newName))
        {
            wallpaper.DisplayName = newName.Trim();
            WallpapersView.Refresh();
            ShowToast(Strings.MsgRenamed);
        }
    }
```

And in Task 6 Step 1, change the test to call `ApplyRenameAsync`:
```csharp
        await vm.ApplyRenameAsync(item, "New Name");
```

This is reflected in the final plan text above. ✅ fix applied.

`AssignWallpaperFromCommandAsync` — Task 6 Step 4(d) def, test call `vm.AssignWallpaperFromCommandAsync(b)`. ✅
`PlaybackForTests` internal — Task 6 Step 4(f) def, test `(TestablePlaybackManager)vm.PlaybackForTests`. ✅
`TestablePlaybackManager` overrides `GetActiveWallpaperId`/`RemoveWallpaperAsync`/`SetWallpaperAsync` — all made `virtual` in Task 6 Step 3. ✅

Plan is internally consistent. Ready for execution.
