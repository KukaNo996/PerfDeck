# 本地一键打包：产出单文件 dist/PerfDeck.exe + zip + SHA256SUMS.txt
#   用法：powershell -ExecutionPolicy Bypass -File tools\make_release.ps1
#   产物：dist\PerfDeck.exe / dist\PerfDeck-v1.0.0-win64.zip / dist\SHA256SUMS.txt
# CI 里跑的是 .github/workflows/release.yml，逻辑与这里保持一致。

param([string]$Version = "")

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

if (-not $Version) {
    $tag = git describe --tags --abbrev=0 2>$null
    if ($LASTEXITCODE -eq 0 -and $tag) { $Version = $tag.Trim() } else { $Version = 'v1.0.0' }
}

Write-Host "=== 构建 PerfDeck $Version ===" -ForegroundColor Cyan
dotnet publish -c Release -r win-x64 --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（退出码 $LASTEXITCODE）" }

$exe = 'bin/Release/net8.0-windows/win-x64/publish/PerfDeck.exe'
if (-not (Test-Path $exe)) { throw "没有找到构建产物：$exe" }

$stage = 'staging'
$dist = 'dist'
Remove-Item -Recurse -Force $stage, $dist -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage, $dist | Out-Null

Copy-Item $exe "$stage/PerfDeck.exe"
Copy-Item 'README.md', 'LICENSE' $stage

# 裸 exe 也放一份，方便直接拿走就用
Copy-Item $exe "$dist/PerfDeck.exe"
Compress-Archive -Path "$stage/*" -DestinationPath "$dist/PerfDeck-$Version-win64.zip" -Force

$lines = foreach ($f in @("$dist/PerfDeck.exe", "$dist/PerfDeck-$Version-win64.zip")) {
    $h = (Get-FileHash $f -Algorithm SHA256).Hash.ToLower()
    "$h  $(Split-Path $f -Leaf)"
}
$lines | Set-Content -Path "$dist/SHA256SUMS.txt" -Encoding ascii

Remove-Item -Recurse -Force $stage
Write-Host ""
Write-Host "=== 完成 ===" -ForegroundColor Green
Get-ChildItem $dist | Select-Object Name, @{n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
