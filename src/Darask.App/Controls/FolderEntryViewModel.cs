using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Darask.Enumeration;
using Darask.Shell;

namespace Darask.App;

/// <summary>
/// FastEnumerator の生データ(FileSystemEntry)を表示用に薄くラップしたもの。
/// アイコン/サムネイルは遅延ロード(docs/05 §6, docs/07 M2) — 行が可視になった時だけ
/// <see cref="BeginLoadIcon"/> を呼ぶ設計(WPF の仮想化 ListView が可視行だけ行コンテナを
/// 生成するので、Loaded/Unloaded に配線すれば自然と「可視行優先 + キャンセル可能」になる)。
/// </summary>
public sealed class FolderEntryViewModel(FileSystemEntry entry, string parentPath, bool showExtensions = true) : INotifyPropertyChanged
{
    public FileSystemEntry Entry { get; } = entry;

    public string Name => Entry.Name;
    public string FullPath => Path.Combine(parentPath, Name);
    public bool IsDirectory => Entry.IsDirectory;
    public long SizeBytes => Entry.SizeBytes;
    public DateTime LastWriteTimeUtc => Entry.LastWriteTimeUtc;
    public DateTime CreationTimeUtc => Entry.CreationTimeUtc;
    public bool IsHidden => Entry.IsHidden;
    public bool IsSystem => Entry.IsSystem;

    /// <summary>拡張子表示トグル(docs/01 §4)。フォルダー名は元々拡張子を持たないので無関係。</summary>
    public string DisplayName => !showExtensions && !IsDirectory && Path.GetExtension(Name) is { Length: > 0 } ext
        ? Name[..^ext.Length]
        : Name;

    public string LastWriteTimeDisplay => LastWriteTimeUtc == DateTime.UnixEpoch
        ? string.Empty
        : LastWriteTimeUtc.ToLocalTime().ToString("yyyy/MM/dd HH:mm");
    public string TypeDisplay => IsDirectory ? "ファイル フォルダー" : (Path.GetExtension(Name) is { Length: > 1 } ext ? ext[1..].ToUpperInvariant() + " ファイル" : "ファイル");
    public string SizeDisplay => IsDirectory ? string.Empty : FormatSize(SizeBytes);

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        private set { _icon = value; OnPropertyChanged(); }
    }

    private bool _isRenaming;
    /// <summary>名前の変更(インライン編集)中かどうか(docs/07 コンテキストメニュー整理)。</summary>
    public bool IsRenaming
    {
        get => _isRenaming;
        set
        {
            if (_isRenaming == value) return;
            _isRenaming = value;
            if (value) EditName = Name;
            OnPropertyChanged();
        }
    }

    private string _editName = string.Empty;
    public string EditName
    {
        get => _editName;
        set { _editName = value; OnPropertyChanged(); }
    }

    private System.Threading.CancellationTokenSource? _loadCts;
    private bool _iconLoaded;

    /// <summary>行が可視になった時に呼ぶ(FolderView の行 Loaded イベント)。バックグラウンドで
    /// 拡張子アイコン(高速)→サムネイル(低優先度)の順に取得し UI スレッドへディスパッチする。
    /// <paramref name="thumbnailSize"/> はアイコングリッドのズームに応じた要求解像度、
    /// <paramref name="largeIcons"/> は 32px シェルアイコンを使うか(大きいズーム時のボケ軽減)。</summary>
    public void BeginLoadIcon(int thumbnailSize = 64, bool largeIcons = false)
    {
        if (_iconLoaded) return;
        _iconLoaded = true;

        _loadCts = new System.Threading.CancellationTokenSource();
        var token = _loadCts.Token;
        string name = Name;
        bool isDirectory = IsDirectory;
        bool isCloudPlaceholder = Entry.IsCloudPlaceholder;
        string fullPath = Path.Combine(parentPath, name);

        System.Threading.Tasks.Task.Run(() =>
        {
            var extIcon = IconService.GetExtensionIcon(name, isDirectory, large: largeIcons);
            if (token.IsCancellationRequested) return;
            if (extIcon is not null)
            {
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (!token.IsCancellationRequested) Icon = extIcon;
                });
            }

            // OneDrive 等のクラウドプレースホルダーは絶対にハイドレートしない(CLAUDE.md 規則14)。
            if (isCloudPlaceholder) return;
            if (token.IsCancellationRequested) return;

            if (isDirectory)
            {
                // desktop.ini の IconResource カスタムフォルダーアイコンを反映するため実パスで再取得
                // (docs/07 M2)。USEFILEATTRIBUTES を外すとシェルの標準ロジックがこれを解決する。
                var realIcon = IconService.GetRealIcon(fullPath, large: largeIcons);
                if (!token.IsCancellationRequested && realIcon is not null)
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (!token.IsCancellationRequested) Icon = realIcon;
                    });
                }
                return;
            }

            if (Path.GetExtension(name).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                // .lnk は実パス経由でないとショートカット矢印オーバーレイが付かない(docs/07 M2)。
                var linkIcon = IconService.GetRealIcon(fullPath, large: largeIcons);
                if (!token.IsCancellationRequested && linkIcon is not null)
                {
                    Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (!token.IsCancellationRequested) Icon = linkIcon;
                    });
                }
                return;
            }

            var thumb = ThumbnailService.GetThumbnail(fullPath, thumbnailSize);
            if (token.IsCancellationRequested || thumb is null) return;
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!token.IsCancellationRequested) Icon = thumb;
            });
        }, token);
    }

    /// <summary>行がリサイクルされた時に呼ぶ(FolderView の行 Unloaded イベント)。</summary>
    public void CancelLoad()
    {
        _loadCts?.Cancel();
        _loadCts = null;
        _iconLoaded = false;
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["バイト", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unitIndex = 0;
        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }
        return unitIndex == 0 ? $"{bytes} {units[0]}" : $"{size:0.#} {units[unitIndex]}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
