using Wpf.Ui.Controls;

namespace Darask.App;

/// <summary>ごみ箱一覧の表示用ラッパー(docs/07 #28)。</summary>
public sealed class RecycleBinItemViewModel(Darask.Shell.RecycleBinEntry entry)
{
    public Darask.Shell.RecycleBinEntry Entry { get; } = entry;

    public string Name => Entry.Name;
    public bool IsFolder => Entry.IsFolder;
    public SymbolRegular IconSymbol => IsFolder ? SymbolRegular.Folder24 : SymbolRegular.Document24;
    public string OriginalPathDisplay => Entry.OriginalPath ?? string.Empty;
    public string DeletedOnDisplay => Entry.DeletedOn?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? string.Empty;
    public string SizeDisplay => IsFolder ? string.Empty : FormatSize(Entry.SizeBytes);

    private static string FormatSize(ulong bytes)
    {
        string[] units = ["バイト", "KB", "MB", "GB", "TB"];
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
