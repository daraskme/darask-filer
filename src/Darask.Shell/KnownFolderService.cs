using Vanara.PInvoke;

namespace Darask.Shell;

/// <summary>
/// 既知フォルダー(SHGetKnownFolderPath)のパス解決。Environment.GetFolderPath に
/// 存在しないもの(ダウンロード等)や、ユーザーがリダイレクトしたフォルダーの実パスを
/// シェルの知識で解決する。
/// </summary>
public static class KnownFolderService
{
    /// <summary>「ダウンロード」フォルダーの実パス。解決失敗時は null。</summary>
    public static string? GetDownloadsPath()
    {
        try
        {
            return Shell32.SHGetKnownFolderPath(Shell32.KNOWNFOLDERID.FOLDERID_Downloads.Guid(),
                Shell32.KNOWN_FOLDER_FLAG.KF_FLAG_DEFAULT, default, out string path).Succeeded
                ? path
                : null;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or EntryPointNotFoundException)
        {
            return null;
        }
    }
}
