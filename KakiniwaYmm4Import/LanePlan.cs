// レーン(レイヤー)の割り当て。取り込みで置くアイテムの種類ごとに、どのレイヤーへ置くかを決める純ロジック。
// 既定は従来どおり「使うレーンだけ登場順に0から詰める」。設定で一部を固定番号にできる。
// 番号は「開始レイヤーを 0 とした相対番号」(Importer が開始レイヤー分を足す)。

using System;
using System.Collections.Generic;
using System.Linq;

namespace KakiniwaYmm4Import
{
    /// <summary>セリフのレイヤーの置き方。</summary>
    public static class VoiceLayerMode
    {
        /// <summary>キャラごとに別レイヤー(従来の動き・既定)</summary>
        public const string PerCharacter = "perCharacter";
        /// <summary>全員のセリフを1つのレイヤーにまとめる</summary>
        public const string Single = "single";
        /// <summary>キャラごとに番号を指定する(指定の無いキャラは自動)</summary>
        public const string Custom = "custom";

        public static string Normalize(string? mode)
            => mode == Single || mode == Custom ? mode : PerCharacter;
    }

    /// <summary>セリフとセリフの間の隙間の決め方。</summary>
    public static class SerifGapMode
    {
        /// <summary>パック(書き庭の書き出し設定)の値に従う(既定)</summary>
        public const string Pack = "pack";
        /// <summary>隙間なし(公式の台本編集のように詰める)</summary>
        public const string None = "none";
        /// <summary>秒数を指定する</summary>
        public const string Custom = "custom";

        public const double MaxSeconds = 10.0;

        public static string Normalize(string? mode)
            => mode == None || mode == Custom ? mode : Pack;

        /// <summary>実際に使う隙間(秒)。壊れた値(負・NaN・無限大・大きすぎ)は 0〜MaxSeconds に収める。</summary>
        public static double Resolve(string? mode, double customSeconds, double packSeconds)
        {
            double v;
            switch (Normalize(mode))
            {
                case None: v = 0; break;
                case Custom: v = customSeconds; break;
                default: v = packSeconds; break;
            }
            if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) return 0;
            return Math.Min(v, MaxSeconds);
        }
    }

    /// <summary>レーンのキー("bg" / "voice:キャラid" など)→ レイヤー番号。</summary>
    public sealed class LanePlan
    {
        /// <summary>設定で番号を固定できる素材の種類(キーの ":" より前)。立ち絵・表情はキャラ単位で重なるので含めない。</summary>
        public static readonly string[] FixableKinds = { "bg", "bgm", "se", "stage", "prop-slot", "effect", "window" };

        readonly Func<string, int?> fixedOf;
        readonly List<string> order = new List<string>();
        readonly Dictionary<string, int> lanes = new Dictionary<string, int>();
        readonly HashSet<int> taken = new HashSet<int>();
        int next;
        bool resolved;

        public LanePlan(Func<string, int?> fixedOf) { this.fixedOf = fixedOf; }

        /// <summary>レーンのキーを、重なり順(上から)に登録する。同じキーは1回だけ数える。</summary>
        public void Add(string key)
        {
            if (!order.Contains(key) && !lanes.ContainsKey(key)) order.Add(key);
        }

        /// <summary>登録が終わったら呼ぶ。固定番号を先に確保し、残りを空いている番号へ詰める。</summary>
        public void Resolve()
        {
            if (resolved) return;
            resolved = true;
            foreach (var key in order)
            {
                var f = fixedOf(key);
                if (f == null) continue;
                var n = Math.Max(0, f.Value);
                lanes[key] = n;
                taken.Add(n);
            }
            foreach (var key in order)
                if (!lanes.ContainsKey(key)) lanes[key] = NextFree();
        }

        /// <summary>キーのレイヤー番号。未登録のキーはその場で空き番号を割り当てる。</summary>
        public int Of(string key)
        {
            Resolve();
            int l;
            if (lanes.TryGetValue(key, out l)) return l;
            var f = fixedOf(key);
            if (f != null) { l = Math.Max(0, f.Value); taken.Add(l); }
            else l = NextFree();
            lanes[key] = l;
            return l;
        }

        int NextFree()
        {
            while (taken.Contains(next)) next++;
            var n = next++;
            taken.Add(n);
            return n;
        }

        /// <summary>キーの種類(":" より前)。</summary>
        public static string KindOf(string key)
        {
            var i = key.IndexOf(':');
            return i < 0 ? key : key.Substring(0, i);
        }

        /// <summary>設定から「このキーを固定する番号」を決める関数を作る。</summary>
        public static Func<string, int?> FixedFromSettings(string? voiceMode,
            IDictionary<string, int>? voiceLayers, IDictionary<string, int>? kindLayers,
            IDictionary<string, int>? assetLayers = null)
        {
            var mode = VoiceLayerMode.Normalize(voiceMode);
            return key =>
            {
                var kind = KindOf(key);
                // 素材名ごとの指定(種類ごとの指定より細かいので優先される)
                if (kind == "asset")
                {
                    int a;
                    return assetLayers != null && assetLayers.TryGetValue(key.Substring(6), out a) && a >= 0 ? a : (int?)null;
                }
                if (kind == "voice")
                {
                    if (mode != VoiceLayerMode.Custom || voiceLayers == null) return null;
                    int v;
                    var id = key.Length > 6 ? key.Substring(6) : "";
                    return voiceLayers.TryGetValue(id, out v) && v >= 0 ? v : (int?)null;
                }
                if (kindLayers != null && Array.IndexOf(FixableKinds, kind) >= 0)
                {
                    int k;
                    if (kindLayers.TryGetValue(kind, out k) && k >= 0) return k;
                }
                return null;
            };
        }

        /// <summary>素材名に個別の番号が指定されていれば専用のキー("asset:名前")、無ければ既定のキー。</summary>
        public static string AssetKey(string defaultKey, string? assetName, IDictionary<string, int>? assetLayers)
            => assetName != null && assetLayers != null && assetLayers.ContainsKey(assetName)
                ? "asset:" + assetName
                : defaultKey;

        /// <summary>「全員で1つ」のときは、すべてのセリフを同じキーに寄せる。</summary>
        public static string CanonicalKey(string key, string? voiceMode)
            => VoiceLayerMode.Normalize(voiceMode) == VoiceLayerMode.Single && KindOf(key) == "voice"
                ? "voice:*"
                : key;
    }
}
