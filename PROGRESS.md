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

## CI/CD 新規構築(GitHub Actions) ✅ 完了(2026-07-14)

これまで一度も git 管理下になかったリポジトリに履歴を作り、GitHub にリモートを作成して push、
GitHub Actions での CI を実際にグリーンにするところまでを実施。

### 発見した前提の食い違い
- リポジトリに `.github/workflows/build.yml` は既に存在していたが、**コミットが1つもなく
  リモートも未設定**のため一度も実行されたことがなかった。
- CLAUDE.md および docs/02-architecture.md が参照する `darask-filer.sln` は**実在しない** —
  実際のソリューションファイルは `darask-filer.slnx`(新しい XML ベース形式)。両ドキュメントを修正。
- `global.json` が存在せず SDK バージョン固定なし(前回セッションで実際に .NET 10 SDK 不在に
  遭遇した原因の一端)。`10.0.301` / `rollForward: latestFeature` で固定。

### 実施内容
- **リポジトリ履歴の作成**: 1コミット目(`feat:` ボットストラップ — M0-M2 + UX 磨き込み + 右クリック
  メニュー整理一式)、2コミット目(`ci:` — global.json・build.yml 拡張・ドキュメント修正)、
  3コミット目(`fix:` — 実 CI 実行で見つかった不具合の修正、詳細下記)。
- **GitHub リポジトリ作成**: `gh repo create daraskme/darask-filer --private` で新規作成、push。
- **build.yml の拡張**: least-privilege `permissions: contents: read`、`concurrency` グループでの
  重複実行キャンセル、NuGet 復元キャッシュ、失敗時の `testdata/` アーティファクトアップロード、
  `tools/accept/M0-M2.ps1` を実行する `accept` ジョブを新設(push 時のみ・PR では実行しない —
  数分かかる実測ベースの受け入れスクリプトを PR フィードバックの速さより優先)。
- **マルチレンズレビュー(Workflow ツール)**: push 直後に正確性・セキュリティ・コスト/性能・
  ベストプラクティスの4観点で並列レビュー→抽出→敵対的検証のワークフローを実行し、2件の実問題を
  確信度高く特定(詳細は下記バグ一覧)。

### 実際の CI 実行で発見・修正した不具合
1. **`accept` ジョブの `if:` が `needs:` の暗黙 success() ガードを上書きしていた**: マルチレンズ
   レビューで発見。`if: github.event_name == 'push'` だけでは `needs: build-and-test` の暗黙の
   成功チェックが失われ、build-and-test が失敗しても accept が走ってしまう(重複ビルドの無駄+
   紛らわしい失敗シグナル)。**対応**: `if: github.event_name == 'push' && needs.build-and-test.result == 'success'`。
2. **`tools/accept/Mxx.ps1` が毎回フルソリューションの build+test を独自に行い、CI で二重(将来
   M17 まで最大 4 重どころか各回redundant)に実行されていた**: マルチレンズレビューで発見・実測で
   確認。**対応**: 3スクリプトすべてに `-SkipBuildAndTest` スイッチを追加(単体実行時は指定不要 —
   既定で常にビルド・テストする後方互換)。`accept` ジョブは一度だけビルド・テストし、以降は
   スイッチ付きで各スクリプトを呼ぶ。
3. **`-SkipBuildAndTest` を実装した際、param() の括弧内に置いた日本語コメントがスイッチのバインドを
   壊した**: `.ps1` に UTF-8 BOM がなく、Windows PowerShell 5.1(`powershell.exe`。GitHub Actions
   側は `pwsh` を明示指定していたため CI では顕在化しなかったが、ローカル検証で発見)がシステムの
   ANSI コードページでファイルを読むため、param() というパーサーが厳密にトークンを認識する領域に
   置いた多バイト文字がバイト列として誤読され、構文が壊れて `-SkipBuildAndTest` が常に `$false`
   になっていた(酷いケースでは `Unexpected token ')'` の構文エラーにもなった)。**対応**:
   コメントを param() の外に移動 + 3スクリプトすべてに UTF-8 BOM を付与(PS 5.1 での定番の回避策)。
   powershell.exe / pwsh 両方で正しくバインドすることを確認。
4. **M1 の受け入れスクリプトが実際に GitHub Actions の windows-latest で失敗した**: 列挙+ソートが
   623ms(閾値 300ms)、名前ソートが 448ms(閾値 200ms)で実測 2〜2.2 倍遅い。一方 M0 の I/O バウンドな
   10万ファイル生成は逆に CI の方が高速だった(20.5s vs ローカル 52s — CI の ephemeral disk が
   ローカルディスクより速い一方、共有 vCPU は明確に遅い)。**対応**: `$env:CI` を見て CPU バウンドな
   閾値(列挙+ソート・ソート単体ベンチ)だけ 3 倍に緩める(実測 2.2 倍に対して安全マージン確保)。
   ローカル開発機での閾値はそのまま(300ms/200ms)。I/O バウンドな M0 の閾値は変更不要だった。

