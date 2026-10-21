// timeline.json(受け渡しパック)のモデル型と、読み込み・検証・版比較・パック内パス解決。

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using YukkuriMovieMaker.Plugin;
using YukkuriMovieMaker.Project;
using YukkuriMovieMaker.Project.Items;
using YukkuriMovieMaker.Settings;

namespace KakiniwaYmm4Import
{
    // ---------- timeline.json DTO(スキーマ v1: /docs/pack-format.md が正) ----------

    /// <summary>このプラグインが解釈できる timeline.json の形式版(/docs/pack-format.md)。
    /// 本体側で形を変えたらこの数を上げ、プラグインも配り直すこと。</summary>
    public static class PackSchema
    {
        public const int Supported = 1;
        /// <summary>このプラグイン自身の版。書き庭側は timeline.json の pluginMin に
        /// 「このパックを正しく読むのに必要な最低版」を書く。突き合わせて古ければ警告する
        /// (本体は自動更新・プラグインは別配布なので、ずれた組み合わせは必ず起きる)。</summary>
        public const string PluginVersion = "0.6.0";
    }

    /// <summary>受け取ったパックの大きさ・値の上限(2026-10-20c 監査)。
    /// パックは第三者から受け取ることもある。上限が無いと、巨大な timeline.json を丸ごと
    /// メモリへ読み、件数の二乗で回る処理(素材の終わりを探す・話者の登場順など)で
    /// YMM4 ごと固まる。時刻が大きすぎるとフレーム数(int)があふれて負の位置・長さになる。</summary>
    public static class PackLimits
    {
        /// <summary>timeline.json の大きさ(バイト)。書き庭の実物は数百KB〜数MB</summary>
        public const long MaxJsonBytes = 32L * 1024 * 1024;
        public const int MaxEvents = 20000;
        public const int MaxCharacters = 200;
        /// <summary>セリフの話者(speaker)の種類。キャラ一覧に無い id も数える</summary>
        public const int MaxSpeakers = 200;
        /// <summary>1行の立ち位置・SE、1キャラの立ち絵・表情の数</summary>
        public const int MaxListPerItem = 1000;
        /// <summary>開始・長さ・間の上限(秒)=24時間</summary>
        public const double MaxSeconds = 24 * 60 * 60;
        /// <summary>位置・高さ(画面比 %)の上限。±1000% を超える指定は画面の外で意味が無い</summary>
        public const double MaxPercent = 1000;
        /// <summary>音声の実尺として信じる上限(秒)=6時間。壊れたヘッダの巨大値を尺に使わない</summary>
        public const double MaxAudioSeconds = 6 * 60 * 60;
        /// <summary>画像の縦横(px)の上限。0 や巨大値で拡大率が Infinity/0 にならないように</summary>
        public const double MaxImagePixels = 100000;
        /// <summary>1つの表情の PSD レイヤーの数(layers・layerIds)。表情を置くたびにこの長さのリストを何本も作るので、
        /// 上限が無いと細工したパックで YMM4 をメモリ不足で落とせた(2026-10-22 監査)。実物の PSD は数百まで</summary>
        public const int MaxLayersPerExpression = 4096;
        /// <summary>表情の id・名前(label)・レイヤー名の長さ</summary>
        public const int MaxNameLength = 256;
        /// <summary>1行のセリフ(本文・読み)の文字数。書き庭の1行はふつう数十〜数百字。
        /// 長大な1行を料金のかかる声へ送らせない・合成で固まらせない(2026-10-22 監査)</summary>
        public const int MaxSerifChars = 2000;
    }

    public class PackGenerator
    {
        public string Name { get; set; } = "";
        public string Version { get; set; } = "";
    }

    public class Pack
    {
        public int Version { get; set; }
        public PackGenerator? Generator { get; set; }
        /// <summary>このパックを正しく読むのに必要なプラグインの最低版(例 "0.2.1")。無ければ不明</summary>
        public string? PluginMin { get; set; }
        /// <summary>パックを書き出した書き庭が知る最新プラグイン版(更新案内用・任意)。
        /// 外部への通信はせず、パック経由の情報だけで新しい版の存在に気づけるようにする</summary>
        public string? PluginLatest { get; set; }
        public string Title { get; set; } = "";
        public string Episode { get; set; } = "";
        public PackTiming Timing { get; set; } = new PackTiming();
        /// <summary>YMM4 でのレイヤー配置の指定(任意)。窓の初期値になる。無ければ従来どおり</summary>
        public PackLayers? Layers { get; set; }
        public List<PackCharacter> Characters { get; set; } = new List<PackCharacter>();
        public List<PackEvent> Events { get; set; } = new List<PackEvent>();
    }

    public class PackTiming
    {
        public double CharsPerSecond { get; set; } = 6;
        public double SerifGap { get; set; } = 0.2;
        public double StageSeconds { get; set; } = 2;
    }

