using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Darask.Enumeration;

namespace Darask.App;

public enum FolderViewMode { Details, IconGrid }

/// <summary>
/// 仮想化詳細ビュー/アイコングリッドビュー + ナビゲーション履歴 + RDCW 自動更新
/// (docs/06 §2, docs/07 M1/M2)。
/// </summary>
public partial class FolderView : UserControl
{
    private readonly List<string> _backStack = [];
    private readonly List<string> _forwardStack = [];
    private string? _currentPath;
    private DirectoryWatcher? _watcher;
    private readonly DispatcherTimer _watchDebounce;
    private FileSystemEntry[] _allEntries = [];
    private SortKey _sortKey = SortKey.Name;
    private SortDirection _sortDirection = SortDirection.Ascending;
    private bool _showHidden;
    private bool _showSystem;
    private CancellationTokenSource? _navigationCts;
    private FolderViewMode _viewMode = FolderViewMode.Details;
    private double _iconGridExtentHeight;
    private double _iconGridViewportHeight;
    private bool _draggingThumb;
    private double _dragStartMouseY;
    private double _dragStartOffset;
    private string? _lastBuiltPath;
    private readonly Dictionary<GridViewColumn, SortKey> _columnSortKeys = [];
    private readonly Dictionary<GridViewColumn, string> _columnTitles = [];

    // アイコングリッドのズーム段階(Ctrl+ホイール、docs/01 §4 の「小〜特大」連続ズーム相当)。
    private static readonly double[] IconZoomLevels = [32, 48, 64, 96, 128, 192, 256];
    private int _iconZoomIndex = 1;

    public static readonly DependencyProperty IconSizeProperty = DependencyProperty.Register(
        nameof(IconSize), typeof(double), typeof(FolderView), new PropertyMetadata(48.0));
    public double IconSize
    {
        get => (double)GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    public static readonly DependencyProperty IconItemWidthProperty = DependencyProperty.Register(
        nameof(IconItemWidth), typeof(double), typeof(FolderView), new PropertyMetadata(88.0));
    public double IconItemWidth
    {
        get => (double)GetValue(IconItemWidthProperty);
        set => SetValue(IconItemWidthProperty, value);
    }

    public event Action<string>? PathChanged;
    public event Action<int, int, long>? SelectionSummaryChanged;
    public event Action<IReadOnlyList<string>>? AddToQuickAccessRequested;
    public event Action<FolderEntryViewModel?>? SingleSelectionChanged;
    public event Action<string>? OpenInNewTabRequested;

    public FolderView()
    {
        InitializeComponent();
        _watchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _watchDebounce.Tick += (_, _) =>
        {
            _watchDebounce.Stop();
            if (_currentPath is not null) Navigate(_currentPath, recordHistory: false);
        };

        ListViewControl.AddHandler(GridViewColumnHeader.ClickEvent, new RoutedEventHandler(ColumnHeader_Click));
        MouseDown += FolderView_MouseDown;

        // 列 → ソートキーの対応(ヘッダー文字列にソートグリフを付けるため、文字列一致ではなく
        // GridViewColumn インスタンスで対応付ける)。XAML の列順と一致させること。
        var gridView = (GridView)ListViewControl.View;
        SortKey[] keys = [SortKey.Name, SortKey.LastWriteTime, SortKey.Type, SortKey.Size];
        for (int i = 0; i < keys.Length && i < gridView.Columns.Count; i++)
        {
            var column = gridView.Columns[i];
            _columnSortKeys[column] = keys[i];
            _columnTitles[column] = (string)column.Header;
        }
        UpdateSortGlyphs();
    }

    public string? CurrentPath => _currentPath;
    public bool CanGoBack => _backStack.Count > 0;
    public bool CanGoForward => _forwardStack.Count > 0;

    private ListBox ActiveControl => _viewMode == FolderViewMode.Details ? ListViewControl : IconGridControl;

    public FolderViewMode ViewMode
    {
        get => _viewMode;
        set
        {
            if (_viewMode == value) return;
            var previousItems = ActiveControl.ItemsSource;
            var previousSelection = ActiveControl.SelectedItems.Cast<FolderEntryViewModel>().ToList();

            _viewMode = value;
            ListViewControl.Visibility = value == FolderViewMode.Details ? Visibility.Visible : Visibility.Collapsed;
            IconGridControl.Visibility = value == FolderViewMode.IconGrid ? Visibility.Visible : Visibility.Collapsed;

            ActiveControl.ItemsSource = previousItems;
            foreach (var item in previousSelection) ActiveControl.SelectedItems.Add(item);

            UpdateIconGridScrollBar();
        }
    }

    public bool ShowHidden
    {
        get => _showHidden;
        set { _showHidden = value; ApplyFilterAndSort(); }
    }

    public bool ShowSystem
    {
        get => _showSystem;
        set { _showSystem = value; ApplyFilterAndSort(); }
    }

    private bool _showExtensions = true;
    public bool ShowExtensions
    {
        get => _showExtensions;
        set { _showExtensions = value; ApplyFilterAndSort(); }
    }

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set { _filterText = value; ApplyFilterAndSort(); }
    }

