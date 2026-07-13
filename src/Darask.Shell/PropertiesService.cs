using Vanara.PInvoke;
using Vanara.Windows.Shell;

namespace Darask.Shell;

/// <summary>
/// 右クリック「プロパティ」ダイアログ表示(docs/07 #20)。
/// エクスプローラーと同じ IContextMenu 経由の "properties" verb 呼び出しなので、
/// 複数選択時もエクスプローラー同様に1つのダイアログに複数タブとしてまとまる。
///
/// CLAUDE.md 絶対規則2は IContextMenu 呼び出しを専用 STA ワーカースレッドへ委譲することを求めるが、
/// 実装を試みたところ「WPF の UI スレッド(既に STA)から新しい STA Dispatcher スレッドを生成して
/// 同期的に待つ」という構成自体が本アプリでは確実にデッドロックすることを確認した
/// (最小再現でも Dispatcher.CurrentDispatcher 呼び出しだけで UI スレッドがフリーズし、
/// COM/シェル呼び出しの有無・ContextMenu のネストしたポンプの有無に関係なく再現— 詳細は
/// PROGRESS.md の該当エントリを参照)。原因未特定のため、当面は UI スレッドから直接
/// 同期呼び出しする方式に留める(実際に確実に動作する)。
/// </summary>
public static class PropertiesService
{
    public static void ShowProperties(IReadOnlyList<string> paths, IntPtr ownerHwnd)
    {
        if (paths.Count == 0) return;

        var items = new ShellItem[paths.Count];
        for (int i = 0; i < paths.Count; i++) items[i] = new ShellItem(paths[i]);

        try
        {
            using var menu = ShellContextMenu.CreateFromItems(items, out System.IDisposable keepAlive);
            try
            {
                menu.InvokeVerb("properties", ShowWindowCommand.SW_SHOWNORMAL, (HWND)ownerHwnd);
            }
            finally
            {
                keepAlive.Dispose();
            }
        }
        finally
        {
            foreach (var item in items) item.Dispose();
        }
    }
}
