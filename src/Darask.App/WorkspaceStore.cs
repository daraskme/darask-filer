using System.IO;
using System.Text.Json;

namespace Darask.App;

/// <summary>1タブ分の保存状態(パス・表示モード・アイコンズーム)。</summary>
public sealed record SessionTab(string Path, int ViewMode = 0, double IconSize = 48);

/// <summary>タブ構成のスナップショット。セッション復元と作業スペースで共用する。</summary>
public sealed record TabSnapshot(List<SessionTab> Tabs, int ActiveIndex);

/// <summary>名前付き作業スペース(プロジェクトごとのタブ構成、ユーザー要望機能)。</summary>
public sealed record Workspace(string Name, List<SessionTab> Tabs, int ActiveIndex);

/// <summary>JSON ストア共通の書き込みヘルパー。一時ファイル + 置換で、書き込み中の
/// プロセス強制終了でも既存ファイルが半端に切り詰められないようにする(sol レビュー #3)。</summary>
internal static class JsonFileStore
{
    public static void WriteAtomic(string filePath, string json)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (dir is not null) Directory.CreateDirectory(dir);
        string tmp = filePath + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, filePath, overwrite: true);
    }

    /// <summary>デシリアライズ結果のタブ列を検証・浄化する。JSON として妥当でも
    /// `{"Tabs":null}` や null 要素・空パスが混ざり得る(sol レビュー #4)。</summary>
    public static List<SessionTab>? SanitizeTabs(List<SessionTab>? tabs) =>
        tabs?.Where(t => t?.Path is { Length: > 0 }).ToList();
}

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
            var loaded = JsonSerializer.Deserialize<List<Workspace>>(json) ?? [];
            // 手編集・破損した JSON でも落とさない: 名前とタブ列が妥当なものだけ通す。
            return loaded
                .Where(w => w is { Name.Length: > 0 })
                .Select(w => w with { Tabs = JsonFileStore.SanitizeTabs(w.Tabs) ?? [] })
                .Where(w => w.Tabs.Count > 0)
                .ToList();
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
            JsonFileStore.WriteAtomic(FilePath, JsonSerializer.Serialize(workspaces.ToList()));
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
            var snapshot = JsonSerializer.Deserialize<TabSnapshot>(json);
            var tabs = JsonFileStore.SanitizeTabs(snapshot?.Tabs);
            return tabs is { Count: > 0 } ? new TabSnapshot(tabs, snapshot!.ActiveIndex) : null;
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
            JsonFileStore.WriteAtomic(FilePath, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
