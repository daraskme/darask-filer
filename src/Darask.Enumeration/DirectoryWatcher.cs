using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;

namespace Darask.Enumeration;

public enum FileChangeKind { Created, Deleted, Modified, RenamedOldName, RenamedNewName }

public readonly record struct FileChangeEvent(FileChangeKind Kind, string Name);

/// <summary>
/// ReadDirectoryChangesW ベースのフォルダー監視(docs/02 §5.4, docs/07 M1)。
/// <para>
/// <b>オーバーラップド I/O 必須</b>: 当初は同期呼び出し(専用スレッドでブロッキング)で実装したが、
/// 同期 ReadDirectoryChangesW がブロック中にハンドルをクローズしても呼び出しが解除されず、
/// 次のファイルシステムイベントが発生するまで永久にハングすることを実測で確認した。
/// <see cref="ThreadPoolBoundHandle"/> + <c>CancelIoEx</c> によるオーバーラップド I/O 方式のみが
/// 安全に Dispose できる — 同期方式へ戻さないこと。
/// </para>
/// <para>
/// オーバーフロー(success かつ numBytes==0)は取りこぼしを意味するため、<see cref="Overflowed"/> を
/// 発火して呼び出し側に再走査を促す(docs/02 §5.4「オーバーフロー→再走査」)。
/// </para>
/// </summary>
public sealed class DirectoryWatcher : IDisposable
{
    private const uint FILE_LIST_DIRECTORY = 0x0001;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
    private const int BufferSize = 64 * 1024;
    private const int ERROR_IO_PENDING = 997;
    private const int ERROR_OPERATION_ABORTED = 995;

    private readonly SafeFileHandle _handle;
    private readonly ThreadPoolBoundHandle _boundHandle;
    private readonly byte[] _buffer = new byte[BufferSize];
    private volatile bool _disposed;

    public event Action<FileChangeEvent>? Changed;
    public event Action? Overflowed;

    public DirectoryWatcher(string path)
    {
        string longPath = LongPath.Ensure(path);
        _handle = PInvoke.CreateFile(
            longPath,
            FILE_LIST_DIRECTORY,
            // FILE_SHARE_DELETE を含めないと監視対象フォルダーをリネーム/削除できなくなる(docs/02 §5.4)。
            FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
            lpSecurityAttributes: null,
            FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            (FILE_FLAGS_AND_ATTRIBUTES)((uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OVERLAPPED),
            hTemplateFile: null);

        if (_handle.IsInvalid)
        {
            _handle.Dispose();
            throw new IOException($"Failed to open directory for watching: {path}");
        }

        _boundHandle = ThreadPoolBoundHandle.BindHandle(_handle);
        BeginRead();
    }

    private unsafe void BeginRead()
    {
        if (_disposed) return;

        NativeOverlapped* overlapped = _boundHandle.AllocateNativeOverlapped(OnCompleted, this, _buffer);

        const FILE_NOTIFY_CHANGE filter =
            FILE_NOTIFY_CHANGE.FILE_NOTIFY_CHANGE_FILE_NAME
            | FILE_NOTIFY_CHANGE.FILE_NOTIFY_CHANGE_DIR_NAME
            | FILE_NOTIFY_CHANGE.FILE_NOTIFY_CHANGE_LAST_WRITE
            | FILE_NOTIFY_CHANGE.FILE_NOTIFY_CHANGE_SIZE
            | FILE_NOTIFY_CHANGE.FILE_NOTIFY_CHANGE_CREATION;

        BOOL ok;
        fixed (byte* pBuffer = _buffer)
        {
            ok = PInvoke.ReadDirectoryChanges(
                (HANDLE)_handle.DangerousGetHandle(),
                pBuffer,
                (uint)_buffer.Length,
                bWatchSubtree: false,
                filter,
                lpBytesReturned: null,
                overlapped,
                lpCompletionRoutine: null);
        }

        if (!ok)
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ERROR_IO_PENDING)
            {
                // 即時失敗(ハンドル無効化等)。overlapped を解放して監視を終了する。
                _boundHandle.FreeNativeOverlapped(overlapped);
            }
        }
    }

    private static unsafe void OnCompleted(uint errorCode, uint numBytes, NativeOverlapped* overlapped)
    {
        if (ThreadPoolBoundHandle.GetNativeOverlappedState(overlapped) is not DirectoryWatcher watcher)
        {
            return;
        }

        watcher._boundHandle.FreeNativeOverlapped(overlapped);

        if (watcher._disposed || errorCode == ERROR_OPERATION_ABORTED)
        {
            return; // Dispose 済み(CancelIoEx によるキャンセル完了) — 次の読み取りは発行しない
        }

        if (errorCode != 0)
        {
            return; // その他のエラー: 監視終了(再作成は呼び出し側の責務)
        }

        if (numBytes == 0)
        {
            watcher.Overflowed?.Invoke();
        }
        else
        {
            watcher.ParseAndRaise(watcher._buffer.AsSpan(0, (int)numBytes));
        }

        watcher.BeginRead(); // 継続監視
    }

    private void ParseAndRaise(ReadOnlySpan<byte> data)
    {
        int offset = 0;
        while (true)
        {
            int nextOffset;
            FILE_ACTION action;
            string name;
            unsafe
            {
                fixed (byte* p = data)
                {
                    var info = (FILE_NOTIFY_INFORMATION*)(p + offset);
                    nextOffset = (int)info->NextEntryOffset;
                    action = info->Action;
                    int nameLenChars = (int)(info->FileNameLength / 2);
                    name = new string(info->FileName.AsSpan(nameLenChars));
                }
            }

            var kind = action switch
            {
                FILE_ACTION.FILE_ACTION_ADDED => FileChangeKind.Created,
                FILE_ACTION.FILE_ACTION_REMOVED => FileChangeKind.Deleted,
                FILE_ACTION.FILE_ACTION_MODIFIED => FileChangeKind.Modified,
                FILE_ACTION.FILE_ACTION_RENAMED_OLD_NAME => FileChangeKind.RenamedOldName,
                FILE_ACTION.FILE_ACTION_RENAMED_NEW_NAME => FileChangeKind.RenamedNewName,
                _ => FileChangeKind.Modified,
            };

            Changed?.Invoke(new FileChangeEvent(kind, name));

            if (nextOffset == 0) break;
            offset += nextOffset;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        unsafe
        {
            PInvoke.CancelIoEx(_handle, (NativeOverlapped*)null); // 保留中の ReadDirectoryChangesW を全キャンセル
        }
        _handle.Dispose();
        _boundHandle.Dispose();
    }
}
