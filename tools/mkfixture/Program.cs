using System.Diagnostics;
using Darask.Tools.MkFixture;

int seed = 42;
string? outDir = null;
string profile = "100k";
int images = 0;
bool flat = false;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seed":
            seed = int.Parse(args[++i]);
            break;
        case "--out":
            outDir = args[++i];
            break;
        case "--profile":
            profile = args[++i];
            break;
        case "--images":
            images = int.Parse(args[++i]);
            break;
        case "--flat":
            flat = true;
            break;
        default:
            Console.Error.WriteLine($"unknown argument: {args[i]}");
            return 1;
    }
}

if (outDir is null)
{
    Console.Error.WriteLine("usage: mkfixture --profile 100k|1m --seed <n> --out <dir> [--images <n>] [--flat]");
    return 1;
}

int targetFileCount = profile switch
{
    "100k" => 100_000,
    "1m" => 1_000_000,
    _ when int.TryParse(profile, out int n) => n,
    _ => throw new ArgumentException($"unknown profile: {profile}"),
};

if (Directory.Exists(outDir))
{
    // 長パス配下の削除に備え \\?\ プレフィックス付きの絶対パスで消す。
    string full = Path.GetFullPath(outDir);
    string longForm = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full : @"\\?\" + full;
    Directory.Delete(longForm, recursive: true);
}

var sw = Stopwatch.StartNew();
var generator = new FixtureGenerator(seed, outDir, targetFileCount, images, flat);
var manifest = generator.Generate();
sw.Stop();

Console.WriteLine($"profile={profile} seed={seed} flat={flat} files={manifest.Entries.Count(e => !e.IsDirectory)} " +
                   $"dirs={manifest.Entries.Count(e => e.IsDirectory)} images={images} skipped={generator.SkippedCount}");
Console.WriteLine($"rootHash={manifest.ComputeRootHash()}");
Console.WriteLine($"elapsedMs={sw.ElapsedMilliseconds}");

return 0;
