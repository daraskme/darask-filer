# 07 — 実装マイルストーン(M0–M17)

## 実装エージェントへの規律

- **順番厳守・ゲート厳守**。各マイルストーンは「動くもの + バイナリ受け入れ基準」で終わる。
  受け入れ基準を満たすまで次へ進まない。基準を勝手に緩めない(緩める必要を感じたら PROGRESS.md に理由を書き、
  ユーザーに確認する)。
- 各マイルストーン完了時に `PROGRESS.md` を更新(達成基準の実測値・既知の逸脱・次の前提)。
- 受け入れスクリプトは `tools/accept/Mxx.ps1` に置き、以後の全マイルストーンで回帰実行できるようにする。
- 参照実装(docs/02 §8)は「読む」もの。ライセンス表記の上で構造移植は可(Files は MIT)。
- 性能受け入れはリリースビルド(R2R)+ mkfixture の決定論フィクスチャで測る。

---

## M0 — リポジトリ基盤 + フィクスチャ生成器

ソリューション骨格(docs/02 §3 の全プロジェクト、Directory.Build.props、.editorconfig、xUnit 配線、
GitHub Actions: ビルド + 単体テスト)。`tools/mkfixture`: **決定論的**(シード固定・チェックサム付き)合成ツリー
生成器 — 100k / 1M エントリプロファイル、日本語・NFC/NFD・サロゲートペア(**非対サロゲート単体も含む**)・
全角・`\\?\` 級長パス・深いネストのコーパス内蔵。**画像モード**(`--images N`): M2 のサムネイルゲート用に、
決定論的に生成した有効な BMP/PNG を N 件含める。
**受け入れ**: `dotnet build` + `dotnet test` green。mkfixture が同一シードで同一チェックサムのツリーを再生成。
100k ツリー生成 < 60 s。`--images 50000` が壊れていない画像ファイル群を生成する。

## M1 — アプリシェル + 高速列挙

FluentWindow(Mica、ダークモード、タブ骨格、基本ナビゲーションペイン: ドライブ + 展開ツリー、
パンくず + Ctrl+L パス編集)+ `Darask.Enumeration`(NtQueryDirectoryFileEx via CsWin32 —
`Microsoft.Windows.WDK.Win32Metadata` パッケージ参照が必要、docs/02 §1 — `\\?\` 長パス)+
仮想化詳細ビュー + StrCmpLogicalW ソート(200k 行 < 200 ms を満たすため**並列マージソート**または
事前計算ソートキーを使ってよい — 単純な単一スレッド `Array.Sort` は基準未達の可能性がある)+
RDCW ウォッチャー(オーバーフロー→再走査)+ **ナビゲーション履歴スタック**(戻る/進む/上へ:
Alt+←/→/↑、マウス4/5ボタン、Backspace)+ ステータスバー(項目数・選択数・選択サイズ)+
隠し/システム項目トグル・拡張子表示トグル + 列のリサイズ/並べ替え。
**受け入れ**: 100k フィクスチャが Enter 後 < 300 ms で全面描画(SSD ウォーム。この定義を性能目標表
docs/01 §2.1 とも統一して使う)/ 10万行スクロールでフレーム落ちゼロ(PerfView レンダースレッド確認)/
300 文字超深パスと日本語ツリー(NFD・サロゲート含む)が正しく一覧/ ソート(名前/サイズ/日付)各
< 200 ms @100k(リリースビルド)/ スクリプトで 10k ファイルバースト生成し RDCW オーバーフロー経路を
強制 → 再走査後、列挙オラクルとの差分ゼロ / Alt+←/→/↑・マウス4/5ボタン・Backspace が Explorer 準拠で
動作 / ステータスバーが選択変更に追従 / **Ctrl+L パス編集ボックスで MS-IME インライン変換・候補ウィンドウ
位置が正しい(実機 Windows 11 + MS-IME で検証)**。

## M2 — アイコン + サムネイルパイプライン

SHGFI 拡張子高速パス、IShellItemImageFactory 優先度キュー、Freeze()+ディスパッチ、LRU キャッシュ、
アイコングリッドビュー(VirtualizingWrapPanel)、一覧(List)ビュー + Ctrl+Shift+1..8 ビュー切替、
ショートカット矢印オーバーレイ(SHGFI_OVERLAYINDEX)、desktop.ini カスタムフォルダーアイコン表示。
Vanara 5.0.5 の ShellItemImages / IShellItemImageFactory 経路のスモークテストを最初に書く
(壊れていたら 4.2.1 へ)。
**受け入れ**: 100k フォルダー初回描画で拡張子アイコン即表示(UI スレッドのディスク I/O ゼロ — ETW 検証)/
可視行サムネイル < 500 ms、高速スクロールでキャンセル / OneDrive プレースホルダーが絶対にハイドレート
されない(属性検証)/ mkfixture `--images 50000` のグリッドがストールなしでスクロール /
Ctrl+Shift+1..8 で各ビューモードへ切替 / .lnk ファイルにショートカット矢印が表示される /
desktop.ini で IconResource を指定したフォルダーがカスタムアイコンで表示される。

## M3 — IFileOperation エンジン + ごみ箱 + アンドゥ

ShellWorker STA スレッド(ポンプ付き)、Vanara ShellFileOperations + progress sink(PostDeleteItem 含む
全 Post イベント)、操作ジャーナル、Ctrl+Z/Ctrl+Y、**Enter/ダブルクリックでの起動**(ShellExecuteEx
既定動詞)+ **.lnk 解決**(IShellLink)。
Vanara 5.0.5 の使用ラッパー(ShellFileOperations/RecycleBin)のスモークテストを最初に書く(壊れていたら 4.2.1 へ)。
**受け入れ**: コピー/移動/リネーム/削除が**本物の** Explorer 進捗・衝突ダイアログを正しい親ウィンドウで表示 /
Del でごみ箱 + Ctrl+Z で復元 / Shift+Del は確認付き完全削除 / move/rename/copy の undo が逆再生 /
衝突リネーム結果(`file (2).txt`)が実際の最終名で記録される / 日本語名・長パスで全操作成功 /
アプリ・ドキュメント・フォルダーへの .lnk(日本語パス含む)が正しく解決・起動する。

## M4 — クリップボード + ドラッグ&ドロップ

OLE クリップボードプロトコル一式、ドラッグアウト(シェル IDataObject + DoDragDrop + IDragSourceHelper)、
ドロップイン(WPF イベント + IDropTargetHelper)、仮想ファイル受け入れ。
**受け入れ**: 本アプリで Ctrl+C → Explorer で Ctrl+V(逆も)/ 切り取りセマンティクス完全
(PERFORMEDDROPEFFECT)/ ファイルを Explorer・Outlook 作成画面・ブラウザーアップロードへドラッグ —
全部ドラッグイメージ付きで受理 / Outlook 添付をドラッグイン → ファイル実体化 / 内部ドラッグ行→フォルダーが
正しく移動。

## M5 — シェルコンテキストメニュー

ContextMenuHost(Vanara ShellContextMenu、TrackPopupMenuEx、HandleMenuMsg2 転送)、背景メニュー +
新規作成サブメニュー、プログラムから開く、送る、プロパティ(SHObjectProperties/SHMultiFileProperties)、
3 秒ウォッチドッグ + スタックサンプリングによる犯人モジュール帰属 + 名指しトースト +
CLSID 個別ブロックリスト(docs/05 §1 の四段構え)。ShellContextMenu/ShellFolder ラッパーのスモークテスト。
**`tools/hang-ext`**: テスト用 IContextMenu 拡張(C#、ComVisible)— `QueryContextMenu` で 10 秒 Sleep する。
受け入れスクリプトが `HKCU\Software\Classes\*\shellex\ContextMenuHandlers` に登録/解除する。
**受け入れ**: 7-Zip + TortoiseGit 導入機で右クリック → 両方がアイコン・動的サブメニュー付きで動作 /
「新規作成 → テキスト ドキュメント」が作成 + リネーム開始 / 複数選択プロパティが本物のマルチファイルシートを
表示 / 非病的フォルダーでメニュー表示 < 300 ms / `tools/hang-ext` 登録状態で右クリック →
UI がフリーズしない(STA 隔離)→ 3 秒後トーストに `hang-ext` のモジュール名が表示される →
ShellWorker が新スレッドに切り替わり次の操作が正常に処理される → 次回右クリックで hang-ext が
ブロックリストにより個別除外され、メニューが正常速度で表示される。

## M6 — インデックスサービス第1部(コンソール)

`DaraskFilerd --console` を昇格実行: C: の FSCTL_ENUM_USN_DATA スイープ、件数とレートを出力。
参照: wangfu91/UsnParser。
**受け入れ**: NVMe で > 100k records/s / C: 全体(≈50万–100万)を < 15 s で列挙 / 日本語ファイル名が
バイト精度でラウンドトリップ(USN レコードは UTF-16)。
(注: このスイープに size/mtime は含まれない — docs/03 §3。ここでは名前/FRN/attrs のみが正しい)

## M7 — インデックスサービス第2部(追尾 + ラップ)

FSCTL_READ_USN_JOURNAL ループ(~250 ms 周期)、リネームペア相関、CLOSE 単位 reason 集約、
(JournalID, lastUsn) チェックポイント、ラップ→再ベースライン。
**受け入れ**: スクリプトによる create/rename/move/delete の嵐(日本語名・ハードリンク含む)が各 < 1 s で
出力に現れ、リネームは単一ペアイベント / `fsutil usn deletejournal /d` → 再起動で ID 変化検出、
再ベースライン、明示的 gap 発行 / 追尾中の kill -9 → チェックポイントからクリーン再開。

## M8 — パックインデックス + 検索エンジン(in-proc)

Entry 構造体 + UTF-8 アリーナ + 折りたたみバッファ(ケース + NFKC 幅、トグル対応)+ マルチスレッド
部分文字列スキャン + ランキング + 順列配列 + チェックサム/USN チェックポイント付きスナップショット。
デバウンスなし・CancellationToken 中断。
**受け入れ**: 1M 合成名で RSS 増分 ≤ 150 MB / クエリ p95 < 100 ms・典型 < 50 ms / 日本語部分文字列
(かな/漢字/全角)正解、`ﾌｧｲﾙ` で `ファイル` がヒット(トグル OFF で挙動が変わる)/ 10万件結果の列再ソート
< 50 ms / 破損スナップショット → 自動再ベースライン、クラッシュなし / **検索オラクル: 素朴 LINQ 実装との
10k ランダムクエリ完全一致(日本語・幅折りたたみ・サロゲート含む)— 常設 CI テスト化**。

## M9 — サービス分離 + パイプ + メタデータ補完 + 劣化モード

本物の Windows サービス(LocalSystem)、名前付きパイププロトコル(docs/02 §4 のフレームカタログを
**全部**実装: Hello/Volumes/SubscribeBaseline/SubscribeTail/TailEvent/MetaUpdate/Gap に加え
DrainSpool/SpoolAck は M12 で使うがフレーム自体はここで定義・実装する)、パイプ終端の強化
(FILE_FLAG_FIRST_PIPE_INSTANCE・PIPE_REJECT_REMOTE_CLIENTS・DACL・クライアント側サーバー検証)、
アプリ側パイプクライアントが M8 のインデックスへ給電。**メタデータ補完**: バックグラウンド補完スイープ
(FileIdExtdDirectoryInfo 列挙 → FileId JOIN)+ 変更ファイルの OpenFileById(FILE_FLAG_BACKUP_SEMANTICS
付き)stat + MetaUpdate プッシュ。サービス不在時のフォルダーインデックス(劣化モード)。
**受け入れ**: 非昇格アプリがパイプ経由で C: 全体のインデックスを取得 / サービス RSS < 50 MB /
パイプファズ(不正フレーム)でサービスが落ちない + **どの RPC 列でもファイル内容が返らないことを assert** /
**名前付きパイプのスクワッティング**(サービス起動前に同名パイプを非特権プロセスが作成)を試みても、
クライアントがサーバーが SYSTEM でないことを検出して接続を拒否する / 補完スイープ完了後
`size:>10mb` フィルターが列挙オラクルと一致 / **1M フィクスチャでベースライン+補完スイープ完了まで
≤ 60 s(NVMe ウォーム、リリースビルド)** / サービス停止状態でも選択ルートを非特権インデックスし検索可
(「制限付き」バッジ)/ E2E: Explorer でファイル作成 → 本アプリ検索に < 2 s で出現。

## M10 — 検索 UX

タイトルバー検索ボックス、Everything 文法フィルター(path:/ext:/size:/dm:、AND/OR/NOT、docs/03 §4 の
値文法)、Ctrl+P ジャンプパレット、インデックス給電のアドレスバー補完(環境変数展開・引用符付きパス貼り付け・
UNC 入力)、検索スコープ切替(現在フォルダー/グローバル)、ストリーム表示の仮想化結果。
**結果行は一級のファイル行**(右クリック・D&D・F2・履歴ペインが動く)。**USN 追尾差分の開タブ反映**:
インデックス済みボリュームで外部変更があった場合、開いている該当フォルダータブへ行差分
(挿入/削除/リネーム、選択保持)をプッシュ(docs/02 §5.4)。
**受け入れ**: 全マシンインデックスで打鍵→初回結果 < 100 ms 体感 / `ext:pdf 請求書` 型の混合クエリ正解 /
`dm:thismonth` 等の日付クエリが正解 / 結果を開くとタブがそのフォルダーへ移動し項目選択 /
結果行で右クリックメニューと D&D が動く / `%USERPROFILE%` 展開・引用符付きパス・UNC 入力が動作、
検索スコープを現在フォルダーとグローバルで切替可能 / Explorer で開いているフォルダータブ対象の
フォルダーにファイルを作成/リネーム/削除 → < 500 ms で行反映、選択が保持される /
**検索ボックスで日本語 IME 入力(変換確定前の打鍵ごと検索を含む)が正しく動作(実機 MS-IME)**。

## M11 — 履歴第1部(アプリ内層)

SQLite イベントストア(docs/04 §2)、Tier 1 インジェスト(ナビゲーション + progress-sink ジャーナル =
アンドゥと共有)、FileIdInfo による同一性インターン、アクティビティサイドバー + フォルダー履歴タブ +
Ctrl+Shift+E パレット。
**受け入れ**: アプリ内での訪問/リネーム/移動がタイムラインに正しい同一性追従で出る(リネームしても履歴が
ついてくる)/ 同一性ステッチと墓石の単体テスト green / 10k イベントのスクリプトセッション後 DB < 3 MB
(docs/04 §5 の目安: 100–200 バイト/イベント)。

## M12 — 履歴第2部(USN 層 + スプール + 外部オープン + プライバシー)

サービスの正規化 USN イベント → 履歴ライター、gap イベント(判定条件は docs/03 §5 / docs/04 §4 と統一:
UsnJournalID 変化、または lastProcessedUsn < FirstUsn、または ERROR_JOURNAL_ENTRY_DELETED)、
**クライアント不在時スプール**(サービス専有 DACL。`DrainSpool`/`SpoolAck` フレーム経由でのみアプリへ渡す
— アプリはスプールファイルを直接開かない)、Recent-Items .lnk ウォッチャー(アプリプロセス)、
インジェスト除外、一時停止、項目忘却、全消去、リテンション/ロールアップ。
events スキーマの gap/pause 表現(docs/04 §2: volume_serial 列、file_id/parent_dir_id の NULL 許容)。
**受け入れ**: メモ帳でファイル編集 → タイムラインに「変更(darask-filer 外)」が < 3 s /
`fsutil usn deletejournal /d` スクリプト(JournalID 変化パス)→ 可視 gap マーカー /
**真のリングラップテスト**: `FSCTL_CREATE_USN_JOURNAL` でジャーナルを数 MB に縮小 → 追尾停止中に
上限を超える churn を生成 → 再起動 → 正しい区間の gap イベントが発生(FirstUsn 判定パスの検証) /
アプリ完全終了中に外部でリネーム → 次回起動時に `DrainSpool` 経由で履歴に反映され、`SpoolAck` 送信後
サービス側スプールが切り詰められる / 除外グロブ(`node_modules`)は churn 下で 0 行 /
全消去で db/-wal/-shm 消滅 / 100 万イベント合成負荷で < 200 MB、500 MB キャップでプルーニング作動。

## M13 — プレビューペイン + 組み込みビューア

PreviewHost 移植(CLSCTX_LOCAL_SERVER、IPreviewHandlerFrame)、ハンドラーごとデナイリスト、
組み込み画像/テキストフォールバックビューア。
**受け入れ**: txt/docx/xlsx/pdf が導入済みハンドラーでプレビュー / prevhost.exe を kill してもアプリ無傷 /
Excel プレビューで Tab/Esc フォーカス往復 / ハンドラークラッシュで組み込みビューアに自動フォールバック。

## M14 — タブ/ペイン/筋肉記憶/ZIP 完成

永続タブコンテンツ、ドラッグ並べ替え、デュアルペイン、フル Explorer キーマップ、フォルダー別ビュー永続化、
先行入力選択、ラバーバンド選択・Ctrl/Shift クリック多重選択、列の追加/削除 UI、
ナビゲーションペイン完成(クイックアクセスのピン留め/並べ替え/ドラッグターゲット、OneDrive ルート)、
セッション復元(RegisterApplicationRestart + 5 秒永続化)、**ZIP: 全展開ウィザード + ZIP 作成**
(`System.IO.Compression`、進捗付き)。**ZIP の日本語名エントリ対応(必須・見落とし厳禁)**:
起動時に `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)` を登録。展開時、UTF-8
汎用フラグの立っていないエントリはシステム ANSI コードページ(`GetACP()`。日本語 Windows では 932)で
`entryNameEncoding` を指定してデコード(レガシー Explorer の「圧縮フォルダー」機能は CP932 で
フラグなし書き込みをするため、これを怠ると文字化けする)。フラグ付きエントリはそのまま UTF-8 で読む。
**作成時は常に UTF-8 フラグ付きで書き込む**。
**受け入れ**: スクリプト化キーボードパリティチェックリスト(F2 セマンティクス: 拡張子除外選択・複数選択
`base (1)`・リネーム中 Tab、Ctrl+Shift+N、Alt+Enter、Ctrl+L、Ctrl+Shift+C、Ctrl+ホイール、先行入力、
Alt+←/→/↑・マウス4/5ボタン・Backspace、ラバーバンド選択、Ctrl/Shift クリック)100% green /
kill -9 → 再起動でタブ/パス復元 / 10 タブ × 10万件でタブ切替 < 50 ms / zip 作成→Explorer で開ける /
**CP932・UTF-8 フラグなしの ZIP(mkfixture で決定論的に生成)を日本語名エントリ込みで文字化けなく全展開
できる**、かつ現代の UTF-8 フラグ付き ZIP も正しく展開できる / ナビゲーションペインに OneDrive ルートが
表示される / **F2 リネームボックスで MS-IME インライン変換・Tab 変換サイクル・候補ウィンドウ位置が
正しい(実機)**。

## M15 — シェル名前空間ルート + 非 NTFS

ごみ箱/ネットワーク/MTP の IShellFolder ブラウズ(またはルートごとの明示的「エクスプローラーで開く」
フォールバック)、ReFS ウォーク + V3 追尾、FAT/ネットワークのフォルダーインデックス + 鮮度バッジ。
**受け入れ**: ごみ箱がブラウズでき復元が動く / **Android スマホ(MTP)が最低限一覧 + コピーアウト、
またはラベル付きフォールバックボタン** / Dev Drive(ReFS)がインデックスされ変更追尾される /
exFAT USB が検索可 +「HH:MM 時点」バッジ / UNC 共有(`\\server\share`)と `\\wsl.localhost` をブラウズできる。

## M16 — パッケージング + 自動更新

Velopack インストーラー + デルタ自動更新チャネル、初回起動時のサービスオプトイン導線
(`DaraskFilerd --install` 昇格実行: `%ProgramData%\darask-filer\service\` への配置 + DACL 設定 +
その場所を指す ImagePath でのサービス登録。docs/02 §7)、サービス自己更新(署名検証付き自己置換、
追加 UAC なし)、署名、アンインストール時のサービス除去(**昇格 UAC を1回追加要求**して
`DaraskFilerd --uninstall` を実行)。
**受け入れ**: まっさらな Win11 25H2 VM でインストール(UAC なし)→ 実行 → サービス有効化(UAC 1回)→
自動更新(サービス側は署名検証のみで UAC なしの自己更新)→ アンインストール(UAC 1回、サービスも消える)
が全部通る / **登録された ImagePath が `%LOCALAPPDATA%` 等ユーザー書き込み可能なパスを指していないことを
確認するネガティブテスト**(サービス設置先の DACL が Administrators/SYSTEM のみ書き込み可であることも
検証)/ 非管理者インストールでも劣化モードで全機能(検索は制限付きバッジ)。

## M17 — リリースゲート(出荷判定)

ベンチハーネス(対 現行 Explorer 同一マシン比較)、48 時間耐久ソーク、アクセシビリティ/IME ストレス、
セキュリティネガティブテスト総仕上げ。
**受け入れ**: ウォーム起動 < 500 ms(常駐 < 100 ms)/ 100k フォルダー表示が同一マシンの現行 Explorer
(最新 KB 適用)比 **≥ 5×** / 48 h ソークでサービス RSS 安定・ハンドルリークゼロ(アプリ・サービス両方)/
Narrator + MS-IME 接続状態で 10万行スクロールがフレームクリーン / **ja-JP リソースで全 UI 文字列が表示され、
125%/150% 混在のマルチモニター環境で Per-Monitor DPI が正しく機能する** / M1–M16 の全受け入れスクリプトが
リリースビルドで green / ベンチ結果を `docs/benchmarks/` にコミット。

---

## O1(post-v1、任意)— インデックス直結の瞬間フォルダー表示

インデックス済み NTFS ボリューム限定のオプトイン高速パス: ナビゲーション時、まず RAM 上の
children-by-parentFRN で名前+アイコンを**即座に**描画し、並行して通常列挙(M1 経路)を走らせて
size/mtime 供給とドリフト修復を行う(列は「確定した順」に埋まる)。
前提: M9 のメタデータ補完が安定していること。ENUM_USN_DATA に size/mtime が無い事実(docs/03 §3)を
このマイルストーンの設計前提として再掲する。
**受け入れ**: 100k フォルダーの初回名前ペイント < 20 ms / 列挙照合でドリフト検出時に行差分修正が走る /
オフ設定で M1 経路と完全同一挙動。
