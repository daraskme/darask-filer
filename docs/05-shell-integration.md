# 05 — シェル統合設計

各項目は正確なメカニズム名まで確定済み。**シェル COM はすべて専用 ShellWorker STA ポンプスレッドで実行**
(明記された例外を除く)。参照実装は files-community/Files の `Files.App/Utils/Shell/*`(MIT、同一言語)。

## 1. コンテキストメニュー(パリティの王冠)

- Vanara の `ShellContextMenu`(内部で公開 API の `SHCreateDefaultContextMenu`/`DEFCONTEXTMENU` を使用)を
  項目群から構築する。静的動詞、プログラムから開く、送る、そして**すべてのクラシック IContextMenu 拡張**
  (7-Zip の 7-zip.dll も TortoiseGit もクラシックハンドラー登録 — 調査で確認済み)を含む
  完全な Explorer クラシックメニューが得られる。**正確なコンストラクター/ファクトリーメソッド名は
  Vanara 5.0.5 の実 API に従うこと**(`SHCreateDefaultContextMenuEx` という名の公開 API は存在しない —
  この文書で使う名前は近似。M5 冒頭のスモークテストで実シグネチャを確認してから実装する)。
- 表示は v1 では**ネイティブ `TrackPopupMenuEx(TPM_RETURNCMD)`**。隠し `BasicMessageWindow` が
  `WM_INITMENUPOPUP` / `WM_MEASUREITEM` / `WM_DRAWITEM` / `WM_MENUCHAR` を
  `IContextMenu3.HandleMenuMsg2`(フォールバック IContextMenu2)へ転送 — Tortoise のオーナードローアイコンと
  7-Zip の動的サブメニューが 100% 動く。Raymond Chen の正典11回シリーズの通りに実装する。
- 実行は**数値コマンド ID** で `CMINVOKECOMMANDINFOEX`(`CMIC_MASK_PTINVOKE` + Shift 状態は
  `CMF_EXTENDEDVERBS`)。**GetCommandString は一部拡張で嘘をつく** — Files が ID 実行なのはこのため。
- フォルダー背景メニュー(シェル本物の**「新規作成」サブメニュー** CLSID_NewMenu 含む)は
  `IShellFolder::CreateViewObject(IID_IContextMenu)`。
- **ウォッチドッグ(帰属機構とリカバリーを含めて確定)**: `SHCreateDefaultContextMenu` は登録済み全
  ContextMenuHandler を**1つに集約した** IContextMenu を返すため、集約後の `QueryContextMenu` が
  タイムアウトしても「集約全体が遅い」ことしか分からず、単体では拡張名を特定できない。犯人特定は
  以下の二段構えで行う:
  1. **タイムアウト検出**: 集約 `QueryContextMenu` に 3 秒タイムアウト。
  2. **帰属**: タイムアウトした ShellWorker スレッドのスタックをサンプリング
     (`SuspendThread` → `GetThreadContext`/StackWalk、または `RtlCaptureStackBackTrace` ベースの
     サンプリングヘルパー)し、shell32 以外の最も深いリターンアドレスをモジュールへ解決
     (`GetModuleHandleEx` + `GetModuleFileName`)。そのモジュールファイル名(例 `7-zip.dll`)を
     トーストの表示名兼ブロックリストのキーとする。
  3. **ブロックリスト適用**: 次回以降、集約メニューではなく**対象アイテムの ContextMenuHandler CLSID を
     レジストリから個別に列挙し、ブロックリスト中の CLSID だけ除外して個別に CoCreate → マージ**する
     (集約 API では単体除外ができないため、除外が効くのはこの個別構築パスのみ)。
  4. **スレッドリカバリー**: ハングした in-proc COM 呼び出しは中断できないため、タイムアウトした
     ShellWorker スレッドは**放棄**(設計上のリーク)し、新しい ShellWorker STA スレッドを起動して
     以後のディスパッチを引き継ぐ(docs/02 §4.5 のスレッド全体ウォッチドッグと同一メカニズム)。
     放棄後に元スレッドが応答しても結果は破棄する。
  - トーストは「メニューが遅い — 拡張 X」を表示し、拡張ごとのブロックリスト設定に給電する。
    Explorer 自身がハングする場面での差別化機能。
  - M5 受け入れテストはこの一連の流れ(ハング→検出→帰属→リカバリー→次回除外)を通しで検証する。
