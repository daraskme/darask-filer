namespace Darask.Enumeration;

/// <summary>
/// 1件のディレクトリエントリ(名前・種別・サイズ・タイムスタンプ・属性)。
/// NtQueryDirectoryFileEx / FindFirstFileExW どちらのバックエンドから来ても同じ形。
/// </summary>
public readonly record struct FileSystemEntry(
    string Name,
    bool IsDirectory,
    long SizeBytes,
    DateTime CreationTimeUtc,
    DateTime LastWriteTimeUtc,
    uint Attributes)
{
    private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
    private const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;
    private const uint FILE_ATTRIBUTE_HIDDEN = 0x2;
    private const uint FILE_ATTRIBUTE_SYSTEM = 0x4;

    public bool IsReparsePoint => (Attributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;

    /// <summary>OneDrive 等のクラウドプレースホルダー。列挙・サムネイルで絶対にハイドレートしない(CLAUDE.md 規則14)。</summary>
    public bool IsCloudPlaceholder => (Attributes & FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS) != 0;

    public bool IsHidden => (Attributes & FILE_ATTRIBUTE_HIDDEN) != 0;
    public bool IsSystem => (Attributes & FILE_ATTRIBUTE_SYSTEM) != 0;
}
