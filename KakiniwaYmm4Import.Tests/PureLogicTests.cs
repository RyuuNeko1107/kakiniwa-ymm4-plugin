// KakiniwaYmm4Import の「YMM4 実行環境なしで動く純ロジック」のテスト。
// private static メソッドはリフレクションで呼ぶ(実装は変更しない方針)。

using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Xunit;

namespace KakiniwaYmm4Import.Tests
{
    static class Priv
    {
        public static object? Call(string name, params object?[] args)
        {
            var m = typeof(Importer).GetMethod(name,
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
            Assert.NotNull(m);
            try { return m!.Invoke(null, args); }
            catch (TargetInvocationException e) when (e.InnerException != null)
            { throw e.InnerException; }
        }
    }

    // ---------- バージョン比較 ----------

    public class CompareVersionsTests
    {
        static int Cmp(string a, string b) => (int)Priv.Call("CompareVersions", a, b)!;

        [Theory]
        [InlineData("0.2.1", "0.2.1", 0)]
        [InlineData("1.0.0", "1.0.0", 0)]
        public void 同一なら0(string a, string b, int _) => Assert.Equal(0, Cmp(a, b));

        [Theory]
        [InlineData("0.2.0", "0.2.1")]
        [InlineData("0.2.4", "0.3.0")]
        [InlineData("0.9.9", "1.0.0")]
        [InlineData("0.2", "0.2.1")]   // 桁数違い: 足りない桁は 0 扱い
        public void 小さい方が負(string a, string b)
        {
            Assert.True(Cmp(a, b) < 0);
            Assert.True(Cmp(b, a) > 0);
        }

        [Fact]
        public void 数字以外の桁は0扱いで安全側()
        {
            Assert.Equal(0, Cmp("0.x.1", "0.0.1"));
            Assert.Equal(0, Cmp("abc", "0"));
        }

        [Fact]
        public void 末尾ゼロは同値()
        {
            Assert.Equal(0, Cmp("1.2", "1.2.0"));
        }
    }

    // ---------- レイヤーパス変換(書き庭「/」区切り ⇔ YMM4 実形式) ----------

    public class LayerPathTests
    {
        static string To(string s) => (string)Priv.Call("ToYmm4LayerPath", s)!;
        static string From(string s) => (string)Priv.Call("FromYmm4LayerPath", s)!;

        [Fact]
        public void 往復で元に戻る()
        {
            var src = "体/顔/目/にっこり";
            Assert.Equal(src, From(To(src)));
        }

        [Fact]
        public void Ymm4形式は各階層に区切り文字と兄弟番号が付く()
        {
            var y = To("a/b");
            Assert.Equal("\u001Ca\u001D0\u001Cb\u001D0", y);
        }

        [Fact]
        public void 形式外の文字列はそのまま返す()
        {
            Assert.Equal("ただの文字列", From("ただの文字列"));
        }

        [Fact]
        public void 単一階層も変換できる()
        {
            Assert.Equal("目", From(To("目")));
        }

        [Fact]
        public void CountSegmentsとLeafNameOf()
        {
            var y = To("体/顔/目");
            Assert.Equal(3, (int)Priv.Call("CountSegments", y)!);
            Assert.Equal("目", (string)Priv.Call("LeafNameOf", y)!);
        }
    }

    // ---------- パック相対パスの封じ込め(ResolveInPack) ----------

    public class ResolveInPackTests : IDisposable
    {
        readonly string dir;
        public ResolveInPackTests()
        {
            dir = Path.Combine(Path.GetTempPath(), "kakiniwa-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "assets"));
            File.WriteAllBytes(Path.Combine(dir, "assets", "a.png"), new byte[] { 1 });
            File.WriteAllText(Path.Combine(dir, "secret.txt"), "x");
        }
        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

        static string? R(string packDir, string? path) => (string?)Priv.Call("ResolveInPack", packDir, path);

        [Fact]
        public void パック内の相対パスは絶対パスに解決される()
        {
            var r = R(dir, "assets/a.png");
            Assert.NotNull(r);
            Assert.Equal(Path.GetFullPath(Path.Combine(dir, "assets", "a.png")), r);
        }

        [Fact]
        public void 存在しないファイルはnull() => Assert.Null(R(dir, "assets/nai.png"));

        [Fact]
        public void 親ディレクトリ脱出はnull()
        {
            // dir の外に実在ファイルを置いて ..\ で狙う
            var outside = Path.Combine(Path.GetTempPath(), "kakiniwa-outside-" + Guid.NewGuid().ToString("N") + ".png");
            File.WriteAllBytes(outside, new byte[] { 1 });
            try { Assert.Null(R(dir, "../" + Path.GetFileName(outside))); }
            finally { File.Delete(outside); }
        }

        [Fact]
        public void UNCパスは存在確認にすら行かずnull()
            => Assert.Null(R(dir, @"\\attacker\share\a.png"));

        [Fact]
        public void 絶対パスは素材拡張子なら許可()
        {
            var abs = Path.Combine(dir, "assets", "a.png");
            Assert.Equal(Path.GetFullPath(abs), R(dir, abs));
        }

        [Fact]
        public void 絶対パスでも素材以外の拡張子はnull()
            => Assert.Null(R(dir, Path.Combine(dir, "secret.txt")));

        [Fact]
        public void 空とnullはnull()
        {
            Assert.Null(R(dir, null));
            Assert.Null(R(dir, ""));
        }
    }

