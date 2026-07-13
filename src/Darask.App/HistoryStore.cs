using System.IO;
using System.Text.Json;

namespace Darask.App;

/// <summary>
/// ナビゲーション履歴(MRU)の永続化。v1 は JSON ファイル(将来 docs/04 の履歴 DB へ統合予定 — M4 以降。
/// あちらはファイル単位の詳細な出来事の履歴、こちらは単なる「最近開いたフォルダー」の軽量な MRU)。
/// </summary>
public static class HistoryStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "darask-filer", "history.json");

    public static List<string> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            string json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<string>>(json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public static void Save(IEnumerable<string> paths)
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(paths.ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 保存失敗はアプリ動作に致命的ではないため無視する。
        }
    }
}
