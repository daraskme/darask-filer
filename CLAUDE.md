# darask-filer — 実装エージェント向けガイド

Windows 11 向けエクスプローラー上位互換ファイラー。C# 14 / .NET 10 LTS / WPF。
**実装を始める前に必ず docs/ を読むこと**(下の地図参照)。設計は確定済み — ここに書かれた決定を
再検討・再選定しないこと(変更が必要だと確信したら、先にユーザーへ理由を添えて確認する)。

## ドキュメント地図

| ファイル | 内容 |
|---|---|
| docs/01-requirements.md | 要件・性能目標数値・エクスプローラーパリティチェックリスト(MUST/SHOULD/LATER) |
| docs/02-architecture.md | 技術選定(却下理由込み)・プロセス構成・モジュール・スレッディング/IPC 規約・配布 |
| docs/03-search-index.md | Everything 型インデックス・**size/mtime 補完パス(重要な罠)**・クエリ・ラップ回復 |
| docs/04-history.md | 履歴機能: スキーマ・3層インジェスト・同一性・スプール・プライバシー |
| docs/05-shell-integration.md | コンテキストメニュー・IFileOperation・D&D・サムネイル・プレビュー(機構名まで確定) |
| docs/06-ui.md | WPF 仮想化規約・カスタムコントロール一覧・IME 検証 |
| docs/07-milestones.md | M0–M17。**順番厳守・受け入れゲート厳守** |

## ビルド / テスト

```
dotnet build darask-filer.slnx -c Release       # PublishReadyToRun は publish 時
dotnet test tests/Darask.Tests
dotnet test tests/Darask.UiTests                # FlaUI(実デスクトップセッション必要)
tools/accept/Mxx.ps1                            # マイルストーン受け入れスクリプト(回帰込み)
tools/mkfixture --profile 100k --seed 42 --out <dir>
DaraskFilerd --console                          # サービスを昇格コンソールで実行(M6+)
```

## 絶対規則(違反は必ずバグになる — 全セッションで遵守)

### スレッディング
1. **UI スレッドで I/O 禁止**(列挙・アイコン・サムネイル・stat 一切)。デバッグビルドの Dispatcher
   watchdog が 5 ms 超のブロックを assert する。
2. **IFileOperation / IContextMenu は STA 専用** → メッセージポンプ付きの長命 ShellWorker STA スレッド
   1本に集約(Files の ThreadWithMessageQueue.cs 参照)。UI スレッドでもMTA プールでも動かさない。
3. アイコン/サムネイルの BitmapSource は `Freeze()` してから UI へディスパッチ。HBITMAP は必ず DeleteObject。

### WPF の罠
4. リスト系は `ScrollViewer.CanContentScroll="True"` 必須 — False にすると**仮想化が黙って死ぬ**。
   `VirtualizationMode=Recycling`、`ScrollUnit=Pixel` とセット。
5. **`SortDescriptions` 禁止**(200k 行で 3 分 vs 2 秒)。素の配列 + `Array.Sort` + StrCmpLogicalW → Reset。
6. **CollectionView グループ化禁止**(実測 30 倍遅い)。グループ化 = 平坦リスト + 合成ヘッダー行。
7. **トップレベル HWND に RegisterDragDrop 禁止**(DRAGDROP_E_ALREADYREGISTERED — WPF が所有済み)。
   **ファイルのドラッグは常に DoDragDrop**(自ウィンドウへのドロップも WPF ドロップイベントで受けて
   内部判定する — ドラッグ開始時点では内外を区別しない)。ポインターイベントだけの自前 D&D は
   タブ並べ替え・クイックアクセスのピン並べ替えなど**ファイルを運ばない UI 要素限定**(docs/05 §4)。
8. タブは ItemsSource 切替でビジュアルツリーが破棄される → 常時インスタンス化ビュー + Visibility 切替。

### インデックス / サービス
9. インデックスのホットパスで**ファイルごとの string 生成禁止** — パック UTF-8 アリーナ + struct 配列
   (docs/03 §2)。CI の RSS/割り当てゲートが落ちたらこの規則違反を最初に疑う。
10. **USN レコードに size/mtime は入っていない**(ENUM_USN_DATA も READ_USN_JOURNAL も)。
    サイズ・日付は補完パス経由(docs/03 §3)。ここを混同したコードを書かない。
11. 非昇格アプリは**ボリュームハンドルを絶対に開かない**。ジャーナル・MFT はすべてサービス経由。
    パイプにファイル内容を流さない(ファイル名/メタデータのみ — セキュリティ境界)。
12. インデックスは使い捨て。スナップショット修復コードを書かない — 疑わしければ再列挙。

### 正しさ
13. ファイル名は UTF-16 end-to-end。検索折りたたみ(ケース/NFKC 幅)は検索用コピーだけ。元名を加工しない。
    UTF-8 アリーナ(docs/03 §2)への格納は**非対サロゲートを含む任意の UTF-16 列をロスレス往復できる
    カスタムエンコーダ(WTF-8 相当)でのみ**許可 — 素の `Encoding.UTF8` は不正列を潰すため禁止。
14. OneDrive プレースホルダー(FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS)を列挙・サムネイルで
    ハイドレートしない。
15. 長パスは自前コードパス全部 `\\?\` + longPathAware。
16. テキスト入力サーフェスを追加したマイルストーンでは日本語 IME 実機検証を受け入れに含める。

## 作業の進め方

- docs/07-milestones.md の順で 1 マイルストーンずつ。**受け入れ基準はバイナリ判定** — 満たすまで完了と
  言わない。実測値を PROGRESS.md に記録。
- 受け入れスクリプトを `tools/accept/` に追加し、以後は回帰として全部回す。
- 参照実装: files-community/Files(シェル層)、wangfu91/UsnParser(USN)、GeeLaw/PreviewHost(プレビュー)。
  同一言語なので構造ごと読める。車輪を発明しない。
- ライブラリバージョンは docs/02 §1 のピンに従う。Vanara のラッパーが壊れていたら 4.2.1 フォールバック
  (ラッパー単位)を試し、それも駄目なら CsWin32 の生 interop で置換(アーキテクチャ変更にはならない)。
- コミットはマイルストーン内の論理単位ごと。メッセージは英語 conventional commits(feat/fix/perf/test/docs)。