    public class PackCharacter
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Ymm4Name { get; set; }
        public string? PortraitDir { get; set; }
        public List<PackExpression> Expressions { get; set; } = new List<PackExpression>();
        /// <summary>複数立ち絵(別PSD切替)。1つだけの従来キャラでは null/空。</summary>
        public List<PackPortrait>? Portraits { get; set; }
        /// <summary>書き庭側の声設定(参考)。取り込み後のログで「YMM4の声をこう合わせる」案内に使う。</summary>
        public PackVoice? Voice { get; set; }
    }

    public class PackVoice
    {
        public string Engine { get; set; } = "";
        public string? Preset { get; set; }
        public Dictionary<string, double>? Params { get; set; }
    }

    /// <summary>立ち絵1つ(別PSDに切り替える単位)。type:"portrait" イベントで Id を指す。</summary>
    public class PackPortrait
    {
        public string Id { get; set; } = "";
        public bool Default { get; set; }
        /// <summary>この立ち絵に割り当てるYMM4キャラ名(null=主キャラのままPSDだけ差し替え)</summary>
        public string? Ymm4Name { get; set; }
        /// <summary>立ち絵のベースPSD参照</summary>
        public PackPsd? Psd { get; set; }
        public string? File { get; set; }
        public List<PackExpression> Expressions { get; set; } = new List<PackExpression>();
    }

    public class PackExpression
    {
        public string Id { get; set; } = "";
        public string? Label { get; set; }
        public bool Default { get; set; }
        public string? File { get; set; }
        public PackPsd? Psd { get; set; }
    }

    public class PackPsd
    {
        public string Path { get; set; } = "";
        public List<string> Layers { get; set; } = new List<string>();
        /// <summary>YMM4内部レイヤーID(nXXXの数値部)。書き庭がPSDの生レコード順から算出。
        /// Layers と同順・同数。省略時はコンシューマがパス解決を試みる</summary>
        public List<int>? LayerIds { get; set; }
    }

    public class PackEvent
    {
        /// <summary>読み込み時に決める印(パックには書かない): このセリフは声を作らない。
        /// セリフがファイルの場所の形のとき(YMM4 がその場所の音声を読み込んでしまうため)。</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool NoVoice { get; set; }
        public int No { get; set; }
        public string Scene { get; set; } = "";
        public string Type { get; set; } = "";
        public double Start { get; set; }
        public double Seconds { get; set; }
        /// <summary>この行に書かれた [間:N] の合計(秒)。
        /// ★実尺で並べ直すと Seconds(見積り)は捨てられ、指定した「間」も一緒に消えて
        /// YMM4 では詰まっていた。間だけは別に受け取り、ボイスの後ろに足す
        /// (2026-08-08 監査)。</summary>
        public double Pause { get; set; }
        public string? Speaker { get; set; }
        public string? SpeakerName { get; set; }
        public string? Expression { get; set; }
        public string? Text { get; set; }
        public string? Reading { get; set; }
        public List<PackAssetRef> Se { get; set; } = new List<PackAssetRef>();
        public PackAssetRef? Asset { get; set; }
        public string? Name { get; set; }
        public string? Arg { get; set; }
        /// <summary>任意: 適用するYMM4アイテムテンプレート名(未登録なら既定で配置)。/docs/pack-format.md 参照</summary>
        public string? Template { get; set; }
        /// <summary>type:"layout" のみ: 立ち位置の指定(以降のイベントに適用)</summary>
        public List<PackPosition>? Positions { get; set; }
        /// <summary>prop/effect: 横位置(画面幅比%・中央0)</summary>
        public double? X { get; set; }
        /// <summary>prop/effect: 縦位置(画面高さ比%・中央0・正=下)</summary>
        public double? Y { get; set; }
        /// <summary>prop/effect: 高さ(画面高さ比%)</summary>
        public double? Height { get; set; }
    }

    public class PackPosition
    {
        public string Speaker { get; set; } = "";
        /// <summary>画面中央からの横位置(画面幅比%・-50〜50・負=左)</summary>
        public double X { get; set; }
        /// <summary>下端揃えからの縦オフセット(画面高さ比%・正=上)。null=既定(下端揃え)</summary>
        public double? Y { get; set; }
        /// <summary>立ち絵の高さ(画面高さ比%)。null=既定値</summary>
        public double? Height { get; set; }
        /// <summary>いま出ている立ち絵のPSD実高(px)。書き庭が入れる。
        /// ★拡大率(%)そのものは動画の解像度に比例するので受け取らない(書き庭の想定解像度と
        /// 実際のプロジェクトの解像度が違うと必ずずれる)。実寸だけ貰い、拡大率はこちらで
        /// プロジェクトの高さから計算する(2026-08-08 監査)。
        /// これがあると、立ち絵を切り替えたあとも正しい実寸で計算できる
        /// (こちらは既定立ち絵のPSDしか読めないため)。</summary>
        public double? PsdHeight { get; set; }
        /// <summary>退場(この時点で立ち絵を終える。以降にセリフが来たら再登場)</summary>
        public bool Exit { get; set; }
    }

    public class PackAssetRef
    {
        public string Name { get; set; } = "";
        public string? Path { get; set; }
    }

    public static partial class Importer
    {
        public static Pack? LoadPack(string packDir, Action<string> log)
        {
            var jsonPath = Path.Combine(packDir, "timeline.json");
            if (!File.Exists(jsonPath))
            {
                log(Tr.T("log.pack.noJson", jsonPath));
                return null;
            }
            // ★読む前に大きさを見る。丸ごとメモリへ読んでから断っても遅い(2026-10-20c)
            var jsonBytes = new FileInfo(jsonPath).Length;
            if (jsonBytes > PackLimits.MaxJsonBytes)
            {
                log(Tr.T("log.pack.tooBig", jsonBytes, PackLimits.MaxJsonBytes));
                return null;
            }
            var pack = JsonSerializer.Deserialize<Pack>(
                File.ReadAllText(jsonPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (pack == null)
            {
                log(Tr.T("log.pack.parseFail"));
                return null;
            }
            // ★パックの形式版を確かめる。本体は自動更新、このプラグインは BOOTH で別配布なので、
            //   版がずれた組み合わせは必ず起きる。確認せずに読むと、知らない形のパックを v1 と
            //   思い込んで並べ、黙って誤配置する(利用者は原因に辿り着けない)。
            //   タイムラインを書き換える操作なので、中途半端に置くより手前で止める。
            if (pack.Version > PackSchema.Supported)
            {
                log(Tr.T("log.pack.newer", pack.Version, PackSchema.Supported));
                return null;
            }
            // 明示 null(手編集・別ツール生成)で NullReferenceException の生スタックを出さない。
            // DTO の初期化子は「キー省略」しか守らないため、"events": null はそのまま入る
            if (pack.Events == null) pack.Events = new List<PackEvent>();
            pack.Events.RemoveAll(e2 => e2 == null);
            if (pack.Characters == null) pack.Characters = new List<PackCharacter>();
            pack.Characters.RemoveAll(c2 => c2 == null);
            if (pack.Timing == null) pack.Timing = new PackTiming();
            // ★件数の上限。超えたら読み込まない(途中まで置くより手前で止める・2026-10-20c)
            var over = PackLimitExceeded(pack);
            if (over != null)
            {
                log(Tr.T("log.pack.tooMany", Tr.T("log.pack.limit." + over.Item1), over.Item2, over.Item3));
                return null;
            }
            foreach (var ev2 in pack.Events)
            {
                if (ev2.Type == null) ev2.Type = "";
                if (ev2.Se == null) ev2.Se = new List<PackAssetRef>();
                // 負や逆順の時間はゼロ長へ丸める(負の Length を YMM4 に渡さない)。
                // ★大きすぎる時間は 24 時間で止める(フレーム数の int があふれて負になる・2026-10-20c)
                ev2.Start = ClampPackSeconds(ev2.Start);
                ev2.Seconds = ClampPackSeconds(ev2.Seconds);
                ev2.Pause = ClampPackSeconds(ev2.Pause);
                // 位置・高さ(画面比 %)は ±1000% へ丸める(巨大値で拡大率・位置が Infinity になる)
                ev2.X = ClampPackPercent(ev2.X, -PackLimits.MaxPercent);
                ev2.Y = ClampPackPercent(ev2.Y, -PackLimits.MaxPercent);
                ev2.Height = ClampPackPercent(ev2.Height, 0);
                if (ev2.Positions != null)
                {
                    ev2.Positions.RemoveAll(p2 => p2 == null);
                    foreach (var p2 in ev2.Positions)
                    {
                        if (p2.Speaker == null) p2.Speaker = "";
                        p2.X = ClampPackPercent(p2.X, -PackLimits.MaxPercent) ?? 0;
                        p2.Y = ClampPackPercent(p2.Y, -PackLimits.MaxPercent);
                        p2.Height = ClampPackPercent(p2.Height, 0);
                    }
                }
                // ★セリフ本文の改行(書き庭の [改行])を、この環境の行区切り(CRLF)へそろえる。
                //   パックは LF で書かれる(JSON の "\n")。YMM4/WPF は CRLF が行の区切りなので、
                //   LF のまま渡すと字幕が1行のままだったり、見えない文字として残ったりする。
                //   ★これは「動く環境では気づけない」たぐいの食い違いなので、受け入れ口で一度だけ
                //   そろえる(各所で個別に直すと、直し漏れた経路だけが静かに壊れる)。
                //   声に出す読みを先に用意してから本文をそろえる(順序を変えると読みに CR が残る)。
                if (!string.IsNullOrEmpty(ev2.Text) && HasLineBreak(ev2.Text!)
                    && string.IsNullOrEmpty(ev2.Reading))
                {
                    // 読みが無いのに本文が折れているパック(手編集・別ツール製)。そのまま
                    // 読み上げに渡すと改行を読む・詰まるので、折り目を外した読みを補う。
                    // 書き庭のパックは折れている行に必ず reading を入れるので、ここは通らない。
                    ev2.Reading = RemoveLineBreaks(ev2.Text!);
                }
                if (ev2.Text != null) ev2.Text = NormalizeLineBreaks(ev2.Text);
                // 読み(声)に改行は入らない約束。混ざっていたら落とす(読み上げに渡すため)
                if (ev2.Reading != null) ev2.Reading = RemoveLineBreaks(ev2.Reading);
                // ★読みやセリフが「ファイルの場所」の形だと、YMM4 は合成せずにその場所の音声を声として読み込む
                //   (VoiceItem.IsCustomVoice。\\server\… ならネットワークへ取りに行く)。パックからは使わせない(2026-10-22 監査)。
                //   読みは捨てて本文から作り、本文がそうなら声を作らない
                if (ev2.Type == "serif" && LooksLikeFileLocation(ev2.Reading))
                {
                    log(Tr.T("log.pack.readingIsPath", ev2.No));
                    ev2.Reading = null;
                }
                if (ev2.Type == "serif" && LooksLikeFileLocation(ev2.Text))
                {
                    log(Tr.T("log.pack.textIsPath", ev2.No));
                    ev2.NoVoice = true;
                }
            }
            if (pack.Version < 1)
            {
                // 版が入っていない=古い書き庭で作られたか、別物の可能性。読めるだけ読む
                log(Tr.T("log.pack.noVersion"));
            }
            // ★パック由来の文字列は LogRedactor.Field で1行に収めてから出す(改行でログの行を偽装させない)
            log(Tr.T("log.pack.loaded", LogRedactor.Field(pack.Title), LogRedactor.Field(pack.Episode), pack.Events.Count, pack.Characters.Count));
            // 版の突き合わせ(整合の確認が一目でできるように・実機要望 2026-08-07)
            log(Tr.T("log.pack.versions",
                string.IsNullOrEmpty(pack.Generator?.Version) ? "?" : LogRedactor.Field(pack.Generator!.Version, 32),
                pack.Version, PackSchema.PluginVersion, PackSchema.Supported));
            // ★版は「数字と点だけ」の形のときだけ使う。以前は先頭の数字だけ比べて、改行や URL を含む文字列ごと
            //   「新しいプラグインが配布されています」の案内へ差し込んでいた(2026-10-22 監査)。形が違えば無視する
            if (IsPlainVersion(pack.PluginMin) &&
                CompareVersions(PackSchema.PluginVersion, pack.PluginMin!) < 0)
            {
                log(Tr.T("log.pack.needPlugin", pack.PluginMin, PackSchema.PluginVersion));
            }
            else if (IsPlainVersion(pack.PluginLatest) &&
                CompareVersions(PackSchema.PluginVersion, pack.PluginLatest!) < 0)
            {
                // 動作には支障がない(pluginMin は満たしている)ので、案内だけ一度出す
                log(Tr.T("log.pack.newPlugin", pack.PluginLatest, PackSchema.PluginVersion));
            }
            return pack;
        }

        static readonly System.Text.RegularExpressions.Regex PlainVersion =
            new System.Text.RegularExpressions.Regex(@"^\d{1,4}(\.\d{1,4}){0,3}$");

        /// <summary>パックに書かれた版(pluginMin・pluginLatest)が「0.5.0」のような数字と点だけの形か。</summary>
        internal static bool IsPlainVersion(string? v) => v != null && PlainVersion.IsMatch(v);

        /// <summary>"0.2.1" 形式の版数を数値で比較(a&lt;b なら負)。桁数違い・数字以外は 0 扱いで安全側</summary>
        static int CompareVersions(string a, string b)
        {
            var pa = a.Split('.');
            var pb = b.Split('.');
            for (var i = 0; i < Math.Max(pa.Length, pb.Length); i++)
            {
                int na = 0, nb = 0;
                if (i < pa.Length) int.TryParse(pa[i], out na);
                if (i < pb.Length) int.TryParse(pb[i], out nb);
                if (na != nb) return na - nb;
            }
            return 0;
        }

        /// <summary>パックの件数の上限を超えているものを返す(無ければ null)。
        /// Item1=辞書の名前(log.pack.limit.*)、Item2=件数、Item3=上限。</summary>
        internal static Tuple<string, int, int>? PackLimitExceeded(Pack pack)
        {
            if (pack.Events.Count > PackLimits.MaxEvents)
                return Tuple.Create("events", pack.Events.Count, PackLimits.MaxEvents);
            if (pack.Characters.Count > PackLimits.MaxCharacters)
                return Tuple.Create("characters", pack.Characters.Count, PackLimits.MaxCharacters);
            var speakers = new HashSet<string>();
            foreach (var e in pack.Events)
            {
                if (e.Speaker != null) speakers.Add(e.Speaker);
                var chars = Math.Max((e.Text ?? "").Length, (e.Reading ?? "").Length);
                if (chars > PackLimits.MaxSerifChars) return Tuple.Create("serif", chars, PackLimits.MaxSerifChars);
                var n = Math.Max(e.Positions != null ? e.Positions.Count : 0, e.Se != null ? e.Se.Count : 0);
                if (n > PackLimits.MaxListPerItem) return Tuple.Create("list", n, PackLimits.MaxListPerItem);
            }
            if (speakers.Count > PackLimits.MaxSpeakers)
                return Tuple.Create("speakers", speakers.Count, PackLimits.MaxSpeakers);
            foreach (var c in pack.Characters)
            {
                var n = Math.Max(c.Portraits != null ? c.Portraits.Count : 0,
                    c.Expressions != null ? c.Expressions.Count : 0);
                if (c.Portraits != null)
                    foreach (var p in c.Portraits)
                        if (p != null && p.Expressions != null) n = Math.Max(n, p.Expressions.Count);
                if (n > PackLimits.MaxListPerItem) return Tuple.Create("list", n, PackLimits.MaxListPerItem);
                // 表情の PSD レイヤーの数と、名前の長さ(プリセット登録の経路も同じパックを読むので、ここで止まる)
                var exprs = new List<PackExpression>();
                if (c.Expressions != null) exprs.AddRange(c.Expressions);
                if (c.Portraits != null)
                    foreach (var p in c.Portraits)
                        if (p != null && p.Expressions != null) exprs.AddRange(p.Expressions);
                foreach (var x in exprs)
                {
                    if (x == null) continue;
                    var longest = Math.Max((x.Id ?? "").Length, (x.Label ?? "").Length);
                    if (x.Psd != null)
                    {
                        var layers = Math.Max(x.Psd.Layers != null ? x.Psd.Layers.Count : 0,
                            x.Psd.LayerIds != null ? x.Psd.LayerIds.Count : 0);
                        if (layers > PackLimits.MaxLayersPerExpression)
                            return Tuple.Create("layers", layers, PackLimits.MaxLayersPerExpression);
                        if (x.Psd.Layers != null)
                            foreach (var l in x.Psd.Layers) longest = Math.Max(longest, (l ?? "").Length);
                    }
                    if (longest > PackLimits.MaxNameLength) return Tuple.Create("name", longest, PackLimits.MaxNameLength);
                }
            }
            return null;
        }

        /// <summary>パックの時間(秒)を 0〜24時間へ丸める。NaN・無限・負は 0。</summary>
        internal static double ClampPackSeconds(double v)
        {
            if (double.IsNaN(v) || v < 0) return 0;
            return v > PackLimits.MaxSeconds ? PackLimits.MaxSeconds : v;
        }

        /// <summary>位置・高さ(画面比 %)を [min, 1000] へ丸める。null と NaN は null(=既定)。</summary>
        internal static double? ClampPackPercent(double? v, double min)
        {
            if (v == null || double.IsNaN(v.Value)) return null;
            return Math.Max(min, Math.Min(PackLimits.MaxPercent, v.Value));
        }

        // ---------- 改行(セリフの中の [改行]) ----------
        //
        // 書き庭の台本は 1行=1セリフ で、セリフの中の折り返しは記法 [改行] で書く。
        // パックにはそれが本文の改行として載ってくる(docs/pack-format.md)。

        /// <summary>本文に行の折り返しが入っているか(CR・LF どちらの綴りでも)</summary>
        static bool HasLineBreak(string s)
        {
            return s.IndexOf('\n') >= 0 || s.IndexOf('\r') >= 0;
        }

        /// <summary>行の折り返しを CRLF へそろえる(混在・CR単独も一様に直す)。
        /// YMM4/WPF の行区切りは CRLF で、LF のままだと字幕が1行のままになったり
        /// 見えない文字として残ったりする。</summary>
        static string NormalizeLineBreaks(string s)
        {
            return s.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
        }

        /// <summary>行の折り返しを取り除く(読み上げに渡す読み・記録の1行表示用)。
        /// 空白へ置き換えないのは、読みでは間として発音されうるため。</summary>
        static string RemoveLineBreaks(string s)
        {
            return s.Replace("\r", "").Replace("\n", "");
        }

        /// <summary>記録(ログ)に1行で出すための整形。折り返しは空白へ
        /// (取り除くと語がくっついて読めなくなる。ここは声ではなく人が読む)。</summary>
        static string SingleLine(string s)
        {
            return NormalizeLineBreaks(s).Replace("\r\n", " ");
        }

        /// <summary>パック由来の相対パスを packDir 配下の実ファイル絶対パスへ解決する。配下でなければ null。
        /// 半信頼パック(第三者から受け取ったものを含む)の素材パスは攻撃者が細工しうるため、
        /// (1)絶対パス/UNC を拒否し、(2)正規化後に packDir 配下であることを必須にする。これをしないと:
        ///  ・UNC(\\attacker\share\a.png)への File.Exists が被害者の NTLM ハッシュを攻撃者SMBへ送る、
        ///  ・絶対パス(C:\Users\…\.ssh\id_rsa)や ..\ でパック外の任意ファイルを参照・読取できる。
        /// File.Exists は必ず配下確認を通した後にだけ呼ぶ(UNC を Exists に到達させない)。</summary>
        /// <summary>フルパス参照(同梱なし書き出しの既定)で許可する素材拡張子。
        /// 絶対パスを無条件に許すと、細工パックで任意のローカルファイル(秘密鍵等)を
        /// タイムラインへ引き込めるため、素材として意味のある種類に限定する。</summary>
        /// ★書き庭が素材として受け付ける種類とそろえる。ここが狭いと、動画の背景や
        /// aac/opus の音楽が「素材が見つからない」でスキップされ、しかも既定は
        /// 「同梱しない(絶対パス参照)」なので、同梱を有効にすると通るのに既定だと
        /// 消えるという食い違いになっていた(2026-08-08 監査)。
        /// ★LogRedactor の「ファイル名を伏せる」拡張子の一覧もここから作る(素材として通るのに
        /// 伏せ字から漏れる種類を作らない・2026-10-20c)。
        internal static readonly string[] AbsAllowedExts = {
            // ★.svg も入れる。書き庭は SVG を正規の画像素材として受け付け、YMM4 も同梱の SvgImageSourcePlugin
            //   (YukkuriMovieMaker.Plugin.FileSource.Svg.dll・v4.56.1 で確認)で描ける。以前はここに無く、
            //   同梱(相対パス)なら置けるのに既定の「同梱しない」では黙ってスキップされていた(2026-10-21 18周目)
            ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".avif", ".svg",
            ".wav", ".mp3", ".ogg", ".flac", ".m4a", ".aac", ".opus",
            ".mp4", ".webm", ".mov", ".avi", ".mkv",
            ".psd",
        };

        static string? ResolveInPack(string packDir, string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (Path.IsPathRooted(path))
            {
                // ★読取専用の許可。書き込み先の決定に使う経路は ResolveInPackForWrite を通す
                //   (絶対パスを許すと、パック外の実在 PSD の隣にサイドカーを書ける。2026-09-02)。
                // ★ローカルドライブ(C:\ 等)の絶対パスは許可する。「同梱しない」書き出しの
                //   既定形がフルパス参照で、全面拒否だと既定のパックが素材ぜんぶスキップに
                //   なっていた(2026-08-07 監査)。拒否を続けるのは:
                //   ・UNC/デバイスパス(\\server\… は File.Exists だけで NTLM ハッシュが
                //     攻撃者の SMB へ飛ぶ)
                //   ・素材らしくない拡張子(任意ファイルの引き込み対策)
                try
                {
                    var abs = Path.GetFullPath(path);
                    if (abs.StartsWith(@"\\")) return null; // UNC・\\?\ デバイスパス
                    if (abs.Length < 3 || !char.IsLetter(abs[0]) || abs[1] != ':' || abs[2] != Path.DirectorySeparatorChar) return null;
                    var ext = Path.GetExtension(abs).ToLowerInvariant();
                    if (Array.IndexOf(AbsAllowedExts, ext) < 0) return null;
                    return File.Exists(abs) ? abs : null;
                }
                catch { return null; }
            }
            string full, baseWithSep;
            try
            {
                var baseDir = Path.GetFullPath(packDir);
                baseWithSep = baseDir.EndsWith(Path.DirectorySeparatorChar.ToString())
                    ? baseDir : baseDir + Path.DirectorySeparatorChar;
                full = Path.GetFullPath(Path.Combine(packDir, path));
            }
            catch { return null; }
            // 正規化後に packDir 配下か(..\ での脱出を弾く)。Windows は大小無視。
            if (!full.StartsWith(baseWithSep, StringComparison.OrdinalIgnoreCase)) return null;
            // ★相対パスでも素材らしい拡張子だけ(絶対パスと同じ)。以前は相対なら何でも通した(2026-10-22 監査)
            if (Array.IndexOf(AbsAllowedExts, Path.GetExtension(full).ToLowerInvariant()) < 0) return null;
            // ★パックの中のフォルダがリンク(ジャンクション等)でリモート(UNC)を指していたら読まない。
            //   File.Exists がリンクをたどった時点で、相手の SMB へ NTLM ハッシュが飛ぶ(2026-10-22 監査)。
            //   上の階層から順に、たどる前に調べる
            if (PathHasRemoteLink(packDir, full)) return null;
            return File.Exists(full) ? full : null;
        }

        /// <summary>packDir から full までの各階層(packDir 自身は含めない)を上から順に見て、
        /// リンクのリンク先がリモート(UNC・\\?\UNC)なら true。調べられなかったときも true(=読まない側)。
        /// ★下の階層を見る前に上の階層を見る(リモートを指すリンクの中へ入る前に止める)。</summary>
        internal static bool PathHasRemoteLink(string packDir, string full)
        {
            try
            {
                var baseFull = Path.GetFullPath(packDir).TrimEnd(Path.DirectorySeparatorChar);
                var target = Path.GetFullPath(full);
                if (!target.StartsWith(baseFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return true;
                var rest = target.Substring(baseFull.Length + 1).Split(Path.DirectorySeparatorChar);
                var cur = baseFull;
                foreach (var part in rest)
                {
                    cur = Path.Combine(cur, part);
                    var lt = LinkTargetOf(cur);
                    if (lt == null) continue;
                    var abs = Path.IsPathRooted(lt) ? lt : Path.Combine(Path.GetDirectoryName(cur) ?? "", lt);
                    if (IsRemotePath(abs)) return true;
                }
                return false;
            }
            catch { return true; }
        }

        /// <summary>そのファイル・フォルダ自身がリンクならリンク先(無い・リンクでなければ null)。
        /// リンクそのものの属性だけを見て、リンク先へはたどらない。</summary>
        static string? LinkTargetOf(string path)
        {
            FileSystemInfo fi = new DirectoryInfo(path);
            if (!fi.Exists) fi = new FileInfo(path);
            if (!fi.Exists) return null;
            if ((fi.Attributes & FileAttributes.ReparsePoint) == 0) return null;
            return fi.LinkTarget;
        }

        /// <summary>リモート(ネットワーク)を指すパスか。\\server\share・\\?\UNC\…・//server/… を見る。</summary>
        internal static bool IsRemotePath(string p)
        {
            var s = (p ?? "").Replace('/', '\\');
            if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase)) return true;
            if (s.StartsWith(@"\\?\") || s.StartsWith(@"\??\")) return false; // \\?\C:\… はローカル
            return s.StartsWith(@"\\");
        }

        /// <summary>書き込み先を決める用途の解決。ResolveInPack と違い絶対パス(ローカル・UNC とも)を
        /// 一切受け付けず、正規化後に packDir 配下にある相対パスだけを通す。
        /// 読取では「同梱しない書き出し」のフルパス参照を素材拡張子付きで許しているが、書込先まで
        /// 許すと、細工パックの psd.path が指す任意の実在 PSD の隣にサイドカー(PSD名-ymm.json)を
        /// 上書きできてしまう(REPORT-131「書込先は入力非依存」の前提が崩れる。2026-09-02)。</summary>
        static string? ResolveInPackForWrite(string packDir, string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (Path.IsPathRooted(path) || path.StartsWith(@"\\")) return null;
            var full = ResolveInPack(packDir, path);
            if (full == null) return null;
            // ★文字列の上では配下でも、途中のフォルダがジャンクション/シンボリックリンクなら
            //   実際の書き込みはパックの外へ出る。解決した経路の各階層を調べ、あれば断る(2026-10-20c)
            return PathHasLink(packDir, full) ? null : full;
        }

        /// <summary>full(packDir 配下の絶対パス)から packDir 自身までの各階層に、
        /// リンク(シンボリックリンク・ジャンクション・マウントポイント)があれば true。
        /// 調べられなかったときも true(=書かない側)。
        /// ★属性の ReparsePoint だけで断らないのは、OneDrive のオンライン専用ファイルなど
        /// 「リンクではない reparse point」でも立つため(LinkTarget は null になる)。</summary>
        internal static bool PathHasLink(string packDir, string full)
        {
            try
            {
                var baseFull = Path.GetFullPath(packDir).TrimEnd(Path.DirectorySeparatorChar);
                var cur = Path.GetFullPath(full).TrimEnd(Path.DirectorySeparatorChar);
                if (!(cur + Path.DirectorySeparatorChar).StartsWith(baseFull + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    return true; // 配下でない(呼び出し側の前提が崩れている)
                while (true)
                {
                    if (IsLink(cur)) return true;
                    if (string.Equals(cur, baseFull, StringComparison.OrdinalIgnoreCase)) return false;
                    var parent = Path.GetDirectoryName(cur);
                    if (string.IsNullOrEmpty(parent)) return true;
                    cur = parent.TrimEnd(Path.DirectorySeparatorChar);
                }
            }
            catch { return true; }
        }

        /// <summary>そのファイル・フォルダ自身がリンクか(無ければ false)。</summary>
        internal static bool IsLink(string path)
        {
            FileSystemInfo fi = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!fi.Exists) return false;
            if ((fi.Attributes & FileAttributes.ReparsePoint) == 0) return false;
            return fi.LinkTarget != null;
        }

        /// <summary>パック相対パスを実ファイルの絶対パスへ。存在しない/パック外なら null。</summary>
        static string? ResolvePackPath(string packDir, string? path) => ResolveInPack(packDir, path);

        static string? ResolveAsset(string packDir, PackAssetRef? asset, List<string> skipped, PackEvent ev)
        {
            if (asset == null || asset.Path == null)
            {
                // 「path=null」は書き庭が“その素材を見つけられなかった”という印。
                // 内部表現をそのまま出しても何をすればよいか伝わらないので、直し方を書く。
                skipped.Add(string.Format(
                    "no.{0}: 素材「{1}」が書き庭側で見つかりませんでした(素材ライブラリに追加するか、台本の名前を直してから書き出し直してください)",
                    ev.No, asset != null ? asset.Name : "(名前なし)"));
                return null;
            }
            // packDir 配下への封じ込め(絶対パス/UNC/.. を拒否)。詳細は ResolveInPack 参照。
            var full = ResolveInPack(packDir, asset.Path);
            if (full == null)
            {
                skipped.Add(string.Format(
                    "no.{0}: 素材ファイルが見つかりません {1}(パックの外を指しているか、書き出し後に移動・削除された可能性があります)",
                    ev.No, asset.Path));
                return null;
            }
            return full;
        }

        /// <summary>立ち絵の実ファイルを探す。
        /// ★portrait(いま出ている立ち絵)を優先する。以前は常にキャラの既定表情から探して
        /// いたので、(a) 立ち絵を切り替えても寸法計算だけ切替前のPSDのままになり、
        /// (b) 表情が portraits[].expressions にしか無いキャラでは常に見つからず、
        /// 仮置き画像が黙って出なくなっていた(2026-08-08 監査)。</summary>
        static string? ResolvePortrait(string packDir, PackCharacter? chara,
            PackPortrait? portrait, string? expression)
        {
            if (chara == null) return null;
            PackExpression? expr = null;
            if (portrait != null && portrait.Expressions != null)
            {
                expr = expression == null
                    ? (portrait.Expressions.FirstOrDefault(x => x.Default) ?? portrait.Expressions.FirstOrDefault())
                    : portrait.Expressions.FirstOrDefault(x => x.Id == expression || x.Label == expression);
            }
            string? rel = null;
            // ① いま出ている立ち絵の、その表情
            if (expr != null && expr.Psd != null) rel = expr.Psd.Path;
            else if (expr != null && expr.File != null && chara.PortraitDir != null)
                rel = chara.PortraitDir + "/" + expr.File;
            // ② いま出ている立ち絵そのもののPSD(表情を持たない立ち絵・表情名が違う立ち絵)
            //    ★ここを chara.Expressions(=既定立ち絵の表情)より**先**に見る。順序が逆だと、
            //    立ち絵を切り替えたのに切替前のPSDを黙って返し、別の顔で描かれる
            //    (2026-08-08 メタ監査)。
            if (rel == null && portrait != null && portrait.Psd != null) rel = portrait.Psd.Path;
            if (rel == null && portrait != null && portrait.File != null && chara.PortraitDir != null)
                rel = chara.PortraitDir + "/" + portrait.File;
            // ③ 立ち絵の指定がない従来キャラ: キャラ直下の表情から
            if (rel == null)
            {
                var flat = expression == null
                    ? (chara.Expressions.FirstOrDefault(x => x.Default) ?? chara.Expressions.FirstOrDefault())
                    : chara.Expressions.FirstOrDefault(x => x.Id == expression || x.Label == expression);
                if (flat != null && flat.Psd != null) rel = flat.Psd.Path;
                else if (flat != null && flat.File != null && chara.PortraitDir != null)
                    rel = chara.PortraitDir + "/" + flat.File;
            }
            if (rel == null) return null;
            // rel は Psd.Path か PortraitDir+File(いずれもパック由来=攻撃者制御しうる)。配下へ封じ込め。
            return ResolveInPack(packDir, rel);
        }
    }
}
