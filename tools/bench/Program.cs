using System.Diagnostics;
using Darask.Enumeration;

namespace Darask.Tools.Bench;

// M17 で対 Explorer 比較ベンチハーネス(docs/07 M17)を本実装する。
// 暫定的に M1 のソート性能検証用サブコマンドを持つ。
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "sort")
        {
            return RunSortBenchmark(args);
        }
        if (args.Length > 0 && args[0] == "enum")
        {
            return RunEnumBenchmark(args);
        }

        Console.WriteLine("bench — placeholder (M17 で本実装)");
        Console.WriteLine("usage: bench sort --count <n>");
        Console.WriteLine("       bench enum --path <dir>");
        return 0;
    }

    /// <summary>
    /// 実ディスク上のフォルダーに対する FastEnumerator 列挙 + EntrySorter ソートの合計時間を計測する。
    /// docs/07 M1 受け入れ基準「100k フィクスチャが Enter 後 &lt; 300 ms で全面描画」のうち、
    /// UI 描画を除いたコアロジック(列挙+ソート)の時間を計測する(mkfixture 生成データで使う)。
    /// </summary>
    private static int RunEnumBenchmark(string[] args)
    {
        string? path = null;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--path") path = args[++i];
        }
        if (path is null)
        {
            Console.Error.WriteLine("usage: bench enum --path <dir>");
            return 1;
        }

        var swEnum = Stopwatch.StartNew();
        var entries = FastEnumerator.Enumerate(path).ToArray();
        swEnum.Stop();

        var swSort = Stopwatch.StartNew();
        EntrySorter.Sort(entries, SortKey.Name, SortDirection.Ascending);
        swSort.Stop();

        Console.WriteLine($"path={path}");
        Console.WriteLine($"entryCount={entries.Length}");
        Console.WriteLine($"enumMs={swEnum.ElapsedMilliseconds}");
        Console.WriteLine($"sortMs={swSort.ElapsedMilliseconds}");
        Console.WriteLine($"totalMs={swEnum.ElapsedMilliseconds + swSort.ElapsedMilliseconds}");
        return 0;
    }

    private static int RunSortBenchmark(string[] args)
    {
        int count = 200_000;
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] == "--count") count = int.Parse(args[++i]);
        }

        var rng = new Random(42);
        var entries = new FileSystemEntry[count];
        for (int i = 0; i < count; i++)
        {
            string name = i % 7 == 0
                ? $"日本語ファイル_{i:D6}.txt"
                : $"file_{i:D6}.txt";
            entries[i] = new FileSystemEntry(
                name,
                IsDirectory: false,
                SizeBytes: rng.Next(0, 10_000_000),
                CreationTimeUtc: DateTime.UtcNow.AddSeconds(-rng.Next(0, 1_000_000)),
                LastWriteTimeUtc: DateTime.UtcNow.AddSeconds(-rng.Next(0, 1_000_000)),
                Attributes: 0);
        }

        // シャッフル(ソート前に順不同にする)
        for (int i = count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (entries[i], entries[j]) = (entries[j], entries[i]);
        }

        var swName = Stopwatch.StartNew();
        var byName = (FileSystemEntry[])entries.Clone();
        EntrySorter.Sort(byName, SortKey.Name, SortDirection.Ascending);
        swName.Stop();

        var swSize = Stopwatch.StartNew();
        var bySize = (FileSystemEntry[])entries.Clone();
        EntrySorter.Sort(bySize, SortKey.Size, SortDirection.Ascending);
        swSize.Stop();

        var swDate = Stopwatch.StartNew();
        var byDate = (FileSystemEntry[])entries.Clone();
        EntrySorter.Sort(byDate, SortKey.LastWriteTime, SortDirection.Ascending);
        swDate.Stop();

        Console.WriteLine($"count={count}");
        Console.WriteLine($"sortByNameMs={swName.ElapsedMilliseconds}");
        Console.WriteLine($"sortBySizeMs={swSize.ElapsedMilliseconds}");
        Console.WriteLine($"sortByDateMs={swDate.ElapsedMilliseconds}");
        return 0;
    }
}
