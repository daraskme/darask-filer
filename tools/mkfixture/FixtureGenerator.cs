using System.Collections.Concurrent;
using System.Text;

namespace Darask.Tools.MkFixture;

/// <summary>
/// 決定論的(シード固定)な合成ファイルツリー生成器。同じ (seed, targetFileCount, imageCount) の
/// 組は常に同じ RootHash を持つツリーを生成する — 「mkfixture が同一シードで同一チェックサムの
/// ツリーを再生成する」(docs/07 M0)の中核ロジック。
///
/// 2フェーズ設計: (1) 単一スレッドで PRNG を消費してツリー構造を決定(プランニング。決定論性は
/// ここでのみ担保すればよい)、(2) 決定済みプランを Parallel.ForEach で並列にディスクへ書き出す
/// (I/O・SHA256計算はスレッド数に依存しても結果に影響しない)。100k ファイルを 60 秒以内に生成する
/// 受け入れ基準(docs/07 M0)を満たすための構成。
/// </summary>
internal sealed class FixtureGenerator
{
    private static readonly NameCategory[] AllCategories =
    [
        NameCategory.Ascii, NameCategory.Japanese, NameCategory.Nfc, NameCategory.Nfd,
        NameCategory.SurrogatePair, NameCategory.UnpairedSurrogate, NameCategory.FullWidth,
    ];

    private readonly Random _rng;
    private readonly string _rootDir;
    private readonly string _rootDirLong; // \\?\ プレフィックス付き絶対パス。ホットパスで毎回 GetFullPath しない
    private readonly int _targetFileCount;
    private readonly int _imageCount;
    private readonly bool _flat;
    private int _fileCount;
    private int _dirCount;
    private int _categoryIndex;
    private int _skippedCount;

    private readonly List<(string RelPath, int Depth)> _plannedDirs = [];
    private readonly List<string> _plannedFiles = [];

    /// <summary>
    /// flat=true の場合、ツリーを掘らずルート直下に全ファイルを平坦配置する。
    /// docs/07 M1 の「単一フォルダーに 10 万エントリ」受け入れシナリオ用
    /// (通常のツリーモードは 100k をディレクトリ階層に分散させるため、そのままでは
    /// 単一フォルダーの列挙/ソート性能を検証できない)。
    /// </summary>
    public FixtureGenerator(int seed, string rootDir, int targetFileCount, int imageCount, bool flat = false)
    {
        _rng = new Random(seed);
        _rootDir = rootDir;
        Directory.CreateDirectory(_rootDir);
        string full = Path.GetFullPath(_rootDir);
        _rootDirLong = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full : @"\\?\" + full;
        _targetFileCount = targetFileCount;
        _imageCount = imageCount;
        _flat = flat;
    }

    public int SkippedCount => _skippedCount;

    public Manifest Generate()
    {
        PlanTree();

        // ディレクトリ・ファイル I/O は NTFS のメタデータ更新オーバーヘッドが支配的で CPU バウンドで
        // はないため、CPU コア数より高い並列度の方がスループットが出る(I/O 待ちを隠蔽できる)。
        var ioOptions = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount * 4 };

        // ディレクトリ作成は深さレベルごとにグループ化して並列化する: 同一レベル内のディレクトリは
        // 親がすべて前のレベルで作成済みなので、レベル内は並列に、レベル間は逐次(親→子の順序を保証)
        // で作成できる。25,000 ディレクトリを逐次作成すると 100k ファイル生成の主要ボトルネックに
        // なることが実測でわかったため、この構成にした(docs/07 M0: 100k を 60 秒以内)。
        foreach (var levelGroup in _plannedDirs.GroupBy(d => d.Depth).OrderBy(g => g.Key))
        {
            Parallel.ForEach(levelGroup, ioOptions, d =>
            {
                Directory.CreateDirectory(LongPathFor(d.RelPath));
            });
        }

        var manifest = new Manifest();
        foreach (var d in _plannedDirs)
        {
            manifest.Add(new ManifestEntry(d.RelPath, IsDirectory: true, SizeBytes: 0, Sha256Hex: ""));
        }

