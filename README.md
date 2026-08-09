# 書き庭 YMM4プラグイン(KakiniwaYmm4Import)

このページは、YMM4 で動画を作る人向けの案内です。プラグインの入れ方と使い方、
ソースからビルドする方法(開発者向け)がわかります。

台本エディタ「[書き庭](https://kakiniwa.jp/)」で書いた台本を、YMM4(ゆっくりMovieMaker4)の
タイムラインへ一括配置するプラグインです。セリフ(ボイス生成込み)・立ち絵・表情・背景・
BGM・SE・立ち位置を、書き庭が書き出す「YMM4受け渡しパック」
(台本と素材をひとつのフォルダにまとめた受け渡し用データ)から読み込んで配置します。

- 入力形式(受け渡しパック)の仕様: [docs/pack-format.md](docs/pack-format.md)
  — この形式を書き出せば、書き庭以外のツールからでも取り込めます
- YMM4への配置のしかた(何がどのアイテムになるか): [docs/ymm4-mapping.md](docs/ymm4-mapping.md)

## インストール(利用者向け)

配布中の `.ymme` を YMM4 にドラッグ&ドロップするのがいちばん簡単です。
配布場所: https://kakiniwa.jp/plugin/

## 使い方

1. YMM4 でプロジェクトを開く(先に保存しておくのがおすすめです)
2. メニューの ツール → 「書き庭の台本を取り込む(β版)」
3. パックのフォルダを選ぶ → 話者の割り当て(YMM4に登録済みのキャラを選択)
   → 配置先(開始レイヤー・開始位置)を確認 → 配置
4. 割り当てや配置先は `user/setting/kakiniwa-import.json` に記憶され、次回から引き継がれます

## ソースからビルドする(開発者向け)

ソースは `KakiniwaYmm4Import/` 配下に責務ごとに分かれています(XAML不使用・UIはコード構築):

| ファイル | 内容 |
|---|---|
| Plugin.cs | プラグインのエントリ(ツールメニュー登録・設定の保存) |
| PackModel.cs | 受け渡しパック(timeline.json)の型と読み込み・検証 |
| Importer.cs | 配置の本体(時間計算・レーン=タイムラインの行の割り当て・実尺=生成されたボイスの実際の長さでの再配置) |
| Ymm4Interop.cs | YMM4 本体をリフレクション(=プログラムの部品を名前で探して呼び出す仕組み)で扱う層 |
| ImportUi.cs | 取り込みダイアログ(話者割り当て・配置先) |
| MediaProbe.cs | 音声の実尺(WAV/MP3/OGG)と画像寸法のヘッダ直読み |
| LayerPath.cs | PSDレイヤーパスの相互変換 |

YMM4 本体の DLL を参照するため、手元に YMM4 のインストールが必要です(DLL は同梱しません)。
YMM4 の場所は環境変数 `YMM4_DIR` で指定します(例: `$env:YMM4_DIR='D:\ymm4'`)。

dotnet SDK が無くてもビルドできます(pwsh 内蔵の Roslyn を使います):

```powershell
pwsh -File build-plugin.ps1   # ビルドして <YMM4>/user/plugin/ へインストール
```

dotnet SDK がある場合は通常ビルドも使えます:

```powershell
dotnet build KakiniwaYmm4Import
dotnet test KakiniwaYmm4Import.Tests   # 純ロジックのテスト(49件)
```

配布用 `.ymme` の作成は `pwsh -File package-ymme.ps1 -Version <版数>` です。

## 互換性についての注意

このプラグインは YMM4 本体のドキュメント外 API(公式に公開されていない内部機能。
`MainModel.AddVoiceItemAsync`、`Timeline.AddItems` など)をリフレクション経由で
使っています。YMM4 の更新で動かなくなる
可能性があり、その際はログに注意を出して安全側(何もしない)に倒れる設計です。
検証済みの環境は YMM4 v4.54.0.1 / .NET 10 です。

## 不具合報告・要望

[Issues](../../issues) へどうぞ。書き庭本体の話題は [公認コミュニティ](https://kakiniwa.jp/discord/) でも受け付けています。

## ライセンス

MIT License。詳細は [LICENSE](LICENSE) を見てください。

YMM4(ゆっくりMovieMaker4)は饅頭遣いさんの製品です。このプラグインは非公式であり、
YMM4 本体のファイルは一切含みません。
