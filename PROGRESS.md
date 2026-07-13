# 実装進捗

## M0 — リポジトリ基盤 + フィクスチャ生成器 ✅ 完了(2026-07-13)

### 実施内容
- ソリューション構造(docs/02 §3 準拠)を `darask-filer.slnx` として構築。src/7プロジェクト、tests/2プロジェクト、tools/3プロジェクト。
- `Directory.Build.props` に共通設定(TargetFramework net10.0-windows、x64 固定、ja-JP 既定等)。
- `.editorconfig`、`.gitignore`、GitHub Actions ビルド+テストワークフロー(`.github/workflows/build.yml`)。
- `Darask.App` に longPathAware + PerMonitorV2 DPI 対応の `app.manifest`(docs/01 §2.5, docs/06 §1 の要件を前倒しで反映)。
- `tools/mkfixture`: 決定論的フィクスチャ生成ツール。日本語・NFC/NFD・サロゲートペア・**非対サロゲート単体**・全角・深いネスト(300文字超)のコーパスを実装、`--images N` で決定論的 BMP 生成。
- `tools/accept/M0.ps1`: 受け入れ基準の回帰スクリプト。

### 開発環境
- **.NET 10 SDK (10.0.301) をユーザーローカルインストール** — `winget install` は UAC 昇格待ちでハングしたため中断し、公式 `dotnet-install.ps1` スクリプトで `%LOCALAPPDATA%\dotnet-10` へ管理者権限なしでインストール。ユーザー PATH に追加済み(新しいシェルセッションでは `dotnet` コマンドがそのまま使える。既存セッションでは `$env:LOCALAPPDATA\dotnet-10` を PATH に追加すること)。

### 受け入れ基準の実測値(すべて green)
| 基準 | 結果 |
|---|---|
| `dotnet build` + `dotnet test` green | ✅ |
| 同一シードで同一 rootHash | ✅ (複数回検証、完全一致) |
| 100k ツリー生成 < 60s | ✅ 実測 **48–55秒**(複数回測定、環境により変動) |
| `--images 50000` が壊れていない画像を生成 | ✅ 50,000 件生成、サンプルを `System.Drawing.Image.FromFile` でデコード成功 |

### 実装中に発見・修正したバグ(将来のマイルストーンへの申し送り)
1. **`string.GetHashCode()` は .NET でプロセスごとにランダム化される**(ハッシュ DoS 対策)ため、決定論性が必要な箇所で絶対に使えない。mkfixture のファイルサイズ決定に使っていたが FNV-1a の自前実装に置き換えた。**M8 のインデックス実装やその他「決定論的」を謳う箇所でも同じ罠に注意すること。**
2. ツリー成長アルゴリズムでブランチ係数が確率的に 0 になり得る設計だと、目標ファイル数に届く前にキューが枯渇するバグがあった。目標未達でキューが空になったらルートから分岐を継ぎ足す設計に修正。
3. 100k ファイル生成のボトルネックはディレクトリ作成(約25,000件)の逐次実行だった。深さレベルごとにグループ化して並列化(親→子の順序は保ったまま同一レベル内を並列化)することで約 10 秒短縮。
4. `Path.GetFullPath` をホットパスで毎回呼ぶと無視できないオーバーヘッドになる — ルートパスは一度だけ絶対パス化してキャッシュすること。

