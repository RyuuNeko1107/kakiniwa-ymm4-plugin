// 取り込みの進捗バーと「あとどれくらいかかるか」の目安(純ロジック。表示は ImportUi)。
//
// 段階の重みは、実機で測った段階ごとの所要時間の比から決めた(2026-10-22・YMM4 v4.56.1・30行/200行。
// docs/ymm4-bridge.md「並列化の実測」)。どの声でも、時間の大半はボイスの作成で、
// 次がボイスの生成待ち(従来の追加方式のときだけ)、配置・並べ直しは合わせても数%だった。
// 残り時間は「ボイスの作成」だけを、実際に作れた件数の速さから出す。根拠の無い数字は出さない
// (最初の数件が終わるまでは「計算中」)。

using System;

namespace KakiniwaYmm4Import
{
    /// <summary>取り込みの段階(進捗バーの区切り)。</summary>
    public enum ImportStage
    {
        Prepare,
        Voices,
        Place,
        Wait,
        Relayout,
        Done,
    }

    /// <summary>進捗の知らせ1件。Fraction は全体の 0〜1。RemainingSeconds は分かるときだけ。</summary>
    public sealed class ImportProgressReport
    {
        public ImportStage Stage;
        public double Fraction;
        public int Done;
        public int Total;
        public double? RemainingSeconds;
        public double? ElapsedSeconds;
    }

    internal static class ImportProgressMath
    {
        // 段階の重み(合計 100)。測った比(ボイスの作成が 9 割強・生成待ちが 6% 前後・残りが数%)を丸めたもの
        static readonly (ImportStage stage, double weight)[] Weights =
        {
            (ImportStage.Prepare, 1),
            (ImportStage.Voices, 88),
            (ImportStage.Place, 4),
            (ImportStage.Wait, 5),
            (ImportStage.Relayout, 2),
        };

        /// <summary>全体の進み具合(0〜1)。その段階より前の重みの合計 + その段階の重み × 段階内の進み具合。</summary>
        internal static double Overall(ImportStage stage, double stageFraction)
        {
            if (stage == ImportStage.Done) return 1.0;
            var f = double.IsFinite(stageFraction) ? Math.Clamp(stageFraction, 0.0, 1.0) : 0.0;
            double before = 0, total = 0, mine = 0;
            var seen = false;
            foreach (var (s, w) in Weights)
            {
                total += w;
                if (s == stage) { mine = w; seen = true; }
                else if (!seen) before += w;
            }
            return Math.Clamp((before + mine * f) / total, 0.0, 1.0);
        }

        /// <summary>残り時間の目安(秒)。基準点からの実績(できた件数と経った時間)の速さで、残りの件数を割り出す。
        /// 実績が minDone 件に満たない・時間が測れていないうちは null(=出さない)。</summary>
        internal static double? EstimateRemaining(int doneSinceRef, double elapsedSinceRef, int remaining, int minDone)
        {
            if (remaining <= 0) return 0;
            if (doneSinceRef < Math.Max(1, minDone)) return null;
            if (!double.IsFinite(elapsedSinceRef) || elapsedSinceRef <= 0) return null;
            return elapsedSinceRef / doneSinceRef * remaining;
        }

        /// <summary>目安を出し始めるのに要る件数: 並べて作るときは1巡目(同時数ぶん)、1本ずつなら3件。
        /// どちらも全体の件数を超えない。</summary>
        internal static int MinSamples(int concurrency, int total)
            => Math.Max(1, Math.Min(total, Math.Max(3, concurrency)));

        /// <summary>表示の丸め。種類と数を返す(文言は Tr で引く)。
        /// 1秒未満=まもなく / 1分未満=5秒刻みで切り上げ / 10分未満=分と10秒刻み(切り上げ)/ それ以上=分(切り上げ)。</summary>
        internal static (string kind, int minutes, int seconds) RoundRemaining(double sec)
        {
            if (!double.IsFinite(sec) || sec < 1) return ("soon", 0, 0);
            if (sec < 60)
            {
                var s = (int)Math.Ceiling(sec / 5.0) * 5;
                if (s >= 60) return ("min", 1, 0);
                return ("sec", 0, s);
            }
            if (sec < 600)
            {
                var t = (int)Math.Ceiling(sec / 10.0) * 10;
                if (t % 60 == 0) return ("min", t / 60, 0);
                return ("minsec", t / 60, t % 60);
            }
            return ("min", (int)Math.Ceiling(sec / 60.0), 0);
        }

        /// <summary>経過時間の表示(秒・1桁の小数まで)。</summary>
        internal static string FormatElapsed(double sec)
            => (double.IsFinite(sec) && sec > 0 ? sec : 0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
    }
}
