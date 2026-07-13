# 02 — アーキテクチャ

## 1. 技術選定(確定)

**一言語・二プロセス・エキゾチックなツールなし。**

| 層 | 選定 | バージョン(ピン) |
|---|---|---|
| 言語/ランタイム | C# 14 / **.NET 10 LTS** (`net10.0-windows`) | SDK-style csproj、`dotnet build` のみ |
| UI | **WPF** | NativeAOT/トリミングは WPF 非対応につき使わない。`PublishReadyToRun=true` |
| テーマ/クローム | **WPF-UI**(lepoco) | **4.3.0 ピン**(FluentWindow, Mica, TitleBar, BreadcrumbBar, AutoSuggestBox, ダークモード) |
| シェル相互運用 | **Vanara**(PInvoke.Shell32 / Windows.Shell / Windows.Shell.Common) | **5.0.5 ピン**、ラッパー単位のフォールバックピン **4.2.1**(Files が 4.x を本番出荷中) |
| 精密 P/Invoke | **Microsoft.Windows.CsWin32** | **0.3.298**(FSCTL、NtQueryDirectoryFileEx、StrCmpLogicalW、OLE)。**注**: `NtQueryDirectoryFileEx` は WDK メタデータ名前空間(`Wdk.Storage.FileSystem`)にあり、既定の SDK メタデータだけでは生成されない。`Darask.Enumeration` に **`Microsoft.Windows.WDK.Win32Metadata`** パッケージ参照を追加すること(M1 着手時に必ず確認) |
| アイコングリッド仮想化 | **VirtualizingWrapPanel**(sbaeumlisberger, MIT) | **2.5.2** |
| サービスホスト | Microsoft.Extensions.Hosting.WindowsServices | 10.x(`UseWindowsService()`) |
| 履歴 DB | **Microsoft.Data.Sqlite** | **10.0.9**、WAL モード |
| IPC シリアライザー | **MessagePack-CSharp** | 3.x(制御フレームのみ。バルクは生 span) |
| インストーラー/更新 | **Velopack** | 単一技術。§7 参照 |
| テスト | xUnit + **FlaUI**(UIA 自動操作) | 受け入れスクリプトは `tools/` の PowerShell + テスト実行 |

### 却下した選択肢と理由(2026-07-13 調査の結論 — 再訪しないこと)

