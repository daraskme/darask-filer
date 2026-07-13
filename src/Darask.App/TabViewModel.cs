using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;

namespace Darask.App;

/// <summary>タブストリップの1タブ分の表示状態(docs/07 #24)。Content はフォルダータブ
/// (<see cref="FolderTabContent"/>)とごみ箱タブ(<see cref="RecycleBinView"/>)の両方を許容する。</summary>
public sealed class TabViewModel(UserControl content) : INotifyPropertyChanged
{
    public UserControl Content { get; } = content;

    private string _title = "新しいタブ";
    public string Title
    {
        get => _title;
        set { _title = value; OnPropertyChanged(); }
    }

    private bool _isActive;
    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
