using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace Darask.App;

/// <summary>
/// コード生成の ContextMenu/MenuItem まわりの安定化ヘルパー。
///
/// 1. ContextMenu 自体: WPF-UI の暗黙スタイル(半透明 Acrylic 前提)が Popup 上で正しく合成されず
///    背後のコンテンツが透けて見える不具合があるため、不透明な背景を明示する(<see cref="ApplyOpaque"/>)。
/// 2. MenuItem: WPF-UI 4.3.0 の既定 MenuItem テンプレートには、マウスホバー時に
///    Storyboard で Background をアニメーションするトリガーが含まれるが、コードから直接
///    `new MenuItem()` で生成したインスタンスをコンテキストメニューに追加した場合、この
///    Storyboard の PropertyPath("(0).(1)")が解決できず
///    <c>System.InvalidOperationException: 'Background' プロパティは...</c> で
///    アプリ全体がクラッシュすることを実機で確認した(ホバーした瞬間に
///    <c>MouseDevice.ChangeMouseOver</c> 経由で例外 → ハンドラなしでプロセス終了)。
///    アニメーションを一切使わない素の Style(<see cref="SafeMenuItemStyle"/>)を明示的に
///    割り当てることで回避する — コード生成 MenuItem には必ずこれを設定すること。
/// </summary>
internal static class MenuTheme
{
    public static void ApplyOpaque(ContextMenu menu)
    {
        bool dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        menu.Background = new SolidColorBrush(dark ? Color.FromRgb(0x2c, 0x2c, 0x2c) : Color.FromRgb(0xfa, 0xfa, 0xfa));
        menu.BorderBrush = new SolidColorBrush(dark ? Color.FromRgb(0x45, 0x45, 0x45) : Color.FromRgb(0xd0, 0xd0, 0xd0));
        menu.BorderThickness = new Thickness(1);
        menu.Foreground = new SolidColorBrush(dark ? Colors.White : Colors.Black);
        menu.Padding = new Thickness(2);
    }

    public static readonly Style SafeMenuItemStyle = CreateSafeMenuItemStyle();

    private static Style CreateSafeMenuItemStyle()
    {
        bool dark = ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark;
        var hoverBrush = new SolidColorBrush(dark ? Color.FromArgb(0x40, 0xff, 0xff, 0xff) : Color.FromArgb(0x18, 0x00, 0x00, 0x00));

        var style = new Style(typeof(MenuItem));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(10, 6, 16, 6)));

        var hoverTrigger = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, hoverBrush));
        style.Triggers.Add(hoverTrigger);

        return style;
    }

    /// <summary>コード生成 MenuItem 用の共通ヘルパー — クラッシュ回避スタイルの割り当てまで含む。</summary>
    public static void AddItem(ItemsControl menu, string header, Action action)
    {
        var item = new MenuItem { Header = header, Style = SafeMenuItemStyle };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }
}