- **WinUI 3 / Windows App SDK**: Microsoft 自身の Explorer が WinUI 化で遅くなった。旗艦の Files App が6年最適化しても
  性能問題(起動 #11836、タブごとのメモリ増加 #15725、高負荷 #18567)を抱え、メンテナ自身が「WinUI の選択は性能に
  影響」と認めている。さらに**日本語 IME の既知欠陥**(microsoft-ui-xaml #9216 IME 起動クラッシュ、#9446、#2704)は
  本プロジェクトの絶対要件に対する直接の脅威。
- **Tauri 2 / WebView2**: WebView2 のネイティブ OLE ドロップとの競合はアーキテクチャ上の一方通行ドア。
  IPreviewHandler ホスティングの Rust 参照実装は存在しない。シェル COM ホストを全部 IPC 境界越しに手作りになる。
- **Rust ネイティブ GUI(egui/iced/Slint/gpui)**: 日本語 IME が dealbreaker(egui/iced)、または学習データ・
  ドキュメント量が薄く LLM 実装リスクが高い(Slint/gpui)。
- **Everything SDK 統合(TC/DOpus 方式)**: 外部インストール依存になり「上位互換の単体アプリ」の要件に反する。
  自前インデクサーは M6–M9 で到達可能と実証済み(参照実装 UsnParser が同一言語で存在)。劣化モード(§ docs/03)が
  サービス拒否時のフォールバックを担う。
- **MSIX**: HKCR 仮想化が ShellNew・CLSCTX 前提を壊す。非パッケージ配布(Velopack)で全リスク回避。

### 選定の核心根拠

1. **シェル忠実性が最難関要件** — その最難関(IContextMenu ホスト、IFileOperation、IPreviewHandler)は
   files-community/Files(MIT, C#)が**同一言語で解決済み**。構造ごと移植し、Files を遅くしている
   WinUI 3 だけを捨てる。
2. **速度は Everything アーキテクチャの忠実な複製** — 公開情報(voidtools 本人のフォーラム解説)があり、
   実測数値(250k ファイル ≈ 5s、1M ≈ 100MB RAM、クエリ < 100ms)を受け入れゲートに直接使える。
3. **OneCommander が実証**: C# + WPF は WinUI 3 でなければ Explorer 級に速くできる。

## 2. プロセストポロジー

```
┌───────────────────────────────────────────────────────────────────┐
│ DaraskFiler.exe(x64、非昇格、WPF、既定で常駐 + トレイ)          │
│                                                                   │
│  UI スレッド (STA+OLE):  WPF ビュー、DoDragDrop 発火、ドロップ    │
│                          受領イベント、プレビュー HwndHost        │
│  ShellWorker スレッド (STA + メッセージポンプ + 隠しウィンドウ):  │
│      すべての IContextMenu / IFileOperation / OpenWith COM        │
│  列挙スレッド (MTA プール):  NtQueryDirectoryFileEx リスティング  │
│  アイコン/サムネイル プール (MTA、スレッドごと CoInitialize):     │
│      IShellItemImageFactory → Freeze() → UI へディスパッチ        │
│  インデックスエンジン (in-proc):  パック名前アリーナ + 検索       │
│  履歴ライター スレッド:  バッチ化 SQLite WAL 挿入                 │
└──────────────┬────────────────────────────────────────────────────┘
               │ 名前付きパイプ \\.\pipe\darask-filer-idx
               │ (バイナリ長さプレフィックス。流れるのはファイル名/
               │  メタデータのみ。ファイル内容は絶対に流れない —
               │  Everything のセキュリティモデル)
┌──────────────┴────────────────────────────────────────────────────┐
│ DaraskFilerd.exe(Windows サービス、LocalSystem、目標 RSS<50MB)  │
│  ボリュームごと(ベースライン方式は種別で異なる。docs/03 §6):    │
│   - NTFS: FSCTL_ENUM_USN_DATA ベースラインスイープ                │
│   - ReFS/Dev Drive: ディレクトリウォークベースライン(MFT非搭載) │
│   - FSCTL_READ_USN_JOURNAL 追尾ループ(~250ms 周期)              │
│   - ジャーナルラップ検出、リネームペア相関、CLOSE 単位の集約      │
│   - FRN→(parentFRN, name) ディレクトリマップ、パス解決            │
│   - 変更ファイルの size/mtime 補完(OpenFileById + GetFileInfo…)  │
│   - クライアント不在時: 正規化イベントをスプールへ永続化(§ docs/04)│
│  状態は (UsnJournalID, lastUsn) チェックポイント + スプールのみ    │
└───────────────────────────────────────────────────────────────────┘
```

**インデックスをサービスでなくアプリに置く理由**(Everything と同じ配置):
サービスの仕事は「昇格が必要な生 I/O」だけに絞る。検索 DB と履歴 DB をユーザープロセスに置くことで
(1) 打鍵ごとの検索が in-proc 呼び出しになり IPC ホップが消える、(2) 昇格プロセスが小さく監査可能になり、
攻撃面が Everything の確立された前例(「ファイル名のみ、内容へのアクセス不可」)と同一になる、
(3) ユーザーごとの履歴というプライバシー境界が自然に守られる。
アプリは終了時にインデックススナップショットを `%LOCALAPPDATA%\darask-filer\index\<volume>.dfi` に書き、
起動時はジャーナルから前進差分する(**ジャーナル読みは常にサービス経由**。アプリは絶対にボリュームハンドルを開かない)。

## 3. ソリューション構成(単一 .slnx、全部 C#)

```
darask-filer.slnx
├── src/
│   ├── Darask.Shell/          # Vanara/CsWin32 ラッパー: ContextMenuHost, FileOperationService(+progress sink),
│   │                          #   DragDropService, ThumbnailService, PreviewHostControl, RecycleBinService,
│   │                          #   PropertiesService。Files の Files.App/Utils/Shell/* から構造移植(MIT)
│   ├── Darask.Enumeration/    # 高速リスティング(NtQueryDirectoryFileEx、fallback FindFirstFileExW+LARGE_FETCH)、
│   │                          #   \\?\ 長パス、OneDrive プレースホルダー、RDCW ウォッチャー(オーバーフロー→再走査)
│   ├── Darask.Index/          # パックインデックス(struct 配列+UTF-8 アリーナ)、検索エンジン、順列配列、
│   │                          #   スナップショット、パイプクライアント、フォルダーインデックス(劣化モード)
│   ├── Darask.History/        # SQLite イベントストア、3層インジェスト、リテンション、プライバシー
│   ├── Darask.Service/        # デーモン(--console フラグで昇格コンソール実行可 — マイルストーン検証用)
│   ├── Darask.Ipc/            # パイププロトコル定義(MessagePack 契約 + バルクフレーミング)
│   │                          #   — Darask.Index と Darask.Service の両方から参照(Darask.Shell は無関係)
│   └── Darask.App/            # WPF 本体: タブ、ペイン、ビュー、アドレスバー、パレット、設定
├── tests/
│   ├── Darask.Tests/          # xUnit 単体・統合(検索オラクル、identity stitching 等)
│   └── Darask.UiTests/        # FlaUI ベース UI 受け入れ
├── tools/
│   ├── mkfixture/             # 決定論的フィクスチャ生成(M0)— チェックサム付き合成ツリー
│   └── bench/                 # ベンチハーネス(対 Explorer 比較、ETW 収集)
└── docs/
```

## 4. スレッディング & IPC 規約(load-bearing — 違反は必ずバグになる)

1. `IFileOperation` と `IContextMenu` は **STA 専用** → **メッセージポンプ付き長命 ShellWorker STA スレッド1本**
   (Files の `ThreadWithMessageQueue.cs` の移植)。UI スレッド禁止(拡張のハングでアプリが固まるため)、
   MTA プール禁止。
2. **UI スレッドは I/O を一切しない**。リスティング/アイコン/サムネイルはすべてバックグラウンド +
   `Freeze()` 済み `BitmapSource` をディスパッチ。デバッグビルドは Dispatcher watchdog が
   5 ms 超のブロックを assert する。
3. インデックスのホットパスで**ファイルごとの string 生成禁止**(パック UTF-8 アリーナ必須)。
   CI で割り当てプロファイル(dotnet-counters)と RSS ゲートを回す。
4. パイププロトコル(`Darask.Ipc`)— **フレームカタログは以下で完結。M9(検索)と M12(履歴/スプール)を
   別セッションで実装しても互換プロトコルになるよう、ここで全部確定させる**:
   - `Hello{protoVer}` — ハンドシェイク
   - `Volumes` — 応答: 監視対象ボリューム一覧
   - `SubscribeBaseline{vol}` — 応答: バルクフレーム(下記)でベースラインレコードをストリーム
   - `SubscribeTail{vol, fromUsn}` — 応答: **`TailEvent`** をライブストリーム
   - **`TailEvent{vol, usn, tsMs, kind(create|write|rename|move|delete), frn, parentFrn, name, oldName?, attrs}`**
     — サービス側で正規化済み(リネームペア相関・CLOSE 単位 reason 集約 済み)の単一イベント形式。
     **ライブ追尾とスプール再生(下記 `DrainSpool`)の両方で同じフレーム型を使う**(docs/04 §4)。
   - `MetaUpdate{vol, frn, size, mtimeUtc}` — 補完メタデータ・変更後 size/mtime のプッシュ(**`vol` 必須**
     — マルチボリューム機で frn だけでは曖昧)
   - `Gap{vol, fromTsMs, toTsMs}` — ジャーナルラップ等で捕捉できなかった区間の通知
     (検出条件は docs/03 §5 と docs/04 §4 で**同一**の判定式を使うこと — 矛盾させない)
   - `DrainSpool{vol}`(app→service) — 応答: 未処理スプールを `TailEvent` のストリームで再生
   - `SpoolAck{vol, throughSeq}`(app→service) — アプリが `throughSeq` まで確実に取り込んだことを通知。
     **サービスはこの Ack を受けて初めて自分のスプールを切り詰める**。アプリは `%ProgramData%` 配下の
     スプールファイルを直接開かない(SYSTEM 専有 DACL — docs/04 §4)。
   - バルク(`SubscribeBaseline` の応答): パック済みレコードの生 span を長さプレフィックスでストリーム。
     **名前は USN レコード由来の生 UTF-16LE バイト列のまま転送**し、アプリ側で自分の UTF-8 アリーナへ
     ロスレス変換する(docs/03 §2 のロスレス往復要件を参照。単純な `Encoding.UTF8` は使わない)。
     (protobuf/gRPC は不採用: Kestrel を昇格プロセスに入れると特権攻撃面が最大化する — 監査済みの判断)
   - スプールのリングバッファが上限(既定 64 MB/ボリューム)を超えて古いイベントを破棄した場合も、
     ジャーナルラップと同様に `Gap` を発行する。
   - **パイプ終端の強化**(サービス側): `FILE_FLAG_FIRST_PIPE_INSTANCE` で名前スクワッティングを防止、
     `PIPE_REJECT_REMOTE_CLIENTS` でリモート到達を遮断、DACL は Authenticated Users の接続のみ許可。
     **クライアント側**: 接続後に `GetNamedPipeServerProcessId` でサーバープロセスのトークンが SYSTEM か、
     イメージパスがインストール済みサービスバイナリと一致するかを検証してから初めてフレームを信頼する。
     `SECURITY_SQOS_PRESENT|SECURITY_IDENTIFICATION` でサーバーによるクライアントなりすましを防止。
   - **サービスはすべてのクライアント入力を検証する**(サービスが特権境界)。
     ネガティブセキュリティテスト(M9 受け入れ): 不正フレームのファズでサービスが落ちないこと、
     どの RPC シーケンスでもファイル内容が返らないこと、パイプ名スクワッティングを試みても
     クライアントが偽サーバーを検出して拒否すること、をそれぞれ assert。
5. クラッシュ隔離:
   - プレビューハンドラーは out-of-proc 限定(`CLSCTX_LOCAL_SERVER` → prevhost.exe)
   - コンテキストメニュー拡張は不可避的に in-proc(Explorer と同じ露出)→ STA スレッド隔離でハング対策 +
     セッション(タブ/パス)を 5 秒ごとに永続化 + `RegisterApplicationRestart`(Windows 公式のクラッシュ再起動
     機構)でタブ復元
   - **ShellWorker 全体のハングウォッチドッグ**: QueryContextMenu 専用の3秒タイムアウト(docs/05 §1)とは別に、
     ShellWorker へディスパッチした**どの呼び出しでも**(InvokeCommand・IFileOperation 実行など)N 秒
     (既定10秒)応答が無ければ、そのスレッドは**放棄**(コム的にスタックした呼び出しは中断できないため
     — 設計上のリーク)し、新しい ShellWorker STA スレッドを立ち上げて以後のディスパッチを引き継ぐ。
     放棄したスレッドが後で応答しても結果は破棄。UI には「シェルサービスを再起動しました」を表示し、
     ハング元の拡張(判明していれば)を自動でブロックリストに追加する。
   - サービスは watchdog 付き(SCM 回復設定: 失敗時再起動)

## 5. データフロー: 10万エントリのフォルダーを開く

1. タブがナビゲート → `Darask.Enumeration` がワーカースレッドで `NtQueryDirectoryFileEx` から 64 KB バッチで
   エントリをストリーム(FindFirstFile 比 ~40% 効率、ウォームで ~1.8M entries/s)。
2. エントリは struct の素の配列へ。ソートは `Array.Sort` + `StrCmpLogicalW` コンパレーター
   (**`SortDescriptions` 禁止** — 実測 200k 行で 3 分 vs 2 秒)。バインドコレクションは Reset で差し替え。
3. 初回描画は拡張子アイコン(`SHGFI_USEFILEATTRIBUTES|SHGFI_SYSICONINDEX` — ディスク I/O ゼロ)。
   項目ごとのアイコン/サムネイルはキャンセル可能な優先度キュー(可視行優先)で後追い。
4. ReadDirectoryChangesW ウォッチャー(`FILE_SHARE_DELETE` 付き、オーバーフロー→再走査)がリスティングを
   ライブに保ち、USN 追尾がグローバルインデックスを独立にライブに保つ。インデックス済みボリュームでは
   USN 差分を開いているタブに**行差分**(挿入/削除/リネーム、選択保持)としてプッシュ(目標 < 500 ms)。

## 6. 状態の置き場所

| データ | 場所 | 備考 |
|---|---|---|
| インデックススナップショット | `%LOCALAPPDATA%\darask-filer\index\*.dfi` | 使い捨て。チェックサム+USN チェックポイント付き。壊れていたら再列挙するだけ |
| 履歴 DB | `%LOCALAPPDATA%\darask-filer\history.db` | ユーザー専用 ACL。WAL |
| 設定・フォルダービュー | `%LOCALAPPDATA%\darask-filer\settings.db`(SQLite) | レジストリ不使用 |
| サービスチェックポイント | `%ProgramData%\darask-filer\checkpoints\` | (UsnJournalID, lastUsn) / ボリューム |
| イベントスプール | `%ProgramData%\darask-filer\spool\` | リングバッファ、上限付き(docs/04 §4) |

**クラッシュ安全性の教義: 「インデックスは使い捨て。真実は MFT にある。」**
スナップショットの修復コードは書かない。チェックポイント検証が疑わしければ安価な全再列挙(1M ≈ 数秒〜十数秒)。

## 7. パッケージング / 配布 / 更新

- **Velopack 一本**。ユーザー単位インストール(**管理者権限不要**)、デルタ自動更新、クリーンアンインストール。
  Velopack の展開先は `%LOCALAPPDATA%` 配下(ユーザー書き込み可能)— **これは重要な制約になる**(下記)。
- **サービスは初回起動時のオプトイン**: アプリ初回起動時にバナー
  「全ドライブ瞬間検索と完全な履歴を有効化(管理者権限が1回必要)」→ 承諾で `DaraskFilerd.exe --install`
  を昇格実行。UAC プロンプトはこの1回だけ。拒否されたら劣化モード(docs/03 §5)で動き続け、いつでも
  設定から有効化できる。
- **サービスバイナリの設置先(セキュリティ上必須の設計)**: `--install` は、まず
  `DaraskFilerd.exe`(と依存 DLL)を **`%ProgramData%\darask-filer\service\`** へコピーし、
  このディレクトリに **Administrators/SYSTEM のみ書き込み可能な明示 DACL** を設定してから、
  **その場所を指す ImagePath** でサービス登録(サービス作成 + 回復設定 + 起動)を行う。
  **`%LOCALAPPDATA%`(Velopack の展開先)を指す ImagePath で登録することは絶対に禁止**
  — ユーザー書き込み可能なパスを LocalSystem サービスの実行イメージにすると、ユーザー権限で動く
  任意のプロセスがその exe を差し替えて次回サービス起動時に SYSTEM 権限を奪取できる
  (古典的なローカル特権昇格)。Everything 自身も Program Files 配下にサービスを置いている前例に倣う。
- **サービスの自己更新**: アプリが更新されても ImagePath は変わらない(`%ProgramData%` 側は固定)。
  非昇格アプリが新しい署名済み `DaraskFilerd.exe` を一時領域にステージし、**稼働中のサービスへ
  「更新確認」を通知**(パイプ経由)→ サービスが新バイナリの Authenticode 署名(発行者が自分自身と
  一致すること)を検証してから `%ProgramData%\darask-filer\service\` を置き換えて自己再起動する。
  追加の UAC プロンプトは発生しない(サービス自身が既に SYSTEM で書き込み権限を持つため)。
- **アンインストール**: サービス削除には昇格が必要(ProgramData への書き込み・SCM 操作のため)。
  Velopack のアンインストールフックが**昇格 UAC を1回追加要求**し、`DaraskFilerd.exe --uninstall`
  (サービス停止・削除・`%ProgramData%\darask-filer\` 配下の除去)を実行する。この UAC は「インストール時
  1回・アンインストール時1回」の想定内であり、docs/01 の「UAC1回」約束はインストール/更新フローに限る
  ものとして扱う。
- 署名バイナリ。x64 専用(in-proc シェル拡張とビットネス一致必須)。
- (判断メモ: 当初案の「WiX MSI + Velopack」二本立ては審査で棄却 — インストーラー技術は一つにする。
  MSI カスタムアクションより、非管理者インストール + 明示的オプトインの方が UX も攻撃面も良い。
  ただし「サービス実行イメージはユーザー書き込み可能ディレクトリに置かない」という一般原則は
  インストーラー技術の選択とは独立した必須要件 — 設計検証で発見された重大リスクなので明記する)

## 8. 参照実装(実装時に常に手元に置くこと)

| 対象 | リポジトリ | 使い方 |
|---|---|---|
| シェル統合層全般 | files-community/Files(MIT, C#) | `Files.App/Utils/Shell/*` を構造移植。STA ポンプは `ThreadWithMessageQueue.cs` |
| MFT/USN | wangfu91/UsnParser(MIT, C#, .NET 10) | DeviceIoControl/FSCTL の動く見本。ほぼ転写元 |
| プレビュー | GeeLaw/PreviewHost(WPF) | 「Hosting a preview handler in WPF, correctly」シリーズの実装 |
| アイコングリッド | sbaeumlisberger/VirtualizingWrapPanel | サンプルあり |
| メニューホストの正典 | Raymond Chen「How to host an IContextMenu」全11回 | HandleMenuMsg2 転送の教科書 |
| RDCW の罠 | Jim Beveridge「Understanding ReadDirectoryChangesW」 | オーバーフロー時の全損挙動 |
| アーキテクチャ全体 | Everything(voidtools)FAQ + フォーラム(作者解説 t=5086, t=12446) | 数値目標と内部構造の出典 |