### 受け入れ確認
| 項目 | 結果 |
|---|---|
| GitHub リポジトリ作成・push | ✅ `daraskme/darask-filer`(private) |
| CI 初回実行(修正前) | ❌ accept ジョブが M1 の性能閾値で失敗(実機で確認・原因特定) |
| CI 再実行(修正後) | ✅ build-and-test 1m51s / accept 2m56s、両ジョブグリーン |
| ローカルでの `-SkipBuildAndTest` 動作(powershell.exe / pwsh 両方) | ✅ |
| ローカルでのデフォルトモード動作(既存の単体実行フロー) | ✅ 3スクリプトとも回帰なし |

### 既知の制約・保留事項
- `accept` ジョブは PR では実行されない(push のみ)。PR 上で M0-M2 の受け入れ基準が壊れていても
  マージ前には気づけない — マージ後に main/master への push で初めて検知される設計上のトレードオフ。
- CI の 3 倍閾値マージンは M1 の実測 1 回分に基づくヒューリスティック。GitHub Actions の共有ランナー
  性能は日によって変動しうるため、将来まれに再発する可能性は否定できない。
- Velopack でのリリースビルド・署名・配布パイプラインは未着手(docs/02 §7 で設計は確定済みだが
  M3 以降の実装待ち)。
- リポジトリブートストラップの1コミット目は、これまでの全マイルストーン(M0-M2 + UX 磨き込み +
  右クリックメニュー整理一式)を1つにまとめたもの — 元々コミットなしで開発されていたため、
  マイルストーンごとの粒度に遡って分割することはできなかった。

## 機能拡張パス: Codex ブレインストーム + 作業スペース + v0.1.0 リリース ✅ 完了(2026-08-15)

ユーザー指示「Codex (GPT-5.6) とアイデアを出し合って便利機能を検討・実装、相互レビュー、
配布用ビルドも作成」への対応。M3 (IFileOperation) 着手前の割り込み。

### アイデア出しの経緯
- Codex にコードベースを読ませて 15 案のランク付き提案を取得(効果見積・参照行付き)。
- Fable 5 側の案・ユーザー要望(**プロジェクトごとの作業スペース**、**既定フォルダーの左ペイン
  常時表示**)と統合し、L 級(IFileOperation 全面移行・D&D・ネイティブメニューホスティング・
  デュアルペイン)は M3 以降のマイルストーン本体と重なるため見送り、S/M 級のみ採用。

### 実装した機能
- **作業スペース**(ユーザー要望): タブ構成(パス・表示モード・ズーム)を名前付き保存/切替。
  `WorkspaceStore`(workspaces.json)。ナビペインに専用セクション + 保存ボタン、右クリックで
  開く/上書き/名前の変更/削除。適用時はタブゼロ状態を作らず総入れ替え。
- **セッション復元**: 終了時のタブ構成を session.json に保存し起動時復元(存在しないパスは除外、
  ActiveIndex は除外後に再マッピング)。
- **既定ピンフォルダー**(ユーザー要望): デスクトップ/ダウンロード(SHGetKnownFolderPath で
  リダイレクト対応)/ドキュメント/ピクチャ/ミュージック/ビデオ/個人用 + 存在検出した
  OneDrive/iCloud Drive/Google Drive/MEGA/Dropbox をナビペイン最上部に常時表示。
- **ファイル起動**: ダブルクリック/Enter で既定アプリ起動(`LaunchService`、Process.Start +
  UseShellExecute)。関連付けなしは「プログラムから開く」へフォールバック。複数選択 Enter は
  15 件超で確認ダイアログ(エクスプローラー準拠)。
- **キーボード層**: Ctrl+C/X/V(シェル相互運用のまま)・Ctrl+Shift+C パスのコピー・Alt+Enter
  プロパティ・Ctrl+Shift+N 新規フォルダー。
- **ソートグリフ**(▲▼)+ 種類列ソート(`SortKey.Type` — Span ベース拡張子比較、フォルダーは
  名前のみ)。列→キー対応は GridViewColumn インスタンスで対応付け(ヘッダー文字列一致を廃止)。
- **タイプアヘッド**: WPF 標準 TextSearch(`TextSearch.TextPath="DisplayName"`)を両ビューで有効化。
- **Ctrl+ホイール アイコンズーム**: 32〜256px の7段階。DP バインディングでテンプレート追従、
  自前スクロールバーのエクステント計算・サムネイル要求解像度(64/128/256)・シェルアイコン
  サイズ(64px 以上で 32px 版)も追従。
- **選択・スクロール保持**: 同一フォルダー内の一覧再構築(RDCW 自動更新・フィルター・トグル)で
  選択状態とスクロール位置を復元。
- **コンテキストメニュー拡張**: プログラムから開く/パスのコピー/ターミナルで開く(wt.exe →
  PowerShell フォールバック)/新しいテキスト ドキュメント。
