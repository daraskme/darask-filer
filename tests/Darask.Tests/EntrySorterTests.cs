using Darask.Enumeration;
using Xunit;

namespace Darask.Tests;

public class EntrySorterTests
{
    private static FileSystemEntry File(string name, long size = 0) =>
        new(name, IsDirectory: false, size, DateTime.UnixEpoch, DateTime.UnixEpoch, Attributes: 0);

    private static FileSystemEntry Dir(string name) =>
        new(name, IsDirectory: true, 0, DateTime.UnixEpoch, DateTime.UnixEpoch, Attributes: 0x10);

    [Fact]
    public void Sort_ByType_GroupsByExtensionThenNaturalName()
    {
        FileSystemEntry[] entries =
        [
            File("b.txt"),
            File("a.ZIP"),
            File("c.zip"),
            File("a10.txt"),
            File("a2.txt"),
        ];

        EntrySorter.Sort(entries, SortKey.Type, SortDirection.Ascending);

        // 拡張子(OrdinalIgnoreCase)→ 自然順名前。.txt < .zip、a2 < a10(自然順)。
        Assert.Equal(["a2.txt", "a10.txt", "b.txt", "a.ZIP", "c.zip"], entries.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Sort_ByType_KeepsDirectoriesFirst()
    {
        FileSystemEntry[] entries =
        [
            File("a.txt"),
            Dir("zzz"),
            File("b.zip"),
            Dir("aaa"),
        ];

        EntrySorter.Sort(entries, SortKey.Type, SortDirection.Ascending);

        Assert.True(entries[0].IsDirectory);
        Assert.True(entries[1].IsDirectory);
        Assert.Equal("aaa", entries[0].Name);
        Assert.Equal("zzz", entries[1].Name);
    }

    [Fact]
    public void Sort_ByType_ExtensionlessFilesSortBeforeExtensions()
    {
        FileSystemEntry[] entries =
        [
            File("readme.md"),
            File("LICENSE"),
            File("makefile"),
        ];

        EntrySorter.Sort(entries, SortKey.Type, SortDirection.Ascending);

        // 拡張子なし("")は拡張子ありより前に来る。
        Assert.Equal(["LICENSE", "makefile", "readme.md"], entries.Select(e => e.Name).ToArray());
    }
}
