// 窓の文言の辞書(日本語・英語)。YMM4 の表示言語(CurrentUICulture)に合わせて選ぶ。
//
// ★対象は「窓の文言」だけ。取り込みログ(Importer.cs の診断メッセージ)は日本語のまま:
//   数が多く、症状の報告に使うもので、読み手(作者)が同じ言語で見られる方が都合が良いため。
//   ただし窓に出す案内・警告(未割り当て・停止の説明など)は辞書にある。
// ★言語の足し方(どちらでもよい。足りないキーは その言語 → 英語 → 日本語 の順で補うので、一部だけ訳してよい):
//   1. コードを触らない: プラグインのフォルダの lang/<言語コード>.json に { "キー": "訳" } を置く(例 lang/ko.json)。
//      キーは下の日本語の辞書(Ja)と同じ。同じコードが組み込みにあれば、書いたキーだけ上書きする。
//   2. ソースに足す: 辞書(例 Ko)を作り、Languages に1行足す。
// ★英語の辞書に日本語を残さない(テストが見張る)。{0} {1} は string.Format の差し込み位置。
//   翻訳で差し込み位置が壊れても窓は落ちない(英語 → 日本語の文言で代用する)。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KakiniwaYmm4Import
{
    public static class Tr
    {
        /// <summary>辞書のある言語(先頭の2文字コード)。</summary>
        public static readonly Dictionary<string, Dictionary<string, string>> Languages =
            new Dictionary<string, Dictionary<string, string>>
            {
                ["ja"] = Strings.Ja,
                ["en"] = Strings.En,
                ["es"] = Strings.Es,
            };

        static string? forced;

        /// <summary>利用者が窓で選んだ言語("auto" や null や辞書に無いコードなら YMM4 の表示言語に従う)。</summary>
        public static string? Preferred { get; set; }

        static readonly Dictionary<string, string> NativeNames = new Dictionary<string, string>
        {
            ["ja"] = "日本語", ["en"] = "English", ["ko"] = "한국어", ["zh"] = "中文", ["es"] = "Español",
            ["pt"] = "Português", ["fr"] = "Français", ["de"] = "Deutsch", ["it"] = "Italiano", ["ru"] = "Русский",
            ["id"] = "Bahasa Indonesia", ["vi"] = "Tiếng Việt", ["th"] = "ไทย", ["tr"] = "Türkçe",
        };

        /// <summary>言語コードの表示名(その言語自身の書き方)。知らないコードは大文字のコード。</summary>
        public static string NativeName(string code)
        {
            string? n;
            return NativeNames.TryGetValue(code, out n) ? n : code.ToUpperInvariant();
        }

        /// <summary>選べる言語のコード(辞書にあるもの。日本語・英語を先頭に、残りは名前順)。</summary>
        public static List<string> AvailableLanguages()
        {
            var rest = Languages.Keys.Where(k => k != "ja" && k != "en").OrderBy(k => k, StringComparer.Ordinal);
            return new[] { "ja", "en" }.Where(k => Languages.ContainsKey(k)).Concat(rest).ToList();
        }

        static Tr()
        {
            // プラグインのフォルダの lang/*.json を読む。読めなくても窓は出す(組み込みの辞書だけで動く)
            try
            {
                var dll = Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(dll))
                    LoadExternal(Path.Combine(Path.GetDirectoryName(dll)!, "lang"), Languages);
            }
            catch { /* 翻訳ファイルの不具合で窓を止めない */ }
        }

        /// <summary>外部の翻訳ファイルの上限(これより大きいものは読まない)。</summary>
        internal const long MaxLangFileBytes = 256 * 1024;

        /// <summary>dir の *.json を言語として登録する。ファイル名が言語コード(英字2〜3文字)、
        /// 中身は { "キー": "訳" }。知らないキー・文字列でない値・空の訳は読み飛ばす。
        /// 同じコードが既にあれば、書かれたキーだけ上書きする(組み込みの辞書そのものは書き換えない)。
        /// 読み込んだ言語の数を返す。</summary>
        internal static int LoadExternal(string dir, IDictionary<string, Dictionary<string, string>> into)
        {
            if (!Directory.Exists(dir)) return 0;
            var loaded = 0;
            foreach (var file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    var code = Path.GetFileNameWithoutExtension(file).ToLowerInvariant();
                    if (!Regex.IsMatch(code, "^[a-z]{2,3}$")) continue;
                    if (new FileInfo(file).Length > MaxLangFileBytes) continue;
                    var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(file));
                    if (raw == null) continue;
                    var merged = into.TryGetValue(code, out var existing)
                        ? new Dictionary<string, string>(existing)
                        : new Dictionary<string, string>();
                    var any = false;
                    foreach (var kv in raw)
                    {
                        if (!Strings.Ja.ContainsKey(kv.Key)) continue;
                        if (kv.Value.ValueKind != JsonValueKind.String) continue;
                        var text = kv.Value.GetString();
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        merged[kv.Key] = text!;
                        any = true;
                    }
                    if (!any) continue;
                    into[code] = merged;
                    loaded++;
                }
                catch { /* 壊れたファイルは読み飛ばす */ }
            }
            return loaded;
        }

        /// <summary>使う言語。YMM4 の表示言語が辞書にあればそれ、無ければ英語。</summary>
        public static string Language
        {
            get
            {
                if (forced != null) return forced;
                var p = Preferred;
                if (p != null && p != "auto" && Languages.ContainsKey(p)) return p;
                return Detect(CultureInfo.CurrentUICulture);
            }
        }

        /// <summary>テスト用: 言語を固定する(null で戻す)。</summary>
        internal static void ForceLanguage(string? lang) { forced = lang; }

        internal static string Detect(CultureInfo culture)
        {
            var two = culture.TwoLetterISOLanguageName;
            return Languages.ContainsKey(two) ? two : "en";
        }

        public static string T(string key, params object[] args)
        {
            var s = Lookup(Language, key);
            if (args.Length == 0) return s;
            // 翻訳で {0} を消したり波括弧を壊したりしても、窓を落とさない(英語 → 日本語の文言で代用)
            foreach (var candidate in new[] { s, Lookup("en", key), Lookup("ja", key) })
            {
                try { return string.Format(CultureInfo.CurrentCulture, candidate, args); }
                catch (FormatException) { }
            }
            return key;
        }

        /// <summary>その言語 → 英語 → 日本語 → キーそのもの、の順で探す。</summary>
        internal static string Lookup(string lang, string key)
        {
            Dictionary<string, string>? d;
            string? v;
            if (Languages.TryGetValue(lang, out d) && d.TryGetValue(key, out v)) return v;
            if (Strings.En.TryGetValue(key, out v)) return v;
            if (Strings.Ja.TryGetValue(key, out v)) return v;
            return key;
        }
    }

    public static partial class Strings
    {
        public static readonly Dictionary<string, string> Ja = new Dictionary<string, string>
        {
            ["menu.name"] = "書き庭の台本を取り込む(β版 v{0})",
            ["window.title"] = "書き庭 台本インポート v{0}",
            ["ui.header"] = "書き庭 台本インポート v{0}  /  YMM4 v{1}",
            ["ui.language"] = "言語 / Language ",
            ["log.psd.noPresetType"] = "PsdPreset 型が見つからない(PSDプラグイン未ロード?)",
            ["log.psd.noSpeaker"] = "  {0}: 話者未割り当てのためスキップ",
            ["log.psd.noTachie"] = "  {0}(→{1}): PSD立ち絵未設定のため既定の表情をスキップ",
            ["log.psd.noHostPsd"] = "  {0}/{1}: 割り当て先YMM4キャラのPSDが見つからずスキップ",
            ["log.psd.notBundled"] = "  {0}/{1}: 同梱していない PSD にはプリセットを書きません: {2}",
            ["log.psd.noPsd"] = "  {0}/{1}: 立ち絵PSDが見つからずスキップ",
            ["log.psd.done"] = "表情プリセット登録完了: 計{0}件(YMM4のPSD素材マネージャを開き直すと反映)",
            ["log.psd.none"] = "登録できるプリセットがありませんでした",
            ["log.psd.noState"] = "  プリセット「{0}」: 全身状態を組めずスキップ",
            ["log.psd.noPresetsProp"] = "  {0}: Presets プロパティが書き替えられない",
            ["log.psd.noSave"] = "  {0}: SaveFromPsdFilePath が見つからない",
            ["log.psd.saved"] = "  {0}: {1}件のプリセットを登録 → {2}-ymm.json",
            ["log.psd.saveFail"] = "  {0}: 保存失敗: {1}",
            ["log.psd.needRow"] = "  {0}/{1}: パックの指定(→{2})だけでは、パックの外の PSD には書きません。取り込み窓の立ち絵の行で、書き込み先のキャラを選んでください",
            ["log.psd.keptForeign"] = "  {0}: 同じ名前のプリセット「{1}」は書き庭が作ったものではないので残しました(置き換えるなら YMM4 で消すか、名前を変えてください)",
            ["log.psd.backupFail"] = "  {0}: 控え(.bak)を作れなかったので書き込みをやめました: {1}",
            ["log.psd.linked"] = "  {0}: プリセットの保存先がリンク(ジャンクション・シンボリックリンク)なので書きません",
            ["log.psd.cancelled"] = "表情プリセットの登録を取りやめました",
            ["log.pre.excluded"] = "既存アイテム {0} 件は再配置の対象外にします",
            ["log.pre.fail"] = "既存アイテムの記録に失敗(続行): {0}",
            ["log.dest"] = "配置先: レイヤー{0}から / {1}",
            ["log.dest.frame"] = "{0}フレーム目から",
            ["log.dest.start"] = "先頭から",
            ["log.layers"] = "レイヤー指定: セリフ={0}{1}",
            ["log.layers.fixed"] = " / 固定={0}",
            ["log.se.noLength"] = "  SEの長さを測れないので1秒で置きます(wav/mp3/ogg/opus 以外): {0}",
            ["log.exit"] = "  退場: {0}(立ち絵を {1}秒で終了)",
            ["log.exit.skip"] = "  退場: {0}(立ち絵未配置のためスキップ)",
            ["log.default"] = "既定",
            ["log.layoutUpdate"] = "  配置更新: {0} x={1}% height={2}",
            ["log.serifTemplate"] = "  ※ セリフのアイテムテンプレート適用は現状未対応です(実機検証後に対応予定)。ボイスは既定の設定で配置します",
            ["log.voice.legacyNext"] = "※ 公式方式でボイスを作れなかったので、この先は従来の追加方式で続けます",
            ["log.tachie.noHost"] = "  立ち絵「{0}」の割り当て先YMM4キャラ「{1}」が見つからないため、パックの画像で仮置きします",
            ["log.tachie.noImage"] = "  立ち絵の画像が見つからないので、大きさの自動調整を省略しました: {0}",
            ["log.tachie.noYmm4"] = "  → 「{0}」はYMM4側に立ち絵が無いので、パックの画像で仮置きします",
            ["log.tachie.noStandin"] = "  仮置きの立ち絵画像が見つかりません: {0}",
            ["log.face.noLayers"] = "  表情「{0}」: PSDレイヤー指定が無いためスキップ",
            ["log.face.noHost"] = "  表情「{0}」: 立ち絵の割り当て先YMM4キャラが見つからないためスキップ",
            ["log.unmapped"] = "⚠ 「{0}」→「{1}」はYMM4未登録の名前です。ボイス・立ち絵は仮配置になります(話者の割り当てドロップダウンで登録キャラを選ぶと本配置)",
            ["log.switch.unknown"] = "  立ち絵切替: 「{0}」は登録されていない立ち絵名のためスキップ({1} は現在の立ち絵のまま)",
            ["log.switch.noHost"] = "  立ち絵「{0}」の割り当て先YMM4キャラ「{1}」が見つからないため、次のセリフまで表示をスキップ",
            ["log.switch"] = "  立ち絵切替: {0} → {1}",
            ["log.switch.replaceFail"] = "  立ち絵切替の置き直しに失敗(続行): {0}",
            ["log.items"] = "アイテム生成: {0}件 (fps={1})",
            ["log.skipped"] = "  スキップ: {0}",
            ["log.skipped.unknownType"] = "no.{0}: 未知の type '{1}'",
            ["log.skipped.frameOverflow"] = "no.{0}: 時刻が大きすぎてフレームに直せないため置きませんでした",
            ["log.emptySerif"] = "  中身の無い行(SE や 間 だけ)を {0}件、ボイスにしませんでした",
            ["log.skippedTotal"] = "※ {0}件を飛ばしました(素材が見つからない等)。上のスキップの行を確認してください",
            ["log.layerList"] = "── 配置レイヤー一覧(番号大=手前のつもり) ──",
            ["log.stopped"] = "停止: {0}/{1} 件まで配置しました(残りは置いていません)",
            ["log.placing"] = "配置: {0}/{1} 件",
            ["log.placing.progress"] = "配置 {0}/{1} 件まで",
            ["log.beforeRelayout"] = "再配置の前",
            ["log.diag.char"] = "診断: 先頭ボイスの Character = 「{0}」",
            ["log.diag.charNull"] = "null(この名前のキャラがYMM4に未登録)",
            ["log.diag.voice"] = "診断: キャラの声設定 = {0}",
            ["log.diag.voiceNull"] = "null(キャラ設定で声が未設定)",
            ["log.diag.fail"] = "診断失敗: {0}",
            ["log.gap"] = "セリフ間の隙間: {0}秒({1})",
            ["log.gap.pack"] = "パックの設定",
            ["log.gap.plugin"] = "プラグインで指定",
            ["log.collision.run"] = "(保険)衝突解決を実行: 重なりのみ整理",
            ["log.collision.fail"] = "衝突解決失敗: {0}",
            ["log.faceOff.nullParam"] = "  ボイス表情オフ不可(パラメータnull): {0}",
            ["log.faceOff.noEnabled"] = "  ボイス表情オフ不可(IsEnabledが無い: {0}): {1}",
            ["log.diag.faceItem"] = "診断[表情アイテム@{0}]: param=",
            ["log.jimaku"] = "テキスト窓があるため、字幕を最前面に設定: {0}件",
            ["log.faceOff"] = "ボイス側の表情変更を無効化: {0}件(表情は表情レイヤーが支配)",
            ["log.post.fail"] = "ボイス後処理失敗: {0}",
            ["log.voiceMemo"] = "── 声の設定メモ(書き庭の試聴と同じ声にする場合、YMM4のキャラ設定を以下に) ──",
            ["log.voiceMemo.speaker"] = " / 話者 {0}",
            ["log.tachie.noSize"] = "  立ち絵サイズ不明のためレイアウト省略: {0}",
            ["log.tachie.layout"] = "  立ち絵レイアウト: {0} zoom={1:F1}% x={2:F0} y={3:F0} ({4})",
            ["log.partialFail"] = "一部失敗",
            ["log.wait.settled"] = "ボイス生成待ち: {0}/{1}件が確定({2}ms)",
            ["log.wait.timeout"] = "ボイス生成待ち: タイムアウト(60秒待っても {0}/{1}件が未確定のまま続行。未確定のボイスは重なる/詰まることがあります)",
            ["log.wait.progress"] = "ボイス生成待ち {0}/{1}",
            ["log.wait.exception"] = "ボイス生成待ちで例外(続行): {0}",
            ["log.prefetch.start"] = "ボイスを先にまとめて作成: {0}件・全体の同時数 {1}(エンジン×件数(同時数): {2})",
            ["log.prefetch.progress"] = "ボイスの作成 {0}/{1}",
            ["log.prefetch.done"] = "ボイスの作成: {0}/{1}件を作成({2}ms)",
            ["log.prefetch.probeFailed"] = "※ 1本目のボイスを先に作れなかったので、残りは先に作らず、1行ずつ作ります",
            ["log.prefetch.skipEngine"] = "ボイスを先に作らない声: {0}(この声は1行ずつ作ります)",
            ["log.prefetch.retry"] = "  ボイス: 先にまとめて作れなかったので、この行だけ作り直します「{0}」",
            ["log.prefetch.mismatch"] = "  ボイス: 先に作ったものと話者・本文が食い違うので、この行は作り直します「{0}」",
            ["log.elapsed"] = "取り込みの所要時間: {0}ms",
            ["log.eta.check"] = "残り時間の目安: {0}/{1}件の時点で約{2}秒と出し、実際は{3}秒だった",
            ["log.wait.skipped"] = "ボイス生成待ち: {0}件とも作る時点で確定済みなので待たずに進みます",
            ["progress.stage.Prepare"] = "準備中",
            ["progress.stage.Voices"] = "ボイスの作成",
            ["progress.stage.Place"] = "配置",
            ["progress.stage.Wait"] = "ボイスの長さの確定待ち",
            ["progress.stage.Relayout"] = "並べ直し",
            ["progress.estimating"] = " ・残り時間を計算中",
            ["progress.remaining.soon"] = " ・まもなく終わります",
            ["progress.remaining.sec"] = " ・残り約{1}秒",
            ["progress.remaining.minsec"] = " ・残り約{0}分{1}秒",
            ["progress.remaining.min"] = " ・残り約{0}分",
            ["progress.done"] = "完了(所要 {0}秒)",
            ["progress.stopped"] = "停止しました",
            ["log.stage"] = "  段階 {0}: {1}ms",
            ["log.stage.perVoice"] = "  音声の生成の合計: {0}ms / {1}行(1行あたり {2}ms)",
            ["log.stage.load"] = "パックの読み込み: {0}ms",
            ["stage.prep"] = "準備(タイムライン・レイヤーの計画)",
            ["stage.prefetch"] = "音声の生成(先にまとめて)",
            ["stage.loop"] = "ほかのアイテムの生成(立ち絵・表情・背景・BGM・SE など)",
            ["stage.loopVoice"] = "音声の生成(1行ずつ・作り直し)",
            ["stage.misc"] = "その他(レイヤーの一覧)",
            ["stage.addItems"] = "タイムラインへの追加",
            ["stage.wait"] = "ボイスの尺待ち",
            ["stage.refresh"] = "尺の取り直し",
            ["stage.resequence"] = "並べ直し",
            ["stage.post"] = "後処理(字幕・表情・診断)",
            ["log.placedVoice"] = "  ボイスの位置: {0}f +{1}f レイヤー{2}「{3}」",
            ["log.reset.noInfo"] = "尺の取り直し: VideoInfo が見つからずスキップ(YMM4 の版が違う可能性)",
            ["log.reset.fail"] = "  尺の取り直し失敗: {0}",
            ["log.reset.done"] = "尺の取り直し: {0}件中 {1}件の長さが変わりました{2}",
            ["log.reset.failed"] = "(失敗 {0}件)",
            ["log.reset.exception"] = "尺の取り直しで例外(続行): {0}",
            ["log.relayout.noItems"] = "再配置: Items が取得できずスキップ",
            ["log.relayout.unsettled"] = "再配置: ボイス実尺が未確定(音声合成待ち?)のため据え置き",
            ["log.relayout.done"] = "実尺で再配置: ボイス {0}件(移動 {1})・他 {2}件移動/{3}件伸縮/{4}件終端打切/{5}件レーン内打切/{6}件入替打切・表情実尺 {7}件・立ち絵切替終端 {8}件・レーン集約 {9}件・gap {10}f",
            ["log.relayout.fail"] = "再配置失敗(据え置き): {0}",
            ["log.i.noCharList"] = "Characters 一覧が取得できない",
            ["log.i.charListFail"] = "キャラ一覧取得失敗: {0}",
            ["log.i.noChars"] = "書き出すキャラがいません(プロジェクトを開いていますか?)",
            ["log.i.charsExported"] = "キャラ設定を書き出し: {0}人 → {1}",
            ["log.i.versionNote"] = "注意: このプラグインの動作確認は YMM4 v{0}(実行中は v{1})。挙動がおかしいときはプラグインの新しい版が無いか確認してください",
            ["log.i.typeNotFound"] = "  {0} 型が見つからない",
            ["log.i.notFound"] = "  {0} が見つからない",
            ["log.i.presetLoadFail"] = "  プリセット読込失敗: {0}",
            ["log.i.mainModelField"] = "MainModel 取得: フィールド {0}.{1}",
            ["log.i.mainModelProp"] = "MainModel 取得: プロパティ {0}",
            ["log.i.noMainModel"] = "MainModel が見つからない → ボイスはファクトリ経路で配置",
            ["log.i.noAddVoice"] = "  ボイス: MainModel.AddVoiceItemAsync(5引数) が見つからない。YMM4の更新で中身が変わった可能性があります。プラグインの新しい版が出ていないか確認してください",
            ["log.i.present"] = "あり",
            ["log.i.voiceUi"] = "  ボイス(UI経路で追加)「{0}」 VoiceLength={1} VoiceCache={2}",
            ["log.i.voiceUiNoRet"] = "  ボイス(UI経路で追加)「{0}」(戻り値からは取れず。追加前後の差分で特定します)",
            ["log.i.voiceUiFail"] = "  ボイス: UI経路での追加失敗「{0}」: {1}",
            ["log.i.noFactory"] = "  ボイス: 正規ファクトリが見つからない(YMM4更新で変わった?)",
            ["log.i.factoryNull"] = "  ボイス: 正規ファクトリが null を返した",
            ["log.i.voiceFactory"] = "  ボイス(正規生成)「{0}」 VoiceLength={1} VoiceCache={2} Length={3}",
            ["log.i.factoryFail"] = "  ボイス: 正規ファクトリでの生成失敗: {0}",
            ["log.i.noVoiceCtor"] = "  ボイス: VoiceItem(キャラ) を作れない",
            ["log.i.voiceOfficial"] = "  ボイス(公式方式)「{0}」 VoiceLength={1} Length={2}{3}",
            ["log.i.cloneSuffix"] = " (複製)",
            ["log.i.officialFail"] = "  ボイス: 公式方式での作成失敗「{0}」: {1}",
            ["log.i.noDataContext"] = "NG: MainWindow.DataContext が null",
            ["log.i.noTimelineVm"] = "NG: ActiveTimelineViewModel が null(プロジェクトが開かれていない?)",
            ["log.i.timelineField"] = "Timeline 取得: フィールド {0}.{1}",
            ["log.i.timelineProp"] = "Timeline 取得: プロパティ {0}.{1}",
            ["log.i.noTimeline"] = "NG: TimelineViewModel から Timeline モデルを見つけられなかった",
            ["log.i.getFail"] = "{0}取得失敗({1})→ {2}",
            ["log.i.charDefaults"] = "  キャラ既定値コピー: {0}項目 ({1})",
            ["log.i.cloneFail"] = "  クローン失敗({0}): {1}",
            ["log.i.tachieNoDefault"] = "  立ち絵: キャラの TachieDefaultItemParameter が null(立ち絵未設定?)",
            ["log.i.tachieNoWrite"] = "  立ち絵: TachieItemParameter が書けない",
            ["log.i.tachieTypeMismatch"] = "  立ち絵: 型不一致 {0} → {1}",
            ["log.i.tachieParamOk"] = "  立ち絵: パラメータ設定OK({0})",
            ["log.i.tachieParamFail"] = "  立ち絵パラメータ設定失敗: {0}",
            ["log.i.psdSwapNoParam"] = "  立ち絵PSD差し替え: パラメータが取れない",
            ["log.i.psdSwapNoWrite"] = "  立ち絵PSD差し替え: FilePath が書けない({0})",
            ["log.i.psdSwap"] = "  立ち絵PSD差し替え: {0}",
            ["log.i.psdSwapFail"] = "  立ち絵PSD差し替え失敗: {0}",
            ["log.i.idNotFound"] = "  ID解決: {0} が見つからない",
            ["log.i.idFail"] = "  ID解決失敗: {0}",
            ["log.i.faceNoPsdPath"] = "  表情: PSDパス不明のためID解決できない",
            ["log.i.faceNoSeparator"] = "  表情: 区切り文字を特定できず(末尾がバラバラ)",
            ["log.i.faceSeparator"] = "  表情: レイヤーパス区切り文字を検出 = U+{0} (表示: '{1}')",
            ["log.i.faceNoDefault"] = "  表情: キャラの TachieDefaultFaceParameter が null(立ち絵未設定?)",
            ["log.i.faceNoClone"] = "  表情: クローン不可のため既定表情のまま",
            ["log.i.faceState"] = "  表情: 全身状態 {0}枚で設定(入替指定 {1}点)",
            ["log.i.faceNoWrite"] = "  表情: TachieFaceParameter が書けない",
            ["log.i.faceTypeMismatch"] = "  表情: 型不一致 {0} → {1}",
            ["log.i.faceParamFail"] = "  表情パラメータ設定失敗: {0}",
            ["log.i.faceNoLayerIds"] = "  表情: レイヤーIDが解決できず設定不可(書き庭のパック書き出しなら layerIds が自動で付きます)",
            ["log.i.propNotFound"] = "  {0}: プロパティが見つからない",
            ["log.i.animNotFinite"] = "  {0}: 数として扱えない値(無限大など)になったので設定しませんでした",
            ["log.i.animUnknown"] = "  {0}: {1} の設定方法が分からない(From/To も Values も無し)",
            ["log.i.setFail"] = "  {0} 設定失敗: {1}",
            ["log.i.itemPropMissing"] = "  [{0}] {1} が無い/書けない",
            ["log.i.itemTypeMismatch"] = "  [{0}] {1} 型不一致: {2} → {3}",
            ["log.i.itemSetFail"] = "  [{0}] {1} 設定失敗: {2}",
            ["log.i.itemPropMissing2"] = "  [{0}] プロパティ {1} が無い/書けない",
            ["log.i.tmplListFail"] = "  テンプレ一覧の取得に失敗: {0}",
            ["log.i.tmplMissing"] = "  テンプレ「{0}」はYMM4に未登録。既定で配置します",
            ["log.i.tmplNoType"] = "  テンプレ「{0}」に {1} が無いため既定で配置します",
            ["log.i.tmplApplied"] = "  テンプレ「{0}」を適用({1})",
            ["log.i.tmplCloneFail"] = "  テンプレ「{0}」複製失敗: {1}",
            ["log.i.noItems"] = "追加するアイテムがありません",
            ["log.i.candidate"] = "候補メソッド: {0}({1})",
            ["log.i.done"] = "完了",
            ["log.i.run"] = "実行: {0} → {1}",
            ["log.i.placed"] = "配置完了: {0}アイテム",
            ["log.i.methodFail"] = "{0} 失敗: {1}",
            ["log.i.addItemsNg"] = "NG: タイムラインへの一括配置(AddItems)が呼べませんでした。YMM4の更新で中身が変わった可能性があります。プラグインの新しい版が出ていないか確認してください(動作確認済み: YMM4 v4.54)",
            ["log.pack.noJson"] = "timeline.json がありません: {0}",
            ["log.pack.tooBig"] = "timeline.json が大きすぎるため読み込みません({0} バイト・上限 {1} バイト)",
            ["log.pack.tooMany"] = "パックの{0}が多すぎるため読み込みません({1} 件・上限 {2} 件)",
            ["log.pack.limit.events"] = "イベント(行)",
            ["log.pack.limit.characters"] = "キャラ",
            ["log.pack.limit.speakers"] = "話者",
            ["log.pack.limit.list"] = "1つの行・キャラの中の項目(立ち位置・効果音・立ち絵・表情)",
            ["log.pack.limit.layers"] = "1つの表情の PSD レイヤー",
            ["log.pack.limit.serif"] = "1行のセリフ(本文・読み)の文字数",
            ["log.pack.readingIsPath"] = "no.{0}: 読みがファイルの場所の形なので使いません(YMM4 がその場所の音声を読み込んでしまうため)。本文から読みを作ります",
            ["log.pack.textIsPath"] = "no.{0}: セリフがファイルの場所の形なので、この行は声を作りません(YMM4 がその場所の音声を読み込んでしまうため)",
            ["log.costly"] = "料金や回数の制限がかかりうる声で作る行: {0}",
            ["log.costly.cancelled"] = "取り込みをやめました(料金のかかる声の確認で「いいえ」)。タイムラインには何も置いていません",
            ["confirm.costly"] = "この取り込みでは、次の声で音声を作ります。オンラインのサービスは料金や回数の制限がかかることがあり、外部コマンドは登録したプログラムを行の数だけ動かします。\n\n{0}\n\n続けますか?",
            ["confirm.costly.line"] = "・{0}: {1}行・{2}文字",
            ["log.pack.limit.name"] = "表情の id・名前・レイヤー名の文字数",
            ["log.pack.parseFail"] = "timeline.json の解析に失敗",
            ["log.pack.newer"] = "このパックは新しい形式(v{0})です。このプラグインが分かるのは v{1} までです。プラグインを新しいものに入れ替えてください: https://kakiniwa.jp/plugin/",
            ["log.pack.noVersion"] = "注意: パックに形式版がありません。古い書き庭で書き出したものかもしれません。",
            ["log.pack.loaded"] = "読み込みOK: 「{0}」『{1}』 events={2} chars={3}",
            ["log.pack.versions"] = "バージョン: 書き庭 v{0} のパック(形式v{1}) / プラグイン v{2}(対応形式 v{3})",
            ["log.pack.needPlugin"] = "⚠ このパックはプラグイン v{0} 以上を想定しています(現在 v{1})。正しく読めない項目があるかもしれません。新しい版に入れ替えてください: https://kakiniwa.jp/plugin/",
            ["log.pack.newPlugin"] = "新しいプラグイン v{0} が配布されています(現在 v{1})。入れ替えはこちら: https://kakiniwa.jp/plugin/",
            ["log.settings.loadFail"] = "設定読み込み失敗: {0}",
            ["log.settings.saved"] = "設定を保存: {0}",
            ["log.settings.saveFail"] = "設定保存失敗: {0}",
            ["ui.language.auto"] = "自動(YMM4に合わせる)",
            ["ui.run"] = "実行",
            ["ui.cancel"] = "キャンセル",
            ["ui.beta"] = "β版です。取り込むと、いま開いているプロジェクトのタイムラインに項目が追加されます。元に戻す操作は確認できていないので、先にプロジェクトを保存しておいてください。",
            ["ui.selectPack"] = "1. 受け渡しパックのフォルダを選ぶ",
            ["ui.speakers"] = "2. 話者の割り当て(パックのキャラ → YMM4のキャラクター)",
            ["ui.baseLayer"] = "配置先: 開始レイヤー ",
            ["ui.startAt"] = "  開始位置 ",
            ["ui.startAt.zero"] = "先頭(0秒)",
            ["ui.startAt.cursor"] = "カーソル位置から",
            ["ui.voiceLayer"] = "セリフのレイヤー ",
            ["ui.voice.perCharacter"] = "キャラごとに分ける",
            ["ui.voice.single"] = "全員で1つにまとめる",
            ["ui.voice.custom"] = "キャラごとに指定する",
            ["ui.gap"] = "セリフの間の隙間 ",
            ["ui.gap.pack"] = "パックの設定に従う",
            ["ui.gap.none"] = "なし(詰める)",
            ["ui.gap.custom"] = "秒数を指定する",
            ["ui.seconds"] = " 秒",
            ["ui.auto"] = "  (空=自動)",
            ["ui.kind.bg"] = "背景",
            ["ui.kind.bgm"] = "BGM",
            ["ui.kind.se"] = "効果音",
            ["ui.kind.stage"] = "ト書き",
            ["ui.kind.propSlot"] = "小物(最初の1枠)",
            ["ui.kind.prop"] = "小物",
            ["ui.kind.effect"] = "エフェクト",
            ["ui.kind.window"] = "窓",
            ["ui.kindsHeader"] = "素材の種類ごとのレイヤーを指定する(開始レイヤーを0とした番号)",
            ["ui.assetsHeader"] = "素材1つずつのレイヤーを指定する(パックを選ぶと一覧が出ます・種類の指定より優先)",
            ["ui.noAssets"] = "このパックには素材がありません",
            ["ui.place"] = "3. タイムラインへ配置",
            ["ui.pause"] = "一時停止",
            ["ui.pauseTip"] = "配置を一時停止します(もう一度押すと再開)",
            ["ui.resume"] = "再開",
            ["ui.stop"] = "停止",
            ["ui.stopTip"] = "配置をやめます。ここまで置いたアイテムはタイムラインに残ります",
            ["ui.exportChars"] = "YMM4のキャラ設定を書き出す(書き庭連携用)",
            ["ui.registerPresets"] = "書き庭の表情をYMM4のプリセットへ登録",
            ["ui.pickExportChars"] = "書き出すキャラを選ぶ",
            ["ui.saveCharsTitle"] = "書き庭連携用のキャラ設定JSONの保存先",
            ["ui.saveCharsFile"] = "ymm4キャラ設定.json",
            ["ui.pickPackTitle"] = "書き庭の受け渡しパック(timeline.json のあるフォルダ)を選択",
            ["ui.pickPresetChars"] = "プリセットを反映するキャラを選ぶ",
            ["ui.confirmPresetTitle"] = "書き換える PSD の確認",
            ["ui.confirmPresetMessage"] = "次の PSD の表情プリセット(PSD の隣の -ymm.json)を書き換えます。書き換えてよいものに印を付けてください。元のファイルは .bak に控えます。",
            ["ui.presetOutsidePack"] = "パックの外",
            ["ui.sameName"] = "(名前そのまま: {0})",
            ["ui.voiceBoxTip"] = "セリフを置くレイヤー番号(開始レイヤーを0とした番号)",
            ["ui.unassigned"] = " ⚠ 未割り当て(ボイスが付きません)",
            ["ui.portraitRow"] = "└ 立ち絵「{0}」 → ",
            ["ui.portraitSame"] = "(上と同じキャラでPSD差し替え)",
            ["log.noChars"] = "YMM4のキャラが取得できません(プロジェクトを開いていますか?)",
            ["log.chars"] = "YMM4登録キャラ: {0}",
            ["log.charsNone"] = "(取得できず)",
            ["log.layersApplied"] = "書き庭側のレイヤー指定を窓に反映しました(窓で変えられます)",
            ["log.noPresetChars"] = "PSDレイヤー付きの表情プリセットを持つキャラがパックにいません",
            ["log.resumed"] = "再開しました",
            ["log.paused"] = "一時停止しました(次の区切りで止まります。「再開」で続き、「停止」でやめます)",
            ["log.stopping"] = "停止します(次の区切りで止まります)…",
            ["log.exception"] = "例外: {0}",
            ["msg.title"] = "書き庭の台本を取り込む",
            ["msg.stopped"] = "取り込みを停止しました。\n\nタイムラインには途中まで配置されたアイテムが残っています。残す必要が無ければ Ctrl+Z で戻すか、プロジェクトを保存せずに開き直してください。",
            ["msg.failed"] = "取り込みの途中で失敗しました。\n\nタイムラインには途中まで配置されたアイテムが残っています。Ctrl+Z で戻すか、プロジェクトを保存せずに開き直してください。\n\n詳しい内容はダイアログ下部のログに出ています。\n\n{0}",
        };

        public static readonly Dictionary<string, string> En = new Dictionary<string, string>
        {
            ["menu.name"] = "Import Kakiniwa script (beta v{0})",
            ["window.title"] = "Kakiniwa Script Import v{0}",
            ["ui.header"] = "Kakiniwa Script Import v{0}  /  YMM4 v{1}",
            ["ui.language"] = "言語 / Language ",
            ["log.psd.noPresetType"] = "PsdPreset type not found (PSD plugin not loaded?)",
            ["log.psd.noSpeaker"] = "  {0}: skipped (no speaker assigned)",
            ["log.psd.noTachie"] = "  {0} (→{1}): skipped the default expression (no PSD portrait set)",
            ["log.psd.noHostPsd"] = "  {0}/{1}: skipped (could not find the PSD of the assigned YMM4 character)",
            ["log.psd.notBundled"] = "  {0}/{1}: presets are not written to a PSD that is not bundled in the pack: {2}",
            ["log.psd.noPsd"] = "  {0}/{1}: skipped (portrait PSD not found)",
            ["log.psd.done"] = "Expression presets registered: {0} in total (reopen the YMM4 PSD asset manager to see them)",
            ["log.psd.none"] = "There were no presets to register",
            ["log.psd.noState"] = "  Preset 「{0}」: skipped (could not build the full-body state)",
            ["log.psd.noPresetsProp"] = "  {0}: cannot overwrite the Presets property",
            ["log.psd.noSave"] = "  {0}: SaveFromPsdFilePath not found",
            ["log.psd.saved"] = "  {0}: registered {1} preset(s) → {2}-ymm.json",
            ["log.psd.saveFail"] = "  {0}: save failed: {1}",
            ["log.psd.needRow"] = "  {0}/{1}: the pack alone (→{2}) is not enough to write to a PSD outside the pack. Choose the target character in the portrait row of the import window",
            ["log.psd.keptForeign"] = "  {0}: kept the existing preset 「{1}」 because Kakiniwa did not create it (delete or rename it in YMM4 to replace it)",
            ["log.psd.backupFail"] = "  {0}: did not write because the backup (.bak) could not be made: {1}",
            ["log.psd.linked"] = "  {0}: not written because the preset file is a link (junction or symbolic link)",
            ["log.psd.cancelled"] = "Expression preset registration was cancelled",
            ["log.pre.excluded"] = "{0} existing item(s) will be excluded from the re-layout",
            ["log.pre.fail"] = "Failed to record existing items (continuing): {0}",
            ["log.dest"] = "Destination: from layer {0} / {1}",
            ["log.dest.frame"] = "from frame {0}",
            ["log.dest.start"] = "from the start",
            ["log.layers"] = "Layer settings: lines={0}{1}",
            ["log.layers.fixed"] = " / fixed={0}",
            ["log.se.noLength"] = "  Cannot measure the SE length (format is not wav/mp3/ogg/opus), so it is placed with a length of 1 second: {0}",
            ["log.exit"] = "  Exit: {0} (portrait ends at {1}s)",
            ["log.exit.skip"] = "  Exit: {0} (skipped: no portrait placed)",
            ["log.default"] = "default",
            ["log.layoutUpdate"] = "  Layout update: {0} x={1}% height={2}",
            ["log.serifTemplate"] = "  * Applying item templates to lines is not supported yet (planned after testing on a real setup). Voices are placed with default settings",
            ["log.voice.legacyNext"] = "* Could not create the voice the official way, so the rest continues with the legacy method",
            ["log.tachie.noHost"] = "  Portrait 「{0}」: the assigned YMM4 character 「{1}」 was not found, so the pack image is placed as a stand-in",
            ["log.tachie.noImage"] = "  Portrait image not found, so automatic size adjustment was skipped: {0}",
            ["log.tachie.noYmm4"] = "  → 「{0}」 has no portrait on the YMM4 side, so the pack image is placed as a stand-in",
            ["log.tachie.noStandin"] = "  Stand-in portrait image not found: {0}",
            ["log.face.noLayers"] = "  Expression 「{0}」: skipped (no PSD layer specified)",
            ["log.face.noHost"] = "  Expression 「{0}」: skipped (the YMM4 character assigned to this portrait was not found)",
            ["log.unmapped"] = "⚠ 「{0}」→「{1}」 is not a registered YMM4 name. Voice and portrait are placed as stand-ins (choose a registered character in the speaker dropdown for proper placement)",
            ["log.switch.unknown"] = "  Portrait switch: skipped (「{0}」 is not a registered portrait name; {1} keeps the current portrait)",
            ["log.switch.noHost"] = "  Portrait 「{0}」: the assigned YMM4 character 「{1}」 was not found, so display is skipped until the next line",
            ["log.switch"] = "  Portrait switch: {0} → {1}",
            ["log.switch.replaceFail"] = "  Failed to re-place the portrait switch (continuing): {0}",
            ["log.items"] = "Items generated: {0} (fps={1})",
            ["log.skipped"] = "  Skipped: {0}",
            ["log.skipped.unknownType"] = "no.{0}: unknown type '{1}'",
            ["log.skipped.frameOverflow"] = "no.{0}: not placed because the time is too large to convert to frames",
            ["log.emptySerif"] = "  {0} line(s) with no content (only SE or a pause) were not turned into voices",
            ["log.skippedTotal"] = "* {0} item(s) were skipped (e.g. assets not found). Check the Skipped lines above",
            ["log.layerList"] = "── Layer list (higher number = in front) ──",
            ["log.stopped"] = "Stopped: placed up to {0}/{1} item(s) (the rest were not placed)",
            ["log.placing"] = "Placing: {0}/{1} item(s)",
            ["log.placing.progress"] = "Placed up to {0}/{1} item(s)",
            ["log.beforeRelayout"] = "Before the re-layout",
            ["log.diag.char"] = "Diagnostics: Character of the first voice = 「{0}」",
            ["log.diag.charNull"] = "null (no character with this name is registered in YMM4)",
            ["log.diag.voice"] = "Diagnostics: character voice setting = {0}",
            ["log.diag.voiceNull"] = "null (no voice set in the character settings)",
            ["log.diag.fail"] = "Diagnostics failed: {0}",
            ["log.gap"] = "Gap between lines: {0}s ({1})",
            ["log.gap.pack"] = "from the pack",
            ["log.gap.plugin"] = "set in the plugin",
            ["log.collision.run"] = "(fallback) Ran collision resolution: only overlaps are tidied",
            ["log.collision.fail"] = "Collision resolution failed: {0}",
            ["log.faceOff.nullParam"] = "  Cannot turn off the voice expression (parameter is null): {0}",
            ["log.faceOff.noEnabled"] = "  Cannot turn off the voice expression (no IsEnabled: {0}): {1}",
            ["log.diag.faceItem"] = "Diagnostics [expression item @{0}]: param=",
            ["log.jimaku"] = "A text window is present, so subtitles were moved to the front: {0} item(s)",
            ["log.faceOff"] = "Disabled expression changes on the voice side: {0} item(s) (expressions are controlled by the expression layer)",
            ["log.post.fail"] = "Voice post-processing failed: {0}",
            ["log.voiceMemo"] = "── Voice settings memo (to match the preview voice in Kakiniwa, set the YMM4 character like this) ──",
            ["log.voiceMemo.speaker"] = " / speaker {0}",
            ["log.tachie.noSize"] = "  Portrait size unknown, so the layout is skipped: {0}",
            ["log.tachie.layout"] = "  Portrait layout: {0} zoom={1:F1}% x={2:F0} y={3:F0} ({4})",
            ["log.partialFail"] = "partly failed",
            ["log.wait.settled"] = "Waiting for voices: {0}/{1} settled ({2} ms)",
            ["log.wait.timeout"] = "Waiting for voices: timed out (still {0}/{1} unsettled after 60 s; continuing. Unsettled voices may overlap or bunch up)",
            ["log.wait.progress"] = "Waiting for voices {0}/{1}",
            ["log.wait.exception"] = "Exception while waiting for voices (continuing): {0}",
            ["log.prefetch.start"] = "Creating voices up front: {0} lines, overall concurrency {1} (engine×lines (concurrency): {2})",
            ["log.prefetch.progress"] = "Creating voices {0}/{1}",
            ["log.prefetch.done"] = "Voice creation: {0}/{1} created ({2} ms)",
            ["log.prefetch.probeFailed"] = "* The first voice could not be created up front, so the rest are created one line at a time",
            ["log.prefetch.skipEngine"] = "Voices not created up front: {0} (these are created one line at a time)",
            ["log.prefetch.retry"] = "  Voice: could not be created up front, recreating just this line 「{0}」",
            ["log.prefetch.mismatch"] = "  Voice: speaker or text differs from the one created up front, recreating this line 「{0}」",
            ["log.elapsed"] = "Import took {0} ms",
            ["log.eta.check"] = "Time-left estimate: at {0}/{1} it showed about {2} s; it actually took {3} s",
            ["log.wait.skipped"] = "Waiting for voices: all {0} were settled when created, so no wait is needed",
            ["progress.stage.Prepare"] = "Preparing",
            ["progress.stage.Voices"] = "Creating voices",
            ["progress.stage.Place"] = "Placing",
            ["progress.stage.Wait"] = "Waiting for voice lengths",
            ["progress.stage.Relayout"] = "Re-laying out",
            ["progress.estimating"] = " · estimating time left",
            ["progress.remaining.soon"] = " · almost done",
            ["progress.remaining.sec"] = " · about {1} s left",
            ["progress.remaining.minsec"] = " · about {0} min {1} s left",
            ["progress.remaining.min"] = " · about {0} min left",
            ["progress.done"] = "Done ({0} s)",
            ["progress.stopped"] = "Stopped",
            ["log.stage"] = "  Stage {0}: {1} ms",
            ["log.stage.perVoice"] = "  Voice creation total: {0} ms / {1} lines ({2} ms per line)",
            ["log.stage.load"] = "Pack loading: {0} ms",
            ["stage.prep"] = "preparation (timeline, layer plan)",
            ["stage.prefetch"] = "voice creation (up front)",
            ["stage.loop"] = "other items (portraits, expressions, backgrounds, BGM, SE, etc.)",
            ["stage.loopVoice"] = "voice creation (one by one, retries)",
            ["stage.misc"] = "other (layer list)",
            ["stage.addItems"] = "adding to the timeline",
            ["stage.wait"] = "waiting for voice lengths",
            ["stage.refresh"] = "re-reading lengths",
            ["stage.resequence"] = "re-layout",
            ["stage.post"] = "post-processing (subtitles, expressions, diagnostics)",
            ["log.placedVoice"] = "  Voice position: {0}f +{1}f layer {2} 「{3}」",
            ["log.reset.noInfo"] = "Re-measuring lengths: VideoInfo not found, skipped (the YMM4 version may differ)",
            ["log.reset.fail"] = "  Re-measuring a length failed: {0}",
            ["log.reset.done"] = "Re-measuring lengths: {1} of {0} had their length changed{2}",
            ["log.reset.failed"] = " ({0} failed)",
            ["log.reset.exception"] = "Exception while re-measuring lengths (continuing): {0}",
            ["log.relayout.noItems"] = "Re-layout: Items could not be obtained, skipped",
            ["log.relayout.unsettled"] = "Re-layout: voice lengths are not settled yet (waiting for speech synthesis?), left as is",
            ["log.relayout.done"] = "Re-layout with real lengths: voices {0} ({1} moved); others {2} moved / {3} stretched / {4} end-clipped / {5} lane-clipped / {6} swap-clipped; expression lengths adjusted {7}; portrait-switch ends {8}; lane merges {9}; gap {10}f",
            ["log.relayout.fail"] = "Re-layout failed (left as is): {0}",
            ["log.i.noCharList"] = "Could not obtain the Characters list",
            ["log.i.charListFail"] = "Failed to get the character list: {0}",
            ["log.i.noChars"] = "No characters to export (is a project open?)",
            ["log.i.charsExported"] = "Exported character settings: {0} → {1}",
            ["log.i.versionNote"] = "Note: this plugin was tested with YMM4 v{0} (running v{1}). If something misbehaves, check whether a newer plugin version is available",
            ["log.i.typeNotFound"] = "  {0} type not found",
            ["log.i.notFound"] = "  {0} not found",
            ["log.i.presetLoadFail"] = "  Failed to load presets: {0}",
            ["log.i.mainModelField"] = "MainModel obtained: field {0}.{1}",
            ["log.i.mainModelProp"] = "MainModel obtained: property {0}",
            ["log.i.noMainModel"] = "MainModel not found → voices are placed via the factory",
            ["log.i.noAddVoice"] = "  Voice: MainModel.AddVoiceItemAsync (5 args) not found. A YMM4 update may have changed it. Check whether a newer plugin version is available",
            ["log.i.present"] = "present",
            ["log.i.voiceUi"] = "  Voice (added via the UI path) 「{0}」 VoiceLength={1} VoiceCache={2}",
            ["log.i.voiceUiNoRet"] = "  Voice (added via the UI path) 「{0}」 (not in the return value; identifying it by comparing before and after)",
            ["log.i.voiceUiFail"] = "  Voice: failed to add via the UI path 「{0}」: {1}",
            ["log.i.noFactory"] = "  Voice: the official factory was not found (changed by a YMM4 update?)",
            ["log.i.factoryNull"] = "  Voice: the official factory returned null",
            ["log.i.voiceFactory"] = "  Voice (official factory) 「{0}」 VoiceLength={1} VoiceCache={2} Length={3}",
            ["log.i.factoryFail"] = "  Voice: creation via the factory failed: {0}",
            ["log.i.noVoiceCtor"] = "  Voice: cannot create VoiceItem(character)",
            ["log.i.voiceOfficial"] = "  Voice (official method) 「{0}」 VoiceLength={1} Length={2}{3}",
            ["log.i.cloneSuffix"] = " (clone)",
            ["log.i.officialFail"] = "  Voice: creation via the official method failed 「{0}」: {1}",
            ["log.i.noDataContext"] = "NG: MainWindow.DataContext is null",
            ["log.i.noTimelineVm"] = "NG: ActiveTimelineViewModel is null (is a project open?)",
            ["log.i.timelineField"] = "Timeline obtained: field {0}.{1}",
            ["log.i.timelineProp"] = "Timeline obtained: property {0}.{1}",
            ["log.i.noTimeline"] = "NG: could not find the Timeline model from TimelineViewModel",
            ["log.i.getFail"] = "Failed to get {0} ({1}) → {2}",
            ["log.i.charDefaults"] = "  Copied character defaults: {0} item(s) ({1})",
            ["log.i.cloneFail"] = "  Clone failed ({0}): {1}",
            ["log.i.tachieNoDefault"] = "  Portrait: the character's TachieDefaultItemParameter is null (no portrait set?)",
            ["log.i.tachieNoWrite"] = "  Portrait: TachieItemParameter is not writable",
            ["log.i.tachieTypeMismatch"] = "  Portrait: type mismatch {0} → {1}",
            ["log.i.tachieParamOk"] = "  Portrait: parameters set OK ({0})",
            ["log.i.tachieParamFail"] = "  Failed to set the portrait parameters: {0}",
            ["log.i.psdSwapNoParam"] = "  Portrait PSD swap: cannot get the parameter",
            ["log.i.psdSwapNoWrite"] = "  Portrait PSD swap: FilePath is not writable ({0})",
            ["log.i.psdSwap"] = "  Portrait PSD swap: {0}",
            ["log.i.psdSwapFail"] = "  Portrait PSD swap failed: {0}",
            ["log.i.idNotFound"] = "  ID resolution: {0} not found",
            ["log.i.idFail"] = "  ID resolution failed: {0}",
            ["log.i.faceNoPsdPath"] = "  Expression: cannot resolve IDs because the PSD path is unknown",
            ["log.i.faceNoSeparator"] = "  Expression: could not determine the separator (the endings vary)",
            ["log.i.faceSeparator"] = "  Expression: detected the layer path separator = U+{0} (shown: '{1}')",
            ["log.i.faceNoDefault"] = "  Expression: the character's TachieDefaultFaceParameter is null (no portrait set?)",
            ["log.i.faceNoClone"] = "  Expression: cannot clone, so the default expression is kept",
            ["log.i.faceState"] = "  Expression: set with a full-body state of {0} layer(s) ({1} swap(s))",
            ["log.i.faceNoWrite"] = "  Expression: TachieFaceParameter is not writable",
            ["log.i.faceTypeMismatch"] = "  Expression: type mismatch {0} → {1}",
            ["log.i.faceParamFail"] = "  Failed to set the expression parameters: {0}",
            ["log.i.faceNoLayerIds"] = "  Expression: cannot set because the layer IDs could not be resolved (packs exported from Kakiniwa include layerIds automatically)",
            ["log.i.propNotFound"] = "  {0}: property not found",
            ["log.i.animNotFinite"] = "  {0}: not set because the value is not a finite number (such as infinity)",
            ["log.i.animUnknown"] = "  {0}: cannot determine how to set {1} (no From/To or Values)",
            ["log.i.setFail"] = "  {0}: failed to set value: {1}",
            ["log.i.itemPropMissing"] = "  [{0}] {1} does not exist or is not writable",
            ["log.i.itemTypeMismatch"] = "  [{0}] {1} type mismatch: {2} → {3}",
            ["log.i.itemSetFail"] = "  [{0}] failed to set {1}: {2}",
            ["log.i.itemPropMissing2"] = "  [{0}] property {1} does not exist or is not writable",
            ["log.i.tmplListFail"] = "  Failed to get the template list: {0}",
            ["log.i.tmplMissing"] = "  Template 「{0}」 is not registered in YMM4. Placing with defaults",
            ["log.i.tmplNoType"] = "  Template 「{0}」 has no {1}, so it is placed with defaults",
            ["log.i.tmplApplied"] = "  Applied template 「{0}」 ({1})",
            ["log.i.tmplCloneFail"] = "  Failed to duplicate template 「{0}」: {1}",
            ["log.i.noItems"] = "There are no items to add",
            ["log.i.candidate"] = "Candidate method: {0}({1})",
            ["log.i.done"] = "done",
            ["log.i.run"] = "Run: {0} → {1}",
            ["log.i.placed"] = "Placement complete: {0} item(s)",
            ["log.i.methodFail"] = "{0} failed: {1}",
            ["log.i.addItemsNg"] = "NG: could not call the bulk placement on the timeline (AddItems). A YMM4 update may have changed it. Check whether a newer plugin version is available (verified with YMM4 v4.54)",
            ["log.pack.noJson"] = "timeline.json not found: {0}",
            ["log.pack.tooBig"] = "timeline.json is too large and was not loaded ({0} bytes; limit {1} bytes)",
            ["log.pack.tooMany"] = "The pack has too many {0} and was not loaded ({1}; limit {2})",
            ["log.pack.limit.events"] = "events (lines)",
            ["log.pack.limit.characters"] = "characters",
            ["log.pack.limit.speakers"] = "speakers",
            ["log.pack.limit.list"] = "items in one line or character (positions, sound effects, portraits, expressions)",
            ["log.pack.limit.layers"] = "PSD layers in one expression",
            ["log.pack.limit.serif"] = "characters in one line of dialogue (text or reading)",
            ["log.pack.readingIsPath"] = "no.{0}: the reading looks like a file location, so it is not used (YMM4 would load the audio at that location). The reading is made from the text instead",
            ["log.pack.textIsPath"] = "no.{0}: the line looks like a file location, so no voice is made for it (YMM4 would load the audio at that location)",
            ["log.costly"] = "Lines using voices that may be charged or rate-limited: {0}",
            ["log.costly.cancelled"] = "Import cancelled (answered No to the paid-voice check). Nothing was placed on the timeline",
            ["confirm.costly"] = "This import creates audio with the following voices. Online services may charge you or limit how often you can use them, and a command-line voice runs your registered program once per line.\n\n{0}\n\nContinue?",
            ["confirm.costly.line"] = "- {0}: {1} lines, {2} characters",
            ["log.pack.limit.name"] = "characters in an expression id, name or layer name",
            ["log.pack.parseFail"] = "Failed to parse timeline.json",
            ["log.pack.newer"] = "This pack uses a newer format (v{0}). This plugin understands up to v{1}. Please replace the plugin with a newer version: https://kakiniwa.jp/plugin/",
            ["log.pack.noVersion"] = "Note: the pack has no format version. It may have been exported by an old version of Kakiniwa.",
            ["log.pack.loaded"] = "Loaded: 「{0}」 『{1}』 events={2} chars={3}",
            ["log.pack.versions"] = "Versions: pack from Kakiniwa v{0} (format v{1}) / plugin v{2} (supports format v{3})",
            ["log.pack.needPlugin"] = "⚠ This pack expects plugin v{0} or later (current v{1}). Some items may not be read correctly. Please update: https://kakiniwa.jp/plugin/",
            ["log.pack.newPlugin"] = "A newer plugin v{0} is available (current v{1}). Get it here: https://kakiniwa.jp/plugin/",
            ["log.settings.loadFail"] = "Failed to load settings: {0}",
            ["log.settings.saved"] = "Settings saved: {0}",
            ["log.settings.saveFail"] = "Failed to save settings: {0}",
            ["ui.language.auto"] = "Auto (follow YMM4)",
            ["ui.run"] = "Run",
            ["ui.cancel"] = "Cancel",
            ["ui.beta"] = "This is a beta version. Importing adds items to the timeline of the project that is currently open. Undo has not been tested, so please save your project first.",
            ["ui.selectPack"] = "1. Choose the hand-off pack folder",
            ["ui.speakers"] = "2. Assign speakers (pack character → YMM4 character)",
            ["ui.baseLayer"] = "Destination: start layer ",
            ["ui.startAt"] = "  Start at ",
            ["ui.startAt.zero"] = "Beginning (0 s)",
            ["ui.startAt.cursor"] = "Cursor position",
            ["ui.voiceLayer"] = "Layer for lines ",
            ["ui.voice.perCharacter"] = "One layer per character",
            ["ui.voice.single"] = "All characters on one layer",
            ["ui.voice.custom"] = "Set a layer per character",
            ["ui.gap"] = "Gap between lines ",
            ["ui.gap.pack"] = "Follow the pack",
            ["ui.gap.none"] = "None (back to back)",
            ["ui.gap.custom"] = "Specify in seconds",
            ["ui.seconds"] = " s",
            ["ui.auto"] = "  (blank = automatic)",
            ["ui.kind.bg"] = "Background",
            ["ui.kind.bgm"] = "BGM",
            ["ui.kind.se"] = "Sound effect",
            ["ui.kind.stage"] = "Stage directions",
            ["ui.kind.propSlot"] = "Props (first slot)",
            ["ui.kind.prop"] = "Prop",
            ["ui.kind.effect"] = "Effect",
            ["ui.kind.window"] = "Text window",
            ["ui.kindsHeader"] = "Set a layer per kind of asset (numbers count from the start layer as 0)",
            ["ui.assetsHeader"] = "Set a layer per asset (the list appears once you choose a pack; overrides the per-kind setting)",
            ["ui.noAssets"] = "This pack has no assets",
            ["ui.place"] = "3. Place on the timeline",
            ["ui.pause"] = "Pause",
            ["ui.pauseTip"] = "Pause placing (press again to resume)",
            ["ui.resume"] = "Resume",
            ["ui.stop"] = "Stop",
            ["ui.stopTip"] = "Stop placing. Items placed so far stay on the timeline",
            ["ui.exportChars"] = "Export YMM4 character settings (for Kakiniwa)",
            ["ui.registerPresets"] = "Register Kakiniwa expressions as YMM4 presets",
            ["ui.pickExportChars"] = "Choose characters to export",
            ["ui.saveCharsTitle"] = "Where to save the character settings JSON for Kakiniwa",
            ["ui.saveCharsFile"] = "ymm4-characters.json",
            ["ui.pickPackTitle"] = "Choose the Kakiniwa hand-off pack (the folder with timeline.json)",
            ["ui.pickPresetChars"] = "Choose characters to apply presets to",
            ["ui.confirmPresetTitle"] = "Confirm the PSDs to change",
            ["ui.confirmPresetMessage"] = "The expression presets of the following PSDs (the -ymm.json next to each PSD) will be rewritten. Check the ones that may be changed. The original files are kept as .bak.",
            ["ui.presetOutsidePack"] = "outside the pack",
            ["ui.sameName"] = "(use the name as is: {0})",
            ["ui.voiceBoxTip"] = "Layer number for this character's lines (counted from the start layer as 0)",
            ["ui.unassigned"] = " ⚠ Not assigned (no voice will be attached)",
            ["ui.portraitRow"] = "└ Portrait 「{0}」 → ",
            ["ui.portraitSame"] = "(same character as above, swap the PSD)",
            ["log.noChars"] = "Cannot get the YMM4 characters (is a project open?)",
            ["log.chars"] = "YMM4 characters: {0}",
            ["log.charsNone"] = "(unavailable)",
            ["log.layersApplied"] = "Applied the layer settings from Kakiniwa to this window (you can change them here)",
            ["log.noPresetChars"] = "No character in the pack has expression presets with PSD layers",
            ["log.resumed"] = "Resumed",
            ["log.paused"] = "Paused (it stops at the next break; press Resume to continue or Stop to quit)",
            ["log.stopping"] = "Stopping (it stops at the next break)...",
            ["log.exception"] = "Exception: {0}",
            ["msg.title"] = "Import Kakiniwa script",
            ["msg.stopped"] = "Import stopped.\n\nItems placed so far remain on the timeline. If you do not need them, undo with Ctrl+Z or reopen the project without saving.",
            ["msg.failed"] = "The import failed part-way.\n\nItems placed so far remain on the timeline. Undo with Ctrl+Z or reopen the project without saving.\n\nSee the log at the bottom of the dialog for details.\n\n{0}",
        };
    }
}
