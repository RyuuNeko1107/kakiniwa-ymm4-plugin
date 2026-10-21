// 取り込みの段階ごとの所要時間を測る(ログの最後に長い順で出す)。
// 「遅い」という報告が来たとき、どこが遅いかをログだけで分かるように。並列化などの前後比較にも使う。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace KakiniwaYmm4Import
{
    internal sealed class StageTimer
    {
        readonly Stopwatch sw = Stopwatch.StartNew();
        long last;
        readonly List<string> order = new List<string>();
        readonly Dictionary<string, long> ms = new Dictionary<string, long>();

        /// <summary>直前の区切りからここまでを name の時間として足す。</summary>
        public void Lap(string name)
        {
            var now = sw.ElapsedMilliseconds;
            Add(name, now - last);
            last = now;
        }

        public void Add(string name, long value)
        {
            if (!ms.ContainsKey(name)) { ms[name] = 0; order.Add(name); }
            ms[name] += Math.Max(0, value);
        }

        /// <summary>from に数えた時間のうち value を to へ付け替える(ループの中の音声の生成を分けるため)。</summary>
        public void Move(string from, string to, long value)
        {
            if (!ms.ContainsKey(from)) return;
            var v = Math.Min(Math.Max(0, value), ms[from]);
            ms[from] -= v;
            Add(to, v);
        }

        public long TotalMs => sw.ElapsedMilliseconds;

        public long Of(string name) => ms.TryGetValue(name, out var v) ? v : 0;

        /// <summary>長い順(同じなら測った順)。0ms の段階も残す(通ったことが分かるように)。</summary>
        public List<KeyValuePair<string, long>> Sorted()
            => order.Select((n, i) => new { n, i })
                .OrderByDescending(x => ms[x.n]).ThenBy(x => x.i)
                .Select(x => new KeyValuePair<string, long>(x.n, ms[x.n])).ToList();
    }
}
