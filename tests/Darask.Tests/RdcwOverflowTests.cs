using Darask.Enumeration;
using Xunit;

namespace Darask.Tests;

/// <summary>
/// docs/07 M1 受け入れ基準: 「10k ファイルバースト生成し RDCW オーバーフロー経路を強制 →
/// 再走査後、列挙オラクルとの差分ゼロ」。
/// </summary>
public class RdcwOverflowTests
{
    [Fact]
    public void Watcher_OverflowsOnBurst_AndFastEnumeratorMatchesOracleAfterRescan()
    {
        string dir = Directory.CreateTempSubdirectory("darask-overflow-").FullName;
        try
        {
            var overflowed = new ManualResetEventSlim(false);
            using var watcher = new DirectoryWatcher(dir);
            watcher.Overflowed += () => overflowed.Set();

            const int fileCount = 10_000;
            // 逐次作成だと 1 件ずつのディスク I/O 待ちの間にオーバーラップド I/O のコールバックが
            // 都度バッファを消費してしまい、64KB を溢れさせるだけの「同時性」が生まれない(実測で
            // 判明)。並列作成で瞬間的なバーストにしてオーバーフロー経路を確実に踏む。
            Parallel.For(0, fileCount, i =>
            {
                File.WriteAllText(Path.Combine(dir, $"burst_{i:D5}.txt"), string.Empty);
            });

            bool sawOverflow = overflowed.Wait(TimeSpan.FromSeconds(15));
            Assert.True(sawOverflow, "64KB バッファに対し 10k 件の同時イベントはオーバーフローするはず(docs/02 §5.4)");

            // 再走査(FastEnumerator)が実ディスク状態(オラクル: System.IO)と完全一致することを確認。
            var actual = FastEnumerator.Enumerate(dir)
                .Select(e => e.Name)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();
            var oracle = Directory.GetFileSystemEntries(dir)
                .Select(Path.GetFileName)
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(oracle, actual);
            Assert.Equal(fileCount, actual.Length);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
