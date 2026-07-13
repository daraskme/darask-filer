# 03 — 検索インデックス設計

方針: **Everything を忠実に複製する**。公開された実証済みアーキテクチャであり、実測数値を受け入れゲートに使える。
独自の工夫は「日本語向け折りたたみ」「ランキング」「メタデータ補完パス」の3点だけ。それ以外で発明しないこと。

## 1. 権限戦略(確定事項)

- **LocalSystem Windows サービス(`DaraskFilerd`)が生 I/O を独占する。**
  `FSCTL_ENUM_USN_DATA` / `FSCTL_READ_USN_JOURNAL` は昇格ボリュームハンドル必須
  (調査結論: Everything・UsnParser・ultrasearch など全ての実装が昇格を要求。SeBackupPrivilege は無関係。
  `\\.\C:` を GENERIC_READ で開けるのは昇格 Administrators/SYSTEM のみ)。
- 非昇格アプリは**絶対に**ボリュームハンドルを開かない。ジャーナル再生は常にサービス経由。
- 非公式 API `FSCTL_READ_UNPRIVILEGED_USN_JOURNAL` は**基盤として明確に却下**
  (ベースライン用の ENUM 相当が存在しない、ACL セマンティクスが未文書)。フィーチャーフラグの裏にのみ残してよい。
- サービス登録は初回起動時オプトイン(docs/02 §7)。UAC は1回だけ。

## 2. インデックス構造(アプリプロセス内、ボリュームごと)

```csharp
struct Entry {
    FileId128 frn;        // 最初から 128-bit(ReFS/Dev Drive は 128-bit ID + USN_RECORD_V3。後付けはスキーマ破壊)
    FileId128 parentFrn;
    uint      attrs;      // FILE_ATTRIBUTE_*
    long      size;       // ★補完フィールド(§3)。ベースライン直後は未確定(-1)
    long      mtimeUtc;   // ★補完フィールド(§3)。同上
    int       nameOffset; // UTF-8 アリーナへのオフセット
    ushort    nameLen;
    byte      flags;      // MetaFilled 等
}
```

- **パック UTF-8 名前アリーナ**(`byte[]` セグメント列)。ファイルごとの `string` オブジェクト禁止
  (naive 実装は 5M ファイルで 600 MB–1 GB + GC ストール。パック方式で ≤ 150 MB / 1M ファイル)。
  **エンコーディング上の必須条件**: NTFS のファイル名は任意の UTF-16 コード単位列であり、
  **非対サロゲート(unpaired surrogate)を含み得る**。アリーナは `Encoding.UTF8`(既定フォールバック、
  不正列を例外または `U+FFFD` に潰す)を使わず、**任意の UTF-16 列をロスレスに往復できるカスタム
  エンコーダ(WTF-8 相当)**で格納すること。CLAUDE.md の「UTF-16 end-to-end」規則は「アリーナ格納は
  ロスレス往復を保証する場合のみ UTF-8 系エンコーディングを許可する」の意味であり、矛盾ではない。
  mkfixture のコーパスと M8 の検索オラクルフィクスチャに非対サロゲートを含む名前を1つ以上含めること
  (正当なサロゲート**ペア**だけでは検出できない)。
- **検索用シャドウバッファ**: 全名前を連結し**ケース折りたたみ + NFKC 幅折りたたみ**(全角/半角、
  `ﾌｧｲﾙ`↔`ファイル`、`１２３`↔`123`)した別バッファ。**元の名前は絶対に加工しない** — 折りたたむのは検索コピーのみ。
  幅折りたたみは設定トグル(既定 ON)。OFF 時は単純ケース折りたたみのみのバッファを再構築。
  注意: NFKC は合成文字で長さが変わるため、シャドウバッファは元エントリへの逆引きオフセットテーブルを持つ。
- パス復元はオンデマンド: `parentFrn` チェーンを辿る(NTFS ルート FRN = 0x…0005)+ ディレクトリパスの LRU。
- **事前ソート済み順列配列**(Everything の "fast sort"): name / size / mtime / path それぞれの順序で
  エントリ index を並べた `int[]`。列ヘッダークリック = 現結果セットをマークして順列配列を歩き、
  マーク済みを回収するだけ。ソートは実行しない。→ 10万件結果の再ソート < 50 ms。

## 3. メタデータ補完パス(size / mtime)— ★設計上の要注意点

