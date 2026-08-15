using System.IO;
using System.Text.Json;

namespace Darask.App;

/// <summary>1タブ分の保存状態(パス・表示モード・アイコンズーム)。</summary>
public sealed record SessionTab(string Path, int ViewMode = 0, double IconSize = 48);

/// <summary>タブ構成のスナップショット。セッション復元と作業スペースで共用する。</summary>
public sealed record TabSnapshot(List<SessionTab> Tabs, int ActiveIndex);

/// <summary>名前付き作業スペース(プロジェクトごとのタブ構成、ユーザー要望機能)。</summary>
public sealed record Workspace(string Name, List<SessionTab> Tabs, int ActiveIndex);

/// <summary>
/// 作業スペースの永続化。v1 は JSON ファイル(QuickAccessStore と同じパターン —
/// docs/02 の settings.db(SQLite) への統合は M4 以降)。
/// </summary>
public static class WorkspaceStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "darask-filer", "workspaces.json");

    public static List<Workspace> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<Workspace>>(json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void Save(IEnumerable<Workspace> workspaces)
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(workspaces.ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保存失敗はアプリ動作に致命的ではないため無視する。
        }
    }
}

/// <summary>
/// 前回終了時のタブ構成(セッション)の永続化。起動時に復元する(Codex ブレインストーム #11)。
/// </summary>
public static class SessionStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "darask-filer", "session.json");

    public static TabSnapshot? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<TabSnapshot>(json);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Save(TabSnapshot snapshot)
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