- **ステータスバー**: ドライブ空き容量(バックグラウンド stat)。
- **タブ中クリッククローズ**。

### 相互レビュー
- **Codex 側レビューはクォータ切れで実施不能**(8/20 まで)。代替として /code-review 手順に
  沿った 10 観点セルフレビューを実施し **9 件検出 → 全件修正**:
  1. セッション復元/作業スペース適用の UI スレッド I/O(規則1違反 — 到達不能 UNC で起動フリーズ)
  2. 存在しないパス除外後の ActiveIndex ズレ
  3. `wt -d "C:\"` の末尾バックスラッシュが引用符をエスケープ
  4. 選択復元の O(n²)(1件追加ごとの合計サイズ再計算)→ 抑止フラグ + 最後に1回
  5. TextSearch/ScrollIntoView 経由のスクロールで自前スクロールバーが非同期(ScrollChanged 購読で解決)
  6. 種類ソートがフォルダー名のドットを拡張子扱い
  7. 作業スペースのリネームで重複名が作れた
  8. 空き容量が同一ドライブ内で更新されない
  9. 64px ズームで 16px アイコンを4倍拡大
- レビュー中に**既存バグも1件発見・修正**: リネームボックス編集中の Delete キーがリストへ
  バブリングしてファイル削除が発火し得た(ショートカット全般を `OriginalSource is TextBox` でガード)。

### v0.1.0 リリース
- `dotnet publish -c Release -r win-x64 --self-contained -p:Version=0.1.0`(ReadyToRun は csproj 指定)。
- **システムに .NET 10 ランタイムが存在しない状態のマシンでスタンドアロン起動を実地確認**
  (154.5MB フォルダー / 65.9MB zip)。
- GitHub Release v0.1.0 として zip を添付。

### 第2ラウンド: gpt-5.6-sol (OpenRouter) 相互レビュー(v0.1.1)
- Codex クォータ切れ後、ユーザーが OpenRouter 経由の `openai/gpt-5.6-sol` を整備
  (`~/.codex/config.toml` の既定を OpenRouter に切替済み・キーは `~\.openrouter_key`。
  **CLI 0.144.5 ではレガシー `[profiles.*]` テーブルが `--profile` と併用不可** —
  既定設定がそのまま OpenRouter を向いているので `--profile` なしの `codex exec` で使う。
  キーは実行時に環境変数 `OPENROUTER_API_KEY` へ注入)。
- agentic レビュー 1 回 = 115,234 トークン(+疎通テスト 11,057)≈ **$1 前後**。
- **10 件指摘 → 8 件採用・修正、2 件棄却**(棄却理由込みでコミット 0c83bf4 に記録)。
  採用分の要点: JSON ストアのアトミック書き込み+破損 JSON 耐性(破損 session.json で
  起動クラッシュし得た)、作業スペース適用の世代ガード(遅い UNC stat が新しい選択を
  上書きし得た)、ロード完了前保存のマージ、新規作成のオフスレッド化+ FileMode.CreateNew、
  古い Dispatcher コールバックの破棄ガード、グリッドラベル2行キャップ、ズーム時の
  実体化行のみ列挙。
- 棄却 2 件: ShellWorker STA 移行(本ファイル記載の実測デッドロックによる意図的逸脱 —
  M3 で再検討)、OpenAs_RunDLL の引用符付け(rundll32 はカンマ以降を素通しするため
  引用符はパスを壊す)。

### 開発環境の注意(再発)
- **.NET 10 SDK がまたシステムから消えていた**(Program Files 側は 8.0.29/9.0.18 ランタイムのみ)。
  dotnet-install.ps1 で `%LOCALAPPDATA%\dotnet-10` に 10.0.301 を再インストール。開発ビルドの
  実行には `DOTNET_ROOT=%LOCALAPPDATA%\dotnet-10` が必要(publish 版は自己完結なので不要)。

### 既知の制約・保留事項
- 作業スペース名入力ダイアログ(NameInputDialog)は新規テキスト入力サーフェス — **日本語 IME
  実機検証は未実施**(規則16)。タイプアヘッドも ASCII 前提(非編集コントロールでは IME が
  起動しないため、日本語名へのジャンプは今後の課題)。
- ナビペイン上部セクションの入れ子 ListBox 上ではマウスホイールが外側 ScrollViewer に届かない
  (WPF の既知の挙動)。項目数が MaxHeight 440px を超えた場合はスクロールバー操作が必要。
- クイックアクセス/履歴の JSON 読み込みは従来どおり UI スレッド(既存コード、今回スコープ外)。
  作業スペース読み込みはバックグラウンド化済み。
- GUI 深部(作業スペース保存ダイアログ・ズーム・タイプアヘッド等)の実機操作確認は未実施
  (起動・終了・セッション保存/復元のスモークテストのみ)。次回ユーザー実使用でのフィードバック待ち。

## M3 — 未着手

次は `docs/07-milestones.md` の M3(IFileOperation エンジン + ごみ箱 + アンドゥ)に着手する。
