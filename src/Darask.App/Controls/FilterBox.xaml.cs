using System.Windows.Controls;

namespace Darask.App;

/// <summary>現在フォルダー内をファイル名で絞り込むテキストボックス(docs/07 #22)。</summary>
public partial class FilterBox : UserControl
{
    public event Action<string>? FilterChanged;

    public FilterBox()
    {
        InitializeComponent();
    }

    public void Clear() => TextBoxControl.Text = string.Empty;

    public void FocusBox()
    {
        TextBoxControl.Focus();
        TextBoxControl.SelectAll();
    }

    private void TextBoxControl_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterChanged?.Invoke(TextBoxControl.Text);
    }
}
