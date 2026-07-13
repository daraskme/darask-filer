# 04 — 履歴機能設計(看板機能)

「このフォルダーで何が起きたか」「このファイルはいつ・どう動いたか」を、
**アプリ外の変更まで含めて**タイムライン表示する。エクスプローラーに存在しない差別化機能。

## 1. ストア

単一の `history.db`(Microsoft.Data.Sqlite 10.0.9)を `%LOCALAPPDATA%\darask-filer\` に配置。ユーザー専用 ACL。

- `journal_mode=WAL, synchronous=NORMAL, busy_timeout=5000, auto_vacuum=INCREMENTAL`
- 専用ライタースレッド1本、バッチトランザクション(250 ms ごと、または 200 イベントで flush)
- SQLite の実測書き込み能力(70k–100k tx/s)に対しイベントは典型 <10/s — 余裕は問題にならない
- アイドル時に `wal_checkpoint(TRUNCATE)`

## 2. スキーマ(イベントソーシング)

```sql
CREATE TABLE files (
  id            INTEGER PRIMARY KEY,
  volume_serial BLOB,     -- 8 bytes
  file_id       BLOB,     -- 16 bytes: FILE_ID_128(ReFS 対応で最初から 128-bit)
  current_path  TEXT,
  current_name  TEXT,
  is_dir        INTEGER,
  first_seen    INTEGER,  -- unix ms
  last_seen     INTEGER,
  tombstoned    INTEGER   -- 削除観測済みフラグ
);
CREATE TABLE events (
  id            INTEGER PRIMARY KEY AUTOINCREMENT,
  file_id       INTEGER NULL REFERENCES files(id),        -- gap/pause 行は NULL
  parent_dir_id INTEGER NULL REFERENCES files(id),         -- gap/pause 行は NULL
  volume_serial BLOB NULL,  -- gap 行のみ設定(対象ボリューム)。file 系イベントは files.volume_serial で辿る
  ts_ms         INTEGER,
  kind          INTEGER,  -- visit|open|create|write|rename|move|delete|restore|copy_in|gap|pause
  source        INTEGER,  -- in_app | usn | shell_recent
  detail        TEXT      -- JSON。種別ごとに意味が異なる(下記)
);
CREATE INDEX idx_events_file   ON events(file_id, ts_ms);
CREATE INDEX idx_events_parent ON events(parent_dir_id, ts_ms);
CREATE INDEX idx_events_ts     ON events(ts_ms);
CREATE INDEX idx_events_vol_ts ON events(volume_serial, ts_ms);  -- gap 行のボリューム別検索用
```

**`detail` 列の種別ごとの中身(確定)**:
- `rename` / `move`: `{"old_path": "...", "new_path": "..."}`
- `gap`: `{"from_ts": ..., "to_ts": ...}`(`file_id`/`parent_dir_id` は NULL、`volume_serial` に対象ボリューム)
- `pause`: `{"until_ts": ...}`(`file_id`/`parent_dir_id`/`volume_serial` すべて NULL — グローバル)
- それ以外の kind: NULL

**タイムラインクエリの合成規則**: 特定ファイル/フォルダーのタイムラインを描画する際は、そのファイルの
`volume_serial`(`files` テーブルから)に一致し、表示期間と重なる `gap` 行を `UNION` して描画に混ぜる
(「この期間は記録が欠けている可能性」の表示に使う)。`pause` 行は常に全タイムラインに重ねる(グローバル)。

## 3. ファイル同一性(この機能の核心)

- キー = `(VolumeSerialNumber, FILE_ID_128)`。取得は `GetFileInformationByHandleEx(FileIdInfo)`。
  同一ボリューム内のリネーム・移動をまたいで安定 → **履歴が自動で追従する**。
- 非昇格でも `FSCTL_READ_FILE_USN_DATA` は**ユーザーが開ける通常のファイルハンドルで動く**
  (管理者不要)— ブラウズ中のファイルを FRN/同一性へオンデマンドでマップするのに使う。
- **FRN 再利用ガード**(NTFS の FRN はシーケンス16bitで再利用され得る):
  (1) USN の FILE_DELETE 観測で必ず tombstone、(2) 既存 identity に新イベントを縫い付ける前に
  作成日時のサニティチェック。
- **ボリューム跨ぎ移動**: アプリ内操作なら1つの MOVE イベントで両 identity をリンク(自分で実行したので確実)。
  外部のボリューム跨ぎは推定(delete+create、同名 + タイムスタンプ窓 ≤5 s)し、UI で「推定」と明示フラグ。
  **サイズ照合はベストエフォート**: USN レコード自体に size は無いため(docs/03 §3)、削除側のサイズは
  索引の補完メタデータ(`flags & MetaFilled` が立っている場合のみ)から取得できれば照合条件に加える。
  未取得(補完スイープ未完了)の場合は名前+タイムスタンプ窓のみで照合し、確信度を「推定(低)」に落とす
  — 削除済みファイルへの stat は不可能なので、ここで新規のファイル I/O を発生させない。
- ハードリンク(1 FileID に複数パス): USN レコードごとに名前単位で記録。

## 4. 3層インジェスト

### Tier 1 — アプリ内イベント(特権ゼロ、最初に出荷)

- フォルダー訪問(タブナビゲーション)= `visit`
- darask-filer からのファイル起動 = `open`(Explorer と同様に `SHAddToRecentDocs` も呼ぶ)
- すべての変更操作は **IFileOperationProgressSink** の Post イベント
  (`PostCopyItem/PostMoveItem/PostRenameItem/PostNewItem/PostDeleteItem` — 衝突リネーム後の**実際の最終名**
  が取れる。**`PostDeleteItem` を忘れないこと** — `psiNewlyCreated` がごみ箱アイテムへの参照になり
  Ctrl+Z 復元と削除タイムラインの両方がこれに依存する。完全削除の場合は `psiNewlyCreated` が null)。
- **この操作ジャーナルはアンドゥスタックと同一サブシステム**: レコーダー1つ、消費者2つ
  (Ctrl+Z 逆再生 + 履歴タイムライン)。二重実装しないこと。

### Tier 2 — USN ジャーナル(システム全体の外部変更)

検索インデックスに給電しているのと同じサービス追尾ストリームを使う。全プロセス由来の
create/write/rename/move/delete が、**darask-filer 非起動中の分まで**入る(NTFS が常時記録しているため)。

サービス側の正規化(DB に入る前に):
- `USN_REASON_CLOSE` サイクルごとに累積 reason マスクを集約(1保存 = 1 `write` イベント。中間の細切れは捨てる)
- `RENAME_OLD_NAME`/`RENAME_NEW_NAME` をペアにして 1 つの rename に(親 FRN が変わった同ペア = move)
  — osquery が使っているのと同じアルゴリズム
- FRN→(parentFRN, name) マップでパス解決

**常時キャプチャ(スプール)**: クライアント(アプリ)が接続していない間、サービスは正規化済みイベントを
`%ProgramData%\darask-filer\spool\<volumeSerial>.evt` の**上限付きリングバッファ**(既定 64 MB/ボリューム)に
スプールする。**このファイルはサービス専有(SYSTEM 専有 DACL)であり、アプリは直接開かない** — アプリが
接続すると `DrainSpool{vol}` フレーム(docs/02 §4)を送り、サービスが未処理分を `TailEvent` ストリームで
再生する。アプリは取り込みが完了した分だけ `SpoolAck{vol, throughSeq}` を返し、**サービスがその Ack を
受けて初めて自分のスプールを切り詰める**(アプリがファイルを切り詰めることはない — 複数ユーザー環境でも
安全)。これによりアプリ常駐を切っているユーザーでも履歴の空白がほぼゼロになる。スプールの中身はファイル名と
イベント種別のみ(Everything の「ローカルユーザーは全ファイル名を見られる」前例と同じ境界 —
ファイル内容は絶対に入らない)。リングバッファが上限を超えて古いイベントを破棄した場合も下記と同じ `gap` を発行する。

**ラップの正直さ(docs/03 §5 と判定式を統一 — 矛盾させないこと)**: 以下のいずれかで gap:
`UsnJournalID` が変化、または `lastProcessedUsn < FirstUsn`(`FSCTL_QUERY_USN_JOURNAL` が返す
`USN_JOURNAL_DATA.FirstUsn`。**`LowestValidUsn` ではない** — `LowestValidUsn` はジャーナル「インスタンス」の
先頭 USN で通常 0 のままリングラップでは動かない。動くのは `FirstUsn`)、または実行時に
`FSCTL_READ_USN_JOURNAL` が `ERROR_JOURNAL_ENTRY_DELETED` を返す。該当時は未知区間を跨ぐ明示的な `gap`
イベントを書き、UI は「この期間の変更は記録できていない可能性があります」と描画する。

既知の限界(UI の説明文に明記): プロセス帰属なし(どのアプリが変更したかは不明)、読み取りイベントなし
(ETW でしか取れない — opt-in で post-1.0 検討)。

### Tier 3 — 外部オープン(ベストエフォート、管理者不要)

`%APPDATA%\Microsoft\Windows\Recent` の `.lnk` 生成を ReadDirectoryChangesW で監視
= Explorer・共通ダイアログ・`SHAddToRecentDocs` 経由のオープンを検出。**アプリプロセスで動かす**
(Recent はユーザーセッション状態 — サービスに置くのは誤り)。バッジは「開かれた(検出)」。

## 5. ノイズ制御とプライバシー(v1 必須、後回し禁止)

- **インジェスト時除外**(除外パスは DB に一切書かれない): `%TEMP%`、ブラウザーキャッシュ、`node_modules`、
  ビルド出力グロブ、Windows Update、履歴 DB 自身。ユーザー編集可能なグロブ/ボリューム除外リスト。
- トレイの**一時停止**(「明日まで」オプション付き)→ pause 区間マーカーを記録
- 右クリック「この項目の履歴を忘れる」
- ワンクリック全消去(接続を閉じて db/-wal/-shm を削除)
- リテンション: 生イベント12ヶ月 → 日次のファイル/フォルダー別ロールアップに集約。DB ハードキャップ 500 MB、
  古い順に LIMIT ループでプルーニング。目安: イベント1件あたり 100–200 バイト(3インデックス + JSON detail
  込み)、10万イベント ≈ 10–20 MB(M11/M12 の受け入れ基準はこの目安に合わせて設定している — docs/07 参照)。
- ローカルのみ・イベントのみ・内容なし(Recall 騒動の教訓 — 楽な側に立つ)

## 6. UX サーフェス

1. **ファイル詳細サイドバー → アクティビティペイン**(Dropbox/Drive パターン): 日別グループの縦タイムライン —
   作成・編集(USN 由来には「darask-filer 外」グリフ)・リネーム a→b・移動・削除/復元・オープン。
   gap マーカーは正直に描画。
2. **フォルダーの「履歴」タブ**: 「最後にいつ来たか、ここで何が起きたか」。
   フィルターチップ: 自分の操作 / 他のアプリ / すべて。
3. **Ctrl+Shift+E 最近の場所パレット**(JetBrains パターン): フォルダー訪問イベント給電。検索インデックスと
   マージして瞬間ジャンプ。
4. 詳細ビューに **「最終訪問(自分)」列** — Explorer には絶対に出せない列。
