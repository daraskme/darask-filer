using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Darask.Shell;

/// <summary>
/// ファイル/フォルダーの既定アプリ起動(ShellExecute 既定動詞)とターミナル起動。
/// Process.Start + UseShellExecute はシェルの既定動詞解決(.lnk の解決・UAC 昇格プロンプト・
/// アプリ選択ダイアログへの誘導)をそのまま使えるため、IContextMenu を持ち出す必要がない。
/// </summary>
public static class LaunchService
{
    /// <summary>既定アプリで開く。関連付けがない場合はエクスプローラー同様「アプリを選択」ダイアログへ。</summary>
    public static void Open(string path)
    {
        var psi = new ProcessStartInfo(path) { UseShellExecute = true };
        if (Path.GetDirectoryName(path) is { Length: > 0 } dir)
        {
            psi.WorkingDirectory = dir;
        }

        try
        {
            Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1155 /* ERROR_NO_ASSOCIATION */)
        {
            ShowOpenWithDialog(path);
        }
        catch (Win32Exception)
        {
            // UAC キャンセル(ERROR_CANCELLED)等はユーザーの意思なので黙って無視する。
        }
    }

    /// <summary>「プログラムから開く」ダイアログを表示する。</summary>
    public static void ShowOpenWithDialog(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe", $"shell32.dll,OpenAs_RunDLL {path}")
            {
                UseShellExecute = false,
            });
        }
        catch (Win32Exception)
        {
        }
    }

    /// <summary>指定フォルダーでターミナルを開く。Windows Terminal 優先、なければ PowerShell。</summary>
    public static void OpenTerminal(string folderPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe", $"-d \"{folderPath}\"") { UseShellExecute = true });
        }
        catch (Win32Exception)
        {
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe")
                {
                    UseShellExecute = true,
                    WorkingDirectory = folderPath,
                });
            }
            catch (Win32Exception)
            {
            }
        }
    }
}
