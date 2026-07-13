using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Darask.App;

/// <summary>ごみ箱の一覧・元に戻す・完全削除(docs/07 #28)。フォルダービューと違い項目数は通常
/// 少ないため、FastEnumerator の仮想化パイプラインは使わず素の ListView で十分。
/// ShellWorker 経由にしない理由は PropertiesService.cs のコメントを参照(UI スレッドから直接
/// 同期呼び出しする)。</summary>
public partial class RecycleBinView : UserControl, ITabContent
{
    private List<Darask.Shell.RecycleBinEntry> _entries = [];

    public RecycleBinView()
    {
        InitializeComponent();
        KeyDown += (_, e) => { if (e.Key == Key.F5) { Load(); e.Handled = true; } };
        Loaded += (_, _) => Load();
    }

    public void Shutdown() => DisposeEntries();

    private void Load()
    {
        DisposeEntries();
        StatusText.Text = "読み込み中...";

        _entries = Darask.Shell.RecycleBinService.GetItems();
        ItemsList.ItemsSource = _entries.Select(e => new RecycleBinItemViewModel(e)).ToList();
        StatusText.Text = $"{_entries.Count} 個の項目";
    }

    private void DisposeEntries()
    {
        if (_entries.Count > 0) Darask.Shell.RecycleBinService.DisposeEntries(_entries);
        _entries = [];
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => Load();

    private void EmptyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_entries.Count == 0) return;

        var result = MessageBox.Show("ごみ箱を空にしますか?\nこの操作は元に戻せません。", "darask-filer",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;

        Darask.Shell.RecycleBinService.Empty();
        Load();
    }

    private void ItemsList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d)
        {
            var container = ItemsControl.ContainerFromElement(ItemsList, d) as ListViewItem;
            if (container?.DataContext is RecycleBinItemViewModel vm && !ItemsList.SelectedItems.Contains(vm))
            {
                ItemsList.SelectedItem = vm;
            }
        }

        var selected = ItemsList.SelectedItems.Cast<RecycleBinItemViewModel>().ToList();
        if (selected.Count == 0) return;

        var menu = new ContextMenu();
        MenuTheme.ApplyOpaque(menu);

        MenuTheme.AddItem(menu, "元に戻す", () =>
        {
            Darask.Shell.RecycleBinService.Restore(selected.Select(s => s.Entry));
            Load();
        });

        MenuTheme.AddItem(menu, "完全に削除", () =>
        {
            var confirm = MessageBox.Show($"選択した {selected.Count} 個の項目を完全に削除しますか?\nこの操作は元に戻せません。",
                "darask-filer", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            Darask.Shell.RecycleBinService.DeletePermanently(selected.Select(s => s.Entry));
            Load();
        });

        ItemsList.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
