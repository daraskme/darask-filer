using System.Collections.Concurrent;
using Darask.Enumeration;
using Xunit;

namespace Darask.Tests;

public class DirectoryWatcherTests
{
    [Fact]
    public void Watcher_DetectsFileCreation()
    {
        string dir = Directory.CreateTempSubdirectory("darask-watch-").FullName;
        try
        {
            var events = new ConcurrentQueue<FileChangeEvent>();
            using var ready = new ManualResetEventSlim(false);
            using var watcher = new DirectoryWatcher(dir);
            watcher.Changed += e =>
            {
                events.Enqueue(e);
                ready.Set();
            };

            File.WriteAllText(Path.Combine(dir, "new.txt"), "hi");

            Assert.True(ready.Wait(TimeSpan.FromSeconds(5)), "no change event observed within timeout");
            Assert.Contains(events, e => e.Kind == FileChangeKind.Created && e.Name == "new.txt");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Watcher_DetectsRename()
    {
        string dir = Directory.CreateTempSubdirectory("darask-watch-").FullName;
        try
        {
            string original = Path.Combine(dir, "before.txt");
            File.WriteAllText(original, "hi");

            var events = new ConcurrentQueue<FileChangeEvent>();
            using var sawNewName = new ManualResetEventSlim(false);
            using var watcher = new DirectoryWatcher(dir);
            watcher.Changed += e =>
            {
                events.Enqueue(e);
                if (e.Kind == FileChangeKind.RenamedNewName) sawNewName.Set();
            };

            File.Move(original, Path.Combine(dir, "after.txt"));

            Assert.True(sawNewName.Wait(TimeSpan.FromSeconds(5)), "no rename-new-name event observed within timeout");
            Assert.Contains(events, e => e.Kind == FileChangeKind.RenamedOldName && e.Name == "before.txt");
            Assert.Contains(events, e => e.Kind == FileChangeKind.RenamedNewName && e.Name == "after.txt");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Watcher_DisposeStopsBackgroundThread()
    {
        string dir = Directory.CreateTempSubdirectory("darask-watch-").FullName;
        try
        {
            var watcher = new DirectoryWatcher(dir);
            watcher.Dispose(); // ハング/例外なく戻ってくることを確認
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
