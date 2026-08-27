// 取り込み(タイムラインへの配置)の一時停止・停止。
//
// 長い台本(数百〜数千のボイス)を取り込むと、AddItems → ボイス生成待ち → 再配置 と
// 続く間、利用者は待つしかなく、途中でやめる手段が無かった(2026-08-27 要望)。
//
// 仕組みはチェックポイント方式。時間のかかる処理の合間(100件ごとの AddItems・
// ボイス生成待ちのループ・再配置の前)で CheckpointAsync を呼び、
//   - 一時停止中なら、再開されるまでそこで待つ(UI スレッドは塞がない)
//   - 停止されていたら ImportStoppedException を投げて、そこで取り込みを終える
// 停止しても「ここまで配置したもの」はタイムラインに残る(元に戻す操作は確認できて
// いないため、勝手に消さない。利用者には Ctrl+Z か保存せず開き直すことを案内する)。
//
// ★YMM4 が内部で進めるボイスの音声合成そのものは止められない(AddItems した時点で
//   YMM4 側の仕事)。止められるのは、まだ AddItems していない分と、こちらの待ち・再配置。
//   だから AddItems を小分けにしている(一括だと「停止」を押しても全部置かれる)。

using System;
using System.Threading.Tasks;

namespace KakiniwaYmm4Import
{
    /// <summary>「停止」で取り込みを打ち切ったことを表す(失敗ではない)。</summary>
    public sealed class ImportStoppedException : Exception
    {
        public ImportStoppedException(string message) : base(message) { }
    }

    public sealed class ImportControl
    {
        volatile bool paused;
        volatile bool stopped;

        public bool IsPaused => paused;
        public bool IsStopped => stopped;

        /// <summary>一時停止中の待ちの刻み(ms)。テストで短くする。</summary>
        public int PollMs { get; set; } = 100;

        public void Pause() { paused = true; }
        public void Resume() { paused = false; }
        /// <summary>停止。一時停止中でも効く(再開を待たずに抜ける)。</summary>
        public void Stop() { stopped = true; }

        /// <summary>時間のかかる処理の合間で呼ぶ。一時停止中は再開まで待ち、停止なら例外で抜ける。</summary>
        public async Task CheckpointAsync(string where)
        {
            while (paused && !stopped) await Task.Delay(PollMs);
            if (stopped) throw new ImportStoppedException("停止しました(" + where + ")");
        }
    }
}
