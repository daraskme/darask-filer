namespace Darask.Tools.MkFixture;

/// <summary>
/// 決定論的な名前コーパス。docs/07 M0 の要件(日本語・NFC/NFD・サロゲートペア・
/// 非対サロゲート単体・全角・深いネスト)を満たすカテゴリを持つ。
/// 同じ (category, index) の組は常に同じ文字列を返す — シード再現性の核。
/// </summary>
internal enum NameCategory
{
    Ascii,
    Japanese,
    Nfc,
    Nfd,
    SurrogatePair,
    UnpairedSurrogate,
    FullWidth,
}

internal static class NameCorpus
{
    private static readonly string[] JapaneseWords =
    [
        "日本語ファイル名", "請求書", "見積書", "議事録", "経費精算", "旅行写真",
        "契約書_最終版", "プロジェクト計画", "顧客リスト", "設計資料",
    ];

    // NFC: 濁点が前の文字と合成済みの単一コードポイント(が = U+304C)
    private const string NfcSample = "がぎぐげござじずぜぞだぢづでどばびぶべぼ";

    // NFD: 基底文字 + 結合濁点(U+3099)に分解した形。バイト列としては NFC と異なる。
    private static readonly string NfdSample = "がぎぐげご".Normalize(System.Text.NormalizationForm.FormD);

    // サロゲートペア: 基本多言語面外の絵文字(😀 = U+1F600)
    private const string EmojiSample = "\U0001F600\U0001F4C1\U0001F5C2";

    private const string FullWidthSample = "ＡＢＣ０１２３ｆｕｌｌｗｉｄｔｈ";

    public static string Build(NameCategory category, int index, bool isDirectory)
    {
        string baseName = category switch
        {
            NameCategory.Ascii => $"file_{index:D6}",
            NameCategory.Japanese => $"{JapaneseWords[index % JapaneseWords.Length]}_{index:D4}",
            NameCategory.Nfc => $"{NfcSample}_{index:D4}",
            NameCategory.Nfd => $"{NfdSample}_{index:D4}",
            NameCategory.SurrogatePair => $"{EmojiSample}_写真_{index:D4}",
            NameCategory.UnpairedSurrogate => BuildUnpairedSurrogateName(index),
            NameCategory.FullWidth => $"{FullWidthSample}_{index:D4}",
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };

        return isDirectory ? baseName : baseName + Extension(index);
    }

    /// <summary>
    /// NTFS のファイル名は禁止文字さえ避ければ任意の UTF-16 コード単位列を許可するため、
    /// 非対(unpaired)サロゲートを単体で含む名前も理論上作成できる(docs/03 §2 のロスレス
    /// 往復要件を検証するための核心フィクスチャ)。高サロゲート単体・低サロゲート単体の
    /// 両方を1件ずつ用意する。
    /// </summary>
    private static string BuildUnpairedSurrogateName(int index)
    {
        // U+D800 (高サロゲート単体、後続が結合しない位置に置く)
        // U+DC00 (低サロゲート単体、先行が結合しない位置に置く)
        // 注意: サフィックスは index 全体を使うこと。以前は index % 10 の下 1 桁だけを使っており、
        // 単一フォルダーに大量生成すると 10 件ごとに名前が重複してファイルを取りこぼすバグがあった
        // (フラットモードの mkfixture --flat で実測発覚: 99566 件中 85726 件しか実体化しなかった)。
        char highSurrogateAlone = '\uD800';
        char lowSurrogateAlone = '\uDC00';
        string suffix = index.ToString("D7");
        return index % 2 == 0
            ? $"unpaired_hi_{highSurrogateAlone}_{suffix}"
            : $"unpaired_lo_{lowSurrogateAlone}_{suffix}";
    }

    private static string Extension(int index) => (index % 5) switch
    {
        0 => ".txt",
        1 => ".pdf",
        2 => ".docx",
        3 => ".xlsx",
        _ => ".dat",
    };
}
