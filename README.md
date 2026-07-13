# darask-filer

[![CI](https://github.com/daraskme/darask-filer/actions/workflows/build.yml/badge.svg)](https://github.com/daraskme/darask-filer/actions/workflows/build.yml)

Windows 11 向けエクスプローラー**上位互換**ファイラー。

- **超高速**: 常駐 < 100 ms でウィンドウ表示、10万ファイルのフォルダーも即描画、UI ストールゼロ
- **Everything 級検索**: NTFS MFT 直読み + USN ジャーナル追尾による全ドライブ瞬間ファイル名検索
  (打鍵ごと・デバウンスなし・全角半角同一視)
- **エクスプローラー機能パリティ**: 本物のシェルコンテキストメニュー(7-Zip/TortoiseGit)、本物の
  コピーエンジン(進捗・衝突ダイアログ・ごみ箱・UAC)、OLE ドラッグ&ドロップ双方向、サムネイル、
  プレビュー、タブ
- **履歴**(独自機能): フォルダー別・ファイル別の操作タイムライン。**アプリ外・非起動中の変更も**
  USN ジャーナルで捕捉。リネーム・移動しても履歴が追従

## 構成

- `DaraskFiler.exe` — WPF アプリ(非昇格、既定で常駐)。検索インデックスと履歴 DB はこちらが保持
- `DaraskFilerd.exe` — LocalSystem サービス(オプトイン)。MFT/USN の特権 I/O 専任。
  ファイル名/メタデータのみを名前付きパイプで供給(ファイル内容には触れない — Everything と同じ
  セキュリティモデル)

スタック: C# 14 / .NET 10 LTS / WPF / WPF-UI / Vanara + CsWin32 / SQLite / Velopack

## ドキュメント

設計書は [docs/](docs/) 一式(01 要件 → 07 マイルストーン)。実装エージェント向け規約は
[CLAUDE.md](CLAUDE.md)。

## ステータス

設計完了。実装は M0–M2 完了 + 右クリックメニュー/履歴パネル/タブ/ごみ箱まで実装済み
(詳細は [PROGRESS.md](PROGRESS.md))。次は M3(IFileOperation エンジン)。
