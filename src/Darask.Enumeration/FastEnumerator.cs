using System.Buffers;
using Windows.Wdk.Storage.FileSystem;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using WdkPInvoke = Windows.Wdk.PInvoke;

namespace Darask.Enumeration;

/// <summary>
/// 高速ディレクトリ列挙(docs/02 §5, docs/07 M1)。第一選択は NtQueryDirectoryFileEx
/// (FindFirstFileEx 比 ~40% 効率、digest 実測)、失敗時(権限・非対応ファイルシステム等)は
/// FindFirstFileExW + FIND_FIRST_EX_LARGE_FETCH へフォールバックする。
/// UI スレッドで呼ばない(CLAUDE.md 規則1)— 呼び出し側がバックグラウンドスレッドで回すこと。
/// </summary>
public static class FastEnumerator
{
    private const uint SL_RESTART_SCAN = 0x00000001;
    private const uint STATUS_NO_MORE_FILES = 0x80000006;
    private const uint FILE_LIST_DIRECTORY = 0x0001;
    // NtQueryDirectoryFileEx のバッチサイズ。64KB だと 10万件規模のフォルダーでシステムコール
    // 往復が多発してボトルネックになることが実測でわかったため 1MB に拡大した。
    private const int BufferSize = 1024 * 1024;

    public static IEnumerable<FileSystemEntry> Enumerate(string directoryPath)
    {
        string longPath = LongPath.Ensure(directoryPath);

        var handle = PInvoke.CreateFile(
            longPath,
            FILE_LIST_DIRECTORY,
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            lpSecurityAttributes: null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS,
            hTemplateFile: null);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            return EnumerateFallback(longPath);
        }

        return EnumerateViaNtQuery(handle, longPath);
    }

    private static IEnumerable<FileSystemEntry> EnumerateViaNtQuery(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string longPathForFallback)
    {
        using (handle)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            try
            {
                bool restart = true;
                bool ntQueryFailedImmediately = true;
                bool anyEntryReturned = false;

                while (true)
                {
                    uint queryFlags = restart ? SL_RESTART_SCAN : 0;
                    restart = false;

                    NTSTATUS status;
                    unsafe
                    {
                        status = WdkPInvoke.NtQueryDirectoryFileEx(
                            (HANDLE)handle.DangerousGetHandle(),
                            default,
                            default,
                            null,
                            out _,
                            buffer.AsSpan(0, BufferSize),
                            FILE_INFORMATION_CLASS.FileFullDirectoryInformation,
                            queryFlags,
                            null);
                    }

                    if ((uint)status == STATUS_NO_MORE_FILES)
                    {
                        yield break;
                    }

                    if (status != 0)
                    {
                        // 初回呼び出しで一件も返さずに失敗した場合のみフォールバックする値打ちがある
                        // (途中まで返した後の失敗はフォールバックしても不整合になるため諦める)。
                        break;
                    }

                    ntQueryFailedImmediately = false;
                    int offset = 0;
                    while (true)
                    {
                        int nextEntryOffset;
                        FileSystemEntry? entry;
                        unsafe
                        {
                            fixed (byte* p = buffer)
                            {
                                var info = (FILE_FULL_DIR_INFORMATION*)(p + offset);
                                nextEntryOffset = (int)info->NextEntryOffset;
                                entry = ParseEntry(info);
                            }
                        }

                        if (entry is { } e)
                        {
                            anyEntryReturned = true;
                            yield return e;
                        }

                        if (nextEntryOffset == 0) break;
                        offset += nextEntryOffset;
                    }
                }

                if (ntQueryFailedImmediately && !anyEntryReturned)
                {
                    foreach (var e in EnumerateFallback(longPathForFallback))
                    {
                        yield return e;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static unsafe FileSystemEntry? ParseEntry(FILE_FULL_DIR_INFORMATION* info)
    {
        int nameLenChars = (int)(info->FileNameLength / 2);
        if (nameLenChars <= 0) return null;

        ReadOnlySpan<char> nameSpan = info->FileName.AsSpan(nameLenChars);
        if (nameSpan is "." or "..") return null;

        string name = new string(nameSpan);
        bool isDirectory = (info->FileAttributes & 0x10 /* FILE_ATTRIBUTE_DIRECTORY */) != 0;

        return new FileSystemEntry(
            Name: name,
            IsDirectory: isDirectory,
            SizeBytes: isDirectory ? 0 : info->EndOfFile,
            CreationTimeUtc: FileTimeToUtc(info->CreationTime),
            LastWriteTimeUtc: FileTimeToUtc(info->LastWriteTime),
            Attributes: info->FileAttributes);
    }

    private static DateTime FileTimeToUtc(long fileTime)
    {
        if (fileTime <= 0) return DateTime.UnixEpoch;
        try
        {
            return DateTime.FromFileTimeUtc(fileTime);
        }
        catch (ArgumentOutOfRangeException)
        {
            return DateTime.UnixEpoch;
        }
    }

    /// <summary>
    /// FindFirstFileExW + FIND_FIRST_EX_LARGE_FETCH フォールバック(非 NTFS・権限不足・NtQuery 非対応時)。
    /// </summary>
    // unsafe の & 演算子はイテレーターメソッド内で使えないため、開始処理だけ別メソッドに分離する。
    private static unsafe FindCloseSafeHandle StartFind(string pattern, out WIN32_FIND_DATAW findData)
    {
        WIN32_FIND_DATAW local = default;
        FindCloseSafeHandle handle = PInvoke.FindFirstFileEx(
            pattern,
            FINDEX_INFO_LEVELS.FindExInfoBasic,
            &local,
            FINDEX_SEARCH_OPS.FindExSearchNameMatch,
            FIND_FIRST_EX_FLAGS.FIND_FIRST_EX_LARGE_FETCH);
        findData = local;
        return handle;
    }

    private static IEnumerable<FileSystemEntry> EnumerateFallback(string longPath)
    {
        string pattern = longPath.TrimEnd('\\') + @"\*";
        FindCloseSafeHandle handle = StartFind(pattern, out WIN32_FIND_DATAW findData);

        if (handle.IsInvalid)
        {
            handle.Dispose();
            yield break;
        }

        using (handle)
        {
            do
            {
                ReadOnlySpan<char> nameSpan = findData.cFileName.AsSpan();
                int nul = nameSpan.IndexOf('\0');
                if (nul >= 0) nameSpan = nameSpan[..nul];
                if (nameSpan is "." or "..") continue;

                bool isDirectory = (findData.dwFileAttributes & 0x10) != 0;
                long size = ((long)findData.nFileSizeHigh << 32) | findData.nFileSizeLow;

                yield return new FileSystemEntry(
                    Name: new string(nameSpan),
                    IsDirectory: isDirectory,
                    SizeBytes: isDirectory ? 0 : size,
                    CreationTimeUtc: FileTimeToUtc(((long)findData.ftCreationTime.dwHighDateTime << 32) | (uint)findData.ftCreationTime.dwLowDateTime),
                    LastWriteTimeUtc: FileTimeToUtc(((long)findData.ftLastWriteTime.dwHighDateTime << 32) | (uint)findData.ftLastWriteTime.dwLowDateTime),
                    Attributes: (uint)findData.dwFileAttributes);
            }
            while (PInvoke.FindNextFile(handle, out findData));
        }
    }
}
