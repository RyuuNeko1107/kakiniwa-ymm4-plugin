// セリフのボイスを、配置の前にまとめて並列で作る(公式の台本機能と同じ流れ)。
//
// 公式の台本(ScenarioEditorViewModel.AddToTimelineAsync)は、全セリフのボイスを
// Parallel.ForEachAsync(同時数 = Environment.ProcessorCount)で先に作り、作り終えてから
// 順にタイムラインへ並べる。以前の取り込みは1行ずつ作るのを待ってから次へ進んでいた。
//
// ここに置くのは YMM4 に依存しない部分だけ(テスト対象):
//   - 同時数の決め方(エンジンの種類ごとの表)
//   - 並列で走らせて、結果をジョブの順番どおりの配列に入れる(失敗はその行だけ null)
// YMM4 の型に触る部分(ボイスを実際に作る・キャラからエンジンを読む)は Importer 側。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KakiniwaYmm4Import
{
    /// <summary>ボイスを作る同時数の決め方。</summary>
    internal static class VoiceConcurrency
    {
        /// <summary>表の値「CPU のコア数ぶん」(公式の台本と同じ)。</summary>
        internal const int Cpu = -1;

        /// <summary>表の値「先に作らない」。配置のループの中で、呼び出し元(UI)のスレッドで従来どおり1本ずつ作る。</summary>
        internal const int NotPrefetched = 0;

        /// <summary>SAPI5(64bit)で Microsoft 以外の声(CeVIO・VOICEROID などの SAPI 版)に付ける印。</summary>
        internal const string ExternalSapiSuffix = "#external";

        /// <summary>エンジン(IVoiceSpeaker の実装の型の完全名)ごとの同時数。
        /// 根拠は YMM4 v4.56.1 の逆コンパイル(docs/ymm4-bridge.md の表)。
        /// ★表に無いエンジン(利用者が入れた外部の音声プラグインなど)は「先に作らない」。中身が分からないものを
        ///   並べて、出力の取り違えや相手側の回数制限・課金を招くよりは、従来どおり UI のスレッドで1本ずつ作る。
        ///   別スレッドで作ると動かないもの(下の SAPI の外部の声)が実際にあったので、1本ずつでも別スレッドには出さない。</summary>
        internal static readonly IReadOnlyDictionary<string, int> Table = new Dictionary<string, int>
        {
            // ── 先にまとめて並べて作る(実機で速くなり、並びも1行ずつと同じだったもの)──
            // AquesTalk1/2/10: 32bit の補助プロセス(Win32Service)へパイプで頼む。補助プロセスは
            // ProcessorCount 本のパイプを並行で受け、合成の関数呼び出しにロックは無い
            // (YMM4 自身もプロジェクトを開くとき ProcessorCount 並列でこの経路を呼ぶ)。
            // 実測: 取り込み全体で 30行 10.7秒 → 1.7秒、200行 64.6秒 → 5.8秒(位置と長さは全件一致)
            // 合成は YMM4 の外の補助プロセスで動くので、万一そこで落ちても YMM4 本体(利用者のプロジェクト)は巻き込まない
            ["YukkuriMovieMaker.Voice.AquesTalk1VoiceSpeaker"] = Cpu,
            ["YukkuriMovieMaker.Voice.AquesTalk2VoiceSpeaker"] = Cpu,
            ["YukkuriMovieMaker.Voice.AquesTalk10VoiceSpeaker"] = Cpu,
            // ── ここから下は「先に作らない」(配置のループで、UI のスレッドで従来どおり1本ずつ作る)──
            // ★SAPI5 の Windows の声(Haruka・Ayumi など。Windows 11 では OneCore の合成エンジンが受け持つ)は、
            //   30行では 11.7秒 → 1.4秒と速くなったが、200行を 16 本並べたところ YMM4 ごと落ちた
            //   (2026-10-22・MSTTSEngine_OneCore.dll の 0xc0000409)。利用者のプロジェクトを失いうるので並べない。
            //   YMM4 の公式の台本機能もコア数ぶん並べるので、同じ落ち方をしうる
            ["YukkuriMovieMaker.Voice.SAPIAnyCPUVoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Voice.SAPIOneCoreVoiceSpeaker"] = NotPrefetched,
            // Microsoft 以外の SAPI の声(CeVIO AI の SAPI 版で実測)は UI 以外のスレッドで作ると音声ができない
            // (1本目から長さ 0 になり、従来の追加方式へ落ちて 28秒 → 43秒と遅くなった)
            ["YukkuriMovieMaker.Voice.SAPIAnyCPUVoiceSpeaker" + ExternalSapiSuffix] = NotPrefetched,
            // 録音した音声: ファイルを写すだけで速さは変わらない。別スレッドでの動作は未確認
            ["YukkuriMovieMaker.Plugin.Community.Voice.Recording.RecordedVoiceSpeaker"] = NotPrefetched,

            // YMM4 が全呼び出しを1本ずつに絞っている(static な SemaphoreSlim(1)・SAPI5 32bit は補助プロセスの
            // UI スレッドで1本ずつ)ので、先に作っても速くならない。実測でも CeVIO AI は 13.6 秒 → 14.2 秒で差が無かった。
            // ★VOICEVOX 互換(AivisSpeech などを含む)は UI 以外のスレッドで作ると音声ができなかった(2026-10-22 実測)。
            //   速くならないうえに壊れうるので、このグループは全部、先に作らない
            ["YukkuriMovieMaker.Voice.SAPIWin32VoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Voice.CeVIOVoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Voice.CeVIOAIVoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Voice.AIVOICEVoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Voice.VOICEPEAKVoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Voice.VOICEVOXVoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Voice.COEIROINK2VoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Voice.TALQuVoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Voice.YomiteProgramVoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Voice.StyleBertVits2.StyleBertVits2Speaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Plugin.Community.Voice.PiperPlus.PiperPlusVoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Plugin.Community.Voice.IrodoriTTS.IrodoriTTSVoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Plugin.Community.Voice.VoiSonaTalk.VoiSonaTalkVoiceSpeaker"] = NotPrefetched,
            // オンライン(同じく YMM4 が1本ずつに絞り、回数の間隔も空けている)
            ["YukkuriMovieMaker.Voice.AmazonPollyVoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Voice.GoogleTTSVoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Voice.MicrosoftAzureTTSVoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Voice.CoeFontVoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Voice.OpenAI.OpenAIVoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Plugin.Community.Voice.GeminiTTS.GeminiTTSVoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Plugin.Community.Voice.GrokTTS.GrokTTSVoiceSpeaker"] = NotPrefetched,
            // オンラインで YMM4 側の守りが無い/薄い(ElevenLabs は2本まで・Kotodama/Livetoon はロックも間隔も無い・
            // Aivis Cloud は従量課金のときだけロックが外れる)。課金と相手の回数制限があり、別スレッドでの動作も未確認
            ["YukkuriMovieMaker.Plugin.Community.Voice.ElevenLabs.ElevenLabsVoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Plugin.Community.Voice.Kotodama.KotodamaVoiceSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Plugin.Community.Voice.LivetoonTTS.LivetoonTTSSpeaker"] = NotPrefetched,
            ["YukkuriMovieMaker.Plugin.Community.Voice.AivisCloudAPI.AivisCloudAPIVoiceSpeaker"] = NotPrefetched,
            // 利用者が登録した外部コマンド。中で何をするか(固定の一時ファイル・別アプリの操作)が分からない
            ["YukkuriMovieMaker.Voice.CommandLineVoiceSpeaker"] = NotPrefetched,
            // 声なし
            ["YukkuriMovieMaker.Voice.NoneVoiceSpeaker"] = NotPrefetched,
        };

        /// <summary>設定の値。0=自動(表に従う・既定) / 1=1行ずつ(従来) / 2以上=同時数の上限</summary>
        internal const int Auto = 0;

        /// <summary>エンジン1種類の同時数。表の値を、コア数と設定の上限で抑える。
        /// 0 は「先に作らない」(表に無いエンジン・読めなかったときも 0)。</summary>
        internal static int LimitFor(string? speakerTypeName, int processorCount, int configured)
        {
            var cpu = Math.Max(1, processorCount);
            if (configured == 1) return 1;
            int table;
            if (string.IsNullOrEmpty(speakerTypeName) || !Table.TryGetValue(speakerTypeName!, out table)) table = NotPrefetched;
            if (table == NotPrefetched) return NotPrefetched;
            var n = table == Cpu ? cpu : table;
            if (configured >= 2) n = Math.Min(n, configured);
            return Math.Max(1, Math.Min(n, cpu));
        }

        /// <summary>全体の同時数(エンジンをまたいだ上限)。自動ならコア数(公式と同じ)。</summary>
        internal static int GlobalLimit(int processorCount, int configured)
        {
            var cpu = Math.Max(1, processorCount);
            if (configured == 1) return 1;
            return configured >= 2 ? Math.Min(configured, cpu) : cpu;
        }

        /// <summary>SAPI5(64bit)の声の名前から、表を引くときのエンジンの名前を決める。
        /// Windows に入っている Microsoft の声だけを「並べてよい SAPI5」とし、それ以外は印を付けて分ける。</summary>
        internal static string SapiEngineKey(string typeName, string? voiceName)
            => typeName == "YukkuriMovieMaker.Voice.SAPIAnyCPUVoiceSpeaker"
               && !(voiceName ?? "").StartsWith("Microsoft ", StringComparison.Ordinal)
                ? typeName + ExternalSapiSuffix : typeName;

        /// <summary>先にまとめて作るか。1行ずつ(1)・従来の追加方式(LegacyVoicePath)なら作らない。</summary>
        internal static bool UsePrefetch(int configured, bool legacyVoicePath)
            => !legacyVoicePath && configured != 1;
    }

    /// <summary>並列で作った結果。Items と Errors はジョブと同じ順番。</summary>
    internal sealed class PrefetchResult<T> where T : class
    {
        public T?[] Items = Array.Empty<T?>();
        public Exception?[] Errors = Array.Empty<Exception?>();
        /// <summary>先頭の1件(試し)で作れなかった。このときは残りを走らせていない(全部 null)。</summary>
        public bool ProbeFailed;
        /// <summary>実際に走らせた件数(試しを含む)</summary>
        public int Started;
    }

    /// <summary>配置のループで、先に作ったボイスをどう扱うか。</summary>
    internal enum PrefetchUse
    {
        /// <summary>先に作っていない(対象外・試しで止めたので走らせていない)→ 今までどおり</summary>
        NotPrefetched,
        /// <summary>先に作ったときとキャラ・本文・読みが違う → 使わずに今までどおり作る</summary>
        Mismatch,
        /// <summary>作れている → そのまま使う</summary>
        UseItem,
        /// <summary>作れなかった → この行だけ今までの経路(UI のスレッドで1本ずつ)で作り直す。
        /// 試しの1本目で作れなかったときも同じ(別スレッドでだけ作れない声があるため。今までの経路でも
        /// 作れなければ、そこで従来どおり本命の作り方を諦める)</summary>
        RetryOneByOne,
    }

    internal static class VoicePrefetch
    {
        /// <summary>先に作った結果の扱い(純ロジック)。</summary>
        internal static PrefetchUse Decide(bool ran, bool keyMatches, bool hasItem)
        {
            if (!ran) return PrefetchUse.NotPrefetched;
            if (!keyMatches) return PrefetchUse.Mismatch;
            if (hasItem) return PrefetchUse.UseItem;
            return PrefetchUse.RetryOneByOne;
        }

        /// <summary>ジョブを並列で走らせ、結果をジョブの順番どおりに返す。
        /// - 同じグループ(エンジン)の中は limitOf(グループ) 本まで、全体では globalLimit 本まで
        /// - 1件の例外や null は、その行だけ null にして他を巻き込まない
        /// - checkpoint(済んだ数, 全数) を、次の1件を始める前に毎回呼ぶ。一時停止ならそこで待ち、
        ///   停止なら例外が出る。その例外は、走っている分が終わるのを待ってから外へ投げる
        ///   (走りかけを置き去りにしない)
        /// - probeFirst なら先頭の1件だけを先に作り、作れなければ残りは走らせない
        ///   (版の違いなどで本命の作り方が壊れているとき、全行ぶんの待ちを払わないため)</summary>
        internal static async Task<PrefetchResult<T>> RunAsync<TJob, T>(
            IReadOnlyList<TJob> jobs,
            Func<TJob, string> groupOf,
            Func<string, int> limitOf,
            int globalLimit,
            Func<TJob, Task<T?>> work,
            Func<int, int, Task> checkpoint,
            bool probeFirst,
            Action<int, int>? onDone = null) where T : class
        {
            var n = jobs.Count;
            var r = new PrefetchResult<T> { Items = new T?[n], Errors = new Exception?[n] };
            if (n == 0) return r;
            var done = 0;
            var started = 0;

            Func<int, Task> runOne = async i =>
            {
                try { r.Items[i] = await work(jobs[i]); }
                catch (Exception ex) { r.Errors[i] = ex; r.Items[i] = null; }
            };
            // 1件終わるたびに(済んだ数, 全数)を知らせる(進捗バー・残り時間の目安)。別スレッドから呼ぶ
            Action<int> notify = d => { try { onDone?.Invoke(d, n); } catch { /* swallow-ok: 表示の失敗で作成を止めない */ } };

            var first = 0;
            if (probeFirst)
            {
                await checkpoint(0, n);
                started = 1;
                // ★試しの1件も他と同じくスレッドプールで走らせる(中で同期的に合成するエンジンが
                //   呼び出し元=UI スレッドを塞がないように)
                await Task.Run(() => runOne(0));
                done = 1;
                notify(1);
                if (r.Items[0] == null)
                {
                    r.ProbeFailed = true;
                    r.Started = started;
                    return r;
                }
                first = 1;
            }

            var global = new SemaphoreSlim(Math.Max(1, globalLimit));
            var groups = new List<KeyValuePair<string, List<int>>>();
            var groupIndex = new Dictionary<string, int>();
            for (var i = first; i < n; i++)
            {
                var g = groupOf(jobs[i]) ?? "";
                int gi;
                if (!groupIndex.TryGetValue(g, out gi))
                {
                    gi = groups.Count;
                    groupIndex[g] = gi;
                    groups.Add(new KeyValuePair<string, List<int>>(g, new List<int>()));
                }
                groups[gi].Value.Add(i);
            }

            Func<string, List<int>, Task> groupLoop = async (g, idx) =>
            {
                var gs = new SemaphoreSlim(Math.Max(1, limitOf(g)));
                var inflight = new List<Task>();
                try
                {
                    foreach (var i in idx)
                    {
                        await gs.WaitAsync();
                        try
                        {
                            await checkpoint(Volatile.Read(ref done), n);
                            await global.WaitAsync();
                        }
                        catch { gs.Release(); throw; }
                        Interlocked.Increment(ref started);
                        var ii = i;
                        inflight.Add(Task.Run(async () =>
                        {
                            try { await runOne(ii); }
                            finally
                            {
                                var d = Interlocked.Increment(ref done);
                                global.Release();
                                gs.Release();
                                notify(d);
                            }
                        }));
                    }
                }
                finally
                {
                    // 停止で抜けるときも、走っている分は終わるまで待つ
                    await Task.WhenAll(inflight);
                }
            };

            var loops = groups.Select(kv => groupLoop(kv.Key, kv.Value)).ToList();
            try
            {
                await Task.WhenAll(loops);
            }
            finally
            {
                r.Started = Volatile.Read(ref started);
            }
            return r;
        }
    }
}
