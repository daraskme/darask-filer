using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Darask.App;

/// <summary>
/// ナビゲーションペイン: ドライブ一覧 + 展開可能ツリー(docs/01 §4, docs/07 M1)。
/// </summary>
public sealed record QuickAccessNode(string DisplayName, string FullPath);
public sealed record HistoryNode(string DisplayName, string FullPath);

public partial class NavigationPane : UserControl
{
    private const int MaxHistoryEntries = 30;

    public event Action<string>? PathSelected;
    public event Action? RecycleBinRequested;
    public event Action<Workspace>? WorkspaceSelected;

    /// <summary>現在のタブ構成のスナップショットを返すデリゲート(MainWindow が設定する)。</summary>
    public Func<TabSnapshot>? WorkspaceSnapshotProvider { get; set; }

    private readonly ObservableCollection<QuickAccessNode> _quickAccess = [];
    private readonly ObservableCollection<HistoryNode> _history = [];
    private readonly ObservableCollection<Workspace> _workspaces = [];

    public NavigationPane()
    {
        InitializeComponent();
        LoadDrives();
        LoadWorkspaces();
        LoadQuickAccess();
        LoadHistory();
    }

    private void LoadWorkspaces()
    {
        foreach (var ws in WorkspaceStore.Load()) _workspaces.Add(ws);
        WorkspaceList.ItemsSource = _workspaces;
    }

    private void PersistWorkspaces() => WorkspaceStore.Save(_workspaces);

    /// <summary>現在のタブ構成を名前を付けて保存(同名があれば上書き)。</summary>
    private void SaveWorkspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (WorkspaceSnapshotProvider is not { } provider) return;

        var snapshot = provider();
        if (snapshot.Tabs.Count == 0) return;

        string? name = NameInputDialog.Show(Window.GetWindow(this)!, "作業スペース名を入力してください");
        if (name is null) return;

