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
        public const string PluginVersion = "0.4.0";
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
                log("timeline.json がありません: " + jsonPath);
                return null;
            }
            var pack = JsonSerializer.Deserialize<Pack>(
                File.ReadAllText(jsonPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (pack == null)
            {
                log("timeline.json の解析に失敗");
                return null;
            }
            // ★パックの形式版を確かめる。本体は自動更新、このプラグインは BOOTH で別配布なので、
            //   版がずれた組み合わせは必ず起きる。確認せずに読むと、知らない形のパックを v1 と
            //   思い込んで並べ、黙って誤配置する(利用者は原因に辿り着けない)。
            //   タイムラインを書き換える操作なので、中途半端に置くより手前で止める。
            if (pack.Version > PackSchema.Supported)
            {
                log(string.Format(
                    "このパックは新しい形式(v{0})です。このプラグインが分かるのは v{1} までです。" +
                    "プラグインを新しいものに入れ替えてください: https://kakiniwa.jp/plugin/",
                    pack.Version, PackSchema.Supported));
                return null;
            }
            // 明示 null(手編集・別ツール生成)で NullReferenceException の生スタックを出さない。
            // DTO の初期化子は「キー省略」しか守らないため、"events": null はそのまま入る
            if (pack.Events == null) pack.Events = new List<PackEvent>();
            pack.Events.RemoveAll(e2 => e2 == null);
            if (pack.Characters == null) pack.Characters = new List<PackCharacter>();
            pack.Characters.RemoveAll(c2 => c2 == null);
            if (pack.Timing == null) pack.Timing = new PackTiming();
            foreach (var ev2 in pack.Events)
            {
                if (ev2.Type == null) ev2.Type = "";
                if (ev2.Se == null) ev2.Se = new List<PackAssetRef>();
                // 負や逆順の時間はゼロ長へ丸める(負の Length を YMM4 に渡さない)
                if (double.IsNaN(ev2.Start) || double.IsInfinity(ev2.Start) || ev2.Start < 0) ev2.Start = 0;
                if (double.IsNaN(ev2.Seconds) || double.IsInfinity(ev2.Seconds) || ev2.Seconds < 0) ev2.Seconds = 0;
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
            }
            if (pack.Version < 1)
            {
                // 版が入っていない=古い書き庭で作られたか、別物の可能性。読めるだけ読む
                log("注意: パックに形式版がありません。古い書き庭で書き出したものかもしれません。");
            }
            log(string.Format("読み込みOK: {0}『{1}』 events={2} chars={3}",
                pack.Title, pack.Episode, pack.Events.Count, pack.Characters.Count));
            // 版の突き合わせ(整合の確認が一目でできるように・実機要望 2026-08-07)
            log(string.Format("バージョン: 書き庭 v{0} のパック(形式v{1}) / プラグイン v{2}(対応形式 v{3})",
                string.IsNullOrEmpty(pack.Generator?.Version) ? "?" : pack.Generator!.Version,
                pack.Version, PackSchema.PluginVersion, PackSchema.Supported));
            if (!string.IsNullOrEmpty(pack.PluginMin) &&
                CompareVersions(PackSchema.PluginVersion, pack.PluginMin!) < 0)
            {
                log(string.Format(
                    "⚠ このパックはプラグイン v{0} 以上を想定しています(現在 v{1})。" +
                    "正しく読めない項目があるかもしれません。新しい版に入れ替えてください: https://kakiniwa.jp/plugin/",
                    pack.PluginMin, PackSchema.PluginVersion));
            }
            else if (!string.IsNullOrEmpty(pack.PluginLatest) &&
                CompareVersions(PackSchema.PluginVersion, pack.PluginLatest!) < 0)
            {
                // 動作には支障がない(pluginMin は満たしている)ので、案内だけ一度出す
                log(string.Format(
                    "新しいプラグイン v{0} が配布されています(現在 v{1})。" +
                    "入れ替えはこちら: https://kakiniwa.jp/plugin/",
                    pack.PluginLatest, PackSchema.PluginVersion));
            }
            return pack;
        }

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
        static readonly string[] AbsAllowedExts = {
            ".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif", ".avif",
            ".wav", ".mp3", ".ogg", ".flac", ".m4a", ".aac", ".opus",
            ".mp4", ".webm", ".mov", ".avi", ".mkv",
            ".psd",
        };

        static string? ResolveInPack(string packDir, string? path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            if (Path.IsPathRooted(path))
            {
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
            return File.Exists(full) ? full : null;
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