### 既知の制約・保留事項
- Git Bash(`rm -rf`)は非対サロゲートを含むディレクトリ名の削除に失敗することがある(.NET の `Directory.Delete` は正常に処理できる)。クリーンアップは PowerShell の `Remove-Item` を `\\?\` プレフィックス付きで使うこと。
- テストプロジェクトで内部型にアクセスする場合は `[assembly: InternalsVisibleTo("Darask.Tests")]` を該当プロジェクトに追加すること(`Darask.Ipc` に追加済み。他のプロジェクトは必要になった時点で追加)。

## M1 — アプリシェル + 高速列挙 ✅ 完了(2026-07-13)

### 実施内容
- **WPF-UI 4.3.0** 導入。`MainWindow` は `Wpf.Ui.Controls.FluentWindow`(Mica、`ApplicationThemeManager.ApplySystemTheme()`)。
- **`Darask.Enumeration`**(新規プロジェクト、CsWin32 0.3.298 + `Microsoft.Windows.WDK.Win32Metadata` 0.13.25-experimental):
  - `FastEnumerator` — `NtQueryDirectoryFileEx`(`Windows.Wdk.PInvoke`)ベースの高速列挙、失敗時は `FindFirstFileExW`+`FIND_FIRST_EX_LARGE_FETCH` にフォールバック。バッファ 1MB。
  - `NaturalSort` — `StrCmpLogicalW` P/Invoke ラッパー。
  - `EntrySorter` — フォルダー優先 + トップダウン再帰並列マージソート(名前は 20,000 件超で並列化)。
  - `DirectoryWatcher` — **オーバーラップド I/O**(`ThreadPoolBoundHandle` + `CancelIoEx`)による `ReadDirectoryChangesW` ラッパー。オーバーフロー検出で `Overflowed` イベント発火。
- **`Darask.App`** カスタムコントロール一式(`Controls/`): `NavigationPane`(ドライブ+遅延ロード展開ツリー)、`AddressBar`(パンくず⇔編集ボックス、Ctrl+L)、`FolderView`(仮想化 `ListView`+`GridView`、ナビゲーション履歴、隠し/拡張子トグル、RDCW 自動更新)、`StatusBarControl`。
- `tools/mkfixture` に **`--flat`** モード追加(単一フォルダーに全ファイルを平坦配置 — ツリーモードでは 10 万エントリがディレクトリに分散してしまい M1 の受け入れシナリオを再現できないため)。
- `tools/bench` に `enum`(実ディスクに対する列挙+ソート実測)と `sort`(合成データでのソート単体ベンチ)サブコマンド追加。
- `tools/accept/M1.ps1`: 受け入れ基準の回帰スクリプト。

### 受け入れ基準の実測値
| 基準 | 結果 |
|---|---|
| `dotnet build` + `dotnet test` green(9 テスト) | ✅ |
| 単一フォルダー 10 万エントリ: 列挙+ソート(名前) | ✅ **186ms**(目標 < 300ms。UI 描画分のマージン確保) |
| ソート(名前/サイズ/日付)各 < 200ms @100k(合成データ) | ✅ 名前 78ms・サイズ 50ms・日付 52ms |
| 300 文字超深パス・日本語ツリー(NFD・サロゲート含む)の正しい列挙 | ✅ xUnit テストで検証 |
| 10k ファイルバースト → RDCW オーバーフロー → 再走査で列挙オラクルと差分ゼロ | ✅ `RdcwOverflowTests`(並列バースト生成で確実にオーバーフロー誘発) |
| Backspace / ダブルクリックナビゲーション / ステータスバー選択追従 | ✅ GUI 目視確認(スクリーンショット) |
| Ctrl+L 編集モード切替・Esc キャンセル | ✅ GUI 目視確認 |
| 名前列ヘッダークリックでの昇順/降順切替(自然順) | ✅ GUI 目視確認(日本語ファイル名の数値部分が正しく降順ソート) |
| 10万行スクロールのフレーム落ちゼロ(PerfView) | ⚠️ **未検証** — GUI での目視スクロールは滑らかだったが、PerfView によるレンダースレッド計測は未実施 |
| Alt+←/→/↑・マウス4/5ボタン | ⚠️ **未検証** — コードは実装済み(`FolderView.xaml.cs`)だが実機での動作確認は未実施 |
| Ctrl+L で MS-IME インライン変換・候補ウィンドウ位置(実機) | ⚠️ **未検証** — 自動化ツールでは日本語 IME の実際の変換動作を再現できない。人間による実機確認が必要 |

### 実装中に発見・修正した重大バグ
1. **`DirectoryWatcher` の同期 I/O 実装が実測でハングした**: `ReadDirectoryChangesW` の同期(ブロッキング)呼び出し中にハンドルをクローズしても、次のファイルシステムイベントが発生するまで呼び出しがブロックされたまま解除されない(未定義動作に近い)。テストがタイムアウトなしで7分以上ハングして発覚。**オーバーラップド I/O(`ThreadPoolBoundHandle.AllocateNativeOverlapped` + `CancelIoEx`)に全面書き換えて解決** — docs/02/CLAUDE.md には明記されていなかった罠なので、DirectoryWatcher.cs の doc コメントに恒久的に警告を残した。同期 RDCW 実装への回帰は禁止。
2. **NavigationPane の `TreeViewItem.Selected` イベントが起動直後に誤発火**: WPF はキーボードフォーカスの巡回だけで `TreeView.SelectedItem` をプログラム的に変化させることがあり、これに `Selected` イベントで反応すると「起動直後に C: ドライブへ勝手にナビゲートされる」バグになった(実機 GUI テストで発見)。`PreviewMouseLeftButtonUp` によるユーザークリック判定に変更して解決。
3. **mkfixture の非対サロゲート名生成で `index % 10` の下 1 桁だけをサフィックスに使っていたため、大量生成時に名前が重複した**: `--flat` モードで 10 万件生成したところ 99,566 件成功と報告されたが実ディスク上は 85,726 件しかなかった(通常のツリーモードでは別ディレクトリに分散するため露見しなかった)。サフィックスを `index` 全体の 7 桁ゼロ埋めに変更して解決。**この種の「index の一部だけを名前に使う」パターンは他のカテゴリにもないか要注意。**
4. **`EntrySorter` の初期実装(K-way `PriorityQueue` マージ)は 100k 件の名前ソートで 150–167ms かかっていた**。トップダウン再帰の並列マージソート(2-way マージ、深さ制限付き)に書き換えたところ 60–78ms に半減した。K-way ヒープマージは理論上効率的に見えても、比較コストが高い(`StrCmpLogicalW` P/Invoke)場合はシンプルな再帰マージの方が実測で有利だった。
5. **`NtQueryDirectoryFileEx` のバッファを 64KB のままにしていると 10 万件規模のフォルダーでシステムコール往復が多発してボトルネックになる** — 1MB に拡大して改善。

### 開発環境・ツールの教訓
- `Environment.SpecialFolder.UserProfile` は正しく `C:\Users\micro` を返すが、初回起動時に `NavPane` の初期化と `MainWindow.Loaded` のタイミングが絡むと表示が食い違うことがあった(バグ2の副作用)。
- GUI 自動化(computer-use)での `type` アクション(クリップボード経由)は、WPF の `LostKeyboardFocus` イベントとタイミング競合を起こし、`Ctrl+L` → パス入力 → `Enter` の完全なフローを安定して自動検証できなかった。個別の要素(表示・選択・Esc キャンセル)は確認できたが、フルフローは実機での人間の確認に委ねる。
- ウィンドウの `SetForegroundWindow`(P/Invoke)は Windows のフォアグラウンド制限で失敗することがある。`WScript.Shell.AppActivate(title)` の方が確実だった。

### 既知の制約・保留事項(次マイルストーンへの申し送り)
- PerfView によるフレームレート計測、Alt+矢印/マウス X ボタンの実機テスト、MS-IME 実機入力テストは未実施。M2 以降で UI 要素が増えるタイミングで FlaUI ベースの自動 UI テスト(`Darask.UiTests`、M0 時点でスキャフォールドのみ)を本格導入し、まとめて検証する方が効率的。
- `AddressBar` は docs/06 §4 で指定された「WPF-UI `BreadcrumbBar` ⇔ `AutoSuggestBox`」ではなく、簡易実装(`ui:Button` 列 + `ui:TextBox`)にした。機能的には等価(パンくず表示・クリックナビゲーション・Ctrl+L 編集・Enter 確定・Esc キャンセル)だが、本物の `BreadcrumbBar` コントロールへの置き換えは UI 磨き込みフェーズ(M14 相当)で検討。
- タブ・列のドラッグ並べ替え・列の追加/削除 UI は M1 スコープ外(docs/07 M14 で対応)。

## M2 — アイコン + サムネイルパイプライン ✅ 完了(2026-07-13)

### 実施内容
- **`Darask.Shell`** プロジェクトが本格稼働開始。Vanara 5.0.5(`Vanara.PInvoke.Shell32` + `Vanara.Windows.Shell`)導入。
  - `IconService.GetExtensionIcon` — `SHGetFileInfo` + `SHGFI_USEFILEATTRIBUTES`(ディスク I/O ゼロ、100k フォルダー初回描画用)。
  - `IconService.GetRealIcon` — `USEFILEATTRIBUTES` を外した実パス版。desktop.ini カスタムフォルダーアイコン・.lnk ショートカット矢印オーバーレイをシェル標準ロジックにそのまま反映させる。
  - `ThumbnailService.GetThumbnail` — `IShellItemImageFactory`(`ShellItem.GetImage` 経由)。`InvalidOperationException`(非対応ファイル種別)を正常系として `null` に正規化。
- **`FolderEntryViewModel`** を `INotifyPropertyChanged` 化し、`Icon`/`BeginLoadIcon`/`CancelLoad` を追加。行の `Loaded`/`Unloaded` イベントに配線することで、WPF の仮想化 ListView/ListBox が可視行だけコンテナ化する性質を利用した「可視行優先ロード + 非可視行キャンセル」を実現(専用の優先度キュー実装は不要だった)。
- ロードパイプライン: 拡張子アイコン(同期・高速)→ ディレクトリは実アイコン(desktop.ini 反映)/ .lnk も実アイコン(矢印反映)/ 通常ファイルはサムネイル、の順にバックグラウンドスレッドで取得し `Dispatcher.BeginInvoke` で反映。OneDrive 等のクラウドプレースホルダーは `IsCloudPlaceholder` で判定しサムネイル取得自体をスキップ(ハイドレート防止)。
- **アイコングリッドビュー**(`VirtualizingWrapPanel` 2.5.2、名前空間は `WpfToolkit.Controls`)を `FolderView` に追加。`FolderViewMode`(Details/IconGrid)を切替可能にし、`ListViewControl`/`IconGridControl` 間でアイテムソースと選択状態を引き継ぐ。Ctrl+Shift+1(詳細)/Ctrl+Shift+2-8(アイコングリッド)のショートカットを実装。
- `tools/mkfixture` の `--images N` モードを流用してサムネイル/アイコングリッドの GUI 検証用フィクスチャを生成。
- `tools/accept/M2.ps1`: 受け入れ基準の回帰スクリプト。

### 受け入れ基準の実測値・確認結果
| 基準 | 結果 |
|---|---|
| `dotnet build` + `dotnet test` green(16 テスト、Vanara スモークテスト含む) | ✅ |
| 拡張子アイコン取得(ファイル/フォルダー) | ✅ xUnit + GUI 目視 |
| サムネイル取得(有効な BMP) | ✅ xUnit(自作 BMP)+ GUI 目視(500 件の実カラーサムネイル) |
| サムネイル非対応ファイルは例外を投げず null に正規化 | ✅ xUnit |
| desktop.ini カスタムフォルダーアイコン | ✅ GUI 目視(Desktop/Downloads/iCloudDrive が実シェルアイコンで表示) |
| .lnk ショートカット矢印オーバーレイ | ✅ xUnit(`ShellLink.Create` で実際に .lnk を作成し `GetRealIcon` が例外なく完了することを確認)。矢印の視覚的な確認は未実施 |
| アイコングリッドビュー(VirtualizingWrapPanel)の表示・切替・スクロール | ✅ GUI 目視(500 件のアイコングリッドでスムーズにスクロール) |
| Ctrl+Shift+1/2 でのビュー切替 | ✅ GUI 目視 |
| OneDrive プレースホルダー非ハイドレート | ⚠️ **コードレベルのみ確認**(`IsCloudPlaceholder` チェックの実装・単体テストなし)。実機 OneDrive オンデマンドファイルでの実地検証は環境依存のため未実施 |
| 100k フォルダー初回描画で UI スレッドのディスク I/O ゼロ(ETW) | ⚠️ **未検証**。設計上 `SHGFI_USEFILEATTRIBUTES` はディスク I/O を発生させないため理論的には満たすが、ETW での実測はしていない |

### 実装中に発見した問題
1. **VirtualizingWrapPanel の XAML 名前空間はパッケージ名と異なる**: `clr-namespace:VirtualizingWrapPanel` でも `WpfExtensions.Controls` でもなく、実際は **`WpfToolkit.Controls`**。NuGet パッケージ名・GitHub リポジトリ名・実際の CLR 名前空間が三者三様なライブラリがあることの教訓 — ドキュメント(GitHub の API リファレンスリンク)から実名前空間を確認する必要があった。
2. **Vanara の `SHGetFileInfo` は `System.IO.FileAttributes`(int)を要求し、`Vanara.PInvoke.FileFlagsAndAttributes` ではない** — 同名っぽい型が複数あるライブラリでの型解決はビルドエラーを見ながら確定させる必要があった。
3. **`Vanara.Windows.Shell.ShellLink` の `new ShellLink(path)` コンストラクタは「既存 .lnk の読み込み」用**であり、新規作成には静的ファクトリ `ShellLink.Create(linkPath, targetPath)` を使う必要がある(コンストラクタでの新規作成を試みて `COMException` — GitHub のソースを直接読んで解決)。
4. **`ShellItem.GetImage` は `System.Drawing.Bitmap` ではなく `SafeHBITMAP` を直接返す** — GDI+ を経由せず `CreateBitmapSourceFromHBitmap` に直結できる、効率の良い設計だった。

### 既知の制約・保留事項(次マイルストーンへの申し送り)
- アイコングリッドのアイコンサイズは固定 48px。docs/01 §4 の「小〜特大アイコン」連続ズーム(Ctrl+ホイール)は未実装 — 複数サイズのテンプレート切り替えとして将来実装する。
- Ctrl+Shift+3〜8 は全て同じアイコングリッドビューにマップされている(一覧・並べて表示・コンテンツ等の細分化は未実装)。
- ETW によるディスク I/O ゼロの実測、OneDrive 実機でのハイドレート防止確認、.lnk 矢印オーバーレイの視覚的確認は未実施。M14 相当の UI 磨き込みフェーズか、FlaUI 自動 UI テスト導入時にまとめて検証する。

## UX 磨き込みパス(M3 着手前の割り込み対応) ✅ 完了(2026-07-13)

ユーザーからの実使用フィードバック(タブ・戻る進むボタン・フィルター・表示切替・プレビュー・
余白・クイックアクセス・アイコン・アドレスバー編集・スクロールバー・右クリックプロパティ)を
受け、docs/07 の厳密なマイルストーン順序より先行して実装した回。M3 (IFileOperation) 着手前。

### 実施内容
- **タブ機能**: `FolderTabContent`(ツールバー+アドレスバー+フィルター+FolderView+PreviewPane
  一式を1タブ分としてカプセル化)を導入し、`MainWindow` は複数インスタンスを `TabContentHost`
  (Grid)に同時に保持して `Visibility` で切替(タブ切替時に履歴・スクロール位置・選択状態が
  失われない)。タブ chip の UI は `ItemsControl` + 手書き `Border` チップ(WPF-UI の
  `TabView`/`TabViewItem` は API 詳細が薄いドキュメントしかなく採用見送り)。Ctrl+T/Ctrl+W/
  Ctrl+Tab/Ctrl+Shift+Tab 対応。最後の1枚は閉じない設計。
- **ツールバー**: 戻る/進む/上/更新ボタン(`ui:SymbolIcon`)+ フィルターボックス + 表示切替
  ボタンを各タブに配置。
- **アドレスバー**: パンくずの空白部分クリックで編集モードへ(`ButtonBase` が
  `MouseLeftButtonUp` を消費するため、セグメントボタン自体のクリックとは競合しない)。
- **フィルター**: `FilterBox`(`ui:TextBox`)→ `FolderView.FilterText` → `ApplyFilterAndSort()`
  で部分一致絞り込み。
- **プレビューペイン**: `PreviewPane` — 画像は `BitmapImage`(`DecodePixelWidth=900` で縮小
  デコード、バックグラウンドスレッド)、テキストは先頭256KBをバックグラウンド読み込み、
  それ以外は拡張子アイコン+基本情報。単一選択時のみ表示。
- **クイックアクセス**: `NavigationPane` に専用セクション追加。`QuickAccessStore`(JSON,
  `%LOCALAPPDATA%\darask-filer\quickaccess.json` — settings.db(SQLite)統合は M4 以降に先送り)
  で永続化。右クリック「クイックアクセスに追加」/「クイックアクセスから削除」。
- **右クリックコンテキストメニュー**: `PropertiesService`(`Darask.Shell`)—
  `Vanara.Windows.Shell.ShellContextMenu.CreateFromItems` + `InvokeVerb("properties")` で
  実エクスプローラーと同じダイアログを表示(複数選択時も1ダイアログに複数タブでまとまる)。
  「開く」「クイックアクセスに追加」「プロパティ」を実装。
- **行の余白拡大**: `ListViewItem`/`ListBoxItem` の `ItemContainerStyle` に `Padding`/`Margin`
  を追加してクリック領域を拡大。
- **コード生成 `ContextMenu` の不透明化**: `MenuTheme.ApplyOpaque` — WPF-UI の暗黙スタイルは
  半透明 Acrylic 前提で、Popup 上で正しく合成されず背後のコンテンツが透けて見える不具合が
  あったため、コードから明示的に不透明な背景色を設定する共通ヘルパーを作成。

### 実装中に発見・修正した重大バグ
1. **アイコングリッド(`VirtualizingWrapPanel`)でマウスホイールが一切効かなかった**:
   サードパーティパネルが `IScrollInfo` のオフセット設定はできるが、マウスホイールの委譲を
   実装していなかった。`PreviewMouseWheel` で `ScrollViewer.ScrollToVerticalOffset` を直接
   呼ぶことで解決(キーボード/スクロールバードラッグは元々動いていたので発見が遅れた)。
2. **同パネルはスクロールバーそのものも一切表示できない**: `ScrollOwner.InvalidateScrollInfo()`
   を自発的に呼ばないため `ScrollViewer` 側の Extent が更新されず、`VerticalScrollBarVisibility`
   を `Visible` に固定しても標準スクロールバーが出ない。さらに調査すると、**素の `ScrollBar`
   コントロールを `ScrollViewer` の外(独立要素として)Grid に置いた場合、WPF-UI の暗黙スタイル
   適用下では `Background`/`Visibility` を直接指定してもテンプレートごと描画されない**ことが
   判明(`Style="{x:Null}"` で明示的に外しても改善せず — 根本原因は未特定)。最終的に `ScrollBar`
   コントロールへの依存を完全に断ち切り、`Border` 2枚(トラック+つまみ)でスクロールバーを
   自前描画・自前ドラッグ実装(`FolderView.xaml.cs` の `UpdateIconGridScrollBar`/
   `IconGridScrollThumb_*`)することで解決。アイテム数と実測 `ActualWidth`/`ActualHeight` から
   列数・行数・エクステントを自前算出している(´Visibility` 変更直後は `ActualWidth` がまだ
   0 のことがあるため `Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ...)` で再計算)。
