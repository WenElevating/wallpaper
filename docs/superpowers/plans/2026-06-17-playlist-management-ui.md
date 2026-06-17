# Playlist Management UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Turn the playlist screen into a usable editor with selection, member management, deletion, and clean selected-state styling.

**Architecture:** Keep `PlaylistService` as the data layer and extend `MainViewModel` with a selected playlist plus derived collections for the current playlist's members and addable wallpapers. `PlaylistView.xaml` will become a two-panel editor: playlist list on the left, selected-playlist editor on the right. Existing EF models and database schema stay unchanged.

**Tech Stack:** WPF, MVVM, `ObservableCollection<T>`, Entity Framework Core, xUnit.

---

### Task 1: Add view-model state for playlist editing

**Files:**
- Modify: `src/WallpaperApp/UI/ViewModels/MainViewModel.cs`
- Test: `tests/WallpaperApp.Tests/UI/MainViewModelPlaylistTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task SelectingPlaylist_PopulatesMembersAndAddableWallpapers()
{
    // create two wallpapers, create one playlist, add one member
    // set the selection
    // assert CurrentPlaylist is set, PlaylistMembers has the existing member,
    // and AddableWallpapers excludes already-added items
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/WallpaperApp.Tests/WallpaperApp.Tests.csproj --filter MainViewModelPlaylistTests.SelectingPlaylist_PopulatesMembersAndAddableWallpapers -p:OutDir=D:\\AiProject\\wallpaper\\.omx\\test-out\\`
Expected: FAIL because `CurrentPlaylist`, `PlaylistMembers`, and `AddableWallpapers` do not exist yet.

- [ ] **Step 3: Write minimal implementation**

