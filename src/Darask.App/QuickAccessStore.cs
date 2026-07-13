using System.IO;
using System.Text.Json;

namespace Darask.App;

/// <summary>
/// クイックアクセス(お気に入りフォルダー)の永続化。
/// v1 は JSON ファイル(将来 docs/02 の settings.db(SQLite) に統合予定 — M4 以降)。
/// </summary>
public static class QuickAccessStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "darask-filer", "quickaccess.json");

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