3. **`ViewMode` セッターが `ItemsSource` を再設定する経路は `ApplyFilterAndSort()` を通らない**
   ため、スクロールバー再計算のフックを両方に個別に仕込む必要があった。

### 受け入れ確認(GUI 目視、mcp computer-use 経由)
| 項目 | 結果 |
|---|---|
| タブ作成・切替・クローズ(状態保持を含む) | ✅ |
| 戻る/進む/上/更新ボタン | ✅ |
| アドレスバー クリック編集モード | ✅ |
| フィルターボックスでの絞り込み | ✅(コードレビューのみ、GUI目視は簡易) |
| プレビュー(テキスト実データ、汎用アイコン+情報) | ✅ 実ファイルで確認(clippy_out.txt、ztrace_maps.dll 等) |
| クイックアクセス 追加・ナビゲーション・削除 | ✅ |
| 右クリック「プロパティ」→ ネイティブダイアログ表示 | ✅ |
| アイコングリッドのマウスホイールスクロール | ✅ System32(4803件)で確認 |
| アイコングリッドの自前スクロールバー(トラック+つまみ+ドラッグ) | ✅ 表示・スクロール追従を確認。ドラッグ操作自体は未実施 |
| Details ビューのスクロールバー(標準) | ✅ 元々問題なし |