**USN レコード(ENUM_USN_DATA / READ_USN_JOURNAL の出力)には
ファイルサイズもタイムスタンプも入っていない**(FRN、parentFRN、FileName、FileAttributes、レコード自身の
TimeStamp のみ)。Everything の「1M ファイル ≈ 1分」はサイズ・日付インデックス込みの数字であり、
生スイープとは別の作業。ここを見落とすと `size:` / `dm:` フィルターと結果一覧の列が実装不能になる。
段階設計:

1. **ベースラインスイープ(名前のみ)**: `FSCTL_ENUM_USN_DATA` で FRN/parent/attrs/名前を取得。
   NVMe で 100k–1M records/s。**この時点で名前検索は完全に使える**(サイズ・日付列は「取得中」)。
2. **補完スイープ(バックグラウンド・低優先度)**: サービスがボリューム全体を
   `GetFileInformationByHandleEx(FileIdExtdDirectoryInfo)` 系のディレクトリ列挙で走査
   (1 ディレクトリ読みで 128-bit FileId + size + タイムスタンプが同時に取れる)。FileId で Entry に JOIN。
   ウォームで ~1.8M entries/s、コールドはディスク律速。完了までは:
   - `size:`/`dm:` フィルターは UI に「メタデータ収集中 n%」を表示
   - 検索結果の可視行だけオンデマンド stat(結果ビューは仮想化されているので可視行のみで安い)
3. **継続更新**: USN 追尾で `USN_REASON_CLOSE`(データ変更含む)を観測したら、サービスが
   `OpenFileById`(**`dwDesiredAccess = FILE_READ_ATTRIBUTES`、`dwFlagsAndAttributes = FILE_FLAG_BACKUP_SEMANTICS
   | FILE_FLAG_OPEN_REPARSE_POINT`** — この2フラグが無いとディレクトリの FRN で失敗し、かつクラウド
   プレースホルダーを開いた際にハイドレートが誘発され得る。属性のみのオープンは絶対にこの組み合わせで行う)
   + `GetFileInformationByHandleEx(FileBasicInfo/FileStandardInfo)` でその FRN の size/mtime を引き、
   `MetaUpdate{vol, frn, size, mtimeUtc}` フレームでアプリへプッシュ。変更レートは低いので per-file コストは許容。
4. **リスティングビューは絶対にインデックスのメタデータに依存しない**(常に実列挙)。列が最重要の場面では
   常に正しい値が出る。

## 4. クエリパイプライン

1. クエリ文字列 → 折りたたみ → 項 分割(空白=AND、`|`=OR、`!`=NOT、`path:` `ext:` `size:` `dm:` —
   Everything 文法サブセット。パワーユーザーは既に知っている)。
   **v1 の値文法(確定 — これ以外を発明しない)**:
   - `size:` — `{>,<,>=,<=}N{kb,mb,gb}`(例 `size:>10mb`)、または範囲 `N{単位}..M{単位}`
   - `dm:`(更新日時) — 相対キーワード `today`/`yesterday`/`thisweek`/`thismonth`/`thisyear`、
     絶対日付 `YYYY`/`YYYY-MM`/`YYYY-MM-DD`、比較演算子付き `{>,<,>=,<=}YYYY-MM-DD`、
     範囲 `YYYY-MM-DD..YYYY-MM-DD`
   - M10 の受け入れテストに `dm:` を使うクエリを最低1件含める(現状 `ext:`/`size:` のみで `dm:` 未検証のため)。
2. マルチスレッド分割**部分文字列スキャン**(`SpanHelpers.IndexOf` = SIMD memchr 系)を折りたたみ済み
   バッファに対して実行。コアごと 1 チャンク、結果マージ。スキャン速度 100–500+ MB/s/スレッドなので
   1–5M 名(25–125 MB)はマルチスレッドで余裕をもって 100 ms 未満。
   **v1 にトライグラム/Tantivy 系の転置インデックスは作らない** — 実測上 ≤5M 名では線形スキャンが勝つ。
   10M 超のマシンでプロファイルが要求した時だけ再訪。
3. **ランキング**: 完全一致 > 前方一致 > 単語境界一致 > 部分一致。フォルダー一致をブースト。
   クエリが延長されたら(`abc`→`abcd`)前回結果セットに対する絞り込みで再利用。
4. **デバウンスなし**。打鍵ごとに CancellationToken 付きでスキャン発火、飛行中スキャンは即中断。
   (固定デバウンスは体感遅延を足すだけ — スキャンが十分安いことがこの設計の前提であり、M8 ゲートで保証する)
5. 同じインデックスがアドレスバーのパス補完と Ctrl+P「どこへでもジャンプ」パレットに給電する。
6. **検索結果は一級のファイル行**: フルコンテキストメニュー、D&D、F2、ファイル操作、履歴ペインが
   検索ヒット上で直接動く。読み取り専用の別サーフェスにしないこと。

