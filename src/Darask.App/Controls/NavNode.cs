using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Darask.Enumeration;

namespace Darask.App;

/// <summary>
/// ナビゲーションペインのツリーノード(ドライブ/フォルダー)。展開時に遅延ロードする
/// (ダミー子ノードを1つ持たせておき、実際に展開された時だけ子孫を列挙する)。
/// </summary>
public sealed class NavNode : INotifyPropertyChanged
{
    private static readonly NavNode Placeholder = new("読み込み中...", string.Empty, isPlaceholder: true);

    public string DisplayName { get; }
    public string FullPath { get; }
    public bool IsPlaceholder { get; }
    public ObservableCollection<NavNode> Children { get; } = [];

    private bool _childrenLoaded;
    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; OnPropertyChanged(); }
    }

    public NavNode(string displayName, string fullPath, bool isPlaceholder = false)
    {
        DisplayName = displayName;
        FullPath = fullPath;
        IsPlaceholder = isPlaceholder;
        if (!isPlaceholder)
        {
            Children.Add(Placeholder);
        }
    }

    public void EnsureChildrenLoaded()
    {
        if (_childrenLoaded || IsPlaceholder) return;
        _childrenLoaded = true;

        Children.Clear();
        try
        {
            foreach (var entry in FastEnumerator.Enumerate(FullPath))
            {
                if (!entry.IsDirectory) continue;
                if (entry.IsHidden || entry.IsSystem) continue; // ナビゲーションツリーは既定で隠しフォルダーを出さない
                Children.Add(new NavNode(entry.Name, Path.Combine(FullPath, entry.Name)));
            }
        }
        catch (UnauthorizedAccessException)
        {
            // アクセス拒否フォルダーは空ノードのまま(展開可能マーカーを消して葉にする)
        }
        catch (IOException)
        {
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