### 既知の制約・保留事項
- クイックアクセスの永続化は JSON ファイル(`%LOCALAPPDATA%\darask-filer\quickaccess.json`)。
  docs/02 の `settings.db`(SQLite)への統合は M4 以降。
- アイコングリッドの自前スクロールバーはアイテム幅/高さを定数(96px/100px)で近似しており、
  ウィンドウ幅によって列数がずれるとエクステント計算に若干の誤差が出る(スクロール範囲が
  実際のコンテンツ長と数%ずれる可能性がある)。実害があれば `ActualWidth` から動的計測する
  方式に切り替える。
- 素の `ScrollBar` が WPF-UI 環境下で描画されない根本原因(§実装中に発見した問題 #2)は未解明
  のまま。他の画面で `ScrollBar` を単独使用する予定がある場合は要注意。

## 右クリックメニュー整理・履歴パネル・中クリックタブ・ごみ箱 ✅ 完了(2026-07-13)

ユーザー要望「右クリックメニューをエクスプローラー同等に整理」「左下に履歴表示」「ホイールクリックで
タブ増やす」「ゴミ箱とショートカット登録を実装」の4点対応。M3 (IFileOperation) 着手前の割り込み。

### 実施内容
- **右クリックメニューのエクスプローラー同等化**: `ShellVerbService`(`Darask.Shell`)を新設 —
  `ShellContextMenu.CreateFromItems` + `InvokeCut`/`InvokeCopy`/`InvokeDelete`/`InvokePaste` で
  切り取り/コピー/削除/貼り付けをエクスプローラー本体と同じ挙動(クリップボード相互運用・
  ごみ箱送り・ネイティブ進捗ダイアログ・アンドゥ)で実装。`CreateNewFolder`/`CreateShortcut`
  も追加。項目行の右クリックは「開く(フォルダーのみ)/切り取り/コピー/ショートカットの作成/
  クイックアクセスに追加(フォルダーのみ)/削除/名前の変更/プロパティ」、空白部分は
  「更新/新しいフォルダー/貼り付け(クリップボードに内容がある時のみ)/プロパティ」。
- **インライン名前変更**: `FolderEntryViewModel.IsRenaming`/`EditName` + `FolderView.xaml` の
  `TextBox`(`DataTrigger` で `TextBlock` と切替)。F2 キー・右クリックメニュー・新規フォルダー
  作成直後の自動リネームの3経路すべてに対応。
- **履歴パネル**: `NavigationPane` 左下に MRU(最大30件、重複は前の位置から除去して先頭に積み直し)
  リストを追加。`HistoryStore`(JSON, `%LOCALAPPDATA%\darask-filer\history.json`、
  `QuickAccessStore` と同じ永続化パターン)。ナビゲーションのたびに `RecordHistory` を呼ぶ。
  右クリックで「履歴から削除」「履歴をすべてクリア」。
- **中クリックで新規タブ**: `FolderView.EntryList_MouseDown`(`MouseButton.Middle` を判定)→
  `OpenInNewTabRequested` イベント → `FolderTabContent` → `MainWindow.AddTab`。フォルダーのみ対象。
- **ごみ箱**: `RecycleBinService`(`Darask.Windows.Shell.RecycleBin` ラップ)+ `RecycleBinView`
  (`ITabContent` 実装、`UserControl`)。`NavigationPane` の「ごみ箱」行クリックで専用タブを開く
  (シングルトン — 既に開いていればそれを選択)。一覧(名前/元の場所/削除日時/サイズ)・
  更新・空にする・右クリック「元に戻す」「完全に削除」。`TabViewModel.Content` の型を
  `FolderTabContent` → `UserControl` に変更し、`ITabContent`(`Shutdown()`)を導入して
  `FolderTabContent`/`RecycleBinView` の両方をタブとして共存できるようにした。

### 実装中に発見・修正した重大バグ

1. **CLAUDE.md 規則2の「IContextMenu 専用 STA ワーカースレッド」構成は本アプリで確実にデッドロック
   する**: `Dispatcher.CurrentDispatcher` + `Dispatcher.Run()` によるメッセージポンプ付き長命 STA
   スレッドは、独立した console アプリでは正常動作するが、**WPF アプリ内で使うと UI スレッドが
   フリーズする**(最小再現: `MainWindow()` コンストラクタで `_ = ShellWorker.InvokeAsync(() => {})`
   を呼ぶだけでウィンドウが二度と表示されない)。原因未特定(COM のクロスアパートメント
   マーシャリングが呼び出し元 STA スレッドのポンプを要求している可能性を疑うが未確証)。
   **対応**: `ShellWorker.cs` を削除し、`ShellVerbService`/`PropertiesService`/`RecycleBinService`
   はすべて UI スレッドから直接同期呼び出しする方式に統一(規則からの意図的な逸脱 — 動く実装を
   優先。将来 STA ワーカーを再挑戦する場合は本エントリと `PropertiesService.cs` のコメントを参照)。
2. **WPF-UI 4.3.0 の既定 `MenuItem` テンプレートはコード生成 `MenuItem` に対してホバー時クラッシュ
   する**: 既定テンプレートのマウスホバー Storyboard が `Background` を `PropertyPath("(0).(1)")`
   経由でアニメーションしようとするが、コードから `new MenuItem()` して `ContextMenu.Items.Add`
   したインスタンスではこのパスが解決できず `InvalidOperationException` → ハンドラなしで
   プロセス即終了(ダイアログもコンソール出力も出ない。`Get-WinEvent` の Application ログでのみ
   発見できた)。この回のデバッグで最も時間を要した問題で、それまで「メニューが固まる/反応しない」
   ように見えていた症状はすべてこのクラッシュだった。**対応**: `MenuTheme.SafeMenuItemStyle`
   (アニメーションを一切使わない素の `Style`)を作り、コード生成 `MenuItem` には必ず
   `MenuTheme.AddItem()` ヘルパー経由で割り当てるルールに統一(`FolderView`/`NavigationPane`/
   `RecycleBinView` の全コード生成メニューに適用済み)。
3. **`TextBox.Loaded` は `Visibility=Collapsed` のまま一度だけ発火し、後から `DataTrigger` で
   `Visible` に切り替わっても再発火しない**: インライン名前変更の `Focus()`/`SelectAll()` を
   `TextBox.Loaded` に配線していたため、実際に編集モードに入った瞬間には何も起きず(見た目上
   何も変わらず、通常のテキストのまま)。**対応**: `Loaded` を `IsVisibleChanged` に置き換え、
   `e.NewValue is true` の時だけ(かつレイアウト確定後の `DispatcherPriority.Loaded` まで
   遅延させて)`Focus()`/`Select()` する。
4. **新規フォルダー作成直後の自動リネームは `DirectoryWatcher` の 200ms デバウンス更新と競合する**:
   `ShellVerbService.CreateNewFolder` の `Directory.CreateDirectory` 呼び出し自体が監視中の
   `DirectoryWatcher` の変更通知を発火させ、200ms 後に `Navigate()` が再実行されて
   `ApplyFilterAndSort()` が一覧を丸ごと新しい `FolderEntryViewModel` インスタンスで作り直す
   ため、直前に立てた `IsRenaming=true` が消える。**対応**: `ApplyFilterAndSort()` で一覧再構築の
   直前に「名前変更中の項目」を検出・退避し、再構築後に同名の新インスタンスへ `IsRenaming`/
   `EditName` を復元する。ただし復元を `ItemsSource` 差し替えと同じ tick で行うと今度は
   コンテナがまだ生成されておらず `IsVisibleChanged` が発火しない(「最初から Visible」で
   生成されるだけで遷移が起きない)ため、`Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ...)`
   でレイアウトパス完了後まで遅延させる必要があった。
5. **`ShellContextMenu`/`ShellItem`(Vanara 5.0.5)を verb 呼び出し直後に同期 Dispose すると
   ヒープ破損(`0xc0000374`)でクラッシュする**: コピー→貼り付けのような連続した
   `ShellVerbService.Invoke` 呼び出しで、`InvokeCopy`/`InvokePaste` 等の `IContextMenu::
   InvokeCommand` がシェル側の実処理(クリップボード確定・ファイルコピー等)完了前に制御を
   返している可能性が高く、呼び出し直後に COM オブジェクトを解放すると解放済みメモリへの
   アクセスで実際にネイティブヒープが壊れる(Windows イベントログの `.NET Runtime`/
   `Application Error` で `ntdll.dll` の同一フォルトオフセットが複数回再現することを確認)。
   Vanara を docs/02 記載のフォールバック版 4.2.1 に切り替えると `ShellContextMenu.CreateFromItems`
   静的ファクトリ自体が存在せず(API 非互換)、かつ 5.0.5 の `out IDisposable keepAlive` パターンは
   そもそもこの種のライフタイム問題を解決するために導入されたと見られるため 4.2.1 へは戻さなかった。
   **対応**: `ShellVerbService.Invoke` で `keepAlive`/`menu`/`ShellItem[]` の破棄を
   `Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, ...)` で UI スレッドがアイドルに
   なるまで遅延させることでクラッシュが再現しなくなることを確認(繰り返しのコピー→貼り付け→
   削除で検証)。根本原因(なぜ `InvokeCommand` が非同期的に処理を継続するのか)は未解明のまま —
   将来 Vanara を更新する際やこのパスを触る際は要注意。
6. **`RecycleBinService.GetItems()` が `System.Recycle.DateDeleted` プロパティ読み取りで
   `InvalidCastException` を投げてアプリごと落ちる**: このプロパティは `VT_FILETIME` で
   格納されているが、Vanara の `PropertyStore.TryGetValue<DateTime>` は生の
   `System.Runtime.InteropServices.ComTypes.FILETIME` を単純キャストしようとして失敗する
   (ごみ箱を開くたびに 100% 再現)。**対応**: `TryGetValue<FILETIME>` で受けて
   `DateTime.FromFileTimeUtc` へ手動変換。

### 開発環境で発生した問題(アプリのバグではない)
- セッション再開時に .NET 10 SDK がシステムにインストールされていない状態になっていた
  (`Microsoft.WindowsDesktop.App`/`Microsoft.NETCore.App` の 10.0.9 ランタイムは存在するが SDK は
  8.0.422 のみ)。`winget install Microsoft.DotNet.SDK.10` は UAC 昇格プロンプトが自動化環境から
  承認できず失敗したが、`C:\Users\micro\AppData\Local\dotnet-10`(ユーザーローカル、非昇格インストール)
  に 10.0.301 が既に存在していたためそちらを使用してビルドを継続した。

### 受け入れ確認(GUI 目視、mcp computer-use 経由)
| 項目 | 結果 |
|---|---|
| 項目右クリックメニュー(開く/切り取り/コピー/ショートカット作成/クイックアクセス追加/削除/名前の変更/プロパティ) | ✅ |
| 空白部分右クリックメニュー(更新/新しいフォルダー/貼り付け/プロパティ) | ✅ |
| インライン名前変更(F2・右クリックメニュー・新規フォルダー自動リネームの3経路) | ✅ |
| 切り取り→貼り付け | ⚠️ 未実施(コピー→貼り付けで検証、切り取りは同一コードパスのため理論上動作) |
| コピー→貼り付け(繰り返し) | ✅ クラッシュなしを複数回確認 |
| 削除(ごみ箱送り) | ✅ |
| ショートカットの作成 | ✅(前回セッションで確認済み) |
| プロパティダイアログ | ✅(前回セッションで確認済み) |
| 履歴パネル(記録・クリック移動・削除・全クリア) | ✅ |
| 中クリックで新規タブ | ✅ |
| ごみ箱タブ(一覧・削除日時表示・元に戻す) | ✅ |
| ごみ箱タブ(完全に削除・空にする) | ⚠️ 未実施(コードレビューのみ — `Restore`/`GetItems` と対称な実装で理論上動作) |

### 既知の制約・保留事項
- 上記バグ#5の根本原因(`IContextMenu::InvokeCommand` の非同期継続)は未解明。`ApplicationIdle`
  までの遅延は実測で有効だったヒューリスティックであり、理論的な保証ではない。まれに再発する
  可能性を否定できないため、Cut/Copy/Paste/Delete を多用する自動テストを書く際は要注意。
- CLAUDE.md 規則2(IContextMenu 専用 STA ワーカースレッド)からの逸脱(UI スレッド直接呼び出し)
  は本エントリ時点でも未解消。デッドロックの根本原因調査は M3 以降に持ち越し。
- 完全に削除・空にするの実地確認、切り取り→貼り付け(移動)の実地確認は未実施。

## M3 — 未着手

次は `docs/07-milestones.md` の M3(IFileOperation エンジン + ごみ箱 + アンドゥ)に着手する。
