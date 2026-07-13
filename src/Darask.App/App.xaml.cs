using System.Windows;
using Wpf.Ui.Appearance;

namespace Darask.App;

public partial class App : Application
{
    // M16 で常駐/トレイ起動戦略(docs/06 §6)を実装する。
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationThemeManager.ApplySystemTheme();
    }
}