- XAML 再描画版のテーマ付きメニュー(`ShellContextMenu.GetItems()`)は後の磨き込み。**v1 の依存にしない。**
- **受容済みギャップ**: Win11 の MSIX 専用 IExplorerCommand トップレベル項目はホスト不可
  (Files #8251、2022年から未解決)。undocumented な Windows.Internal API に工数ゼロ。

## 2. ファイル操作とごみ箱

- **すべての変更操作は IFileOperation**(Vanara `ShellFileOperations`、Files の `ShellFileOperations2.cs` の
  ように拡張): 本物の Explorer コピーエンジン — ネイティブ進捗ダイアログ、「ファイルの置換またはスキップ」
  衝突ダイアログ、`FOF_ALLOWUNDO` / `FOFX_RECYCLEONDELETE` のごみ箱削除、CLSCTX_LOCAL_SERVER 経由の
  UAC 昇格、混在バッチ操作。
- `SetOwnerWindow` にメインウィンドウ HWND を渡す(忘れると z-order バグの古典)。
- `IFileOperationProgressSink` を Advise — 項目ごとの Post イベント
  (`PostCopyItem/PostMoveItem/PostRenameItem/PostNewItem/PostDeleteItem`。**`PostDeleteItem` 必須** —
  `psiNewlyCreated` がごみ箱アイテムへの参照になり、Ctrl+Z 復元(§3)と削除の履歴記録はこれに依存する)
  が衝突リネーム後の実際の最終名を含め、アンドゥ+履歴の一次ソース。
- ごみ箱のブラウズ/復元は Vanara `RecycleBin`。

## 3. アンドゥ/リドゥ

自前実装(explorer.exe の undo スタックを再生できる公開 API は存在しない):
操作ジャーナルが入力 + sink 結果を記録し、Ctrl+Z は逆操作を再生 —
undo move = 逆移動 / undo recycle = RecycleBin 復元 / undo rename = 逆リネーム / undo copy = コピー先をごみ箱へ。
**履歴 Tier 1 と共有のサブシステム**(docs/04 §4)。

## 4. ドラッグ&ドロップ

- **アウト**: WPF の DragDrop は使わない — シェル自身の IDataObject を取得
  (`IShellItem.BindToHandler(BHID_DataObject)`)し、ole32 `DoDragDrop` を P/Invoke。
  ~30 行の IDropSource + `IDragSourceHelper::InitializeFromBitmap`(Explorer 風ドラッグイメージ)。
  これで本物の CF_HDROP + CFSTR_SHELLIDLIST が得られ、Outlook・Teams・ブラウザーが全部受け取る。
- **イン**: まず WPF ドロップイベントで受ける(WPF の `DataObject` は `ComTypes.IDataObject` を実装
  しているので `IDropTargetHelper` と生シェルフォーマットがそのまま使える)。
  **トップレベル HWND への RegisterDragDrop は絶対禁止**(`DRAGDROP_E_ALREADYREGISTERED` — WPF が既に所有)。
  ドラッグイメージ忠実度が必要になった場合のみ子 HWND に RegisterDragDrop(後回し)。
- **仮想ファイル受け入れ**(CFSTR_FILEDESCRIPTORW + CFSTR_FILECONTENTS、lindex ごとに TYMED_ISTREAM):
  Outlook 添付のドラッグインが動く。大容量ドロップは IDataObjectAsyncCapability で非ブロッキング。
- **内部/外部の判定ルール(確定)**: ドラッグ開始時点では最終的なドロップ先が自ウィンドウ内か外かは
  分からないため、**ファイル行のドラッグは常に上記の DoDragDrop 経路で開始する**。ドロップが自ウィンドウ内
  (別タブ/別ペインのフォルダー行など)であれば、それは「イン」で説明した WPF ドロップイベント経由で
  受け取り、内部判定して move/copy を自前実行する — 外に出たか内で完結したかは DoDragDrop 側から見て
  区別する必要がない(同じ IDataObject が両方の経路に流れるだけ)。
  **ポインターイベントだけで完結する自前 D&D は、ファイルのドラッグには使わない** — タブの並べ替えや
  クイックアクセスのピン並べ替えなど、**ファイルを運ばない UI 要素のドラッグ限定**(CLAUDE.md 規則7の
  「内部 D&D はポインターイベント」はこの非ファイル UI ドラッグを指す)。

## 5. クリップボード

フルシェルプロトコル: コピー = `OleSetClipboard` + CFSTR_PREFERREDDROPEFFECT=COPY /
切り取り = MOVE + 項目グレー表示 / 貼り付け = OleGetClipboard → IFileOperation。
貼り付け移動後は CFSTR_PERFORMEDDROPEFFECT / CFSTR_PASTESUCCEEDED を設定
(Explorer 発の切り取りが正しく完了するために必須)。Ctrl+Shift+C = パスのコピー。

## 6. サムネイルとアイコン

- `IShellItemImageFactory::GetImage`(アスペクト正しい。OneDrive プレースホルダー対応)—
  **バックグラウンドスレッド限定**。二段フェッチ: まず SIIGBF_INCACHEONLY パス、次にキャンセル可能な
  優先度キュー(可視行優先)で抽出。
- HBITMAP → `Imaging.CreateBitmapSourceFromHBitmap` → `Freeze()` → UI へディスパッチ。`DeleteObject` 必須。
- アイコン: SHGFI_SYSICONINDEX + SHGetImageList(SHIL_*、JUMBO=256px)。オーバーレイは
  SHGFI_OVERLAYINDEX の上位8bit。10万件リスティングの初回描画は拡張子単位の
  SHGFI_USEFILEATTRIBUTES 高速パス(ディスク I/O ゼロ)。
- LRU BitmapSource キャッシュ、キーは (path|ext, size, mtime)。

## 7. プレビューペイン

**GeeLaw/PreviewHost を移植**(WPF ネイティブ、「Hosting a preview handler in WPF, correctly」シリーズ):
- CoCreateInstance は **CLSCTX_LOCAL_SERVER 限定**(Adobe/Office のクラッシュは prevhost.exe が死ぬだけで
  アプリは無傷)。CO_E_SERVER_EXEC_FAILURE でリトライ。
- 初期化順: IInitializeWithStream → File → Item、SetWindow/SetRect/DoPreview。
- `IPreviewHandlerFrame.GetWindowContext` + TranslateAccelerator(Excel が要求する)。
- 矩形 HwndHost ペイン。レイアウトは**何もその上に浮かせない**(WPF airspace 制約)。
- ハンドラーごとのデナイリスト設定。CLSID 検索は HKCR `<ext>\shellex\{8895b1c6-…}`。
- 組み込みフォールバックビューア(画像/テキスト)— ハンドラークラッシュ時に自動フォールバック。

## 8. プロパティダイアログ

`SHObjectProperties(hwnd, SHOP_FILEPATH, path, NULL)`(単一)/ `SHMultiFileProperties(IDataObject, 0)`
(複数選択)。どちらもシェルスレッドで非ブロッキング。Alt+Enter 配線。**再実装禁止**
(セキュリティ/詳細/以前のバージョンタブが無償で付いてくる)。

## 9. シェル名前空間の場所

ファイルシステム高速パスがナビゲーションの 99% を担う。特別なルート — ごみ箱、ネットワーク、
**MTP スマホ**(Explorer++ の正典的失敗点)— には別系統の IShellFolder/pidl ブラウズパス。
どのルートでもまだカバーできないものには**「エクスプローラーで開く」ボタン**を保証された脱出ハッチとして出す。
受け入れ基準(M15): Android スマホが最低限「一覧 + コピーアウト」できる、
またはラベル付きフォールバックボタンが出る。

## 10. Explorer 筋肉記憶パリティ

- `StrCmpLogicalW` 自然順ソート
- F2 = 拡張子を除く名前部を選択。複数選択 F2 → `base (1)` パターン。リネーム中 Tab/Shift+Tab で次項目
- Ctrl+Shift+N、Alt+Enter、Ctrl+L/Alt+D/F4、Ctrl+Shift+C、Ctrl+ホイールでビューズーム、先行入力選択
- フォルダー別ビュー設定は自前 DB にパスをキーとして永続化(レジストリ Bags 不使用)
- 送る = `shell:sendto` の内容
- 長パスは自前コードパス全部で `\\?\` + longPathAware マニフェスト
  (メニューから起動される一部シェル拡張は 260 超で失敗し得る — Explorer と同じ現実として文書化)
