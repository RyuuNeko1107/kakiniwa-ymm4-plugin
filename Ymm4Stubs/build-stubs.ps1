# CI用 YMM4 スタブのビルド。4つのスタブ DLL を dist/ に出す。
# 使い方: pwsh ymm4-plugin/Ymm4Stubs/build-stubs.ps1
#   → その後 $env:YMM4_DIR = "<repo>/ymm4-plugin/Ymm4Stubs/dist" で dotnet test を回す。
# dotnet の場所は -Dotnet で上書き可(既定は PATH の dotnet)。
param([string]$Dotnet = "dotnet")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"

foreach ($name in @(
    "YukkuriMovieMaker.Plugin",
    "YukkuriMovieMaker",
    "YukkuriMovieMaker.Controls",
    "YukkuriMovieMaker.Settings")) {
  & $Dotnet build (Join-Path $root "$name\$name.csproj") -c Release -o $dist --nologo -v quiet
  if ($LASTEXITCODE -ne 0) { throw "スタブのビルド失敗: $name" }
}

Write-Host "スタブ出力: $dist"
Get-ChildItem $dist -Filter "YukkuriMovieMaker*.dll" | ForEach-Object { Write-Host ("  " + $_.Name) }
