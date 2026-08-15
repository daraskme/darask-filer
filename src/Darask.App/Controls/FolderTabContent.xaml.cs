using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace Darask.App;

/// <summary>
/// 1タブ分のフォルダー表示一式(ツールバー・アドレスバー・フィルター・詳細/アイコングリッド・
/// プレビュー)。タブごとに独立したナビゲーション履歴を持つ(docs/07 #24)。
/// ナビゲーションペイン(左サイドバー)はタブ間で共有するため MainWindow 側に残す。
/// </summary>
public partial class FolderTabContent : UserControl, ITabContent
{
    public event Action<string>? PathChanged;
    public event Action<int, int, long>? SelectionSummaryChanged;
    public event Action<IReadOnlyList<string>>? AddToQuickAccessRequested;
    public event Action<string>? OpenInNewTabRequested;

    public FolderTabContent()
    {
        InitializeComponent();

        AddressBarControl.NavigateRequested += path => MainFolderView.Navigate(path);
        MainFolderView.PathChanged += path =>
        {
            AddressBarControl.SetPath(path);
            UpdateToolbarState();
            PathChanged?.Invoke(path);
        };
        MainFolderView.SelectionSummaryChanged += (total, selected, size) =>
            SelectionSummaryChanged?.Invoke(total, selected, size);
        MainFolderView.SingleSelectionChanged += vm => PreviewPaneControl.ShowPreview(vm);
        MainFolderView.AddToQuickAccessRequested += paths => AddToQuickAccessRequested?.Invoke(paths);
        MainFolderView.OpenInNewTabRequested += path => OpenInNewTabRequested?.Invoke(path);
        FilterBoxControl.FilterChanged += text => MainFolderView.FilterText = text;
        MainFolderView.PathChanged += _ => FilterBoxControl.Clear();
    }

    public string? CurrentPath => MainFolderView.CurrentPath;
    public string TabTitle => CurrentPath is { Length: > 0 } p
        ? (System.IO.Path.GetFileName(p.TrimEnd('\\')) is { Length: > 0 } name ? name : p)
        : "新しいタブ";

    /// <summary>セッション/作業スペース保存用のビュー状態(docs 外のユーザー要望機能)。</summary>
    public FolderViewMode ViewMode => MainFolderView.ViewMode;
    public double IconSize => MainFolderView.IconSize;
    public void SetIconZoom(double size) => MainFolderView.SetIconZoom(size);

    public void Navigate(string path) => MainFolderView.Navigate(path);
    public void Refresh() => MainFolderView.Refresh();
    public void FocusAddressEditMode() => AddressBarControl.FocusEditMode();
    public void FocusFilterBox() => FilterBoxControl.FocusBox();
    public void ToggleHidden() => MainFolderView.ShowHidden = !MainFolderView.ShowHidden;
    public void ToggleExtensions() => MainFolderView.ShowExtensions = !MainFolderView.ShowExtensions;
    public void SetViewMode(FolderViewMode mode)
    {
        MainFolderView.ViewMode = mode;
        ViewToggleIcon.Symbol = mode == FolderViewMode.Details ? SymbolRegular.Grid24 : SymbolRegular.List24;
    }

    /// <summary>タブが閉じられる時に呼ぶ(ディレクトリ監視ハンドルの解放)。</summary>
    public void Shutdown() => MainFolderView.Shutdown();

    private void UpdateToolbarState()
    {
        BackButton.IsEnabled = MainFolderView.CanGoBack;
        ForwardButton.IsEnabled = MainFolderView.CanGoForward;
        UpButton.IsEnabled = CurrentPath is { } p && System.IO.Directory.GetParent(p) is not null;
    }

    private void BackButton_Click(object sender, System.Windows.RoutedEventArgs e) => MainFolderView.GoBack();
    private void ForwardButton_Click(object sender, System.Windows.RoutedEventArgs e) => MainFolderView.GoForward();
    private void UpButton_Click(object sender, System.Windows.RoutedEventArgs e) => MainFolderView.GoUp();
    private void RefreshButton_Click(object sender, System.Windows.RoutedEventArgs e) => MainFolderView.Refresh();

    private void ViewToggleButton_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        var next = MainFolderView.ViewMode == FolderViewMode.Details ? FolderViewMode.IconGrid : FolderViewMode.Details;
        SetViewMode(next);
    }
}
