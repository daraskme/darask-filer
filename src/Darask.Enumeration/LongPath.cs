namespace Darask.Enumeration;

/// <summary>
/// `\\?\` プレフィックスの付与(docs/01 §2.5, CLAUDE.md 規則15: 長パスは自前コードパス全部 `\\?\`)。
/// </summary>
public static class LongPath
{
    public static string Ensure(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return full;
        }
        // UNC パス(\\server\share\...)は \\?\UNC\server\share\... の形にする。
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + full[2..];
        }
        return @"\\?\" + full;
    }
}
