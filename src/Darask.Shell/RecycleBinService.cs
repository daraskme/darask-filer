using System.Runtime.InteropServices.ComTypes;
using Vanara.PInvoke;
using Vanara.Windows.Shell;

namespace Darask.Shell;

/// <summary>
/// ごみ箱の1項目。<see cref="Item"/>(実 COM オブジェクト)は元に戻す/完全に削除する操作に必要なため
/// 一覧表示中は生かしたまま保持する — 呼び出し側は不要になったら <see cref="RecycleBinService.DisposeEntries"/>
/// で必ず破棄すること。
/// </summary>
public sealed record RecycleBinEntry(ShellItem Item, string Name, string? OriginalPath, DateTime? DeletedOn, ulong SizeBytes, bool IsFolder);

/// <summary>
/// ごみ箱の一覧・復元・完全削除(docs/07 #28)。ShellWorker 経由にしない理由は
/// PropertiesService.cs のコメントを参照(UI スレッドから直接同期呼び出しする)。
/// </summary>
public static class RecycleBinService
{
    public static List<RecycleBinEntry> GetItems()
    {
        var result = new List<RecycleBinEntry>();
        foreach (ShellItem item in RecycleBin.GetItems())
        {
            item.Properties.TryGetValue<string>(Ole32.PROPERTYKEY.System.Recycle.DeletedFrom, out var originalPath);

            // System.Recycle.DateDeleted は VT_FILETIME で格納されており、Vanara の
            // PropertyStore.TryGetValue<DateTime> は生の FILETIME を単純キャストしようとして
            // InvalidCastException を投げる(実機で確認)。FILETIME で受けて手動変換する。
            DateTime? deletedOn = null;
            if (item.Properties.TryGetValue<FILETIME>(Ole32.PROPERTYKEY.System.Recycle.DateDeleted, out var deletedFileTime))
            {
                long fileTime = ((long)deletedFileTime.dwHighDateTime << 32) | (uint)deletedFileTime.dwLowDateTime;
                if (fileTime > 0) deletedOn = DateTime.FromFileTimeUtc(fileTime);
            }

            ulong size = 0;
            bool isFolder = item.IsFolder;
            if (!isFolder)
            {
                item.Properties.TryGetValue<ulong>(Ole32.PROPERTYKEY.System.Size, out size);
            }

            result.Add(new RecycleBinEntry(item, item.Name ?? string.Empty, originalPath, deletedOn, size, isFolder));
        }
        return result;
    }

    public static void Restore(IEnumerable<RecycleBinEntry> entries) =>
        RecycleBin.Restore(entries.Select(e => e.Item), hideUI: false);

    /// <summary>ごみ箱内の項目を完全に削除する(二重ごみ箱送りにはならない — 元に戻せない)。</summary>
    public static void DeletePermanently(IEnumerable<RecycleBinEntry> entries) =>
        ShellFileOperations.Delete(entries.Select(e => e.Item),
            ShellFileOperations.OperationFlags.NoConfirmation | ShellFileOperations.OperationFlags.Silent);

    public static void Empty() => RecycleBin.Empty(hideUI: false, noConfirmation: true, noSound: false);

    public static void DisposeEntries(IEnumerable<RecycleBinEntry> entries)
    {
        foreach (var entry in entries) entry.Item.Dispose();
    }
}