```csharp
public ObservableCollection<PlaylistMember> PlaylistMembers { get; } = new();
public ObservableCollection<WallpaperItem> AddableWallpapers { get; } = new();
private Playlist? _selectedPlaylist;
public Playlist? SelectedPlaylist
{
    get => _selectedPlaylist;
    set
    {
        if (_selectedPlaylist == value) return;
        _selectedPlaylist = value;
        OnPropertyChanged();
        _ = RefreshSelectedPlaylistAsync();
    }
}

private async Task RefreshSelectedPlaylistAsync()
{
    PlaylistMembers.Clear();
    AddableWallpapers.Clear();
    if (_selectedPlaylist == null) return;

    var playlist = await _playlistService.GetByIdAsync(_selectedPlaylist.Id);
    if (playlist == null) return;

    _selectedPlaylist = playlist;
    OnPropertyChanged(nameof(SelectedPlaylist));
    foreach (var member in playlist.Members) PlaylistMembers.Add(member);
    var members = playlist.Members.Select(m => m.WallpaperId).ToHashSet();
    foreach (var item in Wallpapers.Where(w => !members.Contains(w.Id)))
        AddableWallpapers.Add(item);
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/WallpaperApp.Tests/WallpaperApp.Tests.csproj --filter MainViewModelPlaylistTests.SelectingPlaylist_PopulatesMembersAndAddableWallpapers -p:OutDir=D:\\AiProject\\wallpaper\\.omx\\test-out\\`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WallpaperApp/UI/ViewModels/MainViewModel.cs tests/WallpaperApp.Tests/UI/MainViewModelPlaylistTests.cs
git commit -m "refactor: expose playlist editor state"
```

### Task 2: Add playlist editing commands

**Files:**
- Modify: `src/WallpaperApp/UI/ViewModels/MainViewModel.cs`
- Test: `tests/WallpaperApp.Tests/UI/MainViewModelPlaylistTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public async Task AddAndRemovePlaylistMembers_UpdateCollections()
{
    // select a playlist
    // invoke AddToPlaylistCommand for an addable wallpaper
    // invoke RemoveFromPlaylistCommand for an existing member
    // assert the in-memory collections update immediately
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/WallpaperApp.Tests/WallpaperApp.Tests.csproj --filter MainViewModelPlaylistTests.AddAndRemovePlaylistMembers_UpdateCollections -p:OutDir=D:\\AiProject\\wallpaper\\.omx\\test-out\\`
Expected: FAIL because the commands do not exist yet.

- [ ] **Step 3: Write minimal implementation**

```csharp
public ICommand AddToPlaylistCommand { get; }
public ICommand RemoveFromPlaylistCommand { get; }
public ICommand DeletePlaylistCommand { get; }

AddToPlaylistCommand = new RelayCommand(async p =>
{
    if (SelectedPlaylist == null || p is not WallpaperItem item) return;
    await _playlistService.AddMemberAsync(SelectedPlaylist.Id, item.Id);
    await RefreshSelectedPlaylistAsync();
});

RemoveFromPlaylistCommand = new RelayCommand(async p =>
{
    if (SelectedPlaylist == null || p is not PlaylistMember member) return;
    await _playlistService.RemoveMemberAsync(SelectedPlaylist.Id, member.WallpaperId);
    await RefreshSelectedPlaylistAsync();
});

DeletePlaylistCommand = new RelayCommand(async _ =>
{
    if (SelectedPlaylist == null) return;
    var id = SelectedPlaylist.Id;
    await _playlistService.DeleteAsync(id);
    SelectedPlaylist = null;
    await LoadPlaylistsAsync();
});
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/WallpaperApp.Tests/WallpaperApp.Tests.csproj --filter MainViewModelPlaylistTests.AddAndRemovePlaylistMembers_UpdateCollections -p:OutDir=D:\\AiProject\\wallpaper\\.omx\\test-out\\`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WallpaperApp/UI/ViewModels/MainViewModel.cs tests/WallpaperApp.Tests/UI/MainViewModelPlaylistTests.cs
git commit -m "feat: edit playlist members from the shell"
```

### Task 3: Rebuild the playlist view as a two-panel editor

**Files:**
- Modify: `src/WallpaperApp/UI/Views/PlaylistView.xaml`
- Test: `tests/WallpaperApp.Tests/UI/MainViewModelPlaylistTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void PlaylistView_Styles_SelectedItemsWithReadableText()
{
    var xaml = File.ReadAllText(Path.Combine(repoRoot, "src", "WallpaperApp", "UI", "Views", "PlaylistView.xaml"));
    Assert.Contains("IsSelected", xaml);
    Assert.DoesNotContain("Run Text=\"{Binding Members.Count\"", xaml);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/WallpaperApp.Tests/WallpaperApp.Tests.csproj --filter MainViewModelPlaylistTests.PlaylistView_Styles_SelectedItemsWithReadableText -p:OutDir=D:\\AiProject\\wallpaper\\.omx\\test-out\\`
Expected: FAIL until the view gets explicit selected-state styling.

- [ ] **Step 3: Write minimal implementation**

```xml
<ListBox ItemsSource="{Binding Playlists}"
         SelectedItem="{Binding SelectedPlaylist, Mode=TwoWay}">
    <ListBox.ItemContainerStyle>
        <Style TargetType="ListBoxItem">
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="BorderThickness" Value="0"/>
            <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
            <Style.Triggers>
                <Trigger Property="IsSelected" Value="True">
                    <Setter Property="Background" Value="{StaticResource SurfaceSelectedBrush}"/>
                    <Setter Property="Foreground" Value="{StaticResource TextBrush}"/>
                </Trigger>
            </Style.Triggers>
        </Style>
    </ListBox.ItemContainerStyle>
</ListBox>
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/WallpaperApp.Tests/WallpaperApp.Tests.csproj --filter MainViewModelPlaylistTests.PlaylistView_Styles_SelectedItemsWithReadableText -p:OutDir=D:\\AiProject\\wallpaper\\.omx\\test-out\\`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/WallpaperApp/UI/Views/PlaylistView.xaml tests/WallpaperApp.Tests/UI/MainViewModelPlaylistTests.cs
git commit -m "fix: make playlist editor readable and selectable"
```

### Task 4: Verify full suite

**Files:**
- No code changes expected

- [ ] **Step 1: Run the full test project**

Run: `dotnet test tests/WallpaperApp.Tests/WallpaperApp.Tests.csproj -p:OutDir=D:\\AiProject\\wallpaper\\.omx\\test-out\\`
Expected: All tests pass.

- [ ] **Step 2: Check the working tree**

Run: `git status --short`
Expected: Only the intended playlist UI files and tests are modified.

- [ ] **Step 3: Commit if needed**

```bash
git add src/WallpaperApp/UI/ViewModels/MainViewModel.cs src/WallpaperApp/UI/Views/PlaylistView.xaml tests/WallpaperApp.Tests/UI/MainViewModelPlaylistTests.cs
git commit -m "feat: make playlists manageable in the UI"
```

### Coverage Check

- `MainViewModel` state and commands are covered in Tasks 1-2.
- `PlaylistView.xaml` readability and selection styling are covered in Task 3.
- Existing playlist creation/load behavior remains covered by the earlier tests and the full suite in Task 4.

