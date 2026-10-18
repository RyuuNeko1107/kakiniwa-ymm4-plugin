// timeline.json の任意項目 `layers`(YMM4 でどのレイヤーへ置くかの指定)。仕様は /docs/pack-format.md。
// すべて任意。無ければ従来どおり(キャラごと・自動)。プラグインの窓の初期値にするだけで、
// 取り込む前に窓で変えられる。古いプラグインはこの項目を知らずに無視する(読み飛ばすだけ)。

using System;
using System.Collections.Generic;
using System.Linq;

namespace KakiniwaYmm4Import
{
    public class PackLayers
    {
        /// <summary>レイヤー番号の上限(書き庭側の MAX_PACK_LAYER と同じ)</summary>
        public const int MaxLayer = 99;

        /// <summary>perCharacter / single / custom。知らない値は perCharacter</summary>
        public string? Voice { get; set; }
        /// <summary>voice が custom のとき: キャラid → 番号</summary>
        public Dictionary<string, int>? VoiceLayers { get; set; }
        /// <summary>素材の種類(bg/bgm/se/stage/prop-slot/effect/window)→ 番号</summary>
        public Dictionary<string, int>? Kinds { get; set; }

        /// <summary>セリフの置き方(正規化済み)。</summary>
        public string VoiceMode() => VoiceLayerMode.Normalize(Voice);

        /// <summary>範囲内(0〜MaxLayer)の番号だけを残したコピー。壊れたパックで窓が壊れないように。</summary>
        public static Dictionary<string, int> Clean(Dictionary<string, int>? src, IEnumerable<string>? allowedKeys = null)
        {
            var d = new Dictionary<string, int>();
            if (src == null) return d;
            var allowed = allowedKeys == null ? null : new HashSet<string>(allowedKeys);
            foreach (var kv in src)
            {
                if (kv.Value < 0 || kv.Value > MaxLayer) continue;
                if (allowed != null && !allowed.Contains(kv.Key)) continue;
                d[kv.Key] = kv.Value;
            }
            return d;
        }

        public Dictionary<string, int> CleanVoiceLayers() => Clean(VoiceLayers);

        /// <summary>知っている種類(LanePlan.FixableKinds)だけを残す。</summary>
        public Dictionary<string, int> CleanKinds() => Clean(Kinds, LanePlan.FixableKinds);
    }
}
