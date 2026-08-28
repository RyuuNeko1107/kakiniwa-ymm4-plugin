# 配布用 .ymme の作成(BOOTH アップロード用)。先に build-plugin.ps1 でビルドしておくこと。
# .ymme = 「親フォルダ名=プラグインフォルダ名」の ZIP を拡張子変更したもの(YMM4 のインストーラーが展開する)。
# -Version は**省略推奨**。指定できる値はソースの PluginVersion ただ一つ(違えば下で
# 止まる)なので、既定では読み取って使う。以前は既定が "0.1.0" 固定で、引数なしだと
# 必ず失敗する状態だった(版を上げるたびに既定が置き去りになる)。
param([string]$Version = "")
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
# ★版の正はソースの PluginVersion。-Version を明示したときだけ突き合わせる
#   (ずれたまま進むと、中身と README・ファイル名が別の版になる)。
$csVer = (Select-String -Path (Join-Path $srcDir "*.cs") -Pattern 'PluginVersion = "([^"]+)"' | Select-Object -First 1).Matches.Groups[1].Value
if (-not $csVer) { throw "ソースに PluginVersion が見つかりません" }
if (-not $Version) { $Version = $csVer }
elseif ($csVer -ne $Version) {
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
対応: YMM4 v4.54.0.1 / v4.55.1.1 で動作確認(本体内部の仕組みを使うため、YMM4の更新で動かなくなる場合があります)

導入: ダウンロードした .ymme ファイル(書き庭_YMM4プラグイン_$Version.ymme)をダブルクリック
      → YMM4 のインストーラーに従う → YMM4 を再起動
うまくいかないとき: このフォルダ(KakiniwaYmm4Import)ごと、YMM4 のフォルダの
      user\plugin\ の中へコピーしてから YMM4 を再起動してください。
      (YMM4 を起動したままだと上書きできないことがあります)
★注意: dll だけを取り出して置かないでください(user\plugin\ の直下や、
      YMM4 本体の plugin\ に dll 単体を置く形は、YMM4 が想定している置き方ではありません)。
      必ず「KakiniwaYmm4Import」フォルダごと user\plugin\ の中に入れます。
YMM4 が起動しなくなったとき: 置いた dll(またはフォルダ)を消せば元に戻ります。
      YMM4 本体の入れ直しは要りません。
使い方: YMM4 でプロジェクトを開いて保存 → ツール →「書き庭の台本を取り込む(β版)」
詳しく: https://kakiniwa.jp/plugin/
"@
Set-Content -Path (Join-Path $stage "README.txt") -Value $readme -Encoding UTF8

$out = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Force $out | Out-Null
# ★.ymme と同じ中身の .zip は作らない。以前は両方作っていて、BOOTH に .zip の方を
#   上げてしまい、解凍した人が dll 単体を手で置いて YMM4 が起動しなくなる報告が出た
#   (2026-08-28)。zip で配るときも「開いたら .ymme が出てくる」形にする。
$ymme = Join-Path $out "書き庭_YMM4プラグイン_$Version.ymme"
if (Test-Path $ymme) { Remove-Item $ymme -Force }
$tmpZip = Join-Path $stageRoot "plugin.zip"
Compress-Archive -Path $stage -DestinationPath $tmpZip
Move-Item $tmpZip $ymme -Force

# 配布用 zip = .ymme + 導入のしかた.txt(zip しか受け付けない配布先向け)
$wrap = Join-Path $stageRoot "wrap"
New-Item -ItemType Directory -Force $wrap | Out-Null
Copy-Item $ymme $wrap
$howto = @"
書き庭 台本インポート(YMM4プラグイン) v$Version の導入のしかた

1. このフォルダにある「書き庭_YMM4プラグイン_$Version.ymme」をダブルクリック
2. YMM4 のインストーラーが開くので、案内に従う
3. YMM4 を再起動 → ツール →「書き庭の台本を取り込む(β版)」

★.ymme をさらに解凍して、中の dll だけを置かないでください。
  ダブルクリックできないときは、.ymme を YMM4 の画面へドラッグ&ドロップしてください。
  それでも駄目なときだけ、.ymme を zip として展開し、中の「KakiniwaYmm4Import」
  フォルダごと YMM4 の user\plugin\ の中へコピーします(dll 単体ではなくフォルダごと)。
YMM4 が起動しなくなったとき: 置いた dll(またはフォルダ)を消せば元に戻ります。
詳しく: https://kakiniwa.jp/plugin/
"@
Set-Content -Path (Join-Path $wrap "導入のしかた.txt") -Value $howto -Encoding UTF8
$dist = Join-Path $out "書き庭_YMM4プラグイン_$Version.zip"
if (Test-Path $dist) { Remove-Item $dist -Force }
Compress-Archive -Path (Join-Path $wrap "*") -DestinationPath $dist
Write-Host "配布物: $out"
Write-Host "  .ymme … BOOTH にはこれをアップロード(ダブルクリックで導入できる形)"
Write-Host "  .zip  … zip しか受け付けない配布先向け(開くと .ymme と 導入のしかた.txt が出る)"
