using Darask.Shell;
using Xunit;

namespace Darask.Tests;

/// <summary>
/// Vanara 5.0.5 の ShellItemImages / IShellItemImageFactory 経路のスモークテスト
/// (docs/07 M2: 「壊れていたら 4.2.1 へ」の判断材料)。実 STA スレッドが必要な API を含むため
/// [STAThread] 相当が要る場合は個別に対応する — 現状の GetImage/SHGetFileInfo は MTA でも動く。
/// </summary>
public class ShellSmokeTests
{
    [Fact]
    public void IconService_GetExtensionIcon_ReturnsBitmapForKnownExtension()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "darask-icon-smoke.txt");
        File.WriteAllText(tempFile, "x");
        try
        {
            var icon = IconService.GetExtensionIcon(tempFile, isDirectory: false, large: true);
            Assert.NotNull(icon);
            Assert.True(icon!.PixelWidth > 0);
            Assert.True(icon.PixelHeight > 0);
            Assert.True(icon.IsFrozen);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void IconService_GetExtensionIcon_ReturnsBitmapForDirectory()
    {
        var icon = IconService.GetExtensionIcon("SomeFolderName", isDirectory: true, large: false);
        Assert.NotNull(icon);
        Assert.True(icon!.IsFrozen);
    }

    [Fact]
    public void ThumbnailService_GetThumbnail_TextFile_ReturnsNullWithoutThrowing()
    {
        // .txt にはサムネイルプロバイダーがない — 例外を投げず null に正規化されることを確認。
        string tempFile = Path.Combine(Path.GetTempPath(), "darask-thumb-smoke.txt");
        File.WriteAllText(tempFile, "hello thumbnail smoke test");
        try
        {
            var thumb = ThumbnailService.GetThumbnail(tempFile, 64);
            Assert.Null(thumb);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void ThumbnailService_GetThumbnail_BmpFile_ReturnsFrozenBitmap()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "darask-thumb-smoke.bmp");
        File.WriteAllBytes(tempFile, CreateMinimalBmp(16, 16));
        try
        {
            var thumb = ThumbnailService.GetThumbnail(tempFile, 64);
            Assert.NotNull(thumb);
            Assert.True(thumb!.IsFrozen);
            Assert.True(thumb.PixelWidth > 0);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void IconService_GetRealIcon_ReturnsBitmapForRealDirectory()
    {
        // desktop.ini カスタムアイコン反映パス(docs/07 M2)。カスタムアイコンなしの通常フォルダーでも
        // 例外なくアイコンが返ることを確認する(実際のカスタムアイコン反映は GUI 目視で確認)。
        string tempDir = Directory.CreateTempSubdirectory("darask-realicon-").FullName;
        try
        {
            var icon = IconService.GetRealIcon(tempDir, large: false);
            Assert.NotNull(icon);
            Assert.True(icon!.IsFrozen);
        }
        finally
        {
            Directory.Delete(tempDir);
        }
    }

    [Fact]
    public void IconService_GetRealIcon_ReturnsBitmapForRealFile()
    {
        string tempFile = Path.Combine(Path.GetTempPath(), "darask-realicon-smoke.txt");
        File.WriteAllText(tempFile, "x");
        try
        {
            var icon = IconService.GetRealIcon(tempFile, large: false);
            Assert.NotNull(icon);
            Assert.True(icon!.IsFrozen);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void IconService_GetRealIcon_ReturnsShortcutArrowIconForLnk()
    {
        string tempDir = Directory.CreateTempSubdirectory("darask-lnk-smoke-").FullName;
        string lnkPath = Path.Combine(tempDir, "shortcut.lnk");
        string targetPath = Path.Combine(tempDir, "target.txt");
        File.WriteAllText(targetPath, "target");
        try
        {
            CreateShellLink(lnkPath, targetPath);
            Assert.True(File.Exists(lnkPath));

            var icon = IconService.GetRealIcon(lnkPath, large: false);
            Assert.NotNull(icon);
            Assert.True(icon!.IsFrozen);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>Vanara.Windows.Shell.ShellLink 経由で .lnk を作成する(.NET 標準 API がないため)。</summary>
    private static void CreateShellLink(string lnkPath, string targetPath)
    {
        using var link = Vanara.Windows.Shell.ShellLink.Create(lnkPath, targetPath);
    }

    private static byte[] CreateMinimalBmp(int width, int height)
    {
        int rowSize = ((width * 3 + 3) / 4) * 4;
        int pixelArraySize = rowSize * height;
        int fileSize = 14 + 40 + pixelArraySize;

        using var ms = new MemoryStream(fileSize);
        using var w = new BinaryWriter(ms);
        w.Write((byte)'B'); w.Write((byte)'M');
        w.Write(fileSize); w.Write(0); w.Write(14 + 40);
        w.Write(40); w.Write(width); w.Write(height);
        w.Write((short)1); w.Write((short)24); w.Write(0);
        w.Write(pixelArraySize); w.Write(2835); w.Write(2835); w.Write(0); w.Write(0);
        for (int i = 0; i < pixelArraySize; i++) w.Write((byte)0x80);
        w.Flush();
        return ms.ToArray();
    }
}
