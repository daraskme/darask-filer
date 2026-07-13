#Requires -Version 5.1
<#
.SYNOPSIS
    M0(リポジトリ基盤 + フィクスチャ生成器)の受け入れ基準を検証する(docs/07 M0)。
.DESCRIPTION
    1. dotnet build + dotnet test が green であること
    2. mkfixture が同一シードで同一チェックサム(rootHash)のツリーを再生成すること
    3. 100k ツリー生成が 60 秒未満であること
    4. --images 50000 が壊れていない画像(Windows API でデコード可能な BMP)を生成すること
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

$testDataDir = Join-Path $repoRoot "testdata\accept-m0"
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
    Write-Host "=== 1-2. dotnet build/test (呼び出し元で検証済みのためスキップ) ===" -ForegroundColor Yellow
} else {
    Write-Host "=== 1. dotnet build ==="
    & $dotnet build "$repoRoot\darask-filer.slnx" -c Release
    Check "dotnet build succeeds" ($LASTEXITCODE -eq 0)

    Write-Host "`n=== 2. dotnet test ==="
    & $dotnet test "$repoRoot\darask-filer.slnx" -c Release --no-build
    Check "dotnet test succeeds" ($LASTEXITCODE -eq 0)
}

Write-Host "`n=== 3. mkfixture determinism ==="
$out1 = Join-Path $testDataDir "det1"
$out2 = Join-Path $testDataDir "det2"
$run1 = & $dotnet run --project "$repoRoot\tools\mkfixture" -c Release --no-build -- --profile 500 --seed 42 --out $out1 --images 5
$run2 = & $dotnet run --project "$repoRoot\tools\mkfixture" -c Release --no-build -- --profile 500 --seed 42 --out $out2 --images 5
$hash1 = ($run1 | Select-String "rootHash=(.+)").Matches.Groups[1].Value
$hash2 = ($run2 | Select-String "rootHash=(.+)").Matches.Groups[1].Value
Write-Host "hash1=$hash1 hash2=$hash2"
Check "same seed -> same rootHash" ($hash1 -eq $hash2 -and $hash1.Length -gt 0)

Write-Host "`n=== 4. 100k tree generation < 60s ==="
$out100k = Join-Path $testDataDir "perf100k"
$runPerf = & $dotnet run --project "$repoRoot\tools\mkfixture" -c Release --no-build -- --profile 100k --seed 42 --out $out100k
$elapsedMs = [int](($runPerf | Select-String "elapsedMs=(\d+)").Matches.Groups[1].Value)
$fileCount = [int](($runPerf | Select-String "files=(\d+)").Matches.Groups[1].Value)
Write-Host "elapsedMs=$elapsedMs files=$fileCount"
Check "100k files generated" ($fileCount -eq 100000)
Check "100k generation < 60000ms" ($elapsedMs -lt 60000)

Write-Host "`n=== 5. --images 50000 produces valid images ==="
$outImg = Join-Path $testDataDir "images50k"
& $dotnet run --project "$repoRoot\tools\mkfixture" -c Release --no-build -- --profile 100 --seed 1 --out $outImg --images 50000 | Out-Null
$imgDir = Join-Path $outImg "images"
$bmpCount = (Get-ChildItem $imgDir -Filter "*.bmp").Count
Check "50000 bmp files created" ($bmpCount -eq 50000)

Add-Type -AssemblyName System.Drawing
$sample = Get-ChildItem $imgDir -Filter "*.bmp" | Select-Object -First 10
$decodeOk = $true
foreach ($f in $sample) {
    try {
        $img = [System.Drawing.Image]::FromFile($f.FullName)
        $img.Dispose()
    } catch {
        $decodeOk = $false
    }
}
Check "sampled bmp files decode successfully" $decodeOk

Write-Host "`n=== Summary ==="
if ($failures.Count -eq 0) {
    Write-Host "M0: ALL CHECKS PASSED" -ForegroundColor Green
    exit 0
} else {
    Write-Host "M0: FAILED CHECKS: $($failures -join ', ')" -ForegroundColor Red
    exit 1
}
