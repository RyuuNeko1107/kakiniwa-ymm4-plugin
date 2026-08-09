// メディアファイルのヘッダ直読み(音声 WAV/MP3/OGG の実尺・画像/PSDのピクセルサイズ)。外部ライブラリ不使用。

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
    public static partial class Importer
    {
        /// <summary>音声ファイルの実尺(秒)。読めなければ null(呼び出し側が既定へフォールバック)。
        /// ★SEの尺が常に1秒固定だと、1秒より長い効果音が途中で切れる(監査)。
        /// WAV=ヘッダから正確に、MP3=Xing/VBRIヘッダか CBR推定、OGG(Vorbis/Opus)=最終ページの
        /// granule から算出。外部ライブラリなしのヘッダ直読み。</summary>
        public static double? AudioDurationSeconds(string path)
        {
            try
            {
                var ext = Path.GetExtension(path).ToLowerInvariant();
                using (var fs = File.OpenRead(path))
                {
                    if (ext == ".wav") return WavDuration(fs);
                    if (ext == ".mp3") return Mp3Duration(fs);
                    if (ext == ".ogg" || ext == ".opus") return OggDuration(fs);
                }
            }
            catch { }
            return null;
        }

        static double? WavDuration(FileStream fs)
        {
            var b = new byte[12];
            if (fs.Read(b, 0, 12) < 12) return null;
            if (b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F') return null;
            uint byteRate = 0;
            // チャンクを順に見て fmt の平均バイトレートと data のサイズを拾う
            var hdr = new byte[8];
            while (fs.Read(hdr, 0, 8) == 8)
            {
                var size = (uint)(hdr[4] | (hdr[5] << 8) | (hdr[6] << 16) | (hdr[7] << 24));
                var id = "" + (char)hdr[0] + (char)hdr[1] + (char)hdr[2] + (char)hdr[3];
                if (id == "fmt ")
                {
                    var f = new byte[Math.Min(size, 24)];
                    if (fs.Read(f, 0, f.Length) < 8) return null;
                    byteRate = (uint)(f[8] | (f[9] << 8) | (f[10] << 16) | (f[11] << 24));
                    fs.Seek(size - f.Length + (size % 2), SeekOrigin.Current);
                }
                else if (id == "data")
                {
                    if (byteRate == 0) return null;
                    return size / (double)byteRate;
                }
                else
                {
                    fs.Seek(size + (size % 2), SeekOrigin.Current);
                }
            }
            return null;
        }

        static readonly int[][] Mp3Bitrates = new[]
        {
            new[] { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 }, // V1 L3
            new[] { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 },     // V2/2.5 L3
        };
        static readonly int[] Mp3Rates = new[] { 44100, 48000, 32000, 22050, 24000, 16000, 11025, 12000, 8000 };

        static double? Mp3Duration(FileStream fs)
        {
            // ID3v2 をスキップ
            var h = new byte[10];
            long start = 0;
            if (fs.Read(h, 0, 10) == 10 && h[0] == 'I' && h[1] == 'D' && h[2] == '3')
                start = 10 + ((h[6] & 0x7F) << 21 | (h[7] & 0x7F) << 14 | (h[8] & 0x7F) << 7 | (h[9] & 0x7F));
            fs.Seek(start, SeekOrigin.Begin);
            // 最初のフレームヘッダを探す(先頭64KBまで)
            var buf = new byte[65536];
            var n = fs.Read(buf, 0, buf.Length);
            for (var i = 0; i + 4 < n; i++)
            {
                if (buf[i] != 0xFF || (buf[i + 1] & 0xE0) != 0xE0) continue;
                var verBits = (buf[i + 1] >> 3) & 3;   // 3=MPEG1, 2=MPEG2, 0=MPEG2.5
                var layerBits = (buf[i + 1] >> 1) & 3; // 1=Layer3
                if (verBits == 1 || layerBits != 1) continue;
                var mpeg1 = verBits == 3;
                var brIdx = (buf[i + 2] >> 4) & 0xF;
                var srIdx = (buf[i + 2] >> 2) & 3;
                if (brIdx == 0 || brIdx == 15 || srIdx == 3) continue;
                var rate = Mp3Rates[(mpeg1 ? 0 : verBits == 2 ? 1 : 2) * 3 + srIdx];
                var samplesPerFrame = mpeg1 ? 1152 : 576;
                // Xing/Info(VBR)ヘッダがあれば総フレーム数から正確に
                var side = mpeg1 ? 36 : 21; // サイドインフォ長+4(モノラル差は Info 位置探索で吸収)
                for (var off = i + 21; off <= i + 36 && off + 8 < n; off++)
                {
                    if ((buf[off] == 'X' && buf[off + 1] == 'i' && buf[off + 2] == 'n' && buf[off + 3] == 'g')
                        || (buf[off] == 'I' && buf[off + 1] == 'n' && buf[off + 2] == 'f' && buf[off + 3] == 'o'))
                    {
                        var flags = (buf[off + 4] << 24) | (buf[off + 5] << 16) | (buf[off + 6] << 8) | buf[off + 7];
                        if ((flags & 1) != 0 && off + 12 < n)
                        {
                            var frames = (buf[off + 8] << 24) | (buf[off + 9] << 16) | (buf[off + 10] << 8) | buf[off + 11];
                            return frames * (double)samplesPerFrame / rate;
                        }
                    }
                }
                _ = side;
                // CBR推定: (ファイル長 − 音声開始位置) ÷ ビットレート
                var kbps = (mpeg1 ? Mp3Bitrates[0] : Mp3Bitrates[1])[brIdx];
                if (kbps <= 0) continue;
                return (fs.Length - start - i) * 8.0 / (kbps * 1000.0);
            }
            return null;
        }

        static double? OggDuration(FileStream fs)
        {
            // 先頭ページから種別とサンプルレートを読む
            var head = new byte[512];
            if (fs.Read(head, 0, head.Length) < 64) return null;
            if (head[0] != 'O' || head[1] != 'g' || head[2] != 'g' || head[3] != 'S') return null;
            long rate = 0;
            var text = System.Text.Encoding.ASCII.GetString(head);
            var vi = text.IndexOf("vorbis", StringComparison.Ordinal);
            var op = text.IndexOf("OpusHead", StringComparison.Ordinal);
            if (op >= 0) rate = 48000; // Opus の granule は常に48kHz換算
            else if (vi >= 0 && vi + 16 < head.Length)
                rate = (uint)(head[vi + 11] | (head[vi + 12] << 8) | (head[vi + 13] << 16) | (head[vi + 14] << 24));
            if (rate <= 0) return null;
            // 末尾から最後の "OggS" ページを探し granulePos(累計サンプル数)を読む
            var tailLen = (int)Math.Min(65536, fs.Length);
            fs.Seek(-tailLen, SeekOrigin.End);
            var tail = new byte[tailLen];
            var read = fs.Read(tail, 0, tailLen);
            for (var i = read - 14; i >= 0; i--)
            {
                if (tail[i] != 'O' || tail[i + 1] != 'g' || tail[i + 2] != 'g' || tail[i + 3] != 'S') continue;
                long g = 0;
                for (var k = 7; k >= 0; k--) g = (g << 8) | tail[i + 6 + k];
                if (g > 0) return g / (double)rate;
            }
            return null;
        }

        /// <summary>画像のピクセルサイズ。PSDはヘッダ直読み、他はWPFデコーダ</summary>
        static Tuple<double, double>? ReadImageSize(string path)
        {
            try
            {
                if (Path.GetExtension(path).Equals(".psd", StringComparison.OrdinalIgnoreCase))
                {
                    using (var fs = File.OpenRead(path))
                    {
                        var header = new byte[26];
                        if (fs.Read(header, 0, 26) < 26) return null;
                        if (header[0] != (byte)'8' || header[1] != (byte)'B'
                            || header[2] != (byte)'P' || header[3] != (byte)'S') return null;
                        double h = (uint)((header[14] << 24) | (header[15] << 16) | (header[16] << 8) | header[17]);
                        double w = (uint)((header[18] << 24) | (header[19] << 16) | (header[20] << 8) | header[21]);
                        return Tuple.Create(w, h);
                    }
                }
                using (var fs = File.OpenRead(path))
                {
                    var frame = System.Windows.Media.Imaging.BitmapFrame.Create(fs,
                        System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                        System.Windows.Media.Imaging.BitmapCacheOption.None);
                    return Tuple.Create((double)frame.PixelWidth, (double)frame.PixelHeight);
                }
            }
            catch { return null; }
        }
    }
}
