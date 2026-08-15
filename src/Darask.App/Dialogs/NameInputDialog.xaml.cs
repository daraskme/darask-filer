using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Darask.App;

/// <summary>
/// 作業スペース名などの短い名前入力用モーダルダイアログ。
/// 空文字/空白のみは OK で確定できない。Enter=OK / Esc=キャンセル。
/// </summary>
public partial class NameInputDialog : FluentWindow
{
    public string ResultName => NameBox.Text.Trim();

    public NameInputDialog(Window owner, string prompt, string initialText = "")
    {
        InitializeComponent();
        Owner = owner;
        PromptText.Text = prompt;
        NameBox.Text = initialText;

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>OK で確定された名前を返す。キャンセル時は null。</summary>
    public static string? Show(Window owner, string prompt, string initialText = "")
    {
        var dialog = new NameInputDialog(owner, prompt, initialText);
        return dialog.ShowDialog() == true ? dialog.ResultName : null;
    }

    private void Confirm()
    {
        if (ResultName.Length == 0) return;
        DialogResult = true;
    }

    private void NameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { Confirm(); e.Handled = true; }
        else if (e.Key == Key.Escape) { DialogResult = false; e.Handled = true; }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Confirm();
    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
