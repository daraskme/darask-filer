using System.IO;
using System.Windows.Media.Imaging;
using Vanara.Windows.Shell;

namespace Darask.Shell;

/// <summary>
/// IShellItemImageFactory によるサムネイル取得(docs/05 §6, docs/07 M2)。
/// **必ずバックグラウンドスレッドから呼ぶこと**(CLAUDE.md 規則1)。UI スレッドで呼ばない。
/// OneDrive 等のクラウドプレースホルダーは呼び出し側が事前にフィルタしてハイドレートを防ぐこと
/// (CLAUDE.md 規則14 — このメソッド自体はハイドレート抑制フラグを渡さない)。
/// </summary>
public static class ThumbnailService
{
    public static BitmapSource? GetThumbnail(string path, int size)
    {
        try
        {
            using var item = ShellItem.Open(path);
            using var hBitmap = item.GetImage(new System.Drawing.Size(size, size), ShellItemGetImageOptions.ThumbnailOnly);
            if (hBitmap.IsInvalid || hBitmap.IsNull)
            {
                return null;
            }

            var src = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap.DangerousGetHandle(), IntPtr.Zero, System.Windows.Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException
                                       or UnauthorizedAccessException or InvalidOperationException)
        {
            // InvalidOperationException は「このファイル種別にサムネイルプロバイダーがない」正常系
            // (例: .txt)。呼び出し側は null を拡張子アイコンへのフォールバック合図として扱う。
            return null;
        }
    }
}