## 5. 鮮度とクラッシュ安全性

- サービスがボリュームごとに `FSCTL_READ_USN_JOURNAL` を **~250 ms 周期**で追尾、正規化レコードをプッシュ。
  アプリは Entry[] + アリーナへ差分適用(追記 + 墓石、アイドル時にコンパクション)。
  開いているタブへは行差分(挿入/削除/リネーム、選択保持)を配信 — 外部変更が **< 500 ms** で見える。
- **ジャーナルラップ回復プロトコル(必須)**: ボリュームごとに `(UsnJournalID, lastProcessedUsn)` を永続化。
  接続時に `FSCTL_QUERY_USN_JOURNAL` — JournalID 不一致、または lastUsn < FirstUsn
  (`ERROR_JOURNAL_ENTRY_DELETED`)なら**全 MFT 再ベースライン**(1M ファイル ≈ 1–10 s の列挙なので安い)。
  NTFS はアプリ停止中もジャーナルを書き続けるので、通常の再起動では取りこぼしゼロ。
- スナップショット(`<volume>.dfi`)はアトミック書き込み(temp + rename)+ チェックサム + 対応 USN
  チェックポイント。壊れていたら黙って再列挙。**修復コードは書かない。インデックスは使い捨て、真実は MFT。**
- サービス側設定(任意): `FSCTL_CREATE_USN_JOURNAL` でジャーナル拡大(目安 512 MB / 40万ファイル級ボリューム)。

## 6. 非 NTFS カバレッジ

| ボリューム | ベースライン | 鮮度 |
|---|---|---|
| NTFS | FSCTL_ENUM_USN_DATA | USN 追尾(リアルタイム) |
| ReFS / Dev Drive | **ディレクトリウォーク**(MFT が存在しない — ENUM_USN_DATA 不可) | USN_RECORD_V3 追尾(`READ_USN_JOURNAL_DATA_V1.MinMajorVersion/MaxMajorVersion=2..3` で交渉。`MFT_ENUM_DATA_V1` は NTFS 側の ENUM ベースライン専用で ReFS には使わない) |
| FAT32 / exFAT | ディレクトリウォーク | ReadDirectoryChangesW + 間隔再走査 |
| ネットワーク (SMB) | ディレクトリウォーク(ユーザー選択ルートのみ) | 間隔再走査のみ(**FSCTL 群は SMB 越しで全滅** — ジャーナル追尾不可能)。「HH:MM 時点」バッジ表示 |

## 7. 劣化モード(サービス未導入時)

フォルダーインデックスエンジン(並列 `NtQueryDirectoryFileEx` ツリーウォーク + ReadDirectoryChangesW +
間隔再走査 — Everything の非 NTFS モードと同じ)が、ユーザー選択ルートを非特権でインデックスする。
UI は率直に表示: 「全ドライブ瞬間検索には darask-filer サービスが必要です」+「制限付き検索」バッジ。

## 8. 性能ゲート(M8/M9 受け入れ基準 — バイナリ判定)

- 1M 合成名で RSS 増分 ≤ 150 MB
- クエリ p95 < 100 ms、典型 < 50 ms(全マシンインデックス)
- 日本語部分文字列(かな/漢字/全角)が正しくヒットし、元名は無加工で表示される
- 10万件結果の列再ソート < 50 ms
- 破損スナップショット → 自動再ベースライン、クラッシュなし
- **検索オラクル**: 同一フィクスチャに対する素朴な LINQ 線形実装と 10k ランダムクエリ
  (日本語・幅折りたたみ形・サロゲートペア含む)で完全一致 — 常設 CI テスト

## 9. 逃げ道(コンティンジェンシー)

M8 のバイナリゲート(RSS/レイテンシ)が C# 実装で万一達成不能な場合の**事前設計済みの継ぎ目**:
`Darask.Index` のホットパス(アリーナ + スキャン + 順列配列)だけを **in-proc C-ABI Rust cdylib**
(~15 関数、opaque handle、UTF-16 境界、ファズテスト付き)に置換する。パイププロトコルは無関係・無変更
(インデックスはパイプの手前=アプリ内にあるため、「パイプの裏で Rust 化」は構造的に不可能 — 審査で確認済み)。
参照 crate: usn-journal-rs(ただしこの逃げ道はインデックスのみ。サービスの FSCTL 層は C# のまま)。
**このパスは M8 ゲート失敗まで着手禁止。**
