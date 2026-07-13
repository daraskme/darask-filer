using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Darask.App;

public sealed record BreadcrumbSegment(string Name, string FullPath);

/// <summary>
/// パンくず(表示)⇔ テキスト編集(Ctrl+L/クリック)複合コントロール(docs/06 §4)。
/// v1 簡易実装: BreadcrumbBar/AutoSuggestBox の代わりにシンプルな Button 列 + TextBox。
/// </summary>
public partial class AddressBar : UserControl
{
    public event Action<string>? NavigateRequested;

    private string _currentPath = string.Empty;

    public AddressBar()
    {
        InitializeComponent();
    }

    public void SetPath(string path)
    {
        _currentPath = path;
        var segments = new List<BreadcrumbSegment>();

        var root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root))
        {
            segments.Add(new BreadcrumbSegment(root.TrimEnd('\\'), root));
            string rest = path[root.Length..];
            string accum = root;
            foreach (string part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                accum = Path.Combine(accum, part);
                segments.Add(new BreadcrumbSegment(part, accum));
            }
        }

        BreadcrumbHost.ItemsSource = segments;
        EditBox.Text = path;
    }

    public void FocusEditMode()
    {
        BreadcrumbHost.Visibility = Visibility.Collapsed;
        EditBox.Visibility = Visibility.Visible;
        EditBox.Text = _currentPath;
        EditBox.Focus();
        EditBox.SelectAll();
    }

    private void ShowBreadcrumbMode()
    {
        EditBox.Visibility = Visibility.Collapsed;
        BreadcrumbHost.Visibility = Visibility.Visible;
    }

    private void BreadcrumbSegment_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Wpf.Ui.Controls.Button { Tag: string path })
        {
            NavigateRequested?.Invoke(path);
        }
    }

    /// <summary>パンくずの空白部分クリックで編集モードへ(セグメントボタン自体のクリックは
    /// ButtonBase が MouseLeftButtonUp を処理済みにするためここには到達しない)。</summary>
    private void Grid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (BreadcrumbHost.Visibility == Visibility.Visible)
        {
            FocusEditMode();
        }
    }

    private void EditBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            string path = EditBox.Text.Trim().Trim('"');
            path = Environment.ExpandEnvironmentVariables(path);
            NavigateRequested?.Invoke(path);
            ShowBreadcrumbMode();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ShowBreadcrumbMode();
            e.Handled = true;
        }
    }

    private void EditBox_LostKeyboardFocus(object sender, RoutedEventArgs e)
    {
        ShowBreadcrumbMode();
    }
}
