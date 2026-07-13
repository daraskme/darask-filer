using System.Security.Cryptography;
using System.Text;

namespace Darask.Tools.MkFixture;

internal sealed record ManifestEntry(string RelativePath, bool IsDirectory, long SizeBytes, string Sha256Hex);

/// <summary>
/// 生成したツリーの検証用マニフェスト。エントリを正規化順(相対パスの序数比較)でソートしてから
/// 連結・SHA256 することで、同一シードなら生成順に依存せず同一の RootHash が得られる —
/// 「mkfixture が同一シードで同一チェックサムのツリーを再生成する」受け入れ基準の根拠。
/// </summary>
internal sealed class Manifest
{
    private readonly List<ManifestEntry> _entries = [];

    public void Add(ManifestEntry entry) => _entries.Add(entry);

    public IReadOnlyList<ManifestEntry> Entries => _entries;

    public string ComputeRootHash()
    {
        var ordered = _entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal);
        var sb = new StringBuilder();
        foreach (var e in ordered)
        {
            sb.Append(e.RelativePath).Append('\t')
              .Append(e.IsDirectory ? '1' : '0').Append('\t')
              .Append(e.SizeBytes).Append('\t')
              .Append(e.Sha256Hex).Append('\n');
        }
        byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    public void WriteJson(string path)
    {
        var ordered = _entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal).ToList();
        using var fs = File.Create(path);
        using var w = new StreamWriter(fs, Encoding.UTF8);
        w.WriteLine("{");
        w.WriteLine($"  \"rootHash\": \"{ComputeRootHash()}\",");
        w.WriteLine($"  \"entryCount\": {ordered.Count},");
        w.WriteLine("  \"entries\": [");
        for (int i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            string comma = i == ordered.Count - 1 ? "" : ",";
            string escapedPath = e.RelativePath.Replace("\\", "\\\\").Replace("\"", "\\\"");
            w.WriteLine($"    {{\"path\": \"{escapedPath}\", \"dir\": {(e.IsDirectory ? "true" : "false")}, \"size\": {e.SizeBytes}, \"sha256\": \"{e.Sha256Hex}\"}}{comma}");
        }
        w.WriteLine("  ]");
        w.WriteLine("}");
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Sha256HexOfStream(Stream s)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(s);
        return Convert.ToHexStringLower(hash);
    }
}
