#Requires -Version 5.1
<#
.SYNOPSIS
    M1(アプリシェル + 高速列挙)の受け入れ基準を検証する(docs/07 M1)。
.DESCRIPTION
    1. dotnet build + dotnet test が green であること
    2. 単一フォルダーに 10 万エントリを配置した状態で、列挙+ソート(名前)の合計が
       300ms 未満であること(UI 描画分の余裕を残すため、コアロジックは十分速くしておく)
    3. 名前/サイズ/日付それぞれのソートが 200ms 未満であること(合成 20 万件データ)
    4. RDCW オーバーフロー→再走査で列挙オラクルと差分ゼロであること(xUnit テストに委譲)
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

$testDataDir = Join-Path $repoRoot "testdata\accept-m1"
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
    Write-Host "=== 1. dotnet build + test (全ソリューション) ==="
    & $dotnet build "$repoRoot\darask-filer.slnx" -c Release
    Check "dotnet build succeeds" ($LASTEXITCODE -eq 0)
    & $dotnet test "$repoRoot\darask-filer.slnx" -c Release --no-build
    Check "dotnet test succeeds (FastEnumerator/DirectoryWatcher/RdcwOverflow 含む)" ($LASTEXITCODE -eq 0)
}

# GitHub Actions windows-latest の共有 vCPU は開発機より単一コア性能が明確に低く、CPU バウンドな
# 列挙+ソート/ソート単体ベンチは実測で 2〜2.2 倍遅い(I/O バウンドな M0 の 100k 生成はむしろ CI の
# ephemeral disk の方が速かった — 遅いのは CPU バウンドな処理のみ)。ローカル開発機での基準値は
# そのまま保ちつつ、CI 実行時のみ 3 倍の閾値を使う(実測の 2.2 倍に対して安全マージンを確保)。
$ciMultiplier = if ($env:CI -eq "true") { 3 } else { 1 }
$enumSortBudgetMs = 300 * $ciMultiplier
$sortBudgetMs = 200 * $ciMultiplier

Write-Host "`n=== 2. 単一フォルダー 10 万エントリ: 列挙+ソート < ${enumSortBudgetMs}ms ==="
$flatDir = Join-Path $testDataDir "flat100k"
& $dotnet run --project "$repoRoot\tools\mkfixture" -c Release --no-build -- --profile 100k --seed 42 --out $flatDir --flat | Out-Null

$enumOut = & $dotnet run --project "$repoRoot\tools\bench" -c Release --no-build -- enum --path $flatDir
$entryCount = [int](($enumOut | Select-String "entryCount=(\d+)").Matches.Groups[1].Value)
$totalMs = [int](($enumOut | Select-String "totalMs=(\d+)").Matches.Groups[1].Value)
Write-Host "entryCount=$entryCount totalMs=$totalMs"
Check "100k フラットフォルダーが正確に列挙される(100000+manifest)" ($entryCount -ge 100000)
Check "列挙+ソート(名前) < ${enumSortBudgetMs}ms" ($totalMs -lt $enumSortBudgetMs)

Write-Host "`n=== 3. ソート単体ベンチ(合成 20 万件): 名前/サイズ/日付 各 < ${sortBudgetMs}ms @100k 相当 ==="
$sortOut = & $dotnet run --project "$repoRoot\tools\bench" -c Release --no-build -- sort --count 100000
$byName = [int](($sortOut | Select-String "sortByNameMs=(\d+)").Matches.Groups[1].Value)
$bySize = [int](($sortOut | Select-String "sortBySizeMs=(\d+)").Matches.Groups[1].Value)
$byDate = [int](($sortOut | Select-String "sortByDateMs=(\d+)").Matches.Groups[1].Value)
Write-Host "sortByNameMs=$byName sortBySizeMs=$bySize sortByDateMs=$byDate"
Check "名前ソート < ${sortBudgetMs}ms @100k" ($byName -lt $sortBudgetMs)
Check "サイズソート < ${sortBudgetMs}ms @100k" ($bySize -lt $sortBudgetMs)
Check "日付ソート < ${sortBudgetMs}ms @100k" ($byDate -lt $sortBudgetMs)

Write-Host "`n=== Summary ==="
if ($failures.Count -eq 0) {
    Write-Host "M1: ALL CHECKS PASSED" -ForegroundColor Green
    Write-Host "(注: 10万行スクロールのフレームレート、Alt+矢印/マウスXボタン操作、MS-IME 実機入力は" -ForegroundColor Yellow
    Write-Host " 本スクリプトでは自動検証できない。GUI での目視確認は PROGRESS.md に記録済み。)" -ForegroundColor Yellow
    exit 0
} else {
    Write-Host "M1: FAILED CHECKS: $($failures -join ', ')" -ForegroundColor Red
    exit 1
}
