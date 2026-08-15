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

    private string? _driveRoot;

    /// <summary>現在パスのドライブ空き容量を表示する。DriveInfo は stat 系 I/O のため
    /// UI スレッドで直接呼ばない(CLAUDE.md 規則1) — バックグラウンドで取得してディスパッチする。</summary>
    public void UpdateDriveSpace(string currentPath)
    {
        string? root;
        try
        {
            root = System.IO.Path.GetPathRoot(currentPath);
        }
        catch (ArgumentException)
        {
            root = null;
        }

        if (root is null or "" || !root.EndsWith('\\'))
        {
            // UNC 等ドライブレターのないパスは表示しない(DriveInfo 非対応)。
            _driveRoot = null;
            DriveSpaceText.Text = string.Empty;
            return;
        }

        if (string.Equals(root, _driveRoot, StringComparison.OrdinalIgnoreCase)) return;
        _driveRoot = root;

        string captured = root;
        _ = System.Threading.Tasks.Task.Run(() =>
        {
            string text;
            try
            {
                var drive = new System.IO.DriveInfo(captured);
                text = $"空き領域: {FormatSize(drive.AvailableFreeSpace)} / {FormatSize(drive.TotalSize)}";
            }
            catch (Exception ex) when (ex is System.IO.IOException or ArgumentException or UnauthorizedAccessException)
            {
                text = string.Empty;
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (string.Equals(captured, _driveRoot, StringComparison.OrdinalIgnoreCase))
                {
                    DriveSpaceText.Text = text;
                }
            });
        });
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
