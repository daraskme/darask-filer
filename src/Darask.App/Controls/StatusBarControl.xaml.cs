using System.Windows.Controls;

namespace Darask.App;

/// <summary>
/// 項目数・選択数・選択サイズを表示するステータスバー(docs/06 §8, docs/07 M1)。
/// </summary>
public partial class StatusBarControl : UserControl
{
    public StatusBarControl()
    {
        InitializeComponent();
    }

    public void UpdateCounts(int totalCount, int selectedCount, long selectedSizeBytes)
    {
        ItemCountText.Text = $"{totalCount} 個の項目";
        SelectionSummaryText.Text = selectedCount > 0
            ? $"{selectedCount} 個選択 ({FormatSize(selectedSizeBytes)})"
            : string.Empty;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return unitIndex == 0 ? $"{bytes} {units[0]}" : $"{size:0.#} {units[unitIndex]}";
    }
}