        // 事前確保した配列にインデックスで直接書き込む(ConcurrentBag のロック/再配置オーバーヘッドを回避)。
        var fileResults = new ManifestEntry?[_plannedFiles.Count];
        int skipped = 0;
        Parallel.For(0, _plannedFiles.Count, ioOptions, i =>
        {
            if (TryWriteDeterministicFile(_plannedFiles[i], out ManifestEntry entry))
            {
                fileResults[i] = entry;
            }
            else
            {
                Interlocked.Increment(ref skipped);
            }
        });
        _skippedCount = skipped;
        var fileEntries = fileResults.Where(e => e is not null).Select(e => e!);

        if (_imageCount > 0)
        {
            string imagesDir = "images";
            Directory.CreateDirectory(LongPathFor(imagesDir));
            manifest.Add(new ManifestEntry(imagesDir, IsDirectory: true, SizeBytes: 0, Sha256Hex: ""));

            var imageEntries = new ConcurrentBag<ManifestEntry>();
            Parallel.For(0, _imageCount, i =>
            {
                imageEntries.Add(WriteImageFile(i));
            });
            foreach (var e in imageEntries) manifest.Add(e);
        }

        foreach (var e in fileEntries) manifest.Add(e);

        manifest.WriteJson(Path.Combine(_rootDir, "manifest.json"));
        return manifest;
    }

    /// <summary>
    /// 単一スレッドで PRNG を消費し、ディレクトリ・ファイルの相対パスをすべて事前決定する。
    /// I/O は一切行わない — 決定論性が必要なのはこのメソッドだけ。
    /// </summary>
    private void PlanTree()
    {
        if (_flat)
        {
            while (_fileCount < _targetFileCount)
            {
                PlanFile("");
            }
            return;
        }

        var queue = new Queue<(string RelDir, int Depth)>();
        queue.Enqueue(("", 0));

        bool deepPathPlanned = false;

        // ブランチ係数がランダムに 0 続きになると木が早期に枯渇し、目標ファイル数に届かないまま
        // キューが空になり得る。目標未達でキューが尽きた場合はルートから新しい分岐を継ぎ足す。
        while (_fileCount < _targetFileCount)
        {
            if (queue.Count == 0)
            {
                queue.Enqueue(("", 0));
            }
            var (relDir, depth) = queue.Dequeue();

            int filesHere = _rng.Next(1, 8);
            for (int i = 0; i < filesHere && _fileCount < _targetFileCount; i++)
            {
                PlanFile(relDir);
            }

            int subdirsHere = depth < 6 ? _rng.Next(1, 4) : 0;
            for (int i = 0; i < subdirsHere; i++)
            {
                string relSubDir = PlanDirectory(relDir, depth);
                queue.Enqueue((relSubDir, depth + 1));
            }

            // 300 文字超の深いネストパスを最低1本は必ず作る(docs/07 M0: `\\?\` 級長パス要件)。
            if (!deepPathPlanned && depth == 0)
            {
                PlanDeepNestedTree(relDir);
                deepPathPlanned = true;
            }
        }
    }

    private void PlanFile(string relDir)
    {
        var category = AllCategories[_categoryIndex % AllCategories.Length];
        _categoryIndex++;
        string name = NameCorpus.Build(category, _fileCount, isDirectory: false);
        _plannedFiles.Add(Combine(relDir, name));
        _fileCount++;
    }

    private string PlanDirectory(string relDir, int parentDepth)
    {
        var category = AllCategories[_categoryIndex % AllCategories.Length];
        _categoryIndex++;
        string name = NameCorpus.Build(category, _dirCount, isDirectory: true) + "_d" + _dirCount.ToString("D5");
        string relPath = Combine(relDir, name);
        _dirCount++;
        _plannedDirs.Add((relPath, parentDepth + 1));
        return relPath;
    }

    /// <summary>
    /// ルート直下に "深いネスト" 専用のサブツリーを掘り、絶対パスが 300 文字を超える
    /// ファイルを最低1つ作る。通常ツリーの深さレベル(0-6程度)と衝突しないよう、深さ値は
    /// 10000 起点の一意な連番を割り振り、GroupBy によるレベル並列化でも親が必ず子より先に
    /// 作成されるようにする。
    /// </summary>
    private void PlanDeepNestedTree(string relDir)
    {
        const int deepLevelBase = 10_000;
        string current = Combine(relDir, "deep_nesting_root");
        _plannedDirs.Add((current, deepLevelBase));

        int rootFullLength = _rootDirLong.Length - 4; // \\?\ の4文字を除く
        int segment = 0;
        while (rootFullLength + current.Length < 320)
        {
            string seg = $"level_{segment:D3}_日本語階層名";
            current = Combine(current, seg);
            _plannedDirs.Add((current, deepLevelBase + segment + 1));
            segment++;
        }

        _plannedFiles.Add(Combine(current, "deep_file_末端ファイル.txt"));
        _fileCount++;
    }

    private ManifestEntry WriteImageFile(int index)
    {
        string name = $"image_{index:D6}.bmp";
        string relPath = Combine("images", name);
        string fullPath = LongPathFor(relPath);

        byte[] bmp = BmpWriter.CreateDeterministicBmp(seed: index, width: 16, height: 16);
        File.WriteAllBytes(fullPath, bmp);
        string hash = Manifest.Sha256Hex(bmp);
        return new ManifestEntry(relPath, IsDirectory: false, SizeBytes: bmp.Length, Sha256Hex: hash);
    }

    /// <summary>
    /// ファイル内容は相対パスから決定論的に導出する(同じシード → 同じ内容 → 同じ SHA256)。
    /// 非対サロゲートを含む名前など、まれに Win32 が拒否するケースは false を返してスキップする —
    /// スキップも決定論的(同じシードなら同じ箇所がスキップされる)なのでチェックサム再現性は保たれる。
    /// </summary>
    private bool TryWriteDeterministicFile(string relPath, out ManifestEntry entry)
    {
        string fullPath = LongPathFor(relPath);

        byte[] content = DeterministicContent(relPath);
        try
        {
            File.WriteAllBytes(fullPath, content);
        }
        catch (IOException)
        {
            entry = default!;
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            entry = default!;
            return false;
        }

        string hash = Manifest.Sha256Hex(content);
        entry = new ManifestEntry(relPath, IsDirectory: false, SizeBytes: content.Length, Sha256Hex: hash);
        return true;
    }

    private static byte[] DeterministicContent(string relPath)
    {
        byte[] pathBytes = Encoding.UTF8.GetBytes(relPath);
        // 注意: string.GetHashCode() は .NET ではプロセスごとにランダム化される(ハッシュ DoS
        // 対策)ため、決定論的フィクスチャ生成には絶対に使えない — FNV-1a を自前実装する。
        // repeat は小さく保つ(深いネストパスは相対パス自体が長く、繰り返しを増やすと 100k 件の
        // 合計 I/O 量が跳ね上がり 60 秒ゲートを脅かす — 実測で判明)。
        int repeat = 1 + (int)(Fnv1a(pathBytes) % 3);
        var buffer = new byte[pathBytes.Length * repeat];
        for (int i = 0; i < repeat; i++)
        {
            pathBytes.CopyTo(buffer, i * pathBytes.Length);
        }
        return buffer;
    }

    private static uint Fnv1a(byte[] data)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        uint hash = offsetBasis;
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= prime;
        }
        return hash;
    }

    private static string Combine(string relDir, string name) =>
        string.IsNullOrEmpty(relDir) ? name : relDir + "\\" + name;

    /// <summary>
    /// あらかじめ計算済みの \\?\ 絶対ルートパスに相対パスを単純結合する(ホットパスで
    /// Path.GetFullPath を毎回呼ばない — 100k ファイル生成の性能に直接効く)。
    /// </summary>
    private string LongPathFor(string relPath) => _rootDirLong + "\\" + relPath;
}
