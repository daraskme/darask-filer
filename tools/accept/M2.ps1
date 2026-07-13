#Requires -Version 5.1
<#
.SYNOPSIS
    M2(アイコン + サムネイルパイプライン)の受け入れ基準を検証する(docs/07 M2)。
.DESCRIPTION
    1. dotnet build + dotnet test が green であること(ShellSmokeTests: Vanara アイコン/サムネイル/
       ショートカット矢印/desktop.ini 実アイコン取得の単体テストを含む)
    2. mkfixture --images でサムネイル用フィクスチャを生成できること
    .NOTES
    以下は自動化できず GUI 目視で確認済み(PROGRESS.md 参照):
    - 100k フォルダー初回描画で拡張子アイコン即表示(ETW での UI スレッド I/O ゼロ検証は未実施。
      設計上 SHGFI_USEFILEATTRIBUTES はディスク I/O を発生させない)
    - 可視行サムネイル読み込み(500 件の BMP で実際のカラーサムネイルを確認)
    - 高速スクロールでのキャンセル(コードで CancellationTokenSource による実装を確認)
    - OneDrive プレースホルダー非ハイドレート(IsCloudPlaceholder チェックをコードで確認。
      実機 OneDrive オンデマンドファイルでの実地検証は環境依存のため未実施)
    - アイコングリッド(VirtualizingWrapPanel)のスクロール(500 件で滑らかな動作を確認)
    - desktop.ini カスタムフォルダーアイコン(Desktop/Downloads/iCloudDrive で実アイコン表示を確認)
#>
# CI 用: build-and-test ジョブで既にビルド・テスト済みの場合、このスクリプト内での重複実行を
# 省略する(単体実行時は指定不要 — 常にビルド・テストする)。
# 注意: BOM なし UTF-8 の .ps1 は Windows PowerShell 5.1 が ANSI コードページで読むため、
# 日本語コメントを param() の括弧の中に置くとパーサーがバイト列を誤読して構文が壊れる
# (実機で確認 — スイッチが常に $false になった)。コメントは必ず param() の外に置くこと。
param(
    [switch]$SkipBuildAndTest
)
$ErrorActionPreference = "Stop"
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$repoRoot = Resolve-Path "$PSScriptRoot\..\.."
$dotnet = "$env:LOCALAPPDATA\dotnet-10\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

$testDataDir = Join-Path $repoRoot "testdata\accept-m2"
if (Test-Path $testDataDir) {
    $long = "\\?\" + (Resolve-Path $testDataDir).Path
    Remove-Item $long -Recurse -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Path $testDataDir -Force | Out-Null

$failures = @()
function Check($name, [bool]$condition) {
    if ($condition) {
        Write-Host "[PASS] $name" -ForegroundColor Green
    } else {
        Write-Host "[FAIL] $name" -ForegroundColor Red
        $script:failures += $name
    }
}

if ($SkipBuildAndTest) {
    Write-Host "=== 1. dotnet build + test (呼び出し元で検証済みのためスキップ) ===" -ForegroundColor Yellow
} else {
    Write-Host "=== 1. dotnet build + test (ShellSmokeTests 含む) ==="
    & $dotnet build "$repoRoot\darask-filer.slnx" -c Release
    Check "dotnet build succeeds" ($LASTEXITCODE -eq 0)
    & $dotnet test "$repoRoot\darask-filer.slnx" -c Release --no-build
    Check "dotnet test succeeds" ($LASTEXITCODE -eq 0)
}

Write-Host "`n=== 2. mkfixture --images でサムネイル用フィクスチャ生成 ==="
$imgDir = Join-Path $testDataDir "images"
$out = & $dotnet run --project "$repoRoot\tools\mkfixture" -c Release --no-build -- --profile 50 --seed 1 --out $imgDir --images 200
$skipped = [int](($out | Select-String "skipped=(\d+)").Matches.Groups[1].Value)
$bmpCount = (Get-ChildItem (Join-Path $imgDir "images") -Filter "*.bmp" -ErrorAction SilentlyContinue).Count
Check "200 bmp files created" ($bmpCount -eq 200)
Check "no skipped entries" ($skipped -eq 0)

Write-Host "`n=== Summary ==="
if ($failures.Count -eq 0) {
    Write-Host "M2: ALL AUTOMATED CHECKS PASSED" -ForegroundColor Green
    Write-Host "(GUI 目視確認項目は PROGRESS.md を参照)" -ForegroundColor Yellow
    exit 0
} else {
    Write-Host "M2: FAILED CHECKS: $($failures -join ', ')" -ForegroundColor Red
    exit 1
}
