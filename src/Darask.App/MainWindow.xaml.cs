using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Darask.App;

public partial class MainWindow : FluentWindow
{
    private readonly ObservableCollection<TabViewModel> _tabs = [];
    private TabViewModel? _activeTab;
    private TabViewModel? _recycleBinTab;
    private FolderTabContent? Active => _activeTab?.Content as FolderTabContent;

    public MainWindow()
    {
        InitializeComponent();
        TabStrip.ItemsSource = _tabs;

        NavPane.PathSelected += path => Active?.Navigate(path);
        NavPane.RecycleBinRequested += OpenRecycleBinTab;
        NavPane.WorkspaceSelected += ApplyWorkspace;
        NavPane.WorkspaceSnapshotProvider = SnapshotTabs;

        InputBindings.Add(new KeyBinding(new RelayCommand(_ => Active?.FocusAddressEditMode()), Key.L, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => Active?.FocusFilterBox()), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => Active?.ToggleHidden()), Key.H, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => Active?.ToggleExtensions()), Key.X, ModifierKeys.Control | ModifierKeys.Shift));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => Active?.Refresh()), Key.F5, ModifierKeys.None));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => AddTab(Active?.CurrentPath ?? DefaultInitialPath())), Key.T, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => { if (_activeTab is not null) CloseTab(_activeTab); }), Key.W, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => CycleTab(1)), Key.Tab, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => CycleTab(-1)), Key.Tab, ModifierKeys.Control | ModifierKeys.Shift));

        // Ctrl+Shift+1..8 ビューショートカット(docs/01 §4)。M2 時点では 詳細/アイコングリッドの
        // 2 モードのみ実装。1=詳細、2-8=アイコングリッド(将来のマイルストーンで一覧・各アイコン
        // サイズ・並べて表示・コンテンツへ細分化する)。
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => Active?.SetViewMode(FolderViewMode.Details)), Key.D1, ModifierKeys.Control | ModifierKeys.Shift));
        for (int i = 2; i <= 8; i++)
        {
            var key = (Key)Enum.Parse(typeof(Key), "D" + i);
            InputBindings.Add(new KeyBinding(new RelayCommand(_ => Active?.SetViewMode(FolderViewMode.IconGrid)), key, ModifierKeys.Control | ModifierKeys.Shift));
        }

        Loaded += (_, _) => RestoreSession();
        Closing += (_, _) => SessionStore.Save(SnapshotTabs());
    }

    private static string DefaultInitialPath() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>前回終了時のタブ構成を復元する。復元できるタブがなければ既定の1タブで開始。
    /// ファイル読み込みと存在チェックは stat 系 I/O(到達不能なネットワークパスでは秒単位で
    /// ブロックし得る)のため UI スレッドで行わない(CLAUDE.md 規則1)。</summary>
    private async void RestoreSession()
    {
        var restored = await Task.Run(() =>
        {
            var session = SessionStore.Load();
            if (session is null) return null;
            var valid = session.Tabs.Where(t => System.IO.Directory.Exists(t.Path)).ToList();
            return valid.Count > 0 ? new TabSnapshot(valid, MapActiveIndex(session.Tabs, session.ActiveIndex, valid)) : null;
        });

        if (restored is null)
        {
            AddTab(DefaultInitialPath());
            return;
        }

        OpenTabSet(restored.Tabs, restored.ActiveIndex);
    }

    /// <summary>存在しないパスの除去後もアクティブタブが同じタブを指すようインデックスを引き直す。</summary>
    private static int MapActiveIndex(List<SessionTab> original, int activeIndex, List<SessionTab> filtered)
    {
        if (activeIndex >= 0 && activeIndex < original.Count)
        {
            int mapped = filtered.IndexOf(original[activeIndex]);
            if (mapped >= 0) return mapped;
        }
        return 0;
    }

    /// <summary>現在のフォルダータブ構成のスナップショット(ごみ箱等の特殊タブは含めない)。</summary>
    private TabSnapshot SnapshotTabs()
    {
        var tabs = new List<SessionTab>();
        int activeIndex = 0;
        foreach (var t in _tabs)
        {
            if (t.Content is not FolderTabContent folder || folder.CurrentPath is not { Length: > 0 } path) continue;
            if (t == _activeTab) activeIndex = tabs.Count;
            tabs.Add(new SessionTab(path, (int)folder.ViewMode, folder.IconSize));
        }
        return new TabSnapshot(tabs, activeIndex);
    }

    /// <summary>作業スペースを開く: 現在の全タブを新しいタブ構成で置き換える(ユーザー要望機能)。
    /// 存在チェックは RestoreSession 同様バックグラウンドで行う(CLAUDE.md 規則1)。</summary>
    private async void ApplyWorkspace(Workspace workspace)
    {
        var (valid, activeIndex) = await Task.Run(() =>
        {
            var tabs = workspace.Tabs.Where(t => System.IO.Directory.Exists(t.Path)).ToList();
            return (tabs, MapActiveIndex(workspace.Tabs, workspace.ActiveIndex, tabs));
        });
        if (valid.Count == 0)
        {
            System.Windows.MessageBox.Show("この作業スペースのフォルダーはいずれも存在しません。", "darask-filer",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        // 先に新タブを全部作ってから旧タブを畳む(「最後の1枚は閉じない」ガードを迂回しつつ
        // 一瞬もタブゼロ状態を作らないため)。
        var oldTabs = _tabs.ToList();
        _recycleBinTab = null;
        OpenTabSet(valid, activeIndex);

        foreach (var t in oldTabs)
        {
            _tabs.Remove(t);
            TabContentHost.Children.Remove(t.Content);
            (t.Content as ITabContent)?.Shutdown();
        }
    }

    private void OpenTabSet(List<SessionTab> tabs, int activeIndex)
    {
        TabViewModel? toSelect = null;
        int clampedActive = Math.Clamp(activeIndex, 0, tabs.Count - 1);
        for (int i = 0; i < tabs.Count; i++)
        {
            var vm = AddTab(tabs[i].Path, (FolderViewMode)tabs[i].ViewMode, tabs[i].IconSize);
            if (i == clampedActive) toSelect = vm;
        }
        if (toSelect is not null) SelectTab(toSelect);
    }

    private TabViewModel AddTab(string path, FolderViewMode viewMode = FolderViewMode.Details, double iconSize = 48)
    {
        var content = new FolderTabContent { Visibility = Visibility.Collapsed };
        TabContentHost.Children.Add(content);

        var vm = new TabViewModel(content);
        content.PathChanged += path =>
        {
            vm.Title = content.TabTitle;
            NavPane.RecordHistory(path);
            if (vm == _activeTab) StatusBarControl.UpdateDriveSpace(path);
        };
        content.SelectionSummaryChanged += (total, selected, size) =>
        {
            if (vm == _activeTab) StatusBarControl.UpdateCounts(total, selected, size);
        };
        content.AddToQuickAccessRequested += paths =>
        {
            foreach (string p in paths) NavPane.AddToQuickAccess(p);
        };
        content.OpenInNewTabRequested += path => AddTab(path);

        _tabs.Add(vm);
        if (viewMode != FolderViewMode.Details) content.SetViewMode(viewMode);
        if (iconSize is not 48) content.SetIconZoom(iconSize);
        content.Navigate(path);
        SelectTab(vm);
        return vm;
    }

    private void SelectTab(TabViewModel vm)
    {
        _activeTab = vm;
        foreach (var t in _tabs)
        {
            t.IsActive = t == vm;
            t.Content.Visibility = t == vm ? Visibility.Visible : Visibility.Collapsed;
        }

        if (vm.Content is FolderTabContent { CurrentPath: { Length: > 0 } path })
        {
            StatusBarControl.UpdateDriveSpace(path);
        }
    }

    private void CloseTab(TabViewModel vm)
    {
        if (_tabs.Count <= 1) return; // 最後の1枚は閉じない(docs/07 #24 v1 の割り切り)

        int index = _tabs.IndexOf(vm);
        _tabs.Remove(vm);
        TabContentHost.Children.Remove(vm.Content);
        (vm.Content as ITabContent)?.Shutdown();
        if (vm == _recycleBinTab) _recycleBinTab = null;

        if (_activeTab == vm)
        {
            SelectTab(_tabs[Math.Max(0, index - 1)]);
        }
    }

    /// <summary>NavigationPane の「ごみ箱」クリックで呼ばれる(docs/07 #28)。既に開いていれば
    /// 新規作成せずそのタブへ切り替える。</summary>
    private void OpenRecycleBinTab()
    {
        if (_recycleBinTab is not null && _tabs.Contains(_recycleBinTab))
        {
            SelectTab(_recycleBinTab);
            return;
        }

        var content = new RecycleBinView { Visibility = Visibility.Collapsed };
        TabContentHost.Children.Add(content);

        var vm = new TabViewModel(content) { Title = "ごみ箱" };
        _tabs.Add(vm);
        _recycleBinTab = vm;
        SelectTab(vm);
    }

    private void CycleTab(int direction)
    {
        if (_tabs.Count < 2 || _activeTab is null) return;
        int index = _tabs.IndexOf(_activeTab);
        int next = (index + direction + _tabs.Count) % _tabs.Count;
        SelectTab(_tabs[next]);
    }

    private void NewTabButton_Click(object sender, RoutedEventArgs e) => AddTab(Active?.CurrentPath ?? DefaultInitialPath());

    private void TabChip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabViewModel vm }) SelectTab(vm);
    }

    // タブ chip の中クリックでタブを閉じる(ブラウザー/エクスプローラー準拠)。
    private void TabChip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && sender is FrameworkElement { DataContext: TabViewModel vm })
        {
            CloseTab(vm);
            e.Handled = true;
        }
    }

    private void TabChip_Close_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabViewModel vm })
        {
            CloseTab(vm);
            e.Handled = true;
        }
    }
}

/// <summary>KeyBinding 用の最小限の ICommand 実装(M1 では MVVM フレームワークを導入しない)。</summary>
internal sealed class RelayCommand(Action<object?> execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
}
