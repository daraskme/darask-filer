namespace Darask.Tools.MkFixture;

/// <summary>
/// 依存ゼロで有効な BMP(24bit, 非圧縮)を書き出す。M2 のサムネイルパイプライン
/// (IShellItemImageFactory)が実際にデコードできる画像を、外部ライブラリなしで
/// 決定論的に生成するための最小実装(docs/07 M0 の --images モード)。
/// </summary>
internal static class BmpWriter
{
    public static byte[] CreateDeterministicBmp(int seed, int width = 16, int height = 16)
    {
        int rowSize = ((width * 3 + 3) / 4) * 4; // 4バイト境界パディング
        int pixelArraySize = rowSize * height;
        int fileSize = 14 + 40 + pixelArraySize;

        using var ms = new MemoryStream(fileSize);
        using var w = new BinaryWriter(ms);

        // ファイルヘッダー
        w.Write((byte)'B');
        w.Write((byte)'M');
        w.Write(fileSize);
        w.Write(0); // 予約
        w.Write(14 + 40); // ピクセルデータオフセット

        // DIB ヘッダー(BITMAPINFOHEADER)
        w.Write(40); // ヘッダーサイズ
        w.Write(width);
        w.Write(height);
        w.Write((short)1); // プレーン数
        w.Write((short)24); // ビット深度
        w.Write(0); // 圧縮なし
        w.Write(pixelArraySize);
        w.Write(2835); // 水平解像度(72dpi相当)
        w.Write(2835);
        w.Write(0); // パレット色数
        w.Write(0); // 重要色数

        // ピクセルデータ(下から上、BGR、シードから決定論的な色)
        var rng = new Random(seed);
        byte r = (byte)rng.Next(256);
        byte g = (byte)rng.Next(256);
        byte b = (byte)rng.Next(256);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                w.Write(b);
                w.Write(g);
                w.Write(r);
            }
            for (int pad = 0; pad < rowSize - width * 3; pad++)
            {
                w.Write((byte)0);
            }
        }

        w.Flush();
        return ms.ToArray();
    }
}
