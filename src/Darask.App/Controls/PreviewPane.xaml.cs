using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Darask.App;

/// <summary>
/// 簡易プレビューペイン(docs/07 #23)。画像はデコードして表示、テキスト系は先頭256KBを表示。
/// それ以外は拡張子アイコン+基本情報のみ(フル IPreviewHandler ホスティングは docs/07 M13 で拡張)。
/// </summary>
public partial class PreviewPane : UserControl
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".ico", ".tiff" };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".json", ".xml", ".csv", ".log", ".cs", ".cpp", ".h", ".c", ".py", ".js", ".ts",
        ".html", ".htm", ".css", ".yaml", ".yml", ".ini", ".config", ".xaml", ".ps1", ".sh", ".bat", ".gitignore",
    };

    private CancellationTokenSource? _loadCts;

    public PreviewPane()
    {
        InitializeComponent();
    }

    public void ShowPreview(FolderEntryViewModel? vm)
    {
        _loadCts?.Cancel();
        _loadCts = null;

        if (vm is null || vm.IsDirectory)
        {
            ShowEmpty();
            return;
        }

        string ext = Path.GetExtension(vm.Name);
        if (ImageExtensions.Contains(ext))
        {
            ShowImagePreview(vm.FullPath);
        }
        else if (TextExtensions.Contains(ext))
        {
            ShowTextPreview(vm.FullPath);
        }
        else
        {
            ShowGenericInfo(vm);
        }
    }

    private void HideAll()
    {
        EmptyState.Visibility = Visibility.Collapsed;
        ImagePreview.Visibility = Visibility.Collapsed;
        TextPreview.Visibility = Visibility.Collapsed;
        GenericInfo.Visibility = Visibility.Collapsed;
    }

    private void ShowEmpty()
    {
        HideAll();
        ImagePreview.Source = null;
        EmptyState.Visibility = Visibility.Visible;
    }

    private void ShowImagePreview(string path)
    {
        HideAll();
        ImagePreview.Source = null;
        ImagePreview.Visibility = Visibility.Visible;

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        Task.Run(() =>
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 900;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                if (!cts.Token.IsCancellationRequested)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!cts.Token.IsCancellationRequested) ImagePreview.Source = bmp;
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or ArgumentException)
            {
            }
        }, cts.Token);
    }

    private void ShowTextPreview(string path)
    {
        HideAll();
        TextPreview.Text = string.Empty;
        TextPreview.Visibility = Visibility.Visible;

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        Task.Run(() =>
        {
            try
            {
                const int maxBytes = 256 * 1024;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                bool truncated = stream.Length > maxBytes;
                var buffer = new byte[Math.Min(stream.Length, maxBytes)];
                int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
                string text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);
                if (truncated) text += "\n\n...(以下省略)";

                if (!cts.Token.IsCancellationRequested)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!cts.Token.IsCancellationRequested) TextPreview.Text = text;
                    });
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }, cts.Token);
    }

    private void ShowGenericInfo(FolderEntryViewModel vm)
    {
        HideAll();
        GenericInfo.Visibility = Visibility.Visible;
        GenericIcon.Source = Darask.Shell.IconService.GetExtensionIcon(vm.Name, false, large: true);
        GenericName.Text = vm.DisplayName;
        GenericMeta.Text = $"{vm.TypeDisplay}  {vm.SizeDisplay}";
    }
}
