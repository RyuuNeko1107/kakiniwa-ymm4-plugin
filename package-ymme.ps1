# 配布用 .ymme の作成(BOOTH アップロード用)。先に build-plugin.ps1 でビルドしておくこと。
# .ymme = 「親フォルダ名=プラグインフォルダ名」の ZIP を拡張子変更したもの(YMM4 のインストーラーが展開する)。
param([string]$Version = "0.1.0")
$ErrorActionPreference = "Stop"
# YMM4 の場所。環境変数 YMM4_DIR があればそれを使い、無ければ既定の候補を順に探す。
# ★作者のPCのパスを直書きしていると、他の人はここで詰まる(2026-08-08 監査)。
$ymm = $env:YMM4_DIR
if (-not $ymm) {
  $候補 = @(
    "$env:USERPROFILE\Desktop\ymm4",
    "$env:USERPROFILE\Documents\ymm4",
    "D:\ymm4",
    "C:\ymm4"
  )
  $ymm = $候補 | Where-Object { Test-Path (Join-Path $_ "YukkuriMovieMaker.exe") } | Select-Object -First 1
}
if (-not $ymm) { throw "YMM4 の場所が分かりません。環境変数 YMM4_DIR に YMM4 のフォルダを指定してください(例: `$env:YMM4_DIR='D:\ymm4')" }
$dll = Join-Path $ymm "user\plugin\KakiniwaYmm4Import\KakiniwaYmm4Import.dll"
if (-not (Test-Path $dll)) { throw "DLL がありません。先に build-plugin.ps1 を実行してください: $dll" }
# ★鮮度チェック: ソース(.cs)が DLL より新しければビルド忘れ=古い DLL を配ってしまう
$srcDir = Join-Path $PSScriptRoot "KakiniwaYmm4Import"
$newestSrc = (Get-ChildItem "$srcDir\*.cs" | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
if ($newestSrc -and $newestSrc.LastWriteTime -gt (Get-Item $dll).LastWriteTime) {
  throw "ソース($($newestSrc.Name))が DLL より新しい=ビルド忘れです。先に build-plugin.ps1 を実行してください"
}
# ★版の突き合わせ: -Version とソースの PluginVersion がずれると README だけ別の版になる
$csVer = (Select-String -Path (Join-Path $srcDir "*.cs") -Pattern 'PluginVersion = "([^"]+)"' | Select-Object -First 1).Matches.Groups[1].Value
if (-not $csVer) { throw "ソースに PluginVersion が見つかりません" }
if ($csVer -ne $Version) {
  throw "指定の -Version $Version がソースの PluginVersion $csVer と一致しません"
}

$stageRoot = Join-Path $env:TEMP "ymme-stage"
$stage = Join-Path $stageRoot "KakiniwaYmm4Import"
if (Test-Path $stageRoot) { Remove-Item $stageRoot -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item $dll $stage
$readme = @"
書き庭 台本インポート(YMM4プラグイン) v$Version

書き庭の「YMM4受け渡しパック」(timeline.json+素材)を YMM4 のタイムラインへ一括配置します。
対応: YMM4 v4.54.0.1 で動作確認(本体内部の仕組みを使うため、YMM4の更新で動かなくなる場合があります)

導入: ダウンロードした .ymme ファイル(書き庭_YMM4プラグイン_$Version.ymme)をダブルクリック
      → YMM4 のインストーラーに従う → YMM4 を再起動
うまくいかないとき: このフォルダ(KakiniwaYmm4Import)ごと、YMM4 のフォルダの
      user\plugin\ の中へコピーしてから YMM4 を再起動してください。
      (YMM4 を起動したままだと上書きできないことがあります)
使い方: YMM4 でプロジェクトを開いて保存 → ツール →「書き庭の台本を取り込む(β版)」
詳しく: https://kakiniwa.jp/plugin/
"@
Set-Content -Path (Join-Path $stage "README.txt") -Value $readme -Encoding UTF8

$out = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Force $out | Out-Null
$zip = Join-Path $out "書き庭_YMM4プラグイン_$Version.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $stage -DestinationPath $zip
Copy-Item $zip (Join-Path $out "書き庭_YMM4プラグイン_$Version.ymme") -Force
Write-Host "配布物: $out (BOOTH には .ymme をアップロード。zip は予備)"
