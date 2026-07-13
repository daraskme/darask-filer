using Darask.Enumeration;
using Xunit;

namespace Darask.Tests;

public class FastEnumeratorTests
{
    [Fact]
    public void Enumerate_ReturnsFilesAndDirectories_ExcludingDotAndDotDot()
    {
        string dir = CreateTempTree();
        try
        {
            var entries = FastEnumerator.Enumerate(dir).ToList();

            Assert.Equal(3, entries.Count(e => !e.IsDirectory));
            Assert.Equal(1, entries.Count(e => e.IsDirectory));
            Assert.DoesNotContain(entries, e => e.Name is "." or "..");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Enumerate_ReportsCorrectFileSize()
    {
        string dir = CreateTempTree();
        try
        {
            var entries = FastEnumerator.Enumerate(dir).ToList();
            var a = entries.Single(e => e.Name == "a.txt");
            Assert.Equal(5, a.SizeBytes); // "hello" = 5 bytes
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Enumerate_HandlesJapaneseAndSurrogatePairNames()
    {
        string dir = Directory.CreateTempSubdirectory("darask-enum-ja-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "日本語ファイル名.txt"), "x");
            File.WriteAllText(Path.Combine(dir, "絵文字😀写真.txt"), "x");

            var entries = FastEnumerator.Enumerate(dir).Select(e => e.Name).ToHashSet();

            Assert.Contains("日本語ファイル名.txt", entries);
            Assert.Contains("絵文字😀写真.txt", entries);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Enumerate_NonExistentDirectory_ReturnsEmpty()
    {
        string dir = Path.Combine(Path.GetTempPath(), "darask-enum-does-not-exist-" + Guid.NewGuid());
        var entries = FastEnumerator.Enumerate(dir).ToList();
        Assert.Empty(entries);
    }

    private static string CreateTempTree()
    {
        string dir = Directory.CreateTempSubdirectory("darask-enum-").FullName;
        File.WriteAllText(Path.Combine(dir, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(dir, "b.txt"), "world!");
        File.WriteAllText(Path.Combine(dir, "c.dat"), "xyz");
        Directory.CreateDirectory(Path.Combine(dir, "subdir"));
        return dir;
    }
}
