// 取り込みログを「ファイルに残す」ときの伏せ字。
// ログには素材のファイル名・フォルダのパス(Windows のユーザー名を含む)・セリフ本文が出る。
// 症状の報告に貼ってもらうためのファイルなので、個人や作品の中身が分かるものは落とす。
// 画面に出すログも同じ伏せ字を通す(スクショに映るため)。

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KakiniwaYmm4Import
{
    public static class LogRedactor
    {
        // C:\... / \\server\share\... / /Users/... のようなパス全体(空白を含むフォルダ名は拾いきれないので、
        // 区切り文字の連なりを取れるだけ取る)
        // ★//server/share(スラッシュで書いた UNC)も拾う。ただし URL(https://…)の // は拾わない(2026-10-20c)
        //   ドライブ名は1文字なので、前が英字のもの(https: の s:)はドライブではない
        static readonly Regex WinPath = new Regex(@"(?:(?<![A-Za-z])[A-Za-z]:[\\/]|\\\\|(?<![:/\w])//(?=[^\s/]))[^\r\n""<>|?*「」]+", RegexOptions.Compiled);
        static readonly Regex UnixPath = new Regex(@"(?<![\w.])/(?:Users|home|Volumes)/[^\s""<>|?*「」]+", RegexOptions.Compiled);
        // 相対パスのフォルダの連なり(assets/秘密の作品/ や 素材\背景\)。ファイル名は下の FileName が伏せる。
        // ★以前は拾わず、パックの中のフォルダ名(作品名・キャラ名が入る)が保存ログに残っていた(2026-10-20c)。
        //   数字と点だけのもの・版数(「10/10件」「v4.54/4.55」)と URL の途中(// の直後)は拾わない
        static readonly Regex RelDirs = new Regex(
            @"(?<![^\s「『(()=:,])(?:(?![vV]?[\d.]+[\\/])[^\s\\/:*?""<>|「」『』()()]+[\\/])+",
            RegexOptions.Compiled);
        // 拡張子つきのファイル名(パスに含まれない単独のもの)。
        // ★拡張子は「素材として通る種類」(Importer.AbsAllowedExts)から作る。手で並べていた頃は
        //   avif/aac/opus/mkv が抜けていて、その素材のファイル名だけ伏せ字から漏れていた(2026-10-20c)
        static readonly string[] ExtraExts = { "ymmp", "ymmt", "json", "csv", "txt", "md", "zip" };
        static readonly Regex FileName = new Regex(
            @"[^\s\\/:*?""<>|「」『』()()]+\.(?:" + string.Join("|",
                Importer.AbsAllowedExts.Select(e => e.TrimStart('.')).Concat(ExtraExts)
                    .Distinct().Select(Regex.Escape)) + @")\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // 「セリフ」本文(ログは「…」で括る)。文字数だけ残す。
        // ★作品名などは『…』で括って出る(log.pack.loaded)。こちらも伏せる(2026-10-20c)
        // ★括弧の中の改行も伏せる対象にする(以前は行をまたぐと素通りしていた・2026-10-22 監査)
        static readonly Regex Quoted = new Regex(@"「([^「」]*)」", RegexOptions.Compiled);
        static readonly Regex DoubleQuoted = new Regex(@"『([^『』]*)』", RegexOptions.Compiled);
        // YMM4 に登録したキャラの名前の一覧(自分で付けた名前が入りうる)。件数だけ残す。
        // ★行の頭の文言は表示言語ごとに違う(辞書の log.chars)。組み込み・外部のどの言語でも拾えるよう、
        //   辞書にある全部の言い方から正規表現を作る。日本語と英語だけ決め打ちにしていたので、
        //   スペイン語にすると素通りだった(スペイン語で実機確認して見つけた)。
        static Regex BuildCharaList(out List<string> noneMarkers)
        {
            var prefixes = new List<string>();
            noneMarkers = new List<string>();
            foreach (var d in Tr.Languages.Values.ToList())
            {
                string? v;
                if (d.TryGetValue("log.chars", out v) && v.Contains("{0}"))
                {
                    var head = v.Substring(0, v.IndexOf("{0}"));
                    if (head.Trim().Length > 0 && !prefixes.Contains(head)) prefixes.Add(head);
                }
                if (d.TryGetValue("log.charsNone", out v) && !noneMarkers.Contains(v)) noneMarkers.Add(v);
            }
            // 保険: 辞書が読めなくても日本語・英語の言い方は拾う
            foreach (var h in new[] { "YMM4登録キャラ: ", "YMM4 characters: " })
                if (!prefixes.Contains(h)) prefixes.Add(h);
            prefixes.Sort((a, b) => b.Length.CompareTo(a.Length));
            var alt = string.Join("|", prefixes.Select(Regex.Escape));
            return new Regex(@"^(\s*(?:" + alt + @")).*$", RegexOptions.Multiline);
        }

        /// <summary>画面に出すログ用。フォルダのパス(Windows のユーザー名を含む)だけ伏せる。
        /// ファイル名・セリフ・キャラ名は、どの素材・どの行で困っているかを本人が見て分かるよう残す。
        /// (保存するログ・貼って報告するログは Redact で全部伏せる)</summary>
        public static string RedactForDisplay(string log)
        {
            if (string.IsNullOrEmpty(log)) return log;
            return UnixPath.Replace(WinPath.Replace(log, "<パス>"), "<パス>");
        }

        public static string Redact(string log)
        {
            if (string.IsNullOrEmpty(log)) return log;
            List<string> noneMarkers;
            var s = BuildCharaList(out noneMarkers).Replace(log, m =>
            {
                var rest = m.Value.Substring(m.Groups[1].Length).TrimEnd('\r');
                var none = rest.StartsWith("(取得できず)") || rest.StartsWith("(unavailable)")
                    || noneMarkers.Any(x => rest.StartsWith(x));
                var n = none ? 0 : rest.Split(',').Length;
                // 日本語は「(N件)」、それ以外は「(N)」
                return m.Groups[1].Value + (m.Groups[1].Value.Contains("登録") ? "(" + n + "件)" : "(" + n + ")");
            });
            s = WinPath.Replace(s, "<パス>");
            s = UnixPath.Replace(s, "<パス>");
            s = RelDirs.Replace(s, "<パス>");
            s = FileName.Replace(s, m => "<ファイル>" + System.IO.Path.GetExtension(m.Value).ToLowerInvariant());
            s = Quoted.Replace(s, m => "「(" + CountChars(m.Groups[1].Value) + "文字)」");
            s = DoubleQuoted.Replace(s, m => "『(" + CountChars(m.Groups[1].Value) + "文字)』");
            return s;
        }

        /// <summary>ログへ出すパック由来の文字列(種類・話者・立ち絵名・作品名・版など)を1行に収める。
        /// 改行・制御文字・書字方向や幅ゼロの文字は \n や \uXXXX の形で見えるようにし、max 文字で切る。
        /// パックは第三者から受け取ることもあるので、ログの行を偽装させない・長さで埋めさせない(2026-10-22 監査)。</summary>
        public static string Field(string? s, int max = 80)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder();
            foreach (var c in s!)
            {
                if (c == '\n') sb.Append("\\n");
                else if (c == '\r') sb.Append("\\r");
                else if (c == '\t') sb.Append("\\t");
                else if (char.IsControl(c) || IsInvisibleFormat(c))
                    sb.Append("\\u").Append(((int)c).ToString("X4"));
                else sb.Append(c);
                if (sb.Length > max) break;
            }
            if (sb.Length > max) return Importer.FirstChars(sb.ToString(), max) + "…";
            return sb.ToString();
        }

        // 書字方向の指定・幅ゼロの文字・BOM・行や段落の区切り(見えないのに表示を変える)
        static bool IsInvisibleFormat(char c)
            => (c >= '\u200B' && c <= '\u200F') || (c >= '\u202A' && c <= '\u202E')
               || (c >= '\u2066' && c <= '\u2069') || c == '\uFEFF' || c == '\u2028' || c == '\u2029';

        // 絵文字(サロゲートペア)を1文字と数える
        static int CountChars(string text)
        {
            var n = 0;
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length) i++;
                n++;
            }
            return n;
        }

        /// <summary>保存用の本文を組み立てる(先頭に版と日時、伏せ字済みのログ)。</summary>
        public static string Compose(string header, string log)
        {
            var sb = new StringBuilder();
            sb.AppendLine(header);
            sb.AppendLine("(ファイル名・パス・セリフ本文は伏せてあります)");
            sb.AppendLine();
            sb.Append(Redact(log));
            return sb.ToString();
        }
    }
}