        var workspace = new Workspace(name, snapshot.Tabs, snapshot.ActiveIndex);
        int existing = IndexOfWorkspace(name);
        if (existing >= 0) _workspaces[existing] = workspace;
        else _workspaces.Add(workspace);
        PersistWorkspaces();
    }

    private int IndexOfWorkspace(string name)
    {
        for (int i = 0; i < _workspaces.Count; i++)
        {
            if (string.Equals(_workspaces[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    private void WorkspaceList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: Workspace ws })
        {
            WorkspaceSelected?.Invoke(ws);
        }
    }

    private void WorkspaceList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: Workspace ws }) return;

        var menu = new ContextMenu();
        MenuTheme.ApplyOpaque(menu);
        MenuTheme.AddItem(menu, "開く", () => WorkspaceSelected?.Invoke(ws));
        MenuTheme.AddItem(menu, "現在のタブ構成で上書き", () =>
        {
            if (WorkspaceSnapshotProvider is not { } provider) return;
            var snapshot = provider();
            if (snapshot.Tabs.Count == 0) return;
            int index = IndexOfWorkspace(ws.Name);
            if (index >= 0)
            {
                _workspaces[index] = new Workspace(ws.Name, snapshot.Tabs, snapshot.ActiveIndex);
                PersistWorkspaces();
            }
        });
        MenuTheme.AddItem(menu, "名前の変更", () =>
        {
            string? newName = NameInputDialog.Show(Window.GetWindow(this)!, "新しい名前を入力してください", ws.Name);
            if (newName is null || string.Equals(newName, ws.Name, StringComparison.Ordinal)) return;
            int index = IndexOfWorkspace(ws.Name);
            if (index >= 0)
            {
                _workspaces[index] = ws with { Name = newName };
                PersistWorkspaces();
            }
        });
        MenuTheme.AddItem(menu, "削除", () =>
        {
            if (_workspaces.Remove(ws)) PersistWorkspaces();
        });

        WorkspaceList.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void LoadQuickAccess()
    {
        foreach (string path in QuickAccessStore.Load())
        {
            _quickAccess.Add(new QuickAccessNode(Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path, path));
        }
        QuickAccessList.ItemsSource = _quickAccess;
    }

    /// <summary>フォルダービューの右クリックメニューから呼ばれる(docs/07 #21)。</summary>
    public void AddToQuickAccess(string path)
    {
        if (_quickAccess.Any(n => string.Equals(n.FullPath, path, StringComparison.OrdinalIgnoreCase))) return;

        string name = Path.GetFileName(path.TrimEnd('\\'));
        _quickAccess.Add(new QuickAccessNode(string.IsNullOrEmpty(name) ? path : name, path));
        QuickAccessStore.Save(_quickAccess.Select(n => n.FullPath));
    }

    private void RemoveFromQuickAccess(string path)
    {
        var node = _quickAccess.FirstOrDefault(n => n.FullPath == path);
        if (node is not null)
        {
            _quickAccess.Remove(node);
            QuickAccessStore.Save(_quickAccess.Select(n => n.FullPath));
        }
    }

    private void QuickAccessList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: QuickAccessNode node })
        {
            PathSelected?.Invoke(node.FullPath);
        }
    }

    private void QuickAccessList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: QuickAccessNode node }) return;

        var menu = new ContextMenu();
        MenuTheme.ApplyOpaque(menu);
        MenuTheme.AddItem(menu, "クイックアクセスから削除", () => RemoveFromQuickAccess(node.FullPath));
        QuickAccessList.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void LoadHistory()
    {
        foreach (string path in HistoryStore.Load())
        {
            _history.Add(new HistoryNode(Path.GetFileName(path.TrimEnd('\\')) is { Length: > 0 } name ? name : path, path));
        }
        HistoryList.ItemsSource = _history;
    }

    /// <summary>いずれかのタブでナビゲーションが起きるたびに呼ばれる(docs/07 #26)。
    /// MRU(最近訪れた順)で先頭に積み、重複は前の位置から取り除いてから積み直す。</summary>
    public void RecordHistory(string path)
    {
        var existing = _history.FirstOrDefault(n => string.Equals(n.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) _history.Remove(existing);

        string name = Path.GetFileName(path.TrimEnd('\\'));
        _history.Insert(0, new HistoryNode(string.IsNullOrEmpty(name) ? path : name, path));
        while (_history.Count > MaxHistoryEntries) _history.RemoveAt(_history.Count - 1);

        HistoryStore.Save(_history.Select(n => n.FullPath));
    }

    private void RemoveFromHistory(string path)
    {
        var node = _history.FirstOrDefault(n => n.FullPath == path);
        if (node is not null)
        {
            _history.Remove(node);
            HistoryStore.Save(_history.Select(n => n.FullPath));
        }
    }

    private void HistoryList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: HistoryNode node })
        {
            PathSelected?.Invoke(node.FullPath);
        }
    }

    private void HistoryList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: HistoryNode node }) return;

        var menu = new ContextMenu();
        MenuTheme.ApplyOpaque(menu);
        MenuTheme.AddItem(menu, "履歴から削除", () => RemoveFromHistory(node.FullPath));
        MenuTheme.AddItem(menu, "履歴をすべてクリア", () =>
        {
            _history.Clear();
            HistoryStore.Save(_history.Select(n => n.FullPath));
        });

        HistoryList.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void RecycleBinRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => RecycleBinRequested?.Invoke();

    private void LoadDrives()
    {
        var roots = new ObservableCollection<NavNode>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady) continue;
            string label = string.IsNullOrEmpty(drive.VolumeLabel)
                ? drive.Name
                : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
            roots.Add(new NavNode(label, drive.RootDirectory.FullName));
        }
        Tree.ItemsSource = roots;
    }

    private void TreeViewItem_Expanded(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is TreeViewItem { DataContext: NavNode node })
        {
            node.EnsureChildrenLoaded();
        }
    }

    // TreeView.SelectedItem はキーボードフォーカスの巡回だけでもプログラム的に変化することがあり、
    // TreeViewItem.Selected イベントで拾うと「起動直後に C: が勝手に選ばれる」誤発火が起きる
    // (実測で確認)。ユーザーの実クリックのみを拾うため PreviewMouseLeftButtonUp で判定する。
    private void Tree_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: NavNode node } && !node.IsPlaceholder)
        {
            PathSelected?.Invoke(node.FullPath);
        }
    }
}