    // ---------- SE 実尺のヘッダ直読み(WAV / MP3 / OGG) ----------

    public class AudioDurationTests : IDisposable
    {
        readonly string dir;
        public AudioDurationTests()
        {
            dir = Path.Combine(Path.GetTempPath(), "kakiniwa-audio-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }
        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

        string Write(string name, byte[] bytes)
        {
            var p = Path.Combine(dir, name);
            File.WriteAllBytes(p, bytes);
            return p;
        }

        static byte[] Le32(uint v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };

        static byte[] Wav(uint byteRate, uint dataSize)
        {
            var ms = new MemoryStream();
            void W(byte[] b) => ms.Write(b, 0, b.Length);
            W(Encoding.ASCII.GetBytes("RIFF")); W(Le32(36 + dataSize)); W(Encoding.ASCII.GetBytes("WAVE"));
            W(Encoding.ASCII.GetBytes("fmt ")); W(Le32(16));
            W(new byte[] { 1, 0, 1, 0 });           // PCM, mono
            W(Le32(byteRate / 2));                   // sampleRate(値は尺計算に使われない)
            W(Le32(byteRate));                       // 平均バイトレート
            W(new byte[] { 2, 0, 16, 0 });          // blockAlign, bits
            W(Encoding.ASCII.GetBytes("data")); W(Le32(dataSize));
            // 実データ本体は尺計算に不要(サイズだけ読む)
            return ms.ToArray();
        }

        [Fact]
        public void WAVはdataサイズとバイトレートから正確な尺()
        {
            var p = Write("a.wav", Wav(byteRate: 16000, dataSize: 32000));
            Assert.Equal(2.0, Importer.AudioDurationSeconds(p)!.Value, 3);
        }

        [Fact]
        public void WAVの壊れたヘッダはnull()
        {
            var p = Write("bad.wav", Encoding.ASCII.GetBytes("NOT A WAV FILE...."));
            Assert.Null(Importer.AudioDurationSeconds(p));
        }

        [Fact]
        public void MP3のCBRはファイル長とビットレートから推定()
        {
            // MPEG1 Layer3 128kbps 44.1kHz のフレームヘッダ + 無音詰め物 16000 バイト
            var bytes = new byte[16000];
            bytes[0] = 0xFF; bytes[1] = 0xFB; bytes[2] = 0x90; bytes[3] = 0x00;
            var p = Write("a.mp3", bytes);
            // 16000 * 8 / 128000 = 1.0 秒
            Assert.Equal(1.0, Importer.AudioDurationSeconds(p)!.Value, 3);
        }

        [Fact]
        public void MP3のID3v2はスキップして推定()
        {
            // ID3 ヘッダ 10 バイト(サイズ 100=syncsafe)+ 100 バイトのタグ + フレーム
            var ms = new MemoryStream();
            ms.Write(new byte[] { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0, 0, 0, 0, 100 });
            ms.Write(new byte[100]);
            var frames = new byte[16000];
            frames[0] = 0xFF; frames[1] = 0xFB; frames[2] = 0x90;
            ms.Write(frames);
            var p = Write("id3.mp3", ms.ToArray());
            Assert.Equal(1.0, Importer.AudioDurationSeconds(p)!.Value, 3);
        }

        [Fact]
        public void OGGのOpusはgranuleを48kHzで割った尺()
        {
            // 先頭ページ: OggS + OpusHead を含む 64 バイト以上
            var head = new byte[128];
            Encoding.ASCII.GetBytes("OggS").CopyTo(head, 0);
            Encoding.ASCII.GetBytes("OpusHead").CopyTo(head, 28);
            // 最終ページ: OggS + granulePos(オフセット+6 から 8 バイト LE)= 96000 → 2.0 秒
            var tailPage = new byte[32];
            Encoding.ASCII.GetBytes("OggS").CopyTo(tailPage, 0);
            Le32(96000).CopyTo(tailPage, 6); // 上位 4 バイトは 0 のまま
            var ms = new MemoryStream();
            ms.Write(head); ms.Write(tailPage);
            var p = Write("a.ogg", ms.ToArray());
            Assert.Equal(2.0, Importer.AudioDurationSeconds(p)!.Value, 3);
        }

        [Fact]
        public void 未対応拡張子はnull()
        {
            var p = Write("a.flac", new byte[] { 1, 2, 3 });
            Assert.Null(Importer.AudioDurationSeconds(p));
        }

        [Fact]
        public void 存在しないファイルはnull()
            => Assert.Null(Importer.AudioDurationSeconds(Path.Combine(dir, "nai.wav")));
    }

    // ---------- PSD ヘッダの寸法直読み ----------

    public class ReadImageSizeTests : IDisposable
    {
        readonly string dir;
        public ReadImageSizeTests()
        {
            dir = Path.Combine(Path.GetTempPath(), "kakiniwa-img-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }
        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

        [Fact]
        public void PSDヘッダから幅と高さを読む()
        {
            var b = new byte[26];
            Encoding.ASCII.GetBytes("8BPS").CopyTo(b, 0);
            // 高さ(14-17)・幅(18-21)はビッグエンディアン
            b[14] = 0; b[15] = 0; b[16] = 0x08; b[17] = 0x00; // 2048
            b[18] = 0; b[19] = 0; b[20] = 0x04; b[21] = 0x00; // 1024
            var p = Path.Combine(dir, "a.psd");
            File.WriteAllBytes(p, b);
            var size = (Tuple<double, double>?)Priv.Call("ReadImageSize", p);
            Assert.NotNull(size);
            Assert.Equal(1024, size!.Item1); // 幅
            Assert.Equal(2048, size.Item2);  // 高さ
        }

        [Fact]
        public void PSDシグネチャ不一致はnull()
        {
            var p = Path.Combine(dir, "bad.psd");
            File.WriteAllBytes(p, new byte[26]);
            Assert.Null(Priv.Call("ReadImageSize", p));
        }
    }

    // ---------- FindMethodByShape(YMM4 バージョン差対策のメソッド照合) ----------

    public class FindMethodByShapeTests
    {
        class Target
        {
            public void M(int x) { }
            public void M(string s) { }
            public void OnlyInt(int x) { }
            public static void S(string a, string b) { }
        }

        [Fact]
        public void 型まで一致するオーバーロードを選ぶ()
        {
            var m = Importer.FindMethodByShape(typeof(Target), "M", new[] { typeof(string) });
            Assert.NotNull(m);
            Assert.Equal(typeof(string), m!.GetParameters()[0].ParameterType);
        }

        [Fact]
        public void 型一致が無ければ引数個数一致へフォールバック()
        {
            var m = Importer.FindMethodByShape(typeof(Target), "OnlyInt", new[] { typeof(string) });
            Assert.NotNull(m);
            Assert.Equal("OnlyInt", m!.Name);
        }

        [Fact]
        public void staticメソッドも見つかる()
        {
            var m = Importer.FindMethodByShape(typeof(Target), "S", new[] { typeof(string), typeof(string) });
            Assert.NotNull(m);
            Assert.True(m!.IsStatic);
        }

        [Fact]
        public void 名前が無ければnull()
            => Assert.Null(Importer.FindMethodByShape(typeof(Target), "Nai", Type.EmptyTypes));

        [Fact]
        public void 引数個数すら合わなければnull()
            => Assert.Null(Importer.FindMethodByShape(typeof(Target), "OnlyInt", Type.EmptyTypes));
    }

    // ---------- AllExpressions(flat + 立ち絵ごとの表情の集約・ID重複除去) ----------

    public class AllExpressionsTests
    {
        [Fact]
        public void flatと立ち絵の表情をIDで重複除去して集める()
        {
            var c = new PackCharacter
            {
                Expressions = new List<PackExpression>
                {
                    new PackExpression { Id = "normal" },
                    new PackExpression { Id = "smile" },
                },
                Portraits = new List<PackPortrait>
                {
                    new PackPortrait
                    {
                        Id = "p1",
                        Expressions = new List<PackExpression>
                        {
                            new PackExpression { Id = "smile" }, // flat と重複
                            new PackExpression { Id = "angry" },
                        },
                    },
                },
            };
            var all = Importer.AllExpressions(c);
            Assert.Equal(new[] { "normal", "smile", "angry" }, all.Select(x => x.Id));
        }

        [Fact]
        public void 立ち絵なしならflatのみ()
        {
            var c = new PackCharacter
            {
                Expressions = new List<PackExpression> { new PackExpression { Id = "a" } },
            };
            Assert.Single(Importer.AllExpressions(c));
        }
    }

    // ---------- timeline.json のパース・バリデーション(LoadPack) ----------

    public class LoadPackTests : IDisposable
    {
        readonly string dir;
        readonly List<string> logs = new();
        void Log(string s) => logs.Add(s);

        public LoadPackTests()
        {
            dir = Path.Combine(Path.GetTempPath(), "kakiniwa-pack-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
        }
        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

        Pack? Load(string json)
        {
            File.WriteAllText(Path.Combine(dir, "timeline.json"), json);
            return Importer.LoadPack(dir, Log);
        }

        [Fact]
        public void timelineJsonが無ければnullで案内()
        {
            var p = Importer.LoadPack(dir, Log);
            Assert.Null(p);
            Assert.Contains(logs, l => l.Contains("timeline.json がありません"));
        }

        [Fact]
        public void 正常なv1パックを読める()
        {
            var p = Load("""
                { "version": 1, "title": "つばめ珈琲", "episode": "第1話",
                  "characters": [ { "id": "a", "name": "アオイ" } ],
                  "events": [ { "no": 1, "type": "serif", "start": 0, "seconds": 2, "speaker": "a", "text": "こんにちは" } ] }
                """);
            Assert.NotNull(p);
            Assert.Equal("つばめ珈琲", p!.Title);
            Assert.Single(p.Events);
            Assert.Single(p.Characters);
            Assert.Contains(logs, l => l.Contains("読み込みOK"));
        }

        [Fact]
        public void 新しい形式版のパックは読まずに止める()
        {
            var p = Load("""{ "version": 2, "events": [] }""");
            Assert.Null(p);
            Assert.Contains(logs, l => l.Contains("新しい形式") && l.Contains("v2"));
        }

        [Fact]
        public void 対応形式版ちょうどは読める()
        {
            Assert.NotNull(Load($$"""{ "version": {{PackSchema.Supported}}, "events": [] }"""));
        }

        [Fact]
        public void 版なしは注意を出しつつ読む()
        {
            var p = Load("""{ "events": [] }""");
            Assert.NotNull(p);
            Assert.Contains(logs, l => l.Contains("形式版がありません"));
        }

        [Fact]
        public void 明示nullのeventsとcharactersは空リストに補正()
        {
            var p = Load("""{ "version": 1, "events": null, "characters": null, "timing": null }""");
            Assert.NotNull(p);
            Assert.Empty(p!.Events);
            Assert.Empty(p.Characters);
            Assert.NotNull(p.Timing);
        }

        [Fact]
        public void null要素のイベントは除去される()
        {
            var p = Load("""{ "version": 1, "events": [ null, { "no": 1, "type": "serif" } ] }""");
            Assert.NotNull(p);
            Assert.Single(p!.Events);
        }

        [Fact]
        public void 負やNaN不可の時間はゼロへ丸める()
        {
            var p = Load("""{ "version": 1, "events": [ { "no": 1, "type": "serif", "start": -5, "seconds": -1 } ] }""");
            Assert.NotNull(p);
            Assert.Equal(0, p!.Events[0].Start);
            Assert.Equal(0, p.Events[0].Seconds);
        }

        [Fact]
        public void typeがnullのイベントは空文字へ補正()
        {
            var p = Load("""{ "version": 1, "events": [ { "no": 1, "type": null } ] }""");
            Assert.NotNull(p);
            Assert.Equal("", p!.Events[0].Type);
            Assert.NotNull(p.Events[0].Se);
        }

        [Fact]
        public void pluginMinが現行版より高ければ警告()
        {
            var p = Load("""{ "version": 1, "pluginMin": "99.0.0", "events": [] }""");
            Assert.NotNull(p); // 読めはする(警告のみ)
            Assert.Contains(logs, l => l.Contains("v99.0.0 以上を想定"));
        }

        [Fact]
        public void pluginLatestが現行版より高ければ更新案内()
        {
            var p = Load("""{ "version": 1, "pluginLatest": "99.0.0", "events": [] }""");
            Assert.NotNull(p);
            Assert.Contains(logs, l => l.Contains("新しいプラグイン v99.0.0"));
        }

        [Fact]
        public void pluginMinを満たしていれば警告なし()
        {
            var p = Load($$"""{ "version": 1, "pluginMin": "{{PackSchema.PluginVersion}}", "events": [] }""");
            Assert.NotNull(p);
            Assert.DoesNotContain(logs, l => l.Contains("以上を想定"));
        }

        [Fact]
        public void キー名は大文字小文字を区別しない()
        {
            var p = Load("""{ "Version": 1, "Events": [ { "No": 7, "Type": "serif" } ] }""");
            Assert.NotNull(p);
            Assert.Equal(7, p!.Events[0].No);
        }
    }
}
