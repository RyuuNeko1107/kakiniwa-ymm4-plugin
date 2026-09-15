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

        [Fact]
        public void パック内の相対パスには拡張子制限がない()
        {
            // 仕様固定: 拡張子の許可リスト(AbsAllowedExts)が効くのは絶対パス参照のみ。
            // パック内(packDir 配下に封じ込め済み)は信頼境界の内側なので、.txt のような
            // 素材外の拡張子でも相対パスなら解決される。これを絞る変更は仕様変更として扱う。
            Assert.Equal(Path.GetFullPath(Path.Combine(dir, "secret.txt")), R(dir, "secret.txt"));
        }
    }

    // ---------- 書き込み先の解決(ResolveInPackForWrite)は配下限定 ----------

    public class ResolveInPackForWriteTests : IDisposable
    {
        readonly string dir;
        public ResolveInPackForWriteTests()
        {
            dir = Path.Combine(Path.GetTempPath(), "kakiniwa-test-w-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "portraits"));
            File.WriteAllBytes(Path.Combine(dir, "portraits", "a.psd"), new byte[] { 1 });
        }
        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

        static string? W(string packDir, string? path) => (string?)Priv.Call("ResolveInPackForWrite", packDir, path);

        [Fact]
        public void 絶対パスのPSDは実在してもnull()
        {
            // 読取用の ResolveInPack は素材拡張子付きの絶対パスを許すが、
            // 書込先(サイドカーを隣に書く)に絶対パスを許すとパック外の PSD を狙える
            var abs = Path.Combine(dir, "portraits", "a.psd");
            Assert.NotNull((string?)Priv.Call("ResolveInPack", dir, abs));
            Assert.Null(W(dir, abs));
        }

        [Fact]
        public void 親ディレクトリ脱出はnull()
        {
            var outside = Path.Combine(Path.GetTempPath(), "kakiniwa-outside-w-" + Guid.NewGuid().ToString("N") + ".psd");
            File.WriteAllBytes(outside, new byte[] { 1 });
            try { Assert.Null(W(dir, "../" + Path.GetFileName(outside))); }
            finally { File.Delete(outside); }
        }

        [Fact]
        public void 配下の相対パスは解決される()
        {
            Assert.Equal(Path.GetFullPath(Path.Combine(dir, "portraits", "a.psd")), W(dir, "portraits/a.psd"));
        }

        [Fact]
        public void UNCパスはnull()
            => Assert.Null(W(dir, @"\\attacker\share\a.psd"));
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
        public void MP3のXingヘッダがあれば総フレーム数から正確な尺()
        {
            // MPEG1 Layer3 44.1kHz のフレームヘッダ + "Xing"(オフセット36)。
            // frames=1000 → 1000 * 1152 / 44100 = 26.122448…秒(手計算)。
            // CBR 推定なら 16000*8/128000=1.0 秒になるので、Xing 経路を通った事実と区別できる。
            var bytes = new byte[16000];
            bytes[0] = 0xFF; bytes[1] = 0xFB; bytes[2] = 0x90; bytes[3] = 0x00;
            Encoding.ASCII.GetBytes("Xing").CopyTo(bytes, 36);
            bytes[43] = 1; // flags(ビッグエンディアン)の最下位ビット=フレーム数あり
            // 総フレーム数 1000(ビッグエンディアン)
            bytes[44] = 0; bytes[45] = 0; bytes[46] = 0x03; bytes[47] = 0xE8;
            var p = Write("vbr.mp3", bytes);
            Assert.Equal(26.122, Importer.AudioDurationSeconds(p)!.Value, 3);
        }

        [Fact]
        public void OGGのVorbisはヘッダのレートとgranuleから尺()
        {
            // 先頭ページ: OggS + "\x01vorbis" 識別ヘッダ(rate はヘッダ内 LE)
            var head = new byte[128];
            Encoding.ASCII.GetBytes("OggS").CopyTo(head, 0);
            head[28] = 0x01;
            Encoding.ASCII.GetBytes("vorbis").CopyTo(head, 29); // vi = 29
            // vi+11..14 = 40..43: サンプルレート 44100 LE
            Le32(44100).CopyTo(head, 40);
            // 最終ページ: granule 88200 → 88200 / 44100 = 2.0 秒(手計算)
            var tailPage = new byte[32];
            Encoding.ASCII.GetBytes("OggS").CopyTo(tailPage, 0);
            Le32(88200).CopyTo(tailPage, 6);
            var ms = new MemoryStream();
            ms.Write(head); ms.Write(tailPage);
            var p = Write("v.ogg", ms.ToArray());
            Assert.Equal(2.0, Importer.AudioDurationSeconds(p)!.Value, 3);
        }

        [Fact]
        public void Opus拡張子のファイルもOGGとして読める()
        {
            var head = new byte[128];
            Encoding.ASCII.GetBytes("OggS").CopyTo(head, 0);
            Encoding.ASCII.GetBytes("OpusHead").CopyTo(head, 28);
            // granule 144000 → 144000 / 48000 = 3.0 秒(手計算)
            var tailPage = new byte[32];
            Encoding.ASCII.GetBytes("OggS").CopyTo(tailPage, 0);
            Le32(144000).CopyTo(tailPage, 6);
            var ms = new MemoryStream();
            ms.Write(head); ms.Write(tailPage);
            var p = Write("a.opus", ms.ToArray());
            Assert.Equal(3.0, Importer.AudioDurationSeconds(p)!.Value, 3);
        }

        [Fact]
        public void WAVの奇数サイズチャンクはパディング込みでスキップされる()
        {
            // data の前に奇数サイズ(3バイト)の LIST チャンクを置く。RIFF 規約では
            // 奇数サイズのチャンク本体の後に 1 バイトのパディングが入る。パディングを
            // スキップしない実装だと以降のチャンク境界が 1 バイトずれて読めなくなる。
            var ms = new MemoryStream();
            void W(byte[] b) => ms.Write(b, 0, b.Length);
            W(Encoding.ASCII.GetBytes("RIFF")); W(Le32(0)); W(Encoding.ASCII.GetBytes("WAVE"));
            W(Encoding.ASCII.GetBytes("LIST")); W(Le32(3)); W(new byte[] { 1, 2, 3, 0 }); // 3+パディング1
            W(Encoding.ASCII.GetBytes("fmt ")); W(Le32(16));
            W(new byte[] { 1, 0, 1, 0 });
            W(Le32(8000));
            W(Le32(16000)); // 平均バイトレート
            W(new byte[] { 2, 0, 16, 0 });
            W(Encoding.ASCII.GetBytes("data")); W(Le32(48000)); // 48000 / 16000 = 3.0 秒(手計算)
            var p = Write("pad.wav", ms.ToArray());
            Assert.Equal(3.0, Importer.AudioDurationSeconds(p)!.Value, 3);
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

    // ---------- 立ち絵の実ファイル解決(ResolvePortrait) ----------
    //
    // ★優先順位(①現立ち絵の該当表情 → ②現立ち絵自体のPSD → ③キャラ直下のflat表情)は
    //   再発バグの核心: 順序が逆だと立ち絵を切り替えたのに切替前のPSDを黙って返し、
    //   別の顔で描かれる(2026-08-08 メタ監査)。ここが未テストだと順序の入れ替えが
    //   全緑のまま通る(2026-08-09 監査第4弾で追加)。

    public class ResolvePortraitTests : IDisposable
    {
        readonly string dir;
        public ResolvePortraitTests()
        {
            dir = Path.Combine(Path.GetTempPath(), "kakiniwa-portrait-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "chars"));
            foreach (var f in new[] { "p1-smile.psd", "p1-base.psd", "flat-normal.psd", "byfile.psd" })
                File.WriteAllBytes(Path.Combine(dir, "chars", f), new byte[] { 1 });
        }
        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

        string? R(PackCharacter? c, PackPortrait? p, string? expression)
            => (string?)Priv.Call("ResolvePortrait", dir, c, p, expression);

        string Abs(string name) => Path.GetFullPath(Path.Combine(dir, "chars", name));

        static PackPsd Psd(string name) => new PackPsd { Path = "chars/" + name };

        /// <summary>①〜③がすべて埋まったキャラ(どれが選ばれたかはファイル名で区別できる)</summary>
        static PackCharacter Chara() => new PackCharacter
        {
            Id = "a",
            Expressions = new List<PackExpression>
            {
                new PackExpression { Id = "normal", Default = true, Psd = Psd("flat-normal.psd") },
            },
        };

        static PackPortrait Portrait(bool withExpressions = true) => new PackPortrait
        {
            Id = "p1",
            Psd = Psd("p1-base.psd"),
            Expressions = withExpressions
                ? new List<PackExpression>
                  {
                      new PackExpression { Id = "smile", Label = "にっこり", Psd = Psd("p1-smile.psd") },
                  }
                : new List<PackExpression>(),
        };

        [Fact]
        public void 現立ち絵の該当表情が最優先で選ばれる()
            => Assert.Equal(Abs("p1-smile.psd"), R(Chara(), Portrait(), "smile"));

        [Fact]
        public void 表情はLabelでも引ける()
            => Assert.Equal(Abs("p1-smile.psd"), R(Chara(), Portrait(), "にっこり"));

        [Fact]
        public void 表情名が立ち絵に無ければ立ち絵自体のPSDがflatより先()
        {
            // flat側(chara.Expressions)にも normal がある状態で、立ち絵に無い表情を指定。
            // ②より③が先に見られる変異だと flat-normal.psd(=切替前の顔)が返ってしまう。
            Assert.Equal(Abs("p1-base.psd"), R(Chara(), Portrait(), "normal"));
        }

        [Fact]
        public void 表情の指定なしでも現立ち絵が優先される()
        {
            // 立ち絵が表情を持たなければ、既定表情(flat)ではなく立ち絵のPSDで描く
            Assert.Equal(Abs("p1-base.psd"), R(Chara(), Portrait(withExpressions: false), null));
        }

        [Fact]
        public void 表情の指定なしなら立ち絵の既定表情()
        {
            var p = Portrait();
            p.Expressions[0].Default = true;
            Assert.Equal(Abs("p1-smile.psd"), R(Chara(), p, null));
        }

        [Fact]
        public void 立ち絵なしの従来キャラはflat表情から()
        {
            Assert.Equal(Abs("flat-normal.psd"), R(Chara(), null, "normal"));
            Assert.Equal(Abs("flat-normal.psd"), R(Chara(), null, null)); // 既定表情
        }

        [Fact]
        public void PortraitDirとFileの組でも解決できる()
        {
            var c = new PackCharacter
            {
                Id = "a",
                PortraitDir = "chars",
                Expressions = new List<PackExpression>
                {
                    new PackExpression { Id = "normal", Default = true, File = "byfile.psd" },
                },
            };
            Assert.Equal(Abs("byfile.psd"), R(c, null, null));
        }

        [Fact]
        public void キャラなしはnull() => Assert.Null(R(null, Portrait(), "smile"));

        [Fact]
        public void 実ファイルが無ければnull()
        {
            var c = Chara();
            c.Expressions[0].Psd = Psd("nai.psd");
            Assert.Null(R(c, null, null));
        }

        [Fact]
        public void パック外へ脱出するパスはnull()
        {
            // rel はパック由来=攻撃者制御しうる。ResolveInPack の封じ込めを通ること
            var outside = Path.Combine(Path.GetTempPath(),
                "kakiniwa-portrait-outside-" + Guid.NewGuid().ToString("N") + ".psd");
            File.WriteAllBytes(outside, new byte[] { 1 });
            try
            {
                var c = Chara();
                c.Expressions[0].Psd = new PackPsd { Path = "../" + Path.GetFileName(outside) };
                Assert.Null(R(c, null, null));
            }
            finally { File.Delete(outside); }
        }
    }

    // ---------- 素材参照の解決とスキップ案内(ResolveAsset) ----------

    public class ResolveAssetTests : IDisposable
    {
        readonly string dir;
        public ResolveAssetTests()
        {
            dir = Path.Combine(Path.GetTempPath(), "kakiniwa-asset-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(dir, "assets"));
            File.WriteAllBytes(Path.Combine(dir, "assets", "a.png"), new byte[] { 1 });
        }
        public void Dispose() { try { Directory.Delete(dir, true); } catch { } }

        static (string?, List<string>) Run(string packDir, PackAssetRef? asset, int no)
        {
            var skipped = new List<string>();
            var r = (string?)Priv.Call("ResolveAsset", packDir, asset, skipped, new PackEvent { No = no });
            return (r, skipped);
        }

        [Fact]
        public void assetがnullならnullで書き庭側の案内が積まれる()
        {
            var (r, skipped) = Run(dir, null, 7);
            Assert.Null(r);
            var msg = Assert.Single(skipped);
            Assert.Contains("書き庭側で見つかりませんでした", msg);
            Assert.Contains("no.7", msg);
            Assert.Contains("(名前なし)", msg);
        }

        [Fact]
        public void Pathがnullなら素材名入りの書き庭側案内()
        {
            var (r, skipped) = Run(dir, new PackAssetRef { Name = "雨のBGM", Path = null }, 12);
            Assert.Null(r);
            var msg = Assert.Single(skipped);
            Assert.Contains("書き庭側で見つかりませんでした", msg);
            Assert.Contains("no.12", msg);
            Assert.Contains("雨のBGM", msg);
        }

        [Fact]
        public void パック外脱出のPathはnullでファイル側の案内()
        {
            var (r, skipped) = Run(dir, new PackAssetRef { Name = "x", Path = "../evil.png" }, 3);
            Assert.Null(r);
            var msg = Assert.Single(skipped);
            Assert.Contains("素材ファイルが見つかりません", msg);
            Assert.Contains("no.3", msg);
        }

        [Fact]
        public void 存在しないファイルもファイル側の案内()
        {
            var (r, skipped) = Run(dir, new PackAssetRef { Name = "x", Path = "assets/nai.png" }, 5);
            Assert.Null(r);
            var msg = Assert.Single(skipped);
            Assert.Contains("素材ファイルが見つかりません", msg);
            Assert.Contains("no.5", msg);
        }

        [Fact]
        public void 正常解決なら絶対パスでskippedは空()
        {
            var (r, skipped) = Run(dir, new PackAssetRef { Name = "a", Path = "assets/a.png" }, 1);
            Assert.Equal(Path.GetFullPath(Path.Combine(dir, "assets", "a.png")), r);
            Assert.Empty(skipped);
        }
    }

    // ---------- レイヤーパス一覧の逆変換(ConvertPathsToSlash) ----------

    public class ConvertPathsToSlashTests
    {
        static List<string> C(object? listObj) => (List<string>)Priv.Call("ConvertPathsToSlash", listObj)!;

        [Fact]
        public void nullや文字列以外の要素はスキップされる()
        {
            var src = new List<object?> { null, 123, "", "\u001C体\u001D0\u001C顔\u001D0" };
            Assert.Equal(new[] { "体/顔" }, C(src));
        }

        [Fact]
        public void Ymm4実形式はスラッシュ区切りへ変換し形式外はそのまま()
        {
            var src = new[] { "\u001Ca\u001D0\u001Cb\u001D0\u001Cc\u001D0", "ただの名前" };
            Assert.Equal(new[] { "a/b/c", "ただの名前" }, C(src));
        }

        [Fact]
        public void 列挙できない引数は空リスト()
        {
            Assert.Empty(C(null));
            Assert.Empty(C(42));
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

    // ---------- 小物レーンの貪欲彩色(AssignGreedySlots) ----------

    public class AssignGreedySlotsTests
    {
        // intervals[i] = (start, end)。index列は 0..n-1 を開始時刻昇順で渡す(実装の前提と同じ)
        static System.Collections.Generic.Dictionary<int, int> Slots(
            params (double start, double end)[] intervals)
        {
            var idx = System.Linq.Enumerable.Range(0, intervals.Length).ToList();
            System.Func<int, double> startOf = i => intervals[i].start;
            System.Func<int, double> endOf = i => intervals[i].end;
            return (System.Collections.Generic.Dictionary<int, int>)
                Priv.Call("AssignGreedySlots", idx, startOf, endOf)!;
        }

        [Fact]
        public void 三つ重なると別レーン_離れた四つ目は最小レーン再利用()
        {
            // (0,10)(1,10)(2,10) は互いに重なる → レーン 0,1,2
            // (12,20) は全部の後 → 空いた最小レーン 0 を再利用
            var s = Slots((0, 10), (1, 10), (2, 10), (12, 20));
            Assert.Equal(0, s[0]);
            Assert.Equal(1, s[1]);
            Assert.Equal(2, s[2]);
            Assert.Equal(0, s[3]);
        }

        [Fact]
        public void 入れ替わり_背中合わせは同一レーン()
        {
            // 前の終了時刻ちょうどに次が始まる(退場→登場)は同じレーンに畳む
            var s = Slots((0, 5), (5, 10));
            Assert.Equal(0, s[0]);
            Assert.Equal(0, s[1]);
        }

        [Fact]
        public void 空いた最小のレーンを選ぶ()
        {
            // (0,10) → 0, (0,3) → 1, (4,10): レーン1だけ空いている(0 は 10 まで塞がる)→ 1
            var s = Slots((0, 10), (0, 3), (4, 10));
            Assert.Equal(0, s[0]);
            Assert.Equal(1, s[1]);
            Assert.Equal(1, s[2]);
        }

        [Fact]
        public void 重なりが一瞬でもあれば別レーン()
        {
            // (0,5.5) と (5,10) は 0.5 秒重なる → 別レーン
            var s = Slots((0, 5.5), (5, 10));
            Assert.Equal(0, s[0]);
            Assert.Equal(1, s[1]);
        }
    }

    // ---------- 再配置の階段関数(ShiftDeltaAt) ----------

    public class ShiftDeltaAtTests
    {
        static int Delta(System.Collections.Generic.List<System.Tuple<int, int>> shifts, int f)
            => (int)Priv.Call("ShiftDeltaAt", shifts, f)!;

        static readonly System.Collections.Generic.List<System.Tuple<int, int>> 階段 =
            new System.Collections.Generic.List<System.Tuple<int, int>>
            {
                System.Tuple.Create(100, -5),
                System.Tuple.Create(200, 3),
            };

        [Fact]
        public void 区間の前は0()
        {
            Assert.Equal(0, Delta(階段, 0));
            Assert.Equal(0, Delta(階段, 99));
        }

        [Fact]
        public void 境界ちょうどからその区間のシフトが効く()
        {
            Assert.Equal(-5, Delta(階段, 100));
            Assert.Equal(-5, Delta(階段, 150));
            Assert.Equal(-5, Delta(階段, 199));
        }

        [Fact]
        public void 後段以降は後段の累積シフト()
        {
            Assert.Equal(3, Delta(階段, 200));
            Assert.Equal(3, Delta(階段, 100000));
        }

        [Fact]
        public void 空の階段は常に0()
        {
            Assert.Equal(0, Delta(new System.Collections.Generic.List<System.Tuple<int, int>>(), 42));
        }
    }

    // ---------- 行内立ち絵の切替計画(FindPortrait / PlanPortraitSwitch) ----------
    //
    // ★portrait イベントは「切替(セリフ開始)→serif→戻し(セリフ終了)」の順で届く契約。
    //   切替時にキャラが画面に居れば、既存アイテムの切断+新しい立ち絵の即置き直しを計画する。
    //   未登録の立ち絵名は null=何もしない(既定PSDフォールバックでの無駄な切断を防ぐ)。

    public class PortraitSwitchPlanTests
    {
        static Dictionary<string, PackPortrait> Map() => new()
        {
            ["p1"] = new PackPortrait { Id = "p1" },
            ["p2"] = new PackPortrait { Id = "p2", Ymm4Name = "ゆっくり霊夢" },
        };

        [Fact]
        public void FindPortraitはIdで引ける()
            => Assert.Equal("p1", Importer.FindPortrait(Map(), "p1")!.Id);

        [Fact]
        public void FindPortraitはYmm4Nameの保険でも引ける()
            => Assert.Equal("p2", Importer.FindPortrait(Map(), "ゆっくり霊夢")!.Id);

        [Fact]
        public void FindPortraitは未登録名でnull()
            => Assert.Null(Importer.FindPortrait(Map(), "nai"));

        [Fact]
        public void 表示中なら切断と置き直しを計画する()
        {
            // fps=30: placed=1.0s, 切替=2.5s, 末尾=10.0s(手計算の固定値)
            var plan = Importer.PlanPortraitSwitch(Map(), "p2", 1.0, 2.5, 10.0, 30, 0);
            Assert.NotNull(plan);
            Assert.Equal("p2", plan!.PortraitId); // 正規idに解決(Ymm4Name名で来ても同じ)
            Assert.True(plan.Replace);
            Assert.Equal(45, plan.CutLength);      // (2.5-1.0)*30
            Assert.Equal(75, plan.NewStartFrame);  // 2.5*30
            Assert.Equal(225, plan.NewLength);     // (10.0-2.5)*30
        }

        [Fact]
        public void Ymm4Name表記でも正規idへ解決される()
        {
            var plan = Importer.PlanPortraitSwitch(Map(), "ゆっくり霊夢", null, 0.0, 5.0, 30, 0);
            Assert.Equal("p2", plan!.PortraitId);
        }

        [Fact]
        public void frameOffsetは開始フレームにだけ効く()
        {
            var plan = Importer.PlanPortraitSwitch(Map(), "p1", 1.0, 2.5, 10.0, 30, 100);
            Assert.Equal(175, plan!.NewStartFrame); // 75 + 100
            Assert.Equal(45, plan.CutLength);       // 長さはオフセット無関係
            Assert.Equal(225, plan.NewLength);
        }

        [Fact]
        public void 未登場なら状態更新のみで置き直さない()
        {
            var plan = Importer.PlanPortraitSwitch(Map(), "p1", null, 2.5, 10.0, 30, 0);
            Assert.NotNull(plan);
            Assert.False(plan!.Replace);
            Assert.Equal(0, plan.CutLength);
        }

        [Fact]
        public void 未登録名はnullで何も計画しない()
            => Assert.Null(Importer.PlanPortraitSwitch(Map(), "nai", 1.0, 2.5, 10.0, 30, 0));

        [Fact]
        public void 同時刻の切断や末尾ちょうどでも最低1フレーム()
        {
            var plan = Importer.PlanPortraitSwitch(Map(), "p1", 2.5, 2.5, 2.5, 30, 0);
            Assert.Equal(1, plan!.CutLength);
            Assert.Equal(1, plan.NewLength);
        }
    }

    // ---------- 立ち絵の配置先キャラ判定(PortraitAssignedName / FindYmm4CharacterByName) ----------
    //
    // ★別YMM4キャラに割り当てた立ち絵(Ymm4Name あり)へ OverrideTachieFilePath でPSDを
    //   強制上書きすると、立ち絵タイプ不一致でYMM4ごと落ちる(実機クラッシュ 2026-08-09)。
    //   Ymm4Name の有無で「別キャラで置く/基底キャラ+PSD差し替え(従来)」を分岐する。

    public class PortraitAssignedNameTests
    {
        [Fact]
        public void null立ち絵は従来動作のnull()
            => Assert.Null(Importer.PortraitAssignedName(null));

        [Fact]
        public void Ymm4Name未指定は従来動作のnull()
        {
            Assert.Null(Importer.PortraitAssignedName(new PackPortrait { Id = "p1" }));
            Assert.Null(Importer.PortraitAssignedName(new PackPortrait { Id = "p1", Ymm4Name = "" }));
        }

        [Fact]
        public void Ymm4Name指定ありはその名前を返す()
            => Assert.Equal("ゆっくり霊夢", Importer.PortraitAssignedName(
                new PackPortrait { Id = "p2", Ymm4Name = "ゆっくり霊夢" }));
    }

    public class FindYmm4CharacterByNameTests
    {
        static System.Collections.Generic.List<CharacterChoice> Chars() => new()
        {
            new CharacterChoice { Display = "霊夢", Name = "霊夢", Model = new object() },
            new CharacterChoice { Display = "魔理沙", Name = "魔理沙", Model = null }, // Model 無し
        };

        [Fact]
        public void 名前一致かつModelありなら返す()
        {
            var c = Importer.FindYmm4CharacterByName(Chars(), "霊夢");
            Assert.NotNull(c);
            Assert.Equal("霊夢", c!.Name);
        }

        [Fact]
        public void Modelが無いキャラは一致してもnull()
            => Assert.Null(Importer.FindYmm4CharacterByName(Chars(), "魔理沙"));

        [Fact]
        public void 未登録名はnull()
            => Assert.Null(Importer.FindYmm4CharacterByName(Chars(), "妖夢"));
    }

    // ---------- 配置キャラの決定(DecideTachieHostName) ----------
    //
    // ★ボイス・立ち絵・表情アイテムはすべてこの決定に従う。音声話者も同じキャラで
    //   生成しないと、表情アイテム・口パクの CharacterName 対応が切れる(追加指示 2026-08-09)。

    public class DecideTachieHostNameTests
    {
        static (string?, bool) D(string? explicitName, string? assigned, string baseName)
        {
            var t = Importer.DecideTachieHostName(explicitName, assigned, baseName);
            return (t.Item1, t.Item2);
        }

        [Fact]
        public void 指定なしは基底キャラでPSD差し替え可()
            => Assert.Equal((null, true), D(null, null, "霊夢"));

        [Fact]
        public void Ymm4Name指定は別キャラで上書き不可()
            => Assert.Equal(("魔理沙", false), D(null, "魔理沙", "霊夢"));

        [Fact]
        public void 明示選択はYmm4Nameより優先()
            => Assert.Equal(("妖夢", false), D("妖夢", "魔理沙", "霊夢"));

        [Fact]
        public void 基底と同名の指定は従来の差し替えに畳む()
        {
            Assert.Equal((null, true), D("霊夢", null, "霊夢"));       // 明示選択が基底と同じ
            Assert.Equal((null, true), D(null, "霊夢", "霊夢"));       // Ymm4Name が基底と同じ
            Assert.Equal((null, true), D("霊夢", "魔理沙", "霊夢"));   // 明示選択が優先して基底へ
        }

        [Fact]
        public void 空文字は未指定扱い()
            => Assert.Equal((null, true), D("", "", "霊夢"));
    }

    // ---------- 実尺再配置での「portraitで切られた立ち絵」の終端合わせ ----------
    //
    // ★戻しイベントはセリフの「終了時刻(推定)」で切るが、シフトの階段はボイス開始フレーム
    //   でしか変わらないため、再配置後も終端が推定尺のまま残る。対応ボイスの実終端に合わせる。

    public class PortraitCutTachieNewLengthTests
    {
        [Fact]
        public void 対応ボイスの実終端に合わせる()
        {
            // 立ち絵: newFrame=75, 推定Length=60(推定終端135)。
            // ボイス: newFrame=75, 実Length=90 → 実終端 165 → 新Length 90(手計算)
            Assert.Equal(90, Importer.PortraitCutTachieNewLength(75, 60, 75, 90));
        }

        [Fact]
        public void 立ち絵がボイスより前から出ていても終端で合わせる()
        {
            // 立ち絵 newFrame=60・ボイス実終端 75+90=165 → 新Length 105(手計算)
            Assert.Equal(105, Importer.PortraitCutTachieNewLength(60, 75, 75, 90));
        }

        [Fact]
        public void 対応ボイスが無ければ触らない()
            => Assert.Null(Importer.PortraitCutTachieNewLength(75, 60, null, 90));

        [Fact]
        public void ボイス実尺が未確定なら触らない()
            => Assert.Null(Importer.PortraitCutTachieNewLength(75, 60, 75, 1));

        [Fact]
        public void 退化した1フレーム項目は触らない()
        {
            // 連続する行内切替で「戻し」と次の「切替」が同時刻のときに残る1フレーム項目。
            // これをボイス実尺へ伸ばすと次の立ち絵と全面的に重なる。
            Assert.Null(Importer.PortraitCutTachieNewLength(75, 1, 75, 90));
        }

        [Fact]
        public void 終端が開始以前へ逆転するなら触らない()
            => Assert.Null(Importer.PortraitCutTachieNewLength(200, 60, 75, 90)); // 165-200 < 1
    }

    // ---------- 書き庭(TS)との timeline.json 契約 ----------
    //
    // ★この境界を守っていたのは、書き庭側から C# ソースを文字列で grep するテスト
    //   (src/features/auditBatch34.test.ts)だけだった。肝心の「フィールド名の対応」は
    //   1つも見ていないので、TS 側で SpeakerName を改名しても、こちらで SpeakerName を
    //   消しても、両側とも緑のまま通り、プラグインが静かにデータを落とす。
    //
    // 代わりに、両側が同じ1つのファイルを読む:
    //   ymm4-plugin/fixtures/timeline.golden.json
    //     ← 書き庭側(src/core/ymm4/packContract.test.ts)が
    //        「実物のビルダの出力と一致するか」を見る
    //     ← ここが「読んだ結果が正しいプロパティに入るか」を見る
    //
    // フィクスチャを更新するときは、必ず両方のテストを見直すこと。
    public class 黄金のtimelineJsonTests
    {
        static string GoldenFile()
        {
            // ビルド出力(bin/…)から ymm4-plugin/fixtures を探して遡る
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var cand = Path.Combine(dir.FullName, "fixtures", "timeline.golden.json");
                if (File.Exists(cand)) return cand;
                dir = dir.Parent;
            }
            throw new FileNotFoundException(
                "ymm4-plugin/fixtures/timeline.golden.json が見つかりません。" +
                "書き庭側の packContract.test.ts が生成します。");
        }

        static Pack Load()
        {
            // LoadPack は「パックのフォルダ」を受け取り、その中の timeline.json を読む。
            // 黄金ファイルは名前で用途が分かるようにしてあるので、実際のパックと同じ形へ
            // 一時フォルダに写してから読む(実運用と同じ経路を通す)。
            var tmp = Path.Combine(Path.GetTempPath(), "kakiniwa-golden-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                File.Copy(GoldenFile(), Path.Combine(tmp, "timeline.json"));
                var logs = new List<string>();
                var p = Importer.LoadPack(tmp, s => logs.Add(s));
                Assert.True(p != null, "黄金の timeline.json を読めない: " + string.Join(" / ", logs));
                return p!;
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        [Fact]
        public void 版と全体の情報が入る()
        {
            var p = Load();
            Assert.Equal(1, p.Version);
            Assert.Equal("0.2.2", p.PluginMin);          // 書き庭の PLUGIN_MIN_VERSION
            Assert.Equal("0.4.1", p.PluginLatest);        // 書き庭の PLUGIN_LATEST_VERSION
            Assert.Equal(PackSchema.PluginVersion, p.PluginLatest); // 実版と案内が一致
            Assert.Equal("つばめ珈琲", p.Title);
            Assert.Equal("朝のつばめ", p.Episode);
            Assert.Equal("書き庭", p.Generator!.Name);
        }

        // ---------- セリフの中の改行(書き庭の [改行]) ----------
        //
        // ★パックは LF で書かれるが、YMM4/WPF の行区切りは CRLF。LF のまま渡すと
        //   字幕が1行のままだったり、見えない文字として残ったりする。動く環境では
        //   気づけないので、受け入れ口でそろえていることをここで押さえる。

        [Fact]
        public void 折り返しのあるセリフを_CRLF_へそろえて読む()
        {
            var 折れた行 = Load().Events.First(
                e => e.Type == "serif" && e.Text != null && e.Text.Contains("\r"));
            // 期待値は手書きの固定リテラル(黄金ファイルの「二行で\n見せる。」に対応)
            Assert.Equal("二行で\r\n見せる。", 折れた行.Text);
            // LF 単独は残っていない(CRLF だけ)
            Assert.DoesNotContain("\n", 折れた行.Text!.Replace("\r\n", ""));
        }

        [Fact]
        public void 読み_声_には折り返しを入れない()
        {
            var 折れた行 = Load().Events.First(
                e => e.Type == "serif" && e.Text != null && e.Text.Contains("\r"));
            // 字幕は2行・声は1続き。ここが崩れると読み上げが改行を読む/詰まる
            Assert.Equal("二行で見せる。", 折れた行.Reading);
        }

        [Fact]
        public void 読みの無いパックでも_折れていれば読みを補う()
        {
            // 手編集・別ツール製のパック。書き庭は折れた行に必ず reading を入れるので
            // 通らない道だが、通ると改行がそのまま読み上げへ渡る
            var tmp = Path.Combine(Path.GetTempPath(), "kakiniwa-br-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                File.WriteAllText(Path.Combine(tmp, "timeline.json"),
                    "{\"version\":1,\"events\":[{\"type\":\"serif\"," +
                    "\"text\":\"一行目\\n二行目\"}]}");
                var p2 = Importer.LoadPack(tmp, _ => { });
                Assert.NotNull(p2);
                var ev = p2!.Events[0];
                Assert.Equal("一行目\r\n二行目", ev.Text);
                Assert.Equal("一行目二行目", ev.Reading);
            }
            finally { try { Directory.Delete(tmp, true); } catch { } }
        }

        [Fact]
        public void 折り返しの無いセリフには読みを足さない()
        {
            // 補いが効きすぎると、ルビも読み辞書も無い行に reading が付き、
            // YMM4 側で Pronounce が上書きされる(発音の指定が増える)
            var 素の行 = Load().Events.First(
                e => e.Type == "serif" && e.Text == "着替えてきた。");
            Assert.True(string.IsNullOrEmpty(素の行.Reading));
        }

        [Fact]
        public void 尺の見積り設定が入る()
        {
            var t = Load().Timing;
            Assert.Equal(6, t.CharsPerSecond);
            Assert.Equal(0.2, t.SerifGap);
            Assert.Equal(1, t.StageSeconds);
        }

        [Fact]
        public void キャラと複数立ち絵が入る()
        {
            var sora = Load().Characters.First(c => c.Id == "sora");
            Assert.Equal("ソラ", sora.Name);
            Assert.Equal("つばめソラ", sora.Ymm4Name);
            Assert.Equal("assets/portraits/sora", sora.PortraitDir);
            Assert.NotNull(sora.Portraits);
            Assert.Equal(2, sora.Portraits!.Count);

            var 制服 = sora.Portraits.First(x => x.Id == "制服");
            Assert.True(制服.Default);
            Assert.Equal("assets/portraits/sora/制服.psd", 制服.Psd!.Path);
            var 私服 = sora.Portraits.First(x => x.Id == "私服");
            Assert.False(私服.Default);
            Assert.Equal("つばめソラ私服", 私服.Ymm4Name); // 立ち絵ごとのYMM4キャラ割り当て
        }

        [Fact]
        public void 表情のレイヤーIDが入る()
        {
            var sora = Load().Characters.First(c => c.Id == "sora");
            var 制服 = sora.Portraits!.First(x => x.Id == "制服");
            var ドヤ = 制服.Expressions.First(e => e.Id == "ドヤ");
            Assert.Equal(new[] { "キャラクター", "キャラクター/!口/*にやにや" }, ドヤ.Psd!.Layers);
            // 書き庭が書き出し時にPSDを読んで付ける(これが無いと表情を切り替えられない)
            Assert.Equal(new[] { 1, 7 }, ドヤ.Psd.LayerIds);
        }

        [Fact]
        public void 声設定が参考情報として入る()
        {
            var sora = Load().Characters.First(c => c.Id == "sora");
            Assert.Equal("voicevox", sora.Voice!.Engine);
            Assert.Equal("3", sora.Voice.Preset);
            Assert.Equal(1.1, sora.Voice.Params!["speedScale"]);
        }

        [Fact]
        public void セリフの各項目が入る()
        {
            var p = Load();
            var serif = p.Events.First(e => e.Type == "serif");
            Assert.Equal("朝のつばめ", serif.Scene);
            Assert.Equal("sora", serif.Speaker);
            Assert.Equal("ソラ", serif.SpeakerName);
            Assert.Equal("ドヤ", serif.Expression);
            Assert.Contains("いらっしゃい", serif.Text);
            // [間:0.5] は seconds(見積り)とは別に届く。プラグインはボイス実尺で並べ直すので、
            // ここが落ちると台本で指定した溜めが YMM4 では詰まる
            Assert.Equal(0.5, serif.Pause);
        }

        [Fact]
        public void SEが名前と場所つきで入る()
        {
            var se = Load().Events.First(e => e.Se.Count > 0).Se[0];
            Assert.Equal("ドンッ", se.Name);
            Assert.Equal("assets/se/ドンッ.wav", se.Path);
        }

        [Fact]
        public void 配置の座標とPSD実寸が入る()
        {
            var layout = Load().Events.First(e => e.Type == "layout");
            var sora = layout.Positions!.First(q => q.Speaker == "sora");
            Assert.Equal(-28, sora.X);
            // 書き庭が渡すPSDの実寸(px)。これが無いと拡大率をこちらで計算できない
            Assert.Equal(4080, sora.PsdHeight);
            var hina = layout.Positions.First(q => q.Speaker == "hina");
            Assert.Equal(10, hina.Y);
            Assert.Equal(80, hina.Height);
        }

        [Fact]
        public void 退場の印が入る()
        {
            var p = Load();
            var 退場 = p.Events.Where(e => e.Type == "layout")
                .SelectMany(e => e.Positions ?? new List<PackPosition>())
                .FirstOrDefault(q => q.Exit);
            Assert.True(退場 != null, "@配置 ソラ:退場 が exit として届いていない");
        }

        [Fact]
        public void 素材つきの演出が入る()
        {
            var p = Load();
            var prop = p.Events.First(e => e.Type == "prop");
            Assert.Equal("カップ", prop.Asset!.Name);
            Assert.Equal("assets/backgrounds/カップ.png", prop.Asset.Path);
            Assert.Equal(-40, prop.X);
            Assert.Equal(18, prop.Y);
            Assert.Equal(18, prop.Height);
        }

        [Fact]
        public void 立ち絵切替とカスタム演出が名前つきで入る()
        {
            var p = Load();
            var portrait = p.Events.First(e => e.Type == "portrait");
            Assert.Equal("sora", portrait.Speaker);
            Assert.Equal("私服", portrait.Name);
            var custom = p.Events.First(e => e.Type == "custom");
            Assert.Equal("カメラ", custom.Name);
        }

        [Fact]
        public void 契約で扱う型がひととおり届く()
        {
            var types = Load().Events.Select(e => e.Type).ToHashSet();
            // ★14種すべて。以前は12種しか書いておらず、se と window_stop が漏れていた
            //   (一覧の正は TS 側 src/core/ymm4/intermediate.ts の PACK_EVENT_TYPES)。
            //   Importer.Run の switch は起動中 YMM4 依存でテストできないので、
            //   case "se" を消しても赤くなるのはここだけ=この一覧が実質唯一のアンカー。
            //   TS 側は型から導く形に直したのに、C# 側は手書きのまま残っていた(2026-08-16 監査)。
            foreach (var t in new[] { "serif", "stage", "bg", "bgm", "bgm_stop", "se", "custom",
                                      "layout", "window", "window_stop", "prop", "prop_stop",
                                      "effect", "portrait" })
            {
                Assert.True(types.Contains(t), $"黄金の timeline.json に {t} が届いていない");
            }
        }
    }

    // ---------- 配置の一時停止・停止(ImportControl / ChunkRanges) ----------
    public class ImportControlTests
    {
        [Theory]
        [InlineData(0, 100, 0)]
        [InlineData(1, 100, 1)]
        [InlineData(100, 100, 1)]
        [InlineData(101, 100, 2)]
        [InlineData(296, 100, 3)]
        public void 件数を小分けにする範囲の数(int count, int size, int expected)
        {
            Assert.Equal(expected, Importer.ChunkRanges(count, size).Count);
        }

        [Fact]
        public void 範囲は隙間なく全件を覆う()
        {
            var r = Importer.ChunkRanges(296, 100);
            Assert.Equal(new[] { 0, 100 }, r[0]);
            Assert.Equal(new[] { 100, 200 }, r[1]);
            Assert.Equal(new[] { 200, 296 }, r[2]);
            Assert.Empty(Importer.ChunkRanges(5, 0)); // 0 で割らない
        }

        [Fact]
        public async System.Threading.Tasks.Task 何もしなければ即通る()
        {
            var c = new ImportControl();
            await c.CheckpointAsync("x");
        }

        [Fact]
        public async System.Threading.Tasks.Task 一時停止中は再開まで待ち_再開すると通る()
        {
            var c = new ImportControl { PollMs = 10 };
            c.Pause();
            var t = c.CheckpointAsync("x");
            await System.Threading.Tasks.Task.Delay(80);
            Assert.False(t.IsCompleted, "一時停止中なのに通った");
            c.Resume();
            await t; // 再開で抜ける
            Assert.True(t.IsCompletedSuccessfully);
        }

        [Fact]
        public async System.Threading.Tasks.Task 停止すると例外で抜ける_一時停止中でも効く()
        {
            var c = new ImportControl { PollMs = 10 };
            c.Pause();
            var t = c.CheckpointAsync("配置 100/296 件まで");
            await System.Threading.Tasks.Task.Delay(40);
            c.Stop();
            var ex = await Assert.ThrowsAsync<ImportStoppedException>(() => t);
            Assert.Contains("配置 100/296", ex.Message);
            // 以後のチェックポイントも全部止まる
            await Assert.ThrowsAsync<ImportStoppedException>(() => c.CheckpointAsync("次"));
        }
    }

}
