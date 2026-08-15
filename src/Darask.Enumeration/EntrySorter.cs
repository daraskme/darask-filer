namespace Darask.Enumeration;

public enum SortKey { Name, Size, LastWriteTime, CreationTime, Type }
public enum SortDirection { Ascending, Descending }

/// <summary>
/// フォルダー優先 + StrCmpLogicalW 自然順(名前列)でのソート(docs/01 §4, docs/06 §2)。
/// `SortDescriptions`/`CollectionView` は使わない(CLAUDE.md 規則5,6) — 呼び出し側は
/// 素の配列をこのメソッドでインプレースソートしてから ItemsSource を Reset すること。
/// </summary>
public static class EntrySorter
{
    // 単一スレッドの Array.Sort が実測で許容できる件数の目安。これを超えたら並列マージソートへ。
    private const int ParallelThreshold = 20_000;

    public static void Sort(FileSystemEntry[] entries, SortKey key, SortDirection direction)
    {
        int sign = direction == SortDirection.Ascending ? 1 : -1;

        Comparison<FileSystemEntry> byKey = key switch
        {
            SortKey.Name => (a, b) => NaturalSort.Compare(a.Name, b.Name),
            SortKey.Size => (a, b) => a.SizeBytes.CompareTo(b.SizeBytes),
            SortKey.LastWriteTime => (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc),
            SortKey.CreationTime => (a, b) => a.CreationTimeUtc.CompareTo(b.CreationTimeUtc),
            SortKey.Type => CompareByType,
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };

        // フォルダー優先は常に有効(Explorer 準拠)。同種同士は指定キーで比較。
        Comparison<FileSystemEntry> comparison = (a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            return sign * byKey(a, b);
        };

        // 名前列(StrCmpLogicalW)は P/Invoke 呼び出しのコストが比較のたびに乗るため、
        // 大量件数(実測 20 万件で単一スレッド Array.Sort が 200ms ゲートを超過)では
        // 並列マージソートに切り替える。サイズ/日付はマネージド比較のみで既に高速。
        if (key == SortKey.Name && entries.Length > ParallelThreshold)
        {
            ParallelMergeSort(entries, comparison);
        }
        else
        {
            Array.Sort(entries, comparison);
        }
    }

    /// <summary>「種類」列 — 拡張子(大文字小文字無視)→ 自然順名前の複合キー。
    /// Span ベースで拡張子を切り出すため文字列アロケーションなし(規則9の精神に準拠)。
    /// フォルダーは全て同一種類(「ファイル フォルダー」)なので名前のみで比較する
    /// (エクスプローラー準拠 — ドット入りフォルダー名を拡張子扱いしない)。</summary>
    private static int CompareByType(FileSystemEntry a, FileSystemEntry b)
    {
        if (a.IsDirectory) return NaturalSort.Compare(a.Name, b.Name);

        var extA = System.IO.Path.GetExtension(a.Name.AsSpan());
        var extB = System.IO.Path.GetExtension(b.Name.AsSpan());
        int c = extA.CompareTo(extB, StringComparison.OrdinalIgnoreCase);
        return c != 0 ? c : NaturalSort.Compare(a.Name, b.Name);
    }

    /// <summary>
    /// トップダウン再帰の並列マージソート。K-way PriorityQueue マージより単純で、
    /// 実測でも高速だった(100k 件の名前ソートが PriorityQueue 版の 150ms 台から改善)。
    /// 葉のチャンクサイズを下回るか並列分割の余地(depth)が尽きたら単一スレッド Array.Sort に切替。
    /// </summary>
    private static void ParallelMergeSort(FileSystemEntry[] entries, Comparison<FileSystemEntry> comparison)
    {
        var temp = new FileSystemEntry[entries.Length];
        int maxDepth = (int)Math.Ceiling(Math.Log2(Math.Max(1, Environment.ProcessorCount)));
        SortRecursive(entries, temp, 0, entries.Length, comparison, maxDepth);
    }

    private const int LeafSize = 5_000;

    private static void SortRecursive(FileSystemEntry[] arr, FileSystemEntry[] temp, int lo, int hi, Comparison<FileSystemEntry> cmp, int depth)
    {
        int len = hi - lo;
        if (len <= LeafSize || depth <= 0)
        {
            Array.Sort(arr, lo, len, Comparer<FileSystemEntry>.Create(cmp));
            return;
        }

        int mid = lo + len / 2;
        Parallel.Invoke(
            () => SortRecursive(arr, temp, lo, mid, cmp, depth - 1),
            () => SortRecursive(arr, temp, mid, hi, cmp, depth - 1));

        Merge(arr, temp, lo, mid, hi, cmp);
    }

    private static void Merge(FileSystemEntry[] arr, FileSystemEntry[] temp, int lo, int mid, int hi, Comparison<FileSystemEntry> cmp)
    {
        int i = lo, j = mid, k = lo;
        while (i < mid && j < hi)
        {
            temp[k++] = cmp(arr[i], arr[j]) <= 0 ? arr[i++] : arr[j++];
        }
        while (i < mid) temp[k++] = arr[i++];
        while (j < hi) temp[k++] = arr[j++];
        Array.Copy(temp, lo, arr, lo, hi - lo);
    }
}
