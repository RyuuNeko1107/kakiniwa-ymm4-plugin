// 受け取ったパック → YMM4 と利用者の PC の境界の守り(2026-10-20c 監査)。
// パックは第三者から受け取ることもある「半信頼」の入力。ここでは:
//   ・大きさ/件数の上限(読み込み時に断る)
//   ・時刻・位置の丸めと、フレーム数(int)のあふれ
//   ・音声・画像のヘッダの壊れた値
//   ・書き込み先のリンク(ジャンクション)
//   ・表情プリセットの書き先(パックの外の PSD)と、同名プリセットの置き換え
//   ・保存ログの伏せ字
// を、実際に動かして確かめる。期待値は手で計算した固定値。

using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace KakiniwaYmm4Import.Tests
{
    // ---------- 読み込み時の上限(LoadPack) ----------

    public class PackLimitsLoadTests : IDisposable
    {
        readonly string dir;
        readonly List<string> logs = new();

        public PackLimitsLoadTests()
        {
            dir = Path.Combine(Path.GetTempPath(), "kakiniwa-limits-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }
        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

        Pack? Load(string json)
        {
            File.WriteAllText(Path.Combine(dir, "timeline.json"), json);
            return Importer.LoadPack(dir, logs.Add);
        }

        static string Events(int n, Func<int, string> one)
            => "{ \"version\": 1, \"events\": [" + string.Join(",", Enumerable.Range(0, n).Select(one)) + "] }";

        [Fact]
        public void 大きすぎるtimelineJsonは読む前に断る()
        {
            // 中身は正しい JSON(空白で 32MB を 1 バイト超える)。大きさの検査が無ければ読めてしまう
            var path = Path.Combine(dir, "timeline.json");
            using (var fs = File.Create(path))
            {
                fs.WriteByte((byte)'{');
                var pad = new byte[1024 * 1024];
                for (var i = 0; i < pad.Length; i++) pad[i] = (byte)' ';
                for (var i = 0; i < 32; i++) fs.Write(pad, 0, pad.Length);
                fs.WriteByte((byte)'}'); // 32MB + 2 バイト
            }
            Assert.Null(Importer.LoadPack(dir, logs.Add));
        }

        [Fact]
        public void イベントが2万件を超えるパックは断る_2万件ちょうどは読める()
        {
            Assert.NotNull(Load(Events(20000, i => "{\"type\":\"custom\"}")));
            Assert.Null(Load(Events(20001, i => "{\"type\":\"custom\"}")));
        }

        [Fact]
        public void キャラが200を超えるパックは断る()
        {
            var chars = string.Join(",", Enumerable.Range(0, 201).Select(i => "{\"id\":\"c" + i + "\",\"name\":\"n" + i + "\"}"));
            Assert.Null(Load("{ \"version\": 1, \"characters\": [" + chars + "], \"events\": [] }"));
        }

        [Fact]
        public void 話者の種類が200を超えるパックは断る()
        {
            // キャラ一覧に無い speaker も数える(話者の登場順の処理は話者の数の二乗で回る)
            Assert.NotNull(Load(Events(200, i => "{\"type\":\"serif\",\"speaker\":\"s" + i + "\",\"text\":\"a\"}")));
            Assert.Null(Load(Events(201, i => "{\"type\":\"serif\",\"speaker\":\"s" + i + "\",\"text\":\"a\"}")));
        }

        [Fact]
        public void 時間は0から24時間へ丸める()
        {
            var p = Load("""
                { "version": 1, "events": [
                  { "type": "serif", "start": 1e12, "seconds": -5, "pause": 1e300, "text": "a" } ] }
                """);
            Assert.NotNull(p);
            Assert.Equal(86400, p!.Events[0].Start);
            Assert.Equal(0, p.Events[0].Seconds);
            Assert.Equal(86400, p.Events[0].Pause);
        }

        [Fact]
        public void 位置と高さはプラスマイナス1000パーセントへ丸める()
        {
            var p = Load("""
                { "version": 1, "events": [
                  { "type": "prop", "x": 1e308, "y": -1e308, "height": -50 },
                  { "type": "layout", "positions": [ { "speaker": "a", "x": 1e9, "y": 2000, "height": 5000 } ] } ] }
                """);
            Assert.NotNull(p);
            Assert.Equal(1000, p!.Events[0].X);
            Assert.Equal(-1000, p.Events[0].Y);
            Assert.Equal(0, p.Events[0].Height);
            var pos = p.Events[1].Positions![0];
            Assert.Equal(1000, pos.X);
            Assert.Equal(1000, pos.Y);
            Assert.Equal(1000, pos.Height);
        }
    }

    // ---------- フレーム数(int)のあふれ ----------

    public class FrameOverflowTests
    {
        [Fact]
        public void 秒からフレームは飽和する_負に回り込まない()
        {
            Assert.Equal(int.MaxValue, Importer.SecondsToFrames(1e300, 60));
            Assert.Equal(int.MinValue, Importer.SecondsToFrames(-1e300, 60));
            Assert.Equal(45, Importer.SecondsToFrames(1.5, 30));
        }

        [Fact]
        public void フレームの足し算は飽和する()
        {
            Assert.Equal(int.MaxValue, Importer.AddFramesSaturating(int.MaxValue, 10));
            Assert.Equal(int.MinValue, Importer.AddFramesSaturating(int.MinValue, -10));
            Assert.Equal(15, Importer.AddFramesSaturating(5, 10));
        }

        [Fact]
        public void 上限に収まらないイベントは置かない()
        {
            // 24時間+24時間 × 60fps = 10,368,000 フレーム → 収まる
            Assert.True(Importer.EventFitsFrames(86400, 86400, 60, 0));
            // 24時間 × 100万fps = 864億フレーム → int に入らない
            Assert.False(Importer.EventFitsFrames(86400, 0, 1e6, 0));
            // 開始位置(カーソル)が上限ぎりぎりなら、1秒でも足すと超える
            Assert.False(Importer.EventFitsFrames(1, 0, 60, Importer.MaxFrame));
        }

        [Fact]
        public void 立ち絵切替の開始フレームは巨大なfpsでも負にならない()
        {
            var portraits = new Dictionary<string, PackPortrait> { ["p"] = new PackPortrait { Id = "p" } };
            // 開始位置(カーソル)の 10 フレームを足しても、int の上限で止まる(素の足し算だと負へ回り込む)
            var plan = Importer.PlanPortraitSwitch(portraits, "p", null, 86400, 86400, 1e9, 10);
            Assert.NotNull(plan);
            Assert.Equal(int.MaxValue, plan!.NewStartFrame);
        }
    }

    // ---------- 音声・画像のヘッダの壊れた値 ----------

    public class MediaHeaderSanityTests : IDisposable
    {
        readonly string dir;
        public MediaHeaderSanityTests()
        {
            dir = Path.Combine(Path.GetTempPath(), "kakiniwa-media-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }
        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

        static byte[] Le32(uint v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };

        static byte[] Wav(uint byteRate, uint dataSize)
        {
            var ms = new MemoryStream();
            void W(byte[] b) => ms.Write(b, 0, b.Length);
            W(Encoding.ASCII.GetBytes("RIFF")); W(Le32(36)); W(Encoding.ASCII.GetBytes("WAVE"));
            W(Encoding.ASCII.GetBytes("fmt ")); W(Le32(16));
            W(new byte[] { 1, 0, 1, 0 }); W(Le32(byteRate)); W(Le32(byteRate)); W(new byte[] { 1, 0, 8, 0 });
            W(Encoding.ASCII.GetBytes("data")); W(Le32(dataSize));
            return ms.ToArray();
        }

        [Fact]
        public void WAVのヘッダが何十年分もの尺を名乗っても使わない()
        {
            // 0xFFFFFFF0 バイト ÷ 1 バイト/秒 ≒ 136 年
            var p = Path.Combine(dir, "huge.wav");
            File.WriteAllBytes(p, Wav(byteRate: 1, dataSize: 0xFFFFFFF0));
            Assert.Null(Importer.AudioDurationSeconds(p));
        }

        [Fact]
        public void 尺は0より大きく6時間以下のときだけ使う()
        {
            Assert.Null(Importer.SaneAudioSeconds(0));
            Assert.Null(Importer.SaneAudioSeconds(-1));
            Assert.Null(Importer.SaneAudioSeconds(21600.001));
            Assert.Null(Importer.SaneAudioSeconds(double.PositiveInfinity));
            Assert.Equal(21600, Importer.SaneAudioSeconds(21600));
            Assert.Equal(1.5, Importer.SaneAudioSeconds(1.5));
        }

        [Fact]
        public void MP3のXingのフレーム数は符号無しで読む()
        {
            // MPEG1 Layer3 44.1kHz のフレームヘッダ + 36 バイト目に Xing(フレーム数あり)
            // フレーム数 0x80000000(最上位ビットが立つ)= 2147483648
            // 2147483648 × 1152 ÷ 44100 = 56097532.029... 秒(int で読むと負になる)
            var b = new byte[4096];
            b[0] = 0xFF; b[1] = 0xFB; b[2] = 0x90; b[3] = 0x00;
            Encoding.ASCII.GetBytes("Xing").CopyTo(b, 36);
            b[43] = 0x01;                 // flags: フレーム数あり
            b[44] = 0x80;                 // フレーム数(ビッグエンディアン)
            var p = Path.Combine(dir, "xing.mp3");
            File.WriteAllBytes(p, b);
            using var fs = File.OpenRead(p);
            var sec = (double?)Priv.Call("Mp3Duration", fs);
            Assert.NotNull(sec);
            Assert.Equal(56097532.029, sec!.Value, 2);
        }

        static string Psd(string dir, string name, uint w, uint h)
        {
            var b = new byte[26];
            Encoding.ASCII.GetBytes("8BPS").CopyTo(b, 0);
            b[14] = (byte)(h >> 24); b[15] = (byte)(h >> 16); b[16] = (byte)(h >> 8); b[17] = (byte)h;
            b[18] = (byte)(w >> 24); b[19] = (byte)(w >> 16); b[20] = (byte)(w >> 8); b[21] = (byte)w;
            var p = Path.Combine(dir, name);
            File.WriteAllBytes(p, b);
            return p;
        }

        [Fact]
        public void 画像の寸法が0なら使わない_拡大率がInfinityになる()
        {
            Assert.Null(Priv.Call("ReadImageSize", Psd(dir, "h0.psd", 100, 0)));
            Assert.Null(Priv.Call("ReadImageSize", Psd(dir, "w0.psd", 0, 100)));
        }

        [Fact]
        public void 画像の寸法が上限を超えたら使わない()
        {
            Assert.Null(Priv.Call("ReadImageSize", Psd(dir, "big.psd", 100, 4000000000)));
            var ok = (Tuple<double, double>?)Priv.Call("ReadImageSize", Psd(dir, "ok.psd", 100000, 200));
            Assert.NotNull(ok);
            Assert.Equal(100000, ok!.Item1);
            Assert.Equal(200, ok.Item2);
        }
    }

    // ---------- SetAnim の入口(有限でない値を YMM4 に渡さない) ----------

    public class SetAnimFiniteTests
    {
        class Item { public double? X { get; set; } }

        [Fact]
        public void 無限大とNaNは設定しない_有限なら設定する()
        {
            var it = new Item();
            var logs = new List<string>();
            Action<string> log = logs.Add;
            Assert.False((bool)Priv.Call("SetAnim", it, "X", double.PositiveInfinity, log)!);
            Assert.False((bool)Priv.Call("SetAnim", it, "X", double.NaN, log)!);
            Assert.Null(it.X);
            Assert.True((bool)Priv.Call("SetAnim", it, "X", 12.5, log)!);
            Assert.Equal(12.5, it.X);
        }
    }

    // ---------- 次の停止・次の同種を前もって引く(二乗にしない) ----------

    public class NextStartIndexTests
    {
        static PackEvent E(string type, double start, string? asset = null, string? name = null)
            => new PackEvent { Type = type, Start = start, Name = name, Asset = asset != null ? new PackAssetRef { Name = asset } : null };

        [Fact]
        public void 次の同種のイベントの開始_無ければ既定()
        {
            var evs = new List<PackEvent> { E("bg", 0), E("serif", 1), E("bg", 5), E("bgm", 6), E("bg", 9) };
            var r = Importer.NextStartOfTypes(evs, new[] { "bg" }, 100);
            Assert.Equal(new double[] { 5, 5, 9, 9, 100 }, r);
        }

        [Fact]
        public void 小物の停止は名前か素材名の一致で一番近いもの()
        {
            var evs = new List<PackEvent>
            {
                E("prop", 0, asset: "猫"),
                E("prop", 1, asset: "犬"),
                E("prop_stop", 3, name: "犬"),
                E("prop_stop", 4, asset: "猫"),
                E("prop", 5, asset: "猫"),
                E("prop_stop", 8, name: "猫"),
            };
            var r = Importer.NextNamedStopStarts(evs, "prop_stop", 100);
            Assert.Equal(4, r[0]);   // 猫 → 4(素材名の一致)
            Assert.Equal(3, r[1]);   // 犬 → 3(名前の一致)
            Assert.Equal(8, r[4]);   // 2回目の猫 → 8(自分より後ろだけを見る)
            Assert.Equal(100, r[5]); // 停止そのもの(素材なし)は既定
        }

        [Fact]
        public void 昇順の配列で値より大きい最初の位置()
        {
            var a = new[] { 1, 3, 3, 3, 7 };
            Assert.Equal(1, Importer.UpperBound(a, 1));
            Assert.Equal(4, Importer.UpperBound(a, 3));
            Assert.Equal(5, Importer.UpperBound(a, 7));
            Assert.Equal(0, Importer.UpperBound(a, 0));
        }
    }

    // ---------- 書き込み先のリンク(ジャンクション) ----------

    public class WriteLinkTests : IDisposable
    {
        readonly string root;
        readonly string pack;
        readonly string outside;

        public WriteLinkTests()
        {
            root = Path.Combine(Path.GetTempPath(), "kakiniwa-link-" + Guid.NewGuid().ToString("N"));
            pack = Path.Combine(root, "pack");
            outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(pack);
            Directory.CreateDirectory(outside);
            File.WriteAllBytes(Path.Combine(outside, "a.psd"), new byte[] { 1 });
        }
        public void Dispose()
        {
            // ジャンクションは中身ごと消すとリンク先まで消えるので、先にリンクだけ外す
            try { foreach (var d in Directory.GetDirectories(pack)) if (Importer.IsLink(d)) Directory.Delete(d); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }

        /// <summary>ジャンクションを作る(管理者権限は要らない)。作れなければテストを落とす(素通りさせない)。</summary>
        static void Junction(string link, string target)
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            Assert.True(Directory.Exists(link) && Importer.IsLink(link), "ジャンクションを作れなかった: " + link);
        }

        [Fact]
        public void パックの中のジャンクション越しの書き込み先は断る()
        {
            Junction(Path.Combine(pack, "j"), outside);
            // 文字列の上ではパック配下(pack\j\a.psd)だが、実体はパックの外
            Assert.Null((string?)Priv.Call("ResolveInPackForWrite", pack, "j/a.psd"));
        }

        [Fact]
        public void 普通のフォルダなら書き込み先として通す()
        {
            Directory.CreateDirectory(Path.Combine(pack, "d"));
            File.WriteAllBytes(Path.Combine(pack, "d", "a.psd"), new byte[] { 1 });
            Assert.Equal(Path.Combine(pack, "d", "a.psd"), (string?)Priv.Call("ResolveInPackForWrite", pack, "d/a.psd"));
        }

        [Fact]
        public void サイドカーの控えがリンクなら書かない()
        {
            var psd = Path.Combine(pack, "a.psd");
            File.WriteAllBytes(psd, new byte[] { 1 });
            Assert.False(Importer.SidecarIsLinked(psd));
            // 控えの名前にリンクを置かれていると、控えを写したつもりがリンク先へ書く
            Junction(Path.Combine(pack, "a-ymm.json.bak"), outside);
            Assert.True(Importer.SidecarIsLinked(psd));
        }
    }

    // ---------- 表情プリセットの書き先(パックの外の PSD) ----------

    public class PresetTargetTests : IDisposable
    {
        // YMM4 のキャラモデルの代わり(TachieDefaultItemParameter.FilePath だけ読まれる)
        class FakeParam { public string FilePath { get; set; } = ""; }
        class FakeModel { public FakeParam TachieDefaultItemParameter { get; set; } = new FakeParam(); }

        readonly string root;
        readonly string pack;
        readonly string basePsd;
        readonly string otherPsd;
        readonly List<string> logs = new();

        public PresetTargetTests()
        {
            root = Path.Combine(Path.GetTempPath(), "kakiniwa-preset-" + Guid.NewGuid().ToString("N"));
            pack = Path.Combine(root, "pack");
            Directory.CreateDirectory(pack);
            Directory.CreateDirectory(Path.Combine(root, "ymm4"));
            basePsd = Path.Combine(root, "ymm4", "base.psd");
            otherPsd = Path.Combine(root, "ymm4", "other.psd");
            File.WriteAllBytes(basePsd, new byte[] { 1 });
            File.WriteAllBytes(otherPsd, new byte[] { 1 });
            File.WriteAllBytes(Path.Combine(pack, "p2.psd"), new byte[] { 1 });
        }
        public void Dispose() { try { Directory.Delete(root, true); } catch { } }

        static PackExpression X(string id, string psd) => new PackExpression { Id = id, Psd = new PackPsd { Path = psd, Layers = new List<string> { "顔/笑" } } };

        Pack MakePack(string? ymm4Name) => new Pack
        {
            Characters = new List<PackCharacter>
            {
                new PackCharacter
                {
                    Id = "c", Name = "アオイ",
                    Portraits = new List<PackPortrait>
                    {
                        new PackPortrait { Id = "p2", Ymm4Name = ymm4Name, Psd = new PackPsd { Path = "p2.psd" },
                            Expressions = new List<PackExpression> { X("笑", "p2.psd") } },
                    },
                },
            },
        };

        CharacterChoice Choice(string name, string psd)
            => new CharacterChoice { Name = name, Display = name, Model = new FakeModel { TachieDefaultItemParameter = new FakeParam { FilePath = psd } } };

        [Fact]
        public void パックのymm4Nameだけではパックの外のPSDを書き先にしない()
        {
            var map = new Dictionary<string, CharacterChoice> { ["c"] = Choice("アオイ", basePsd) };
            var t = Importer.PlanPresetTargets(MakePack("ヒナタ"), pack, map, new HashSet<string> { "アオイ" }, logs.Add);
            Assert.Empty(t);
        }

        [Fact]
        public void 立ち絵の行で選んだキャラのPSDは書き先になる_パックの外と印が付く()
        {
            var map = new Dictionary<string, CharacterChoice>
            {
                ["c"] = Choice("アオイ", basePsd),
                ["c/p2"] = Choice("ヒナタ", otherPsd),
            };
            var t = Importer.PlanPresetTargets(MakePack("ヒナタ"), pack, map, new HashSet<string> { "アオイ" }, logs.Add);
            var only = Assert.Single(t);
            Assert.Equal(otherPsd, only.PsdPath);
            Assert.False(only.InPack);
            Assert.Equal("c/p2", only.MapKey);
        }

        [Fact]
        public void 同じキャラのままの立ち絵はパック内のPSDへ()
        {
            var map = new Dictionary<string, CharacterChoice> { ["c"] = Choice("アオイ", basePsd) };
            var t = Importer.PlanPresetTargets(MakePack(null), pack, map, new HashSet<string> { "アオイ" }, logs.Add);
            var only = Assert.Single(t);
            Assert.Equal(Path.Combine(pack, "p2.psd"), only.PsdPath);
            Assert.True(only.InPack);
        }

        [Fact]
        public void 書き先の決め方_手で並べた表()
        {
            Assert.Equal(Importer.PortraitPresetKind.NeedRowSelection, Importer.DecidePortraitPresetTarget(null, "A", "B", false, true));
            Assert.Equal(Importer.PortraitPresetKind.RowSelection, Importer.DecidePortraitPresetTarget("B", "A", null, false, true));
            Assert.Equal(Importer.PortraitPresetKind.DefaultPsd, Importer.DecidePortraitPresetTarget(null, "A", null, true, true));
            Assert.Equal(Importer.PortraitPresetKind.InPackPsd, Importer.DecidePortraitPresetTarget(null, "A", "A", false, true));
            Assert.Equal(Importer.PortraitPresetKind.InPackPsd, Importer.DecidePortraitPresetTarget("A", "A", "B", false, true));
        }

        [Fact]
        public void 確認の一覧で最初から印を付けるのは_パック内か利用者が選んだ行だけ()
        {
            var auto = new HashSet<string> { "c/p2" };
            Assert.False(Importer.PresetTargetCheckedByDefault(new Importer.PresetTarget { MapKey = "c/p2", InPack = false }, auto));
            Assert.True(Importer.PresetTargetCheckedByDefault(new Importer.PresetTarget { MapKey = "c/p3", InPack = false }, auto));
            Assert.True(Importer.PresetTargetCheckedByDefault(new Importer.PresetTarget { MapKey = "c/p2", InPack = true }, auto));
        }

        [Fact]
        public void 同名プリセットは書き庭が作った記録のあるものだけ置き換える()
        {
            var existing = new HashSet<string> { "笑" };
            var owned = new HashSet<string>();
            Assert.False(Importer.MayWritePreset(basePsd, "笑", existing, owned));   // 利用者が作った同名
            Assert.True(Importer.MayWritePreset(basePsd, "泣", existing, owned));    // 新しい名前
            owned.Add(Importer.OwnedPresetKey(basePsd, "笑"));
            Assert.True(Importer.MayWritePreset(basePsd, "笑", existing, owned));    // 書き庭が作った同名
            Assert.False(Importer.MayWritePreset(otherPsd, "笑", existing, owned));  // 記録は PSD ごと
        }

        [Fact]
        public void 書き換える前にサイドカーを控えへ写す()
        {
            var sidecar = Path.Combine(root, "ymm4", "base-ymm.json");
            File.WriteAllText(sidecar, "{\"Presets\":[1]}");
            Assert.Equal(1, Importer.BackupSidecars(basePsd));
            Assert.Equal("{\"Presets\":[1]}", File.ReadAllText(sidecar + ".bak"));
        }
    }

    // ---------- 保存ログの伏せ字 ----------

    [Collection("Tr")]
    public class LogRedactorLeakTests
    {
        [Fact]
        public void 相対パスのフォルダの連なりも伏せる()
            => Assert.Equal("素材: <パス><ファイル>.png", LogRedactor.Redact("素材: assets/秘密の作品/bg.png"));

        [Fact]
        public void 素材として通る拡張子のファイル名は全部伏せる()
            => Assert.Equal("<ファイル>.avif <ファイル>.opus <ファイル>.mkv <ファイル>.aac",
                LogRedactor.Redact("秘密.avif 秘密.opus 秘密.mkv 秘密.aac"));

        [Fact]
        public void 二重かぎ括弧も文字数だけ残す()
            => Assert.Equal("読み込みOK: 『(4文字)』", LogRedactor.Redact("読み込みOK: 『秘密の話』"));

        [Fact]
        public void スラッシュで書いたUNCも伏せる()
            => Assert.Equal("参照: <パス>", LogRedactor.Redact("参照: //server/share/作品"));

        [Fact]
        public void URLと数字の区切りは伏せない()
        {
            var line = "新しい版はこちら: https://kakiniwa.jp/plugin/ 10/10件 v4.54/4.55";
            Assert.Equal(line, LogRedactor.Redact(line));
        }
    }

    // ---------- 2026-10-22 監査(プラグインのセキュリティ見直し) ----------

    public class PackLimitsExpressionTests : IDisposable
    {
        readonly string dir;
        readonly List<string> logs = new();
        public PackLimitsExpressionTests()
        {
            dir = Path.Combine(Path.GetTempPath(), "kakiniwa-expr-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }
        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

        Pack? Load(string json)
        {
            File.WriteAllText(Path.Combine(dir, "timeline.json"), json);
            return Importer.LoadPack(dir, logs.Add);
        }

        static string Pack(string expr)
            => "{ \"version\": 1, \"characters\": [ { \"id\": \"a\", \"name\": \"A\", \"expressions\": [" + expr + "] } ], \"events\": [] }";

        static string Layers(int n, int len = 4)
            => "[" + string.Join(",", Enumerable.Range(0, n).Select(i => "\"" + new string('x', len) + "\"")) + "]";

        [Fact]
        public void 表情のPSDレイヤーが4096を超えるパックは断る_4096ちょうどは読める()
        {
            Assert.NotNull(Load(Pack("{\"id\":\"e\",\"psd\":{\"layers\":" + Layers(4096) + "}}")));
            Assert.Null(Load(Pack("{\"id\":\"e\",\"psd\":{\"layers\":" + Layers(4097) + "}}")));
        }

        [Fact]
        public void layerIdsだけが多すぎても断る()
        {
            var ids = "[" + string.Join(",", Enumerable.Range(0, 4097)) + "]";
            Assert.Null(Load(Pack("{\"id\":\"e\",\"psd\":{\"layers\":[\"a\"],\"layerIds\":" + ids + "}}")));
        }

        [Fact]
        public void 表情のidと名前とレイヤー名は256文字まで()
        {
            Assert.NotNull(Load(Pack("{\"id\":\"" + new string('i', 256) + "\",\"label\":\"" + new string('l', 256) + "\",\"psd\":{\"layers\":" + Layers(2, 256) + "}}")));
            Assert.Null(Load(Pack("{\"id\":\"" + new string('i', 257) + "\"}")));
            Assert.Null(Load(Pack("{\"id\":\"e\",\"label\":\"" + new string('l', 257) + "\"}")));
            Assert.Null(Load(Pack("{\"id\":\"e\",\"psd\":{\"layers\":" + Layers(1, 257) + "}}")));
        }

        [Fact]
        public void 立ち絵の側の表情も数える()
        {
            var json = "{ \"version\": 1, \"characters\": [ { \"id\": \"a\", \"name\": \"A\", \"portraits\": [ { \"id\": \"p\", \"expressions\": [ {\"id\":\"e\",\"psd\":{\"layers\":"
                + Layers(4097) + "}} ] } ] } ], \"events\": [] }";
            Assert.Null(Load(json));
        }

        [Theory]
        [InlineData("0.5.0", true)]
        [InlineData("1", true)]
        [InlineData("1.2.3.4", true)]
        [InlineData("10000.0", false)]
        [InlineData("1.2.3.4.5", false)]
        [InlineData("9.9.9\nhttps://evil.example/", false)]
        [InlineData("9.9.9 https://evil.example/", false)]
        [InlineData("v9.9.9", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void 版は数字と点だけの形のときだけ使う(string? v, bool expected)
        {
            Assert.Equal(expected, Importer.IsPlainVersion(v));
        }

        [Fact]
        public void 形の違う版は案内文に差し込まない()
        {
            var json = "{ \"version\": 1, \"pluginLatest\": \"99.0.0\\nhttps://evil.example/\", \"events\": [] }";
            Assert.NotNull(Load(json));
            Assert.DoesNotContain(logs, l => l.Contains("evil.example"));
            logs.Clear();
            Assert.NotNull(Load("{ \"version\": 1, \"pluginLatest\": \"99.0.0\", \"events\": [] }"));
            Assert.Contains(logs, l => l.Contains("99.0.0"));
        }

        [Fact]
        public void 作品名の改行はログの行を割らない()
        {
            Assert.NotNull(Load("{ \"version\": 1, \"title\": \"題\\n✓ 安全なパックです\", \"events\": [] }"));
            Assert.DoesNotContain(logs, l => l.Contains("\n"));
            Assert.Contains(logs, l => l.Contains("\\n"));
        }
    }

    public class LogFieldTests
    {
        [Fact]
        public void 改行と制御文字と見えない文字は見える形にする()
        {
            Assert.Equal("a\\nb\\rc\\td", LogRedactor.Field("a\nb\rc\td"));
            Assert.Equal("x\\u0007y", LogRedactor.Field("x\u0007y"));
            Assert.Equal("\\u202Eabc", LogRedactor.Field("\u202Eabc"));     // 書字方向の上書き
            Assert.Equal("a\\u200Bb", LogRedactor.Field("a\u200Bb"));       // 幅ゼロの空白
            Assert.Equal("", LogRedactor.Field(null));
        }

        [Fact]
        public void 長さで切る()
        {
            Assert.Equal(new string('a', 10) + "…", LogRedactor.Field(new string('a', 50), 10));
            Assert.Equal("短い", LogRedactor.Field("短い", 10));
        }

        [Fact]
        public void かぎ括弧の中に改行があっても保存ログでは伏せる()
        {
            Assert.Equal("ボイス「(6文字)」", // 改行の2文字も数える
                 LogRedactor.Redact("ボイス「秘密\r\n本文」"));
        }

        [Fact]
        public void ログに出すセリフの頭は折り返しを外してから切る()
        {
            Assert.Equal("一行目 二行目", Importer.ShortSerif("一行目\n二行目"));
            Assert.Equal("あいうえおかきくけこさし…", Importer.ShortSerif("あいうえおかきくけこさしすせそ"));
        }
    }

    public class RememberedPickTests
    {
        [Fact]
        public void 前回の記憶で付いた割り当ては_パックの外のPSDに最初から印を付けない()
        {
            var notChosen = Importer.NotChosenInWindow(new[] { "auto" }, new[] { "remembered" });
            var outside = new Importer.PresetTarget { MapKey = "remembered", InPack = false };
            var chosen = new Importer.PresetTarget { MapKey = "picked", InPack = false };
            var inPack = new Importer.PresetTarget { MapKey = "remembered", InPack = true };
            Assert.False(Importer.PresetTargetCheckedByDefault(outside, notChosen));
            Assert.True(Importer.PresetTargetCheckedByDefault(chosen, notChosen));
            Assert.True(Importer.PresetTargetCheckedByDefault(inPack, notChosen));
            Assert.Contains("auto", notChosen);
        }
    }

    public class ReadLinkTests
    {
        [Theory]
        [InlineData(@"\\server\share\a", true)]
        [InlineData(@"\\?\UNC\server\share\a", true)]
        [InlineData(@"\??\UNC\server\share", true)]
        [InlineData("//server/share/a", true)]
        [InlineData(@"\\?\C:\work\a", false)]
        [InlineData(@"C:\work\a", false)]
        [InlineData(@"..\a", false)]
        public void リモートを指すパスか(string p, bool expected)
        {
            Assert.Equal(expected, Importer.IsRemotePath(p));
        }

        [Fact]
        public void ローカルを指すジャンクションの中の素材は読める()
        {
            var root = Path.Combine(Path.GetTempPath(), "kakiniwa-link-" + Guid.NewGuid().ToString("N"));
            var pack = Path.Combine(root, "pack");
            var real = Path.Combine(root, "real");
            Directory.CreateDirectory(pack);
            Directory.CreateDirectory(real);
            File.WriteAllText(Path.Combine(real, "bg.png"), "x");
            try
            {
                var p = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + Path.Combine(pack, "assets") + "\" \"" + real + "\"")
                { CreateNoWindow = true, UseShellExecute = false });
                p!.WaitForExit();
                Assert.False(Importer.PathHasRemoteLink(pack, Path.Combine(pack, "assets", "bg.png")));
                Assert.NotNull(Priv.Call("ResolveInPack", pack, "assets/bg.png"));
            }
            finally { try { Directory.Delete(Path.Combine(pack, "assets")); } catch { } try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void パックの外を指す引数は調べずに断る()
        {
            Assert.True(Importer.PathHasRemoteLink(@"C:\pack", @"C:\other\a.png"));
        }
    }

    public class CostlyVoiceAndFileLocationTests : IDisposable
    {
        readonly string dir;
        readonly List<string> logs = new();
        public CostlyVoiceAndFileLocationTests()
        {
            dir = Path.Combine(Path.GetTempPath(), "kakiniwa-costly-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }
        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

        Pack? Load(string json)
        {
            File.WriteAllText(Path.Combine(dir, "timeline.json"), json);
            return Importer.LoadPack(dir, logs.Add);
        }

        [Fact]
        public void 料金のかかる声と外部コマンドだけを行数と文字数で数える()
        {
            const string polly = "YukkuriMovieMaker.Voice.AmazonPollyVoiceSpeaker";
            const string cmd = "YukkuriMovieMaker.Voice.CommandLineVoiceSpeaker";
            const string aq = "YukkuriMovieMaker.Voice.AquesTalk1VoiceSpeaker";
            var r = Importer.CostlyVoiceSummary(new[]
            {
                Tuple.Create(polly, "こんにちは"), Tuple.Create(aq, "ゆっくり"), Tuple.Create(polly, "abc"),
                Tuple.Create(cmd, "1234567"),
            });
            Assert.Equal(2, r.Count);
            Assert.Contains(Tuple.Create(polly, 2, 8), r);
            Assert.Contains(Tuple.Create(cmd, 1, 7), r);
            Assert.Empty(Importer.CostlyVoiceSummary(new[] { Tuple.Create(aq, "a") }));
        }

        [Theory]
        [InlineData(@"C:\Users\a\voice.wav", true)]
        [InlineData(@"\\server\share\voice.wav", true)]
        [InlineData("file:///C:/a.wav", true)]
        [InlineData("  C:\\a.wav  ", true)]
        [InlineData("https://example.com/a.wav", false)]
        [InlineData("こんにちは", false)]
        [InlineData("voice.wav", false)]
        [InlineData("[間:1] 次へ", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void ファイルの場所の形か(string? s, bool expected)
        {
            Assert.Equal(expected, Importer.LooksLikeFileLocation(s));
        }

        [Fact]
        public void 読みがファイルの場所なら捨て_本文がそうなら声を作らない印を付ける()
        {
            var p = Load(@"{ ""version"": 1, ""events"": [
                { ""no"": 1, ""type"": ""serif"", ""text"": ""ふつう"", ""reading"": ""\\\\attacker\\share\\a.wav"" },
                { ""no"": 2, ""type"": ""serif"", ""text"": ""C:\\Windows\\Media\\chimes.wav"" },
                { ""no"": 3, ""type"": ""serif"", ""text"": ""ふつう"", ""reading"": ""ふつう"" } ] }");
            Assert.NotNull(p);
            Assert.Null(p!.Events[0].Reading);
            Assert.False(p.Events[0].NoVoice);
            Assert.True(p.Events[1].NoVoice);
            Assert.Equal("ふつう", p.Events[2].Reading);
            Assert.False(p.Events[2].NoVoice);
        }

        [Fact]
        public void 一行のセリフが2000字を超えるパックは断る()
        {
            Assert.NotNull(Load("{ \"version\": 1, \"events\": [ { \"type\": \"serif\", \"text\": \"" + new string('あ', 2000) + "\" } ] }"));
            Assert.Null(Load("{ \"version\": 1, \"events\": [ { \"type\": \"serif\", \"text\": \"" + new string('あ', 2001) + "\" } ] }"));
            Assert.Null(Load("{ \"version\": 1, \"events\": [ { \"type\": \"serif\", \"text\": \"a\", \"reading\": \"" + new string('あ', 2001) + "\" } ] }"));
        }
    }
}
