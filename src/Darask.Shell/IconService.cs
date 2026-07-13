using System.Windows.Media.Imaging;
using Vanara.PInvoke;
using static Vanara.PInvoke.Shell32;
using Imaging = System.Windows.Interop.Imaging;

namespace Darask.Shell;

/// <summary>
/// アイコン/サムネイル取得(docs/05 §6, docs/07 M2)。
/// 拡張子高速パス(SHGFI_USEFILEATTRIBUTES)はディスク I/O ゼロ — UI スレッドから呼んでもよい。
/// サムネイル取得(IShellItemImageFactory)は必ずバックグラウンドスレッドから呼ぶこと(CLAUDE.md 規則1)。
/// </summary>
public static class IconService
{
    /// <summary>
    /// 拡張子ベースの汎用アイコン(実ファイルの中身を読まない — ディスク I/O ゼロ)。
    /// 100k フォルダーの初回描画で使う(docs/07 M2 受け入れ: UI スレッドのディスク I/O ゼロ)。
    /// </summary>
    public static BitmapSource? GetExtensionIcon(string name, bool isDirectory, bool large)
    {
        var flags = SHGFI.SHGFI_ICON | SHGFI.SHGFI_USEFILEATTRIBUTES
                     | (large ? SHGFI.SHGFI_LARGEICON : SHGFI.SHGFI_SMALLICON);
        var attrs = isDirectory ? System.IO.FileAttributes.Directory : System.IO.FileAttributes.Normal;

        var shfi = new SHFILEINFO();
        IntPtr result = SHGetFileInfo(name, attrs, ref shfi, System.Runtime.InteropServices.Marshal.SizeOf(shfi), flags);
        if (result == IntPtr.Zero || shfi.hIcon.IsNull)
        {
            return null;
        }

        try
        {
            return ToFrozenBitmapSource(shfi.hIcon.DangerousGetHandle());
        }
        finally
        {
            User32.DestroyIcon(shfi.hIcon);
        }
    }

    /// <summary>
    /// 実パスに対する本物のアイコン取得(ディスク I/O あり — 必ずバックグラウンドスレッドから呼ぶこと)。
    /// `SHGFI_USEFILEATTRIBUTES` を付けないため、desktop.ini の IconResource カスタムフォルダーアイコンや
    /// .lnk のショートカット矢印オーバーレイ(docs/07 M2)がシェルの標準ロジックでそのまま反映される。
    /// </summary>
    public static BitmapSource? GetRealIcon(string fullPath, bool large)
    {
        var flags = SHGFI.SHGFI_ICON | (large ? SHGFI.SHGFI_LARGEICON : SHGFI.SHGFI_SMALLICON);

        var shfi = new SHFILEINFO();
        IntPtr result = SHGetFileInfo(fullPath, 0, ref shfi, System.Runtime.InteropServices.Marshal.SizeOf(shfi), flags);
        if (result == IntPtr.Zero || shfi.hIcon.IsNull)
        {
            return null;
        }

        try
        {
            return ToFrozenBitmapSource(shfi.hIcon.DangerousGetHandle());
        }
        finally
        {
            User32.DestroyIcon(shfi.hIcon);
        }
    }

    private static BitmapSource ToFrozenBitmapSource(IntPtr hIcon)
    {
        var src = Imaging.CreateBitmapSourceFromHIcon(hIcon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
    }
}