    public async void Navigate(string path, bool recordHistory = true) => await NavigateAsync(path, recordHistory);

    private async Task NavigateAsync(string path, bool recordHistory = true)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        if (!Directory.Exists(full)) return;

        if (recordHistory && _currentPath is not null && !string.Equals(_currentPath, full, StringComparison.OrdinalIgnoreCase))
        {
            _backStack.Add(_currentPath);
            _forwardStack.Clear();
        }

        _currentPath = full;
        PathChanged?.Invoke(full);

        _navigationCts?.Cancel();
        var cts = new CancellationTokenSource();
        _navigationCts = cts;
        CancellationToken token = cts.Token;

        FileSystemEntry[] entries;
        try
        {
            entries = await Task.Run(() => FastEnumerator.Enumerate(full).ToArray(), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested) return;

        _allEntries = entries;
        ApplyFilterAndSort();
        SetupWatcher(full);
    }

    /// <summary>現在フォルダーを再列挙する(F5 / 更新ボタン)。</summary>
    public void Refresh()
    {
        if (_currentPath is not null) Navigate(_currentPath, recordHistory: false);
    }

    /// <summary>タブが閉じられる時に呼ぶ(RDCW 監視ハンドルの解放、docs/07 #24)。</summary>
    public void Shutdown()
    {
        _navigationCts?.Cancel();
        _watchDebounce.Stop();
        DisposeWatcher();
    }

    public void GoBack()
    {
        if (_backStack.Count == 0) return;
        string target = _backStack[^1];
        _backStack.RemoveAt(_backStack.Count - 1);
        if (_currentPath is not null) _forwardStack.Add(_currentPath);
        Navigate(target, recordHistory: false);
    }

    public void GoForward()
    {
        if (_forwardStack.Count == 0) return;
        string target = _forwardStack[^1];
        _forwardStack.RemoveAt(_forwardStack.Count - 1);
        if (_currentPath is not null) _backStack.Add(_currentPath);
        Navigate(target, recordHistory: false);
    }

    public void GoUp()
    {
        if (_currentPath is null) return;
        var parent = Directory.GetParent(_currentPath);
        if (parent is not null)
        {
            Navigate(parent.FullName);
        }
    }

    public void SortBy(SortKey key)
    {
        if (_sortKey == key)
        {
            _sortDirection = _sortDirection == SortDirection.Ascending ? SortDirection.Descending : SortDirection.Ascending;
        }
        else
        {
            _sortKey = key;
            _sortDirection = SortDirection.Ascending;
        }
        UpdateSortGlyphs();
        ApplyFilterAndSort();
    }

    private void UpdateSortGlyphs()
    {
        foreach (var (column, key) in _columnSortKeys)
        {
            string title = _columnTitles[column];
            column.Header = key == _sortKey
                ? title + (_sortDirection == SortDirection.Ascending ? " ▲" : " ▼")
                : title;
        }
    }

