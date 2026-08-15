using System.IO;
using System.Windows;
using Vanara.PInvoke;
using Vanara.Windows.Shell;

namespace Darask.Shell;

/// <summary>
/// エクスプローラー標準の右クリック操作(切り取り/コピー/貼り付け/削除/ショートカット作成)。
/// 切り取り・コピー・削除は IContextMenu 経由でエクスプローラー本体と同じ動作(クリップボード
/// 相互運用・ごみ箱送り・ネイティブ進捗ダイアログ・アンドゥ)にする(docs/07 コンテキストメニュー整理)。
/// ShellWorker 経由にしない理由は PropertiesService.cs のコメントを参照。
/// </summary>
public static class ShellVerbService
{
    public static void Cut(IReadOnlyList<string> paths, IntPtr ownerHwnd) => Invoke(paths, ownerHwnd, m => m.InvokeCut());
    public static void Copy(IReadOnlyList<string> paths, IntPtr ownerHwnd) => Invoke(paths, ownerHwnd, m => m.InvokeCopy());
    public static void Delete(IReadOnlyList<string> paths, IntPtr ownerHwnd) => Invoke(paths, ownerHwnd, m => m.InvokeDelete());

    /// <summary>フォルダー自身の右クリック「貼り付け」— クリップボードの内容をそのフォルダーへ貼り付ける。</summary>
    public static void PasteInto(string folderPath, IntPtr ownerHwnd) => Invoke([folderPath], ownerHwnd, m => m.InvokePaste());

    public static bool CanPaste() => System.Windows.Clipboard.ContainsFileDropList();

    private static void Invoke(IReadOnlyList<string> paths, IntPtr ownerHwnd, Action<ShellContextMenu> action)
    {
        if (paths.Count == 0) return;

        var items = new ShellItem[paths.Count];
        for (int i = 0; i < paths.Count; i++) items[i] = new ShellItem(paths[i]);

        var menu = ShellContextMenu.CreateFromItems(items, out System.IDisposable keepAlive);
        action(menu);

        // IContextMenu::InvokeCommand はコピー/切り取り/貼り付け/削除の verb を呼んだ直後、
        // シェル側の実処理(クリップボード確定・ファイルコピー等)がまだ完了していない状態で
        // 制御を返すことがある。呼び出し直後に ShellItem/ShellContextMenu を同期的に Dispose
        // すると、その処理が参照している COM オブジェクトを解放してしまいヒープ破損
        // (0xc0000374)でクラッシュすることを実機で確認した。UI スレッドがアイドルになる
        // (=保留中のメッセージ/コールバックが捌けた)まで解放を遅延させることで回避する。
        Application.Current?.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () =>
        {
            keepAlive.Dispose();
            menu.Dispose();
            foreach (var item in items) item.Dispose();
        });
    }

    /// <summary>「新しいフォルダー」を作成する(未使用の連番付き名前を自動選定)。</summary>
    public static string CreateNewFolder(string parentPath)
    {
        const string baseName = "新しいフォルダー";
        string name = baseName;
        int n = 2;
        while (Directory.Exists(Path.Combine(parentPath, name)))
        {
            name = $"{baseName} ({n++})";
        }

        string fullPath = Path.Combine(parentPath, name);
        Directory.CreateDirectory(fullPath);
        return fullPath;
    }

    /// <summary>「新しいテキスト ドキュメント」を作成する(未使用の連番付き名前を自動選定)。</summary>
    public static string CreateNewTextFile(string parentPath)
    {
        const string baseName = "新しいテキスト ドキュメント";
        string name = baseName + ".txt";
        int n = 2;
        while (File.Exists(Path.Combine(parentPath, name)))
        {
            name = $"{baseName} ({n++}).txt";
        }

        string fullPath = Path.Combine(parentPath, name);
        using (File.Create(fullPath)) { }
        return fullPath;
    }

    /// <summary>「ショートカットの作成」— 対象と同じフォルダーに .lnk を作成する。</summary>
    public static string CreateShortcut(string targetPath, string parentPath)
    {
        string baseName = $"{Path.GetFileName(targetPath.TrimEnd('\\'))} - ショートカット";
        string linkPath = Path.Combine(parentPath, baseName + ".lnk");
        int n = 2;
        while (File.Exists(linkPath))
        {
            linkPath = Path.Combine(parentPath, $"{baseName} ({n++}).lnk");
        }

        string? workingDirectory = File.Exists(targetPath) ? Path.GetDirectoryName(targetPath) : targetPath;
        using var link = ShellLink.Create(linkPath, targetPath, null, workingDirectory, null);
        return linkPath;
    }
}
