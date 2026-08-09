# 書き庭 台本インポートプラグインのSDKレスビルド+インストール。
# dotnet SDK 不要: pwsh 内蔵 Roslyn で、YMM4 同梱の .NET アセンブリ一式を参照してコンパイルする。
# 使い方: pwsh -File build-plugin.ps1  (YMM4起動中ならロック解除を待ってインストール)
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
$srcDir = Join-Path $PSScriptRoot "KakiniwaYmm4Import"
$out = Join-Path ([IO.Path]::GetTempPath()) "KakiniwaYmm4Import.dll"
if (Test-Path $out) { Remove-Item $out -Force }

Add-Type -Path "$PSHOME\Microsoft.CodeAnalysis.dll" | Out-Null
Add-Type -Path "$PSHOME\Microsoft.CodeAnalysis.CSharp.dll" | Out-Null

# 参照 = YMM4 同梱のマネージドDLLだけ(ネイティブDLLは除外)。pwsh 側の参照は混ぜない
$refs = [System.Collections.Generic.List[Microsoft.CodeAnalysis.MetadataReference]]::new()
$count = 0
foreach ($f in Get-ChildItem "$ymm\*.dll") {
  try {
    [System.Reflection.AssemblyName]::GetAssemblyName($f.FullName) | Out-Null
    $refs.Add([Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($f.FullName))
    $count++
  } catch { }
}
Write-Host "参照アセンブリ: $count 件"

# フォルダ内の *.cs をすべてコンパイル(責務ごとに分割: Plugin/PackModel/Importer 等)
$trees = [System.Collections.Generic.List[Microsoft.CodeAnalysis.SyntaxTree]]::new()
foreach ($cs in Get-ChildItem "$srcDir\*.cs") {
  $trees.Add([Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -Raw $cs.FullName)))
}
Write-Host "ソース: $($trees.Count) ファイル"
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new(
  [Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$options = $options.WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
$comp = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(
  "KakiniwaYmm4Import", $trees.ToArray(), $refs, $options)

$fs = [IO.File]::Create($out)
$result = $comp.Emit($fs)
$fs.Dispose()
if (-not $result.Success) {
  Remove-Item $out -Force -ErrorAction SilentlyContinue
  Write-Host "=== ビルド失敗 ==="
  $result.Diagnostics | Where-Object { $_.Severity -eq "Error" } |
    Select-Object -First 15 | ForEach-Object { Write-Host $_.ToString() }
  exit 1
}
Write-Host "ビルド成功: $([Math]::Round((Get-Item $out).Length/1KB,1)) KB"

# インストール(YMM4起動中はロック解除を待つ・最大10分)
$dest = "$ymm\user\plugin\KakiniwaYmm4Import"
New-Item -ItemType Directory -Force $dest | Out-Null
$deadline = (Get-Date).AddMinutes(10)
while ($true) {
  try {
    Copy-Item $out "$dest\KakiniwaYmm4Import.dll" -Force -ErrorAction Stop
    Write-Host "インストール完了: $dest\KakiniwaYmm4Import.dll"
    break
  } catch {
    if ((Get-Date) -gt $deadline) { Write-Host "ロック解除待ちタイムアウト。YMM4を閉じて再実行してください"; exit 1 }
    Write-Host "YMM4起動中(ロック)… 閉じられるのを待っています"
    Start-Sleep -Seconds 3
  }
}
