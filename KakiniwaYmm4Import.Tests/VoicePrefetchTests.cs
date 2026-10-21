// セリフのボイスを先にまとめて並列で作る部分(VoicePrefetch.cs)のテスト。
// YMM4 には触らない: 「作る」処理は偽物(遅延・例外・null を返すだけ)に差し替えて、
// 順番の保ち方・同時数・失敗した行だけの扱い・停止/一時停止を見る。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace KakiniwaYmm4Import.Tests
{
    public class VoiceConcurrencyTests
    {
        const string Sapi = "YukkuriMovieMaker.Voice.SAPIAnyCPUVoiceSpeaker";
        const string AqTk1 = "YukkuriMovieMaker.Voice.AquesTalk1VoiceSpeaker";
        const string CeVioAi = "YukkuriMovieMaker.Voice.CeVIOAIVoiceSpeaker";
        const string Voicevox = "YukkuriMovieMaker.Voice.VOICEVOXVoiceSpeaker";
        const string Eleven = "YukkuriMovieMaker.Plugin.Community.Voice.ElevenLabs.ElevenLabsVoiceSpeaker";
        const string Kotodama = "YukkuriMovieMaker.Plugin.Community.Voice.Kotodama.KotodamaVoiceSpeaker";
        const string CmdLine = "YukkuriMovieMaker.Voice.CommandLineVoiceSpeaker";

        [Theory]
        // 自動(0): PC の中で完結しロックの無いもの=コア数、それ以外は先に作らない(0)
        [InlineData(Sapi, 16, 0, 0)]     // Windows の声: 200行を並べて YMM4 ごと落ちた(実測)
        [InlineData(AqTk1, 16, 0, 16)]
        [InlineData(CeVioAi, 16, 0, 0)]  // YMM4 が1本ずつに絞る=速くならない
        [InlineData(Voicevox, 16, 0, 0)] // UI 以外のスレッドで作れなかった(実測)
        [InlineData(Eleven, 16, 0, 0)]   // オンライン・課金
        [InlineData(Kotodama, 16, 0, 0)]
        // 外部コマンド・知らないエンジン(外部の音声プラグイン)・読めなかったときは「先に作らない」(0)
        [InlineData(CmdLine, 16, 0, 0)]
        [InlineData("Someone.Plugin.MyVoiceSpeaker", 16, 0, 0)]
        [InlineData("", 16, 0, 0)]
        [InlineData(null, 16, 0, 0)]
        // SAPI5 の Microsoft 以外の声(CeVIO などの SAPI 版)は先に作らない
        [InlineData(Sapi + "#external", 16, 0, 0)]
        [InlineData(Sapi + "#external", 16, 4, 0)]
        // 設定の上限で抑える。表より大きくはしない
        [InlineData(AqTk1, 16, 4, 4)]
        [InlineData(CeVioAi, 16, 4, 0)]
        [InlineData(AqTk1, 16, 8, 8)]
        // 1=1行ずつ
        [InlineData(AqTk1, 16, 1, 1)]
        // コア数を超えない・コア数が読めなくても 1 未満にしない
        [InlineData(AqTk1, 1, 0, 1)]
        [InlineData(AqTk1, 0, 0, 1)]
        [InlineData(AqTk1, 4, 8, 4)]
        public void エンジンごとの同時数(string? engine, int cpu, int configured, int expected)
        {
            Assert.Equal(expected, VoiceConcurrency.LimitFor(engine, cpu, configured));
        }

        [Theory]
        [InlineData(16, 0, 16)]
        [InlineData(16, 1, 1)]
        [InlineData(16, 4, 4)]
        [InlineData(2, 8, 2)]
        [InlineData(0, 0, 1)]
        public void 全体の同時数(int cpu, int configured, int expected)
        {
            Assert.Equal(expected, VoiceConcurrency.GlobalLimit(cpu, configured));
        }

        [Theory]
        [InlineData(0, false, true)]
        [InlineData(4, false, true)]
        [InlineData(1, false, false)] // 1行ずつ
        [InlineData(0, true, false)]  // 従来の追加方式(LegacyVoicePath)では先に作らない
        public void 先にまとめて作るか(int configured, bool legacy, bool expected)
        {
            Assert.Equal(expected, VoiceConcurrency.UsePrefetch(configured, legacy));
        }

        [Fact]
        public void 表の値はすべて1以上かコア数か先に作らないの印()
        {
            Assert.All(VoiceConcurrency.Table.Values, v => Assert.True(v >= 1 || v == VoiceConcurrency.Cpu || v == VoiceConcurrency.NotPrefetched));
        }

        [Theory]
        [InlineData(Sapi, "Microsoft Haruka Desktop", Sapi)]
        [InlineData(Sapi, "Microsoft Zira Desktop", Sapi)]
        [InlineData(Sapi, "CeVIO-AI-IA", Sapi + "#external")]
        [InlineData(Sapi, null, Sapi + "#external")]
        [InlineData(Sapi, "microsoft haruka", Sapi + "#external")] // 大小を変えた名前は Microsoft の声と見なさない
        [InlineData("YukkuriMovieMaker.Voice.SAPIOneCoreVoiceSpeaker", "Microsoft Ayumi", "YukkuriMovieMaker.Voice.SAPIOneCoreVoiceSpeaker")]
        [InlineData(AqTk1, "f1", AqTk1)]
        public void SAPI5は声ごとに分ける(string type, string? voice, string expected)
        {
            Assert.Equal(expected, VoiceConcurrency.SapiEngineKey(type, voice));
        }
    }

    public class PrefetchDecideTests
    {
        [Theory]
        [InlineData(false, true, true, "NotPrefetched")]
        [InlineData(true, false, true, "Mismatch")]   // 作れていても食い違えば使わない
        [InlineData(true, true, true, "UseItem")]
        // 作れなかった行(試しの1本目を含む)は、その行だけ今までの経路(UI のスレッド)で作り直す。
        // 別スレッドでだけ作れない声があったので、試しの失敗で本命の作り方を諦めない
        [InlineData(true, true, false, "RetryOneByOne")]
        public void 先に作った結果の扱い(bool ran, bool keyMatches, bool hasItem, string expected)
        {
            Assert.Equal(expected, VoicePrefetch.Decide(ran, keyMatches, hasItem).ToString());
        }
    }

    public class VoicePrefetchRunTests
    {
        sealed class Job
        {
            public int No;
            public string Group = "g";
            public int DelayMs;
            public bool Throw;
            public bool ReturnNull;
        }

        /// <summary>同時に走っている数の最大を数える(グループごと・全体)</summary>
        sealed class Meter
        {
            int now;
            public int Max;
            readonly Dictionary<string, int> gNow = new Dictionary<string, int>();
            public readonly Dictionary<string, int> GMax = new Dictionary<string, int>();
            public void Enter(string g)
            {
                lock (this)
                {
                    now++; Max = Math.Max(Max, now);
                    gNow[g] = (gNow.TryGetValue(g, out var c) ? c : 0) + 1;
                    GMax[g] = Math.Max(GMax.TryGetValue(g, out var m) ? m : 0, gNow[g]);
                }
            }
            public void Leave(string g) { lock (this) { now--; gNow[g]--; } }
            public int Now { get { lock (this) return now; } }
        }

        static Func<Job, Task<string?>> Work(Meter m, List<int>? startedOrder = null) => async j =>
        {
            m.Enter(j.Group);
            if (startedOrder != null) lock (startedOrder) startedOrder.Add(j.No);
            try
            {
                await Task.Delay(j.DelayMs);
                if (j.Throw) throw new InvalidOperationException("合成に失敗 " + j.No);
                return j.ReturnNull ? null : "voice" + j.No;
            }
            finally { m.Leave(j.Group); }
        };

        static readonly Func<int, int, Task> NoCheck = (_, __) => Task.CompletedTask;

        [Fact]
        public async Task 終わる順がばらばらでも結果はジョブの順番どおり()
        {
            // 後ろの行ほど早く終わる
            var jobs = Enumerable.Range(0, 12).Select(i => new Job { No = i, DelayMs = (12 - i) * 15 }).ToList();
            var r = await VoicePrefetch.RunAsync(jobs, j => j.Group, _ => 8, 8, Work(new Meter()), NoCheck, probeFirst: true);
            Assert.Equal(Enumerable.Range(0, 12).Select(i => "voice" + i), r.Items);
            Assert.False(r.ProbeFailed);
            Assert.Equal(12, r.Started);
        }

        [Fact]
        public async Task エンジンごとの同時数と全体の同時数を超えない_かつ実際に並べる()
        {
            var jobs = new List<Job>();
            for (var i = 0; i < 12; i++) jobs.Add(new Job { No = i, Group = "sapi", DelayMs = 40 });
            for (var i = 12; i < 18; i++) jobs.Add(new Job { No = i, Group = "cevio", DelayMs = 40 });
            var m = new Meter();
            var r = await VoicePrefetch.RunAsync(jobs, j => j.Group,
                g => g == "sapi" ? 4 : 1, 5, Work(m), NoCheck, probeFirst: false);
            Assert.All(r.Items, x => Assert.NotNull(x));
            Assert.Equal(4, m.GMax["sapi"]);  // 4 まで並ぶ(並ばなければ 1 のまま)
            Assert.Equal(1, m.GMax["cevio"]); // 1本ずつのエンジンは重ねない
            Assert.Equal(5, m.Max);           // エンジンをまたいで並ぶ

            // 全体の上限(3)は、エンジンごとの和(4+1)より小さければそちらで抑える
            var m2 = new Meter();
            await VoicePrefetch.RunAsync(jobs, j => j.Group, g => g == "sapi" ? 4 : 1, 3, Work(m2), NoCheck, probeFirst: false);
            Assert.Equal(3, m2.Max);
        }

        [Fact]
        public async Task 中で同期的に作るエンジンでも並ぶ_呼び出し元を塞がない()
        {
            // SAPI5(64bit)は CreateVoiceAsync の中で同期的に合成する。そのまま await すると1本ずつになる
            var m = new Meter();
            var jobs = Enumerable.Range(0, 6).Select(i => new Job { No = i }).ToList();
            Func<Job, Task<string?>> syncWork = j =>
            {
                m.Enter(j.Group);
                try { Thread.Sleep(60); return Task.FromResult<string?>("v" + j.No); }
                finally { m.Leave(j.Group); }
            };
            var r = await VoicePrefetch.RunAsync(jobs, j => j.Group, _ => 6, 6, syncWork, NoCheck, probeFirst: true);
            Assert.All(r.Items, x => Assert.NotNull(x));
            Assert.True(m.Max >= 2, "同期的な作り方だと並んでいない: " + m.Max);
        }

        [Fact]
        public async Task 失敗した行だけ空になり_ほかは巻き込まない()
        {
            var jobs = Enumerable.Range(0, 8).Select(i => new Job { No = i, DelayMs = 10 }).ToList();
            jobs[3].Throw = true;
            jobs[5].ReturnNull = true;
            var r = await VoicePrefetch.RunAsync(jobs, j => j.Group, _ => 4, 4, Work(new Meter()), NoCheck, probeFirst: true);
            Assert.Null(r.Items[3]);
            Assert.IsType<InvalidOperationException>(r.Errors[3]);
            Assert.Null(r.Items[5]);
            Assert.Null(r.Errors[5]);
            foreach (var i in new[] { 0, 1, 2, 4, 6, 7 }) Assert.Equal("voice" + i, r.Items[i]);
            Assert.False(r.ProbeFailed);
        }

        [Fact]
        public async Task 試しの1本目で作れなければ残りは走らせない()
        {
            var started = new List<int>();
            var jobs = Enumerable.Range(0, 6).Select(i => new Job { No = i }).ToList();
            jobs[0].ReturnNull = true;
            var r = await VoicePrefetch.RunAsync(jobs, j => j.Group, _ => 4, 4, Work(new Meter(), started), NoCheck, probeFirst: true);
            Assert.True(r.ProbeFailed);
            Assert.Equal(new[] { 0 }, started);
            Assert.Equal(1, r.Started);
            Assert.All(r.Items, x => Assert.Null(x));
        }

        [Fact]
        public async Task 停止すると新しく始めず_走っている分を待ってから例外で抜ける()
        {
            var c = new ImportControl { PollMs = 5 };
            var started = new List<int>();
            var m = new Meter();
            var jobs = Enumerable.Range(0, 20).Select(i => new Job { No = i, DelayMs = 60 }).ToList();
            Func<int, int, Task> check = async (d, t) =>
            {
                await c.CheckpointAsync("作成 " + d + "/" + t);
            };
            var run = VoicePrefetch.RunAsync(jobs, j => j.Group, _ => 3, 3, Work(m, started), check, probeFirst: true);
            await Task.Delay(100); // 試し + 最初の数件が走っている頃
            c.Stop();
            await Assert.ThrowsAsync<ImportStoppedException>(() => run);
            int n;
            lock (started) n = started.Count;
            Assert.True(n < 20, "停止したのに全部始めた");
            // 抜けた時点で走りかけは残っていない
            Assert.Equal(0, m.Now);
            await Task.Delay(150);
            lock (started) Assert.Equal(n, started.Count); // 抜けたあとに始まったものは無い
        }

        [Fact]
        public async Task 一時停止中は新しく始めない_再開すると続きを作る()
        {
            var c = new ImportControl { PollMs = 5 };
            var started = new List<int>();
            var jobs = Enumerable.Range(0, 10).Select(i => new Job { No = i, DelayMs = 20 }).ToList();
            Func<int, int, Task> check = (d, t) => c.CheckpointAsync("作成");
            c.Pause();
            var run = VoicePrefetch.RunAsync(jobs, j => j.Group, _ => 2, 2, Work(new Meter(), started), check, probeFirst: true);
            await Task.Delay(120);
            lock (started) Assert.Empty(started);
            c.Resume();
            var r = await run;
            Assert.Equal(Enumerable.Range(0, 10).Select(i => "voice" + i), r.Items);
        }

        [Fact]
        public async Task ジョブが無ければ何もしない()
        {
            var r = await VoicePrefetch.RunAsync(new List<Job>(), j => j.Group, _ => 4, 4, Work(new Meter()), NoCheck, probeFirst: true);
            Assert.Empty(r.Items);
            Assert.Equal(0, r.Started);
        }
    }

    public class StageTimerTests
    {
        [Fact]
        public void 長い順に並び_同じ名前は足し合わせ_付け替えは元から引く()
        {
            var t = new StageTimer();
            t.Add("準備", 5);
            t.Add("ループ", 100);
            t.Add("追加", 30);
            t.Add("準備", 10);
            t.Move("ループ", "音声", 70);
            t.Move("ループ", "音声", 1000); // 元より多くは引かない
            Assert.Equal(new[] { "音声", "追加", "準備", "ループ" }, t.Sorted().Select(kv => kv.Key));
            Assert.Equal(100, t.Of("音声"));
            Assert.Equal(0, t.Of("ループ"));
            Assert.Equal(15, t.Of("準備"));
            Assert.Equal(0, t.Of("無い段階"));
        }
    }

    public class CanSkipVoiceWaitTests
    {
        [Theory]
        [InlineData(30, 30, true)]   // 全部が本命の手順で作られ尺も確定 → 待たない
        [InlineData(30, 29, false)]  // 1本でも従来の追加方式・未確定があれば待つ
        [InlineData(0, 0, false)]    // 今回置いたボイスが無い(待つ処理の側で即抜ける)
        public void 生成待ちを省いてよいか(int newVoices, int officialReady, bool expected)
        {
            Assert.Equal(expected, Importer.CanSkipVoiceWait(newVoices, officialReady));
        }
    }

    public class ImportProgressMathTests
    {
        [Fact]
        public void 全体の進み具合は段階の重みで積み上がり_単調に増える()
        {
            Assert.Equal(0.0, ImportProgressMath.Overall(ImportStage.Prepare, 0), 6);
            Assert.Equal(0.01, ImportProgressMath.Overall(ImportStage.Voices, 0), 6);   // 準備(1)の後
            Assert.Equal(0.45, ImportProgressMath.Overall(ImportStage.Voices, 0.5), 6); // 1 + 88×0.5
            Assert.Equal(0.89, ImportProgressMath.Overall(ImportStage.Place, 0), 6);
            Assert.Equal(0.98, ImportProgressMath.Overall(ImportStage.Relayout, 0), 6);
            Assert.Equal(1.0, ImportProgressMath.Overall(ImportStage.Done, 0), 6);
            // 段階内の値が壊れていても 0〜1 に収める
            Assert.Equal(0.89, ImportProgressMath.Overall(ImportStage.Voices, 5), 6);
            Assert.Equal(0.01, ImportProgressMath.Overall(ImportStage.Voices, double.NaN), 6);
            var seq = new[] { ImportStage.Prepare, ImportStage.Voices, ImportStage.Place, ImportStage.Wait, ImportStage.Relayout, ImportStage.Done };
            var last = -1.0;
            foreach (var st in seq)
                foreach (var f in new[] { 0.0, 0.5, 1.0 })
                {
                    var v = ImportProgressMath.Overall(st, f);
                    Assert.True(v >= last, st + " " + f);
                    last = v;
                }
        }

        [Theory]
        [InlineData(2, 10.0, 8, 3, null)]   // まだ 3 件に満たない → 出さない
        [InlineData(3, 6.0, 9, 3, 18.0)]    // 1件 2 秒 × 残り 9 件
        [InlineData(16, 4.0, 14, 16, 3.5)]  // 並列の1巡目(16件)が 4 秒 → 残り 14 件は 3.5 秒
        [InlineData(5, 0.0, 5, 3, null)]    // 時間が測れていない
        [InlineData(10, 5.0, 0, 3, 0.0)]    // 残りなし
        public void 残り時間の目安(int done, double elapsed, int remaining, int minDone, double? expected)
        {
            var r = ImportProgressMath.EstimateRemaining(done, elapsed, remaining, minDone);
            if (expected == null) Assert.Null(r);
            else Assert.Equal(expected.Value, r!.Value, 6);
        }

        [Theory]
        [InlineData(1, 30, 3)]
        [InlineData(16, 200, 16)]
        [InlineData(16, 10, 10)]
        [InlineData(4, 2, 2)]
        [InlineData(1, 0, 1)]
        public void 目安を出し始める件数(int concurrency, int total, int expected)
        {
            Assert.Equal(expected, ImportProgressMath.MinSamples(concurrency, total));
        }

        [Theory]
        [InlineData(0.4, "soon", 0, 0)]
        [InlineData(1.0, "sec", 0, 5)]
        [InlineData(12.0, "sec", 0, 15)]
        [InlineData(57.0, "min", 1, 0)]     // 5秒刻みで切り上げると 60 → 1分
        [InlineData(61.0, "minsec", 1, 10)]
        [InlineData(125.0, "minsec", 2, 10)]
        [InlineData(178.0, "min", 3, 0)]
        [InlineData(601.0, "min", 11, 0)]
        [InlineData(double.NaN, "soon", 0, 0)]
        public void 表示の丸め(double sec, string kind, int minutes, int seconds)
        {
            Assert.Equal((kind, minutes, seconds), ImportProgressMath.RoundRemaining(sec));
        }
    }

    public class PrefetchProgressCallbackTests
    {
        [Fact]
        public async System.Threading.Tasks.Task 一件終わるたびに済んだ数を知らせ_最後は全数()
        {
            var seen = new System.Collections.Concurrent.ConcurrentBag<int>();
            var jobs = Enumerable.Range(0, 8).ToList();
            await VoicePrefetch.RunAsync<int, string>(jobs, _ => "g", _ => 4, 4,
                async j => { await System.Threading.Tasks.Task.Delay(10); return "v" + j; },
                (_, __) => System.Threading.Tasks.Task.CompletedTask, probeFirst: true,
                onDone: (d, t) => { Assert.Equal(8, t); seen.Add(d); });
            Assert.Equal(Enumerable.Range(1, 8), seen.OrderBy(x => x));
        }
    }
}