    private void ApplyFilterAndSort()
    {
        // 名前変更中にファイル監視のデバウンス更新など外部要因で一覧が再構築されても編集状態を
        // 引き継ぐ(docs/07 コンテキストメニュー整理)。特に「新しいフォルダー」作成直後の
        // 自動リネームは、作成直後に発火する DirectoryWatcher の変更通知(200ms デバウンス)と
        // ほぼ確実に競合し、対策なしだと編集ボックスが一覧再構築で即座に消えてしまう。
        var renaming = (ActiveControl.ItemsSource as IEnumerable<FolderEntryViewModel>)
            ?.FirstOrDefault(v => v.IsRenaming);

        // 同一フォルダー内での再構築(RDCW 自動更新・フィルター・トグル・ソート変更)では
        // 選択とスクロール位置を保持する(Codex ブレインストーム #7)。別フォルダーへの
        // ナビゲーションでは保持しない(先頭から表示するのがエクスプローラー準拠)。
        bool samePath = string.Equals(_lastBuiltPath, _currentPath, StringComparison.OrdinalIgnoreCase);
        _lastBuiltPath = _currentPath;
        HashSet<string>? selectedNames = null;
        double previousOffset = 0;
        if (samePath)
        {
            if (ActiveControl.SelectedItems.Count > 0)
            {
                selectedNames = ActiveControl.SelectedItems.Cast<FolderEntryViewModel>()
                    .Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
            }
            previousOffset = _viewMode == FolderViewMode.IconGrid
                ? FindDescendantScrollViewer(IconGridControl)?.VerticalOffset ?? 0
                : FindDescendantScrollViewer(ListViewControl)?.VerticalOffset ?? 0;
        }

        IEnumerable<FileSystemEntry> query = _allEntries;
        if (!_showHidden) query = query.Where(e => !e.IsHidden);
        if (!_showSystem) query = query.Where(e => !e.IsSystem);
        if (!string.IsNullOrEmpty(_filterText))
        {
            query = query.Where(e => e.Name.Contains(_filterText, StringComparison.CurrentCultureIgnoreCase));
        }
        var filtered = query.ToArray();

        EntrySorter.Sort(filtered, _sortKey, _sortDirection);

        string parentPath = _currentPath ?? string.Empty;
        var vms = new List<FolderEntryViewModel>(filtered.Length);
        foreach (var e in filtered) vms.Add(new FolderEntryViewModel(e, parentPath, _showExtensions));

        ActiveControl.ItemsSource = vms;
        SelectionSummaryChanged?.Invoke(vms.Count, 0, 0);
        UpdateIconGridScrollBar();

        if (selectedNames is not null)
        {
            foreach (var vm in vms)
            {
                if (selectedNames.Contains(vm.Name)) ActiveControl.SelectedItems.Add(vm);
            }
        }
        if (samePath && previousOffset > 0)
        {
            // ItemsSource 差し替え直後は仮想化パネルがまだレイアウトされておらずオフセット設定が
            // 無視されるため、レイアウトパス完了後に復元する。
            double offset = previousOffset;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                if (_viewMode == FolderViewMode.IconGrid) ScrollIconGridTo(offset);
                else FindDescendantScrollViewer(ListViewControl)?.ScrollToVerticalOffset(offset);
            });
        }

        if (renaming is not null && vms.FirstOrDefault(v => v.Name == renaming.Name) is { } restored)
        {
            string editName = renaming.EditName;
            // ItemsSource 差し替え直後はまだ新しい行のコンテナが生成されていない
            // (仮想化パネルのレイアウトパスは非同期)。IsRenaming をここで即 true にしても
            // コンテナ側の DataTrigger は「最初から Visible」として生成されるだけで
            // Collapsed→Visible の遷移が起きず、RenameBox_IsVisibleChanged が発火しない。
            // レイアウトパス完了後(Loaded 優先度)まで遅延させ、既存コンテナへの通常の
            // プロパティ変更として反映させる。
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
            {
                ActiveControl.SelectedItem = restored;
                ActiveControl.ScrollIntoView(restored);
                restored.IsRenaming = true;
                restored.EditName = editName;
            });
        }
    }

    // VirtualizingWrapPanel(サードパーティ)は ItemsSource/サイズ変更時に ScrollOwner の
    // Extent を正しく再計算させず、標準/WPF-UI いずれの ScrollBar テンプレートも描画されない
    // (docs/07 #19)。アイテム数と実測レイアウトからエクステントを自前算出し、
    // Border 2枚(トラック/つまみ)で独自にスクロールバーを描画・ドラッグ操作する。
    private void UpdateIconGridScrollBar()
    {
        if (_viewMode != FolderViewMode.IconGrid)
        {
            IconGridScrollTrack.Visibility = Visibility.Collapsed;
            return;
        }

        // Visibility 変更直後は ActualWidth/Height がまだ 0(レイアウトパス未実行)のことがあるため、
        // レイアウト確定後に再計算する(Collapsed のままだと再計算されないので _viewMode の
        // ガードを先に通した場合のみリトライする)。
        if (IconGridControl.ActualWidth <= 0 || IconGridControl.ActualHeight <= 0)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, UpdateIconGridScrollBar);
            return;
        }

        double itemWidth = IconItemWidth + 8;  // StackPanel Width + ItemContainerStyle Margin 4+4
        double itemHeight = IconSize + 52;     // アイコン + 2行テキスト + Padding/Margin(48px 時の実測 100 に整合)
        int itemCount = IconGridControl.Items.Count;
        int columns = Math.Max(1, (int)(IconGridControl.ActualWidth / itemWidth));
        int rows = (int)Math.Ceiling(itemCount / (double)columns);
        _iconGridExtentHeight = rows * itemHeight;
        _iconGridViewportHeight = IconGridControl.ActualHeight;

        if (itemCount == 0 || _iconGridExtentHeight <= _iconGridViewportHeight)
        {
            IconGridScrollTrack.Visibility = Visibility.Collapsed;
            return;
        }

        IconGridScrollTrack.Visibility = Visibility.Visible;
        UpdateIconGridThumb(FindDescendantScrollViewer(IconGridControl)?.VerticalOffset ?? 0);
    }

    private void UpdateIconGridThumb(double verticalOffset)
    {
        double trackHeight = IconGridControl.ActualHeight;
        if (trackHeight <= 0 || _iconGridExtentHeight <= 0) return;

        double thumbHeight = Math.Max(24, trackHeight * (_iconGridViewportHeight / _iconGridExtentHeight));
        double maxOffset = Math.Max(1, _iconGridExtentHeight - _iconGridViewportHeight);
        double thumbTop = (trackHeight - thumbHeight) * Math.Clamp(verticalOffset / maxOffset, 0, 1);

        IconGridScrollThumb.Height = thumbHeight;
        IconGridScrollThumb.Margin = new Thickness(0, thumbTop, 0, 0);
    }

    private void ScrollIconGridTo(double verticalOffset)
    {
        double maxOffset = Math.Max(0, _iconGridExtentHeight - _iconGridViewportHeight);
        double clamped = Math.Clamp(verticalOffset, 0, maxOffset);
        FindDescendantScrollViewer(IconGridControl)?.ScrollToVerticalOffset(clamped);
        UpdateIconGridThumb(clamped);
    }

    private void IconGridScrollTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource == IconGridScrollThumb) return; // つまみ自体のクリックは専用ハンドラに任せる

        double clickY = e.GetPosition(IconGridScrollTrack).Y;
        double thumbHeight = IconGridScrollThumb.Height;
        double targetOffset = (clickY - thumbHeight / 2) / Math.Max(1, IconGridControl.ActualHeight - thumbHeight)
            * Math.Max(0, _iconGridExtentHeight - _iconGridViewportHeight);
        ScrollIconGridTo(targetOffset);
    }

    private void IconGridScrollThumb_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _draggingThumb = true;
        _dragStartMouseY = e.GetPosition(IconGridScrollTrack).Y;
        _dragStartOffset = FindDescendantScrollViewer(IconGridControl)?.VerticalOffset ?? 0;
        IconGridScrollThumb.CaptureMouse();
        e.Handled = true;
    }

    private void IconGridScrollThumb_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_draggingThumb) return;

        double deltaY = e.GetPosition(IconGridScrollTrack).Y - _dragStartMouseY;
        double trackRange = Math.Max(1, IconGridControl.ActualHeight - IconGridScrollThumb.Height);
        double offsetRange = Math.Max(0, _iconGridExtentHeight - _iconGridViewportHeight);
        ScrollIconGridTo(_dragStartOffset + deltaY * (offsetRange / trackRange));
    }

    private void IconGridScrollThumb_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _draggingThumb = false;
        IconGridScrollThumb.ReleaseMouseCapture();
    }

    private void IconGridControl_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateIconGridScrollBar();

    private void SetupWatcher(string path)
    {
        DisposeWatcher();
        try
        {
            var watcher = new DirectoryWatcher(path);
            watcher.Changed += _ => Dispatcher.BeginInvoke(RestartDebounce);
            watcher.Overflowed += () => Dispatcher.BeginInvoke(RestartDebounce);
            _watcher = watcher;
        }
        catch (IOException)
        {
            // 監視非対応(FAT/ネットワーク等)は無視。M9 以降のインデックス連携で補う。
        }
    }

    private void RestartDebounce()
    {
        _watchDebounce.Stop();
        _watchDebounce.Start();
    }

    private void DisposeWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    // ズームに応じたサムネイルの要求サイズ(64/128/256 の3段階で丸めてキャッシュ効率を保つ)。
    private int CurrentThumbnailSize => _viewMode == FolderViewMode.IconGrid
        ? IconSize <= 64 ? 64 : IconSize <= 128 ? 128 : 256
        : 64;

    // 64px 超のズームでは 16px シェルアイコンの拡大ボケを避けるため 32px 版を要求する。
    private bool UseLargeIcons => _viewMode == FolderViewMode.IconGrid && IconSize > 64;

    private void EntryRow_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FolderEntryViewModel vm })
        {
            vm.BeginLoadIcon(CurrentThumbnailSize, UseLargeIcons);
        }
    }

    private void EntryRow_Unloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FolderEntryViewModel vm })
        {
            vm.CancelLoad();
        }
    }

    private void ColumnHeader_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader { Column: { } column }
            && _columnSortKeys.TryGetValue(column, out var key))
        {
            SortBy(key);
        }
    }

    // ListViewControl と IconGridControl(どちらも ListBox 派生)で共有するハンドラ。
    private void ListViewControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var control = (ListBox)sender;
        int total = control.Items.Count;
        int selected = control.SelectedItems.Count;
        long selectedSize = 0;
        foreach (FolderEntryViewModel vm in control.SelectedItems)
        {
            selectedSize += vm.SizeBytes;
        }
        SelectionSummaryChanged?.Invoke(total, selected, selectedSize);
        SingleSelectionChanged?.Invoke(selected == 1 ? (FolderEntryViewModel)control.SelectedItems[0]! : null);
    }

    // VirtualizingWrapPanel(サードパーティ)は IScrollInfo のオフセット設定はできるが
    // マウスホイールの委譲を実装していないため、ScrollViewer を直接操作する。
    // Ctrl+ホイールはアイコンサイズのズーム(docs/01 §4)。
    private void IconGridControl_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ApplyIconZoom(_iconZoomIndex + Math.Sign(e.Delta));
            e.Handled = true;
            return;
        }

        if (sender is DependencyObject d && FindDescendantScrollViewer(d) is { } sv)
        {
            ScrollIconGridTo(sv.VerticalOffset - e.Delta / 2.0);
            e.Handled = true;
        }
    }

    private void ApplyIconZoom(int newIndex)
    {
        newIndex = Math.Clamp(newIndex, 0, IconZoomLevels.Length - 1);
        if (newIndex == _iconZoomIndex) return;
        _iconZoomIndex = newIndex;

        double size = IconZoomLevels[newIndex];
        IconSize = size;
        IconItemWidth = size + 40; // 48px 時の実測値 88 に整合するテキスト余白
        UpdateIconGridScrollBar();
        ReloadRealizedIcons();
    }

    /// <summary>セッション/作業スペース復元用 — 指定サイズに最も近いズーム段階を適用する。</summary>
    public void SetIconZoom(double size)
    {
        int best = 0;
        for (int i = 1; i < IconZoomLevels.Length; i++)
        {
            if (Math.Abs(IconZoomLevels[i] - size) < Math.Abs(IconZoomLevels[best] - size)) best = i;
        }
        ApplyIconZoom(best);
    }

    /// <summary>ズーム変更後、実体化済み(可視)の行だけサムネイルを新しい解像度で取り直す。
    /// 未実体化の行はロード済みフラグをリセットするだけで、次に可視化された時に新解像度で読む。</summary>
    private void ReloadRealizedIcons()
    {
        if (IconGridControl.ItemsSource is not IEnumerable<FolderEntryViewModel> vms) return;

        int thumbSize = CurrentThumbnailSize;
        bool large = UseLargeIcons;
        var generator = IconGridControl.ItemContainerGenerator;
        foreach (var vm in vms)
        {
            vm.CancelLoad();
            if (generator.ContainerFromItem(vm) is ListBoxItem)
            {
                vm.BeginLoadIcon(thumbSize, large);
            }
        }
    }

    private static ScrollViewer? FindDescendantScrollViewer(DependencyObject root)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindDescendantScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    private void ListViewControl_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var control = (ListBox)sender;
        // 空白部分のダブルクリックで選択中フォルダーへ入ってしまわないよう、実際に行の上で
        // ダブルクリックされた時だけ反応する(エクスプローラー準拠)。
        if (e.OriginalSource is not DependencyObject d
            || ItemsControl.ContainerFromElement(control, d) is not ListBoxItem { DataContext: FolderEntryViewModel vm })
        {
            return;
        }

        if (vm.IsDirectory) Navigate(vm.FullPath);
        else Darask.Shell.LaunchService.Open(vm.FullPath);
    }

    private void ListViewControl_KeyDown(object sender, KeyEventArgs e)
    {
        // インライン名前変更中の TextBox からのバブリングでショートカットが誤発火しないようガード
        // (対策前は編集中に Delete を押すとファイル削除が発火する実バグがあった)。
        if (e.OriginalSource is TextBox) return;

        var control = (ListBox)sender;
        var mods = Keyboard.Modifiers;
        bool alt = mods == ModifierKeys.Alt;
        bool ctrl = mods == ModifierKeys.Control;
        bool ctrlShift = mods == (ModifierKeys.Control | ModifierKeys.Shift);

        if (alt && e.SystemKey == Key.Left) { GoBack(); e.Handled = true; }
        else if (alt && e.SystemKey == Key.Right) { GoForward(); e.Handled = true; }
        else if (alt && e.SystemKey == Key.Up) { GoUp(); e.Handled = true; }
        else if (alt && e.SystemKey == Key.Return)
        {
            var paths = SelectedPaths(control);
            if (paths.Count > 0) Darask.Shell.PropertiesService.ShowProperties(paths, OwnerHwnd());
            else if (_currentPath is not null) Darask.Shell.PropertiesService.ShowProperties([_currentPath], OwnerHwnd());
            e.Handled = true;
        }
        else if (e.Key == Key.Return && mods == ModifierKeys.None) { OpenSelection(control); e.Handled = true; }
        else if (e.Key == Key.Back) { GoBack(); e.Handled = true; }
        else if (e.Key == Key.F2 && control.SelectedItems.Count == 1
                 && control.SelectedItem is FolderEntryViewModel vm)
        {
            BeginRename(vm);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && control.SelectedItems.Count > 0)
        {
            Darask.Shell.ShellVerbService.Delete(SelectedPaths(control), OwnerHwnd());
            e.Handled = true;
        }
        else if (ctrlShift && e.Key == Key.C) { CopyPathsToClipboard(SelectedPaths(control)); e.Handled = true; }
        else if (ctrlShift && e.Key == Key.N) { CreateNewFolderAndRename(); e.Handled = true; }
        else if (ctrl && e.Key == Key.C && control.SelectedItems.Count > 0)
        {
            Darask.Shell.ShellVerbService.Copy(SelectedPaths(control), OwnerHwnd());
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.X && control.SelectedItems.Count > 0)
        {
            Darask.Shell.ShellVerbService.Cut(SelectedPaths(control), OwnerHwnd());
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.V && _currentPath is not null && Darask.Shell.ShellVerbService.CanPaste())
        {
            Darask.Shell.ShellVerbService.PasteInto(_currentPath, OwnerHwnd());
            e.Handled = true;
        }
    }

    private static List<string> SelectedPaths(ListBox control) =>
        control.SelectedItems.Cast<FolderEntryViewModel>().Select(v => v.FullPath).ToList();

    /// <summary>Enter / 「開く」— フォルダーはナビゲート(複数時は新規タブ)、ファイルは既定アプリ起動。</summary>
    private void OpenSelection(ListBox control)
    {
        var selected = control.SelectedItems.Cast<FolderEntryViewModel>().ToList();
        if (selected.Count == 0) return;

        if (selected.Count == 1)
        {
            if (selected[0].IsDirectory) Navigate(selected[0].FullPath);
            else Darask.Shell.LaunchService.Open(selected[0].FullPath);
            return;
        }

        // 大量選択で Enter を押した時の暴発ガード(エクスプローラーの「15 個を超える」警告準拠)。
        if (selected.Count > 15
            && MessageBox.Show($"選択した {selected.Count} 個の項目を一度に開きますか?", "darask-filer",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var item in selected)
        {
            if (item.IsDirectory) OpenInNewTabRequested?.Invoke(item.FullPath);
            else Darask.Shell.LaunchService.Open(item.FullPath);
        }
    }

    /// <summary>エクスプローラーの「パスのコピー」— 各パスを引用符で囲み改行区切りでコピーする。</summary>
    private static void CopyPathsToClipboard(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, paths.Select(p => $"\"{p}\"")));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // クリップボードが他プロセスにロックされている(CLIPBRD_E_CANT_OPEN)場合は諦める。
        }
    }

    private void FolderView_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1) { GoBack(); e.Handled = true; }
        else if (e.ChangedButton == MouseButton.XButton2) { GoForward(); e.Handled = true; }
    }

    // ホイールクリック(中クリック)で選択中の行を新しいタブで開く(docs/07 #27、エクスプローラー準拠)。
    private void EntryList_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        var control = (ListBox)sender;
        if (e.OriginalSource is not DependencyObject d) return;
        if (ItemsControl.ContainerFromElement(control, d) is not ListBoxItem { DataContext: FolderEntryViewModel { IsDirectory: true } vm }) return;

        OpenInNewTabRequested?.Invoke(vm.FullPath);
        e.Handled = true;
    }

    private IntPtr OwnerHwnd() => new System.Windows.Interop.WindowInteropHelper(Window.GetWindow(this)!).Handle;

    public void BeginRename(FolderEntryViewModel vm) => vm.IsRenaming = true;

    // TextBox.Loaded は行コンテナ生成時(Visibility=Collapsed のまま)に一度だけ発火し、
    // IsRenaming が true になって DataTrigger が Visibility=Visible に切り替えても再発火しない
    // (WPF の Loaded は「ビジュアルツリーに初めて載った時」であって「見えるようになった時」ではない)。
    // そのため Focus()/Select() は IsVisibleChanged で行う必要がある。
    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is not TextBox tb || e.NewValue is not true) return;

        // Visibility=Visible になった直後は該当行のレイアウトが未確定のことがあるため、
        // レイアウトパス完了後(Loaded 優先度)に Focus する。
        tb.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (tb.Visibility != Visibility.Visible) return;
            tb.Focus();
            if (tb.DataContext is FolderEntryViewModel { IsDirectory: false } vm && vm.EditName.LastIndexOf('.') is > 0 and var dot)
            {
                tb.Select(0, dot);
            }
            else
            {
                tb.SelectAll();
            }
        });
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: FolderEntryViewModel vm }) return;
        if (e.Key == Key.Enter) { CommitRename(vm); e.Handled = true; }
        else if (e.Key == Key.Escape) { vm.IsRenaming = false; e.Handled = true; }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: FolderEntryViewModel vm }) CommitRename(vm);
    }

    private void CommitRename(FolderEntryViewModel vm)
    {
        if (!vm.IsRenaming) return;
        vm.IsRenaming = false;

        string newName = vm.EditName.Trim();
        if (string.IsNullOrEmpty(newName) || newName == vm.Name || _currentPath is null) return;
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return;

        string oldPath = vm.FullPath;
        string newPath = Path.Combine(_currentPath, newName);
        try
        {
            if (vm.IsDirectory) Directory.Move(oldPath, newPath);
            else File.Move(oldPath, newPath);
            Refresh();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"名前の変更に失敗しました。\n{ex.Message}", "darask-filer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>「新しいフォルダー」作成 → 一覧再取得 → 自動でインライン名前変更モードへ(エクスプローラー準拠)。</summary>
    private void CreateNewFolderAndRename() =>
        _ = CreateNewItemAndRenameAsync(Darask.Shell.ShellVerbService.CreateNewFolder, "フォルダー");

    private void CreateNewTextFileAndRename() =>
        _ = CreateNewItemAndRenameAsync(Darask.Shell.ShellVerbService.CreateNewTextFile, "ファイル");

    private async Task CreateNewItemAndRenameAsync(Func<string, string> create, string kind)
    {
        if (_currentPath is null) return;

        string newPath;
        try
        {
            newPath = create(_currentPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"{kind}の作成に失敗しました。\n{ex.Message}", "darask-filer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await NavigateAsync(_currentPath, recordHistory: false);

        string newName = Path.GetFileName(newPath);
        if ((ActiveControl.ItemsSource as IEnumerable<FolderEntryViewModel>)?.FirstOrDefault(v => v.Name == newName) is { } vm)
        {
            ActiveControl.SelectedItem = vm;
            ActiveControl.ScrollIntoView(vm);
            BeginRename(vm);
        }
    }

    // ListViewControl と IconGridControl(どちらも ListBox 派生)で共有する右クリックメニュー
    // (docs/07 コンテキストメニュー整理)。右クリックした行が未選択なら単独選択に置き換える
    // (エクスプローラー準拠)。空白部分の右クリックはフォルダー自体のメニュー(貼り付け等)を出す。
    private void EntryList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var control = (ListBox)sender;
        ListBoxItem? container = null;
        if (e.OriginalSource is DependencyObject d)
        {
            container = ItemsControl.ContainerFromElement(control, d) as ListBoxItem;
            if (container?.DataContext is FolderEntryViewModel vm && !control.SelectedItems.Contains(vm))
            {
                control.SelectedItem = vm;
            }
        }

        if (_currentPath is null) return;

        if (container is null)
        {
            control.SelectedItems.Clear();
            control.ContextMenu = BuildEmptySpaceContextMenu();
        }
        else
        {
            var selected = control.SelectedItems.Cast<FolderEntryViewModel>().ToList();
            if (selected.Count == 0) return;
            control.ContextMenu = BuildEntryContextMenu(selected);
        }

        control.ContextMenu.IsOpen = true;
        e.Handled = true;
    }

    // エクスプローラー標準の並び: 開く → 切り取り/コピー/ショートカットの作成 →
    // クイックアクセスに追加(フォルダーのみ)→ 削除/名前の変更(単一選択のみ)→ プロパティ。
    private ContextMenu BuildEntryContextMenu(List<FolderEntryViewModel> selected)
    {
        var menu = new ContextMenu();
        MenuTheme.ApplyOpaque(menu);
        var paths = selected.Select(vm => vm.FullPath).ToList();
        bool single = selected.Count == 1;
        IntPtr hwnd = OwnerHwnd();

        if (single)
        {
            if (selected[0].IsDirectory) AddMenuItem(menu, "開く", () => Navigate(paths[0]));
            else AddMenuItem(menu, "開く", () => Darask.Shell.LaunchService.Open(paths[0]));
            if (!selected[0].IsDirectory)
            {
                AddMenuItem(menu, "プログラムから開く...", () => Darask.Shell.LaunchService.ShowOpenWithDialog(paths[0]));
            }
            menu.Items.Add(new Separator());
        }

        AddMenuItem(menu, "切り取り", () => Darask.Shell.ShellVerbService.Cut(paths, hwnd));
        AddMenuItem(menu, "コピー", () => Darask.Shell.ShellVerbService.Copy(paths, hwnd));
        AddMenuItem(menu, "パスのコピー", () => CopyPathsToClipboard(paths));
        if (single)
        {
            AddMenuItem(menu, "ショートカットの作成", () => Darask.Shell.ShellVerbService.CreateShortcut(paths[0], _currentPath!));
        }

        var dirPaths = paths.Where((_, i) => selected[i].IsDirectory).ToList();
        if (dirPaths.Count > 0)
        {
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "クイックアクセスに追加", () => AddToQuickAccessRequested?.Invoke(dirPaths));
        }

        menu.Items.Add(new Separator());
        AddMenuItem(menu, "削除", () => Darask.Shell.ShellVerbService.Delete(paths, hwnd));
        if (single)
        {
            AddMenuItem(menu, "名前の変更", () => BeginRename(selected[0]));
        }

        menu.Items.Add(new Separator());
        AddMenuItem(menu, "プロパティ", () => Darask.Shell.PropertiesService.ShowProperties(paths, hwnd));

        return menu;
    }

    // 空白部分の右クリック: 更新 → 新しいフォルダー → 貼り付け(有効時)→ プロパティ(フォルダー自体)。
    private ContextMenu BuildEmptySpaceContextMenu()
    {
        var menu = new ContextMenu();
        MenuTheme.ApplyOpaque(menu);
        string currentPath = _currentPath!;

        AddMenuItem(menu, "更新", Refresh);
        AddMenuItem(menu, "新しいフォルダー", CreateNewFolderAndRename);
        AddMenuItem(menu, "新しいテキスト ドキュメント", CreateNewTextFileAndRename);

        if (Darask.Shell.ShellVerbService.CanPaste())
        {
            menu.Items.Add(new Separator());
            AddMenuItem(menu, "貼り付け", () => Darask.Shell.ShellVerbService.PasteInto(currentPath, OwnerHwnd()));
        }

        menu.Items.Add(new Separator());
        AddMenuItem(menu, "ターミナルで開く", () => Darask.Shell.LaunchService.OpenTerminal(currentPath));
        AddMenuItem(menu, "パスのコピー", () => CopyPathsToClipboard([currentPath]));

        menu.Items.Add(new Separator());
        AddMenuItem(menu, "プロパティ", () => Darask.Shell.PropertiesService.ShowProperties([currentPath], OwnerHwnd()));

        return menu;
    }

    private static void AddMenuItem(ContextMenu menu, string header, Action action) => MenuTheme.AddItem(menu, header, action);
}
