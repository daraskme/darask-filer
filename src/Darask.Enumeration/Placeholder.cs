namespace Darask.Enumeration;

// M1 で NtQueryDirectoryFileEx ベースの高速リスティングを実装する(docs/02 §5, docs/07 M1)。
// NtQueryDirectoryFileEx は WDK メタデータ(Microsoft.Windows.WDK.Win32Metadata)が必要 — docs/02 §1。
internal static class ProjectMarker
{
    public const string Name = "Darask.Enumeration";
}
