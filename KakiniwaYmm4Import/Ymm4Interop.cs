// リフレクションでYMM4本体(MainModel・Timeline・キャラ設定・PSDプラグイン等)を操作する層。

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
        /// <summary>YMM4に登録済みのキャラクター一覧(TimelineViewModel.Characters をリフレクションで辿る)</summary>
        public static List<CharacterChoice> GetYmm4Characters(Action<string> log)
        {
            var result = new List<CharacterChoice>();
            try
            {
                var mainWindow = Application.Current != null ? Application.Current.MainWindow : null;
                var mainVm = mainWindow != null ? mainWindow.DataContext : null;
                var tlProp = mainVm != null ? mainVm.GetType().GetProperty("ActiveTimelineViewModel") : null;
                var tlVm = tlProp != null ? tlProp.GetValue(mainVm) : null;
                var chProp = tlVm != null ? tlVm.GetType().GetProperty("Characters") : null;
                var list = chProp != null ? chProp.GetValue(tlVm) as System.Collections.IEnumerable : null;
                if (list == null) { log(Tr.T("log.i.noCharList")); return result; }

                foreach (var entry in list)
                {
                    if (entry == null) continue;
                    // entry がキャラモデルそのものか、キャラを抱えるVMかの両対応
                    object model = entry;
                    var nameProp = entry.GetType().GetProperty("Name");
                    if (nameProp == null)
                    {
                        var inner = entry.GetType().GetProperty("Character");
                        var innerVal = inner != null ? inner.GetValue(entry) : null;
                        if (innerVal != null)
                        {
                            model = innerVal;
                            nameProp = innerVal.GetType().GetProperty("Name");
                        }
                    }
                    var name = nameProp != null ? nameProp.GetValue(model) as string : null;
                    if (string.IsNullOrEmpty(name)) continue;
                    result.Add(new CharacterChoice { Display = name!, Name = name!, Model = model });
                }
            }
            catch (Exception ex)
            {
                log(Tr.T("log.i.charListFail", ex.Message));
            }
            return result;
        }

        /// <summary>YMM4の登録キャラ設定を書き庭連携用JSONへ書き出す(名前・声・立ち絵PSD・既定表情)。
        /// レイヤーパスは書き庭側の「/」区切りへ逆変換して出す</summary>
        public static void ExportCharacters(string path, HashSet<string> only, Action<string> log)
        {
            var chars = GetYmm4Characters(log);
            if (chars.Count == 0) { log(Tr.T("log.i.noChars")); return; }
            Action<string> quiet = _ => { };
            var list = new List<Dictionary<string, object?>>();
            foreach (var c in chars)
            {
                var model = c.Model;
                if (model == null) continue;
                if (!only.Contains(c.Name)) continue;
                var entry = new Dictionary<string, object?>();
                entry["name"] = c.Name;
                var group = GetPropValue(model, "GroupName") as string;
                if (!string.IsNullOrEmpty(group)) entry["group"] = group;
                var color = GetPropValue(model, "Color");
                if (color != null) entry["color"] = color.ToString();

                var voiceObj = GetPropValue(model, "Voice");
                if (voiceObj != null)
                {
                    var voice = new Dictionary<string, object?>();
                    foreach (var p in voiceObj.GetType().GetProperties())
                    {
                        try
                        {
                            if (p.GetIndexParameters().Length > 0) continue;
                            var v = p.GetValue(voiceObj);
                            if (v is string || (v != null && (v.GetType().IsPrimitive || v.GetType().IsEnum)))
                                voice[p.Name] = v.ToString();
                        }
                        catch { }
                    }
                    entry["voice"] = voice;
                }

                var itemParam = GetPropValue(model, "TachieDefaultItemParameter");
                if (itemParam != null)
                {
                    var tachie = new Dictionary<string, object?>();
                    var tt = GetPropValue(model, "TachieType");
                    if (tt != null) tachie["type"] = tt.ToString();
                    tachie["filePath"] = GetPropValue(itemParam, "FilePath") as string;
                    tachie["defaultLayers"] = ConvertPathsToSlash(GetPropValue(itemParam, "EnableLayerPaths"));
                    var faceParam = GetPropValue(model, "TachieDefaultFaceParameter");
                    if (faceParam != null)
                        tachie["defaultFaceLayers"] = ConvertPathsToSlash(GetPropValue(faceParam, "EnableLayerPaths"));
                    // PSDサイドカーの表情プリセットも含める(書き庭で expressions[].psd に取り込める形)
                    var psdPath = tachie["filePath"] as string;
                    if (!string.IsNullOrEmpty(psdPath) && File.Exists(psdPath))
                    {
                        var settings = LoadPsdFileSettings(psdPath!, quiet);
                        var presets = settings != null ? GetPropValue(settings, "Presets") as System.Collections.IEnumerable : null;
                        if (presets != null)
                        {
                            var presetList = new List<Dictionary<string, object?>>();
                            foreach (var preset in presets)
                            {
                                if (preset == null) continue;
                                presetList.Add(new Dictionary<string, object?>
                                {
                                    ["name"] = GetPropValue(preset, "Name") as string,
                                    ["layers"] = ConvertPathsToSlash(GetPropValue(preset, "LayerPaths")),
                                });
                            }
                            if (presetList.Count > 0) tachie["presets"] = presetList;
                        }
                    }
                    entry["tachie"] = tachie;
                }
                list.Add(entry);
            }
            var payload = new Dictionary<string, object?>
            {
                ["version"] = 1,
                ["source"] = "YMM4",
                ["characters"] = list,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
            log(Tr.T("log.i.charsExported", list.Count, path));
        }

        static object? GetPropValue(object obj, string name)
        {
            var p = obj.GetType().GetProperty(name);
            return p != null ? p.GetValue(obj) : null;
        }

        // ---------- PSD表情プリセット(<PSD名>-ymm.json)の読み書き ----------
        // YMM4のPSDプラグインの PsdFileSettings.LoadFromPsdFilePath / SaveFromPsdFilePath を
        // リフレクションで呼ぶ(形式ズレを避けるため自前でJSONは書かない)

        static Type? FindLoadedType(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var t = asm.GetType(fullName);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        /// <summary>名前+引数の型でメソッドを探す(YMM4バージョン差対策)。
        /// ★名前+引数個数だけの検索は、YMM4側にオーバーロードが増えた版で別物を掴む。
        /// 型まで一致するものを最優先し、無ければ名前+個数一致へフォールバック。</summary>
        public static MethodInfo? FindMethodByShape(Type type, string name, Type[] paramTypes)
        {
            var all = type.GetMethods(BindingFlags.Instance | BindingFlags.Static
                | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == name).ToList();
            foreach (var m in all)
            {
                var ps = m.GetParameters();
                if (ps.Length != paramTypes.Length) continue;
                var ok = true;
                for (var i = 0; i < ps.Length; i++)
                    if (!ps[i].ParameterType.IsAssignableFrom(paramTypes[i])) { ok = false; break; }
                if (ok) return m;
            }
            return all.FirstOrDefault(m => m.GetParameters().Length == paramTypes.Length);
        }

        /// <summary>動作確認済みのYMM4版(この版で公開DLLの中身を実測した)。
        /// 4.54.0.1 で実測、4.55.1.1 で取り込み・改行の字幕を確認(2026-08-27)</summary>
        static readonly string[] TestedYmm4 = { "4.54", "4.55" };
        static bool versionWarned;
        /// <summary>実行中のYMM4が動作確認版と違えば一度だけ注意をログに出す(致命ではない)</summary>
        public static void LogYmm4VersionIfUntested(Action<string> log)
        {
            if (versionWarned) return;
            versionWarned = true;
            try
            {
                var v = Application.ResourceAssembly != null ? Application.ResourceAssembly.GetName().Version : null;
                if (v == null) return;
                var cur = v.Major + "." + v.Minor;
                if (Array.IndexOf(TestedYmm4, cur) < 0)
                    log(Tr.T("log.i.versionNote", string.Join("/", TestedYmm4), v.ToString(3)));
            }
            catch { }
        }

        static object? LoadPsdFileSettings(string psdPath, Action<string> log)
        {
            var type = FindLoadedType("YukkuriMovieMaker.Plugin.Tachie.Psd.PsdFileSettings");
            if (type == null) { log(Tr.T("log.i.typeNotFound", "PsdFileSettings")); return null; }
            // 引数の型(string 1つ)まで確認して選ぶ(バージョン差でのオーバーロード違い対策)
            var m = FindMethodByShape(type, "LoadFromPsdFilePath", new[] { typeof(string) });
            if (m == null) { log(Tr.T("log.i.notFound", "LoadFromPsdFilePath")); return null; }
            try { return m.Invoke(null, new object?[] { psdPath }); }
            catch (Exception ex) { log(Tr.T("log.i.presetLoadFail", ex.InnerException != null ? ex.InnerException.Message : ex.Message)); return null; }
        }

        /// <summary>型に合わせて文字列リストを作る(ImmutableList / List / IList対応)</summary>
        static object? MakeStringList(Type target, List<string> values)
        {
            if (target.IsAssignableFrom(typeof(List<string>))) return values;
            if (target.IsAssignableFrom(typeof(System.Collections.Immutable.ImmutableList<string>)))
                return System.Collections.Immutable.ImmutableList.CreateRange(values);
            try
            {
                var inst = Activator.CreateInstance(target);
                if (inst is System.Collections.IList list)
                {
                    foreach (var v in values) list.Add(v);
                    return inst;
                }
            }
            catch { }
            return null;
        }

        /// <summary>MainViewModel が抱えている MainModel(アプリ中枢モデル)を探す</summary>
        static object? FindMainModel(Action<string> log)
        {
            var mainWindow = Application.Current != null ? Application.Current.MainWindow : null;
            var mainVm = mainWindow != null ? mainWindow.DataContext : null;
            if (mainVm == null) return null;
            const string TargetType = "YukkuriMovieMaker.Project.MainModel";
            for (var t = mainVm.GetType(); t != null; t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (f.FieldType.FullName != TargetType) continue;
                    var v = f.GetValue(mainVm);
                    if (v != null) { log(Tr.T("log.i.mainModelField", t.Name, f.Name)); return v; }
                }
            }
            foreach (var p in mainVm.GetType().GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (p.PropertyType.FullName != TargetType || p.GetIndexParameters().Length > 0) continue;
                var v = p.GetValue(mainVm);
                if (v != null) { log(Tr.T("log.i.mainModelProp", p.Name)); return v; }
            }
            log(Tr.T("log.i.noMainModel"));
            return null;
        }

        /// <summary>UIのセリフ追加と同じ完全経路 MainModel.AddVoiceItemAsync(frame, layer, character, serif, decorations)。
        /// タイムラインへの追加までやるので、成功時は items へ入れないこと</summary>
        static async System.Threading.Tasks.Task<Tuple<bool, VoiceItem?>> AddVoiceViaMainModel(
            object mainModel, int frame, int layer, object character, string serif, Action<string> log)
        {
            var shortSerif = ShortSerif(serif);
            try
            {
                var m = mainModel.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(x => x.Name == "AddVoiceItemAsync" && x.GetParameters().Length == 5);
                if (m == null)
                {
                    log(Tr.T("log.i.noAddVoice"));
                    return Tuple.Create(false, (VoiceItem?)null);
                }
                var taskObj = m.Invoke(mainModel, new object?[] { frame, layer, character, serif, null });
                VoiceItem? item = null;
                if (taskObj is System.Threading.Tasks.Task task)
                {
                    await task;
                    var rp = taskObj.GetType().GetProperty("Result");
                    item = rp != null ? rp.GetValue(taskObj) as VoiceItem : null;
                }
                if (item != null)
                {
                    Func<string, object?> read = name =>
                    {
                        var p2 = FindProp(item.GetType(), name);
                        return p2 != null ? p2.GetValue(item) : null;
                    };
                    log(Tr.T("log.i.voiceUi", shortSerif, DumpValue(read("VoiceLength")),
                        read("VoiceCache") == null ? "null" : Tr.T("log.i.present")));
                }
                else
                {
                    log(Tr.T("log.i.voiceUiNoRet", shortSerif));
                }
                return Tuple.Create(true, item);
            }
            catch (Exception ex)
            {
                log(Tr.T("log.i.voiceUiFail", shortSerif, ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                return Tuple.Create(false, (VoiceItem?)null);
            }
        }

        /// <summary>YMM4の正規ファクトリ static VoiceItem.CreateVoiceItemAsync(character, serif, …) で
        /// ボイスアイテムを生成する(UIのセリフ追加と同じ経路)。生成結果の実態もログに出す</summary>
        static async System.Threading.Tasks.Task<VoiceItem?> CreateVoiceItemViaFactory(
            object character, string serif, Action<string> log,
            string? hatsuon = null, object? videoInfo = null)
        {
            try
            {
                var m = typeof(VoiceItem)
                    .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(x => x.Name == "CreateVoiceItemAsync");
                if (m == null) { log(Tr.T("log.i.noFactory")); return null; }
                var ps = m.GetParameters();
                var args = new object?[ps.Length];
                args[0] = character;
                args[1] = serif;
                for (var i = 2; i < ps.Length; i++)
                    args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                        : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                // ★読み指定は作る時点で渡す。作ったあとに Pronounce を書くと、声が作り直されて尺が変わる
                //   (その時点では実尺を測り終えているので、ずれる)。第4引数が hatsuon(文字列)の版だけ使う。
                if (!string.IsNullOrEmpty(hatsuon) && ps.Length > 3 && ps[3].ParameterType == typeof(string))
                    args[3] = hatsuon;
                var taskObj = m.Invoke(null, args);
                VoiceItem? item = null;
                if (taskObj is System.Threading.Tasks.Task task)
                {
                    await task;
                    var rp = taskObj.GetType().GetProperty("Result");
                    item = rp != null ? rp.GetValue(taskObj) as VoiceItem : null;
                }
                else item = taskObj as VoiceItem;
                if (item == null) { log(Tr.T("log.i.factoryNull")); return null; }
                // 公式の台本編集と同じく、生成が済んだ直後に実尺を取り直す(ここで尺が確定する)
                if (videoInfo != null) ResetVoiceLength(item, videoInfo, log);

                var shortSerif = ShortSerif(serif);
                Func<string, object?> read = name =>
                {
                    var p2 = FindProp(item.GetType(), name);
                    return p2 != null ? p2.GetValue(item) : null;
                };
                log(Tr.T("log.i.voiceFactory", shortSerif, DumpValue(read("VoiceLength")),
                    read("VoiceCache") == null ? "null" : Tr.T("log.i.present"), DumpValue(read("Length"))));
                return item;
            }
            catch (Exception ex)
            {
                log(Tr.T("log.i.factoryFail", ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                return null;
            }
        }

        /// <summary>公式の台本編集(ScenarioLineViewModel.GetVoiceItemAsync)と同じ手順でボイスを作る。
        /// new VoiceItem(キャラ) → BeginEdit → 文と読みを設定 → (読みが無ければ SerifToHatsuonAsync)
        /// → EndEditAsync(ここで音声の生成まで待つ)→ ResetLength → 複製。
        /// ★ファクトリ CreateVoiceItemAsync だけでは音声がまだ作られず(VoiceLength=0・Length は既定の300フレーム)、
        ///   その尺のまま並べてしまう。生成の完了を待ってから尺を確定させるのがこの関数の役目。
        /// YMM4 の型はリフレクションで触る(版の差・テスト用スタブに依存しない)。作れなければ null。</summary>
        static async System.Threading.Tasks.Task<VoiceItem?> CreateVoiceItemOfficial(
            object character, string serif, string? reading, object? videoInfo, Action<string> log)
        {
            var shortSerif = ShortSerif(serif);
            try
            {
                var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                var item = Activator.CreateInstance(typeof(VoiceItem), new object[] { character }) as VoiceItem;
                if (item == null) { log(Tr.T("log.i.noVoiceCtor")); return null; }
                var t = item.GetType();
                t.GetMethod("BeginEdit", flags, null, Type.EmptyTypes, null)?.Invoke(item, null);
                SetProps(item, log, P("Serif", serif));
                if (!string.IsNullOrEmpty(reading))
                    SetProps(item, log, P("Hatsuon", reading!));
                else
                {
                    SetProps(item, log, P("Hatsuon", serif));
                    var conv = t.GetMethod("SerifToHatsuonAsync", flags, null, Type.EmptyTypes, null);
                    if (conv != null && conv.Invoke(item, null) is System.Threading.Tasks.Task ct) await ct;
                }
                // 編集の確定。ここで音声の生成が走り、完了まで待つ(ValueTask なので AsTask で待つ)
                var end = t.GetMethod("EndEditAsync", flags, null, Type.EmptyTypes, null)?.Invoke(item, null);
                if (end != null)
                {
                    var asTask = end.GetType().GetMethod("AsTask", Type.EmptyTypes);
                    if (asTask != null && asTask.Invoke(end, null) is System.Threading.Tasks.Task et) await et;
                }
                // 念のため、VoiceLength が入るまで少し待つ(最大20秒)
                for (var i = 0; i < 100 && VoiceReadiness(item) <= 0; i++)
                    await System.Threading.Tasks.Task.Delay(200);
                if (videoInfo != null) ResetVoiceLength(item, videoInfo, log);
                // 公式は複製を返す。複製で音声が失われていたら元のものを使う
                var clone = t.GetMethod("GetClone", flags, null, Type.EmptyTypes, null)?.Invoke(item, null) as VoiceItem;
                var result = clone != null && VoiceReadiness(clone) > 0 ? clone : item;
                var lp = FindProp(result.GetType(), "Length");
                log(Tr.T("log.i.voiceOfficial", shortSerif,
                    DumpValue(GetPropValue(result, "VoiceLength")),
                    DumpValue(lp != null ? lp.GetValue(result) : null),
                    clone != null && result == clone ? Tr.T("log.i.cloneSuffix") : ""));
                return VoiceReadiness(result) > 0 ? result : null; // 生成できていなければ従来の方式へ回す
            }
            catch (Exception ex)
            {
                log(Tr.T("log.i.officialFail", shortSerif, ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                return null;
            }
        }

        /// <summary>公式の台本編集と同じ ItemEx.ResetLength(item, videoInfo, false) で、ボイスの尺を音声の実尺へ合わせる。
        /// 第3引数 resetContentOffset は公式の呼び出しに合わせて false。見つからない・失敗したら false を返す。</summary>
        static bool ResetVoiceLength(object item, object videoInfo, Action<string> log)
        {
            try
            {
                var reset = typeof(VoiceItem).Assembly.GetType("YukkuriMovieMaker.Project.Items.ItemEx")
                    ?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m => m.Name == "ResetLength" && m.GetParameters().Length == 3);
                if (reset == null) return false;
                reset.Invoke(null, new object?[] { item, videoInfo, false });
                return true;
            }
            catch (Exception ex)
            {
                log(Tr.T("log.reset.fail", ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                return false;
            }
        }

        /// <summary>ログに出すセリフの頭(12文字)。★折り返しを外してから切る。改行が残ると「」が行をまたぎ、
        /// 保存ログの伏せ字(「…」の中を文字数にする)から漏れる(2026-10-22 監査)</summary>
        internal static string ShortSerif(string serif)
        {
            var one = SingleLine(serif ?? "");
            return one.Length > 12 ? FirstChars(one, 12) + "…" : one;
        }

        static Tuple<string, object> P(string name, object value)
        {
            return Tuple.Create(name, value);
        }

        /// <summary>MainWindow.DataContext → ActiveTimelineViewModel → 内部の Timeline モデルを辿る</summary>
        static Timeline? FindActiveTimeline(Action<string> log)
        {
            var mainWindow = Application.Current != null ? Application.Current.MainWindow : null;
            var mainVm = mainWindow != null ? mainWindow.DataContext : null;
            if (mainVm == null) { log(Tr.T("log.i.noDataContext")); return null; }
            log("MainViewModel: " + mainVm.GetType().FullName);

            var tlProp = mainVm.GetType().GetProperty("ActiveTimelineViewModel");
            var tlVm = tlProp != null ? tlProp.GetValue(mainVm) : null;
            if (tlVm == null) { log(Tr.T("log.i.noTimelineVm")); return null; }
            log("TimelineViewModel: " + tlVm.GetType().FullName);

            for (var t = tlVm.GetType(); t != null; t = t.BaseType)
            {
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (typeof(Timeline).IsAssignableFrom(f.FieldType))
                    {
                        var tl = f.GetValue(tlVm) as Timeline;
                        if (tl != null)
                        {
                            log(Tr.T("log.i.timelineField", t.Name, f.Name));
                            return tl;
                        }
                    }
                }
                foreach (var pr in t.GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    if (typeof(Timeline).IsAssignableFrom(pr.PropertyType) && pr.GetIndexParameters().Length == 0)
                    {
                        var tl = pr.GetValue(tlVm) as Timeline;
                        if (tl != null)
                        {
                            log(Tr.T("log.i.timelineProp", t.Name, pr.Name));
                            return tl;
                        }
                    }
                }
            }
            log(Tr.T("log.i.noTimeline"));
            return null;
        }

        static double ReadFps(Timeline timeline, Action<string> log)
        {
            return ReadVideoInfoValue(timeline, "FPS", 30, log);
        }

        static double ReadVideoInfoValue(Timeline timeline, string name, double fallback, Action<string> log)
        {
            try
            {
                var viProp = timeline.GetType().GetProperty("VideoInfo");
                var vi = viProp != null ? viProp.GetValue(timeline) : null;
                var p = vi != null ? vi.GetType().GetProperty(name) : null;
                var obj = p != null ? p.GetValue(vi) : null;
                if (obj != null)
                {
                    var v = Convert.ToDouble(obj);
                    if (v > 0) return v;
                }
            }
            catch (Exception ex) { log(Tr.T("log.i.getFail", name, ex.Message, fallback)); }
            return fallback;
        }

        /// <summary>キャラ設定の既定値をアイテムへコピーする(YMM4がUI追加時にやることの再現)。
        /// 同名・同型の書き込み可能プロパティのうち、値型と文字列のみ(参照の共有事故を避ける)</summary>
        static void CopyCharacterDefaults(object item, object character, Action<string> log)
        {
            var skip = new[] { "Name", "GroupName", "KeyGesture", "Layer", "LicenseOverviewDummy" };
            var copied = 0;
            foreach (var cp in character.GetType().GetProperties())
            {
                try
                {
                    if (skip.Contains(cp.Name)) continue;
                    if (cp.Name.StartsWith("Tachie") || cp.Name.StartsWith("VoiceItem") || cp.Name.StartsWith("CustomVoice")) continue;
                    var ip = item.GetType().GetProperty(cp.Name);
                    if (ip == null || !ip.CanWrite) continue;
                    if (ip.PropertyType != cp.PropertyType) continue;
                    if (!cp.PropertyType.IsValueType && cp.PropertyType != typeof(string)) continue;
                    ip.SetValue(item, cp.GetValue(character));
                    copied++;
                }
                catch { }
            }
            log(Tr.T("log.i.charDefaults", copied, item.GetType().Name));
        }

        /// <summary>オブジェクトの安全なクローン。GetClone() があればそれを、
        /// 無ければ空ctor+書き込み可能プロパティの浅いコピー。失敗時は null(共有参照は返さない)</summary>
        static object? CloneObject(object source, Action<string> log)
        {
            var type = source.GetType();
            try
            {
                var cloneMethod = type.GetMethods()
                    .FirstOrDefault(m2 => m2.Name == "GetClone" && m2.GetParameters().Length == 0);
                if (cloneMethod != null) return cloneMethod.Invoke(source, null);
                var clone = Activator.CreateInstance(type);
                if (clone == null) return null;
                foreach (var p2 in type.GetProperties())
                {
                    if (!p2.CanRead || !p2.CanWrite || p2.GetIndexParameters().Length > 0) continue;
                    try { p2.SetValue(clone, p2.GetValue(source)); } catch { }
                }
                return clone;
            }
            catch (Exception ex)
            {
                log(Tr.T("log.i.cloneFail", type.Name, ex.Message));
                return null;
            }
        }

        static string DumpValue(object? v)
        {
            if (v == null) return "null";
            if (v is string s) return s;
            if (v is System.Collections.IEnumerable list)
            {
                var parts = new List<string>();
                foreach (var x in list) { parts.Add(x?.ToString() ?? "null"); if (parts.Count >= 10) { parts.Add("…"); break; } }
                return "[" + string.Join(", ", parts) + "]";
            }
            return v.ToString() ?? "null";
        }

        /// <summary>立ち絵アイテムに、キャラ設定の既定立ち絵パラメータ(PSDファイル指定等)のクローンを入れる。
        /// これが無いと TachieItem は描くものが無く何も表示されない。成功したら true</summary>
        static bool SetTachieParameter(object tachieItem, object character, Action<string> log)
        {
            try
            {
                var dp = character.GetType().GetProperty("TachieDefaultItemParameter");
                var def = dp != null ? dp.GetValue(character) : null;
                if (def == null) { log(Tr.T("log.i.tachieNoDefault")); return false; }
                var clone = CloneObject(def, log) ?? def; // 書き換えないので共有でも実害はないがクローン優先
                var tp = FindProp(tachieItem.GetType(), "TachieItemParameter");
                if (tp == null || !tp.CanWrite) { log(Tr.T("log.i.tachieNoWrite")); return false; }
                if (!tp.PropertyType.IsInstanceOfType(clone))
                {
                    log(Tr.T("log.i.tachieTypeMismatch", clone.GetType().Name, tp.PropertyType.Name));
                    return false;
                }
                tp.SetValue(tachieItem, clone);
                log(Tr.T("log.i.tachieParamOk", clone.GetType().Name));
                return true;
            }
            catch (Exception ex) { log(Tr.T("log.i.tachieParamFail", ex.Message)); return false; }
        }

        /// <summary>立ち絵アイテムのPSDパス(TachieItemParameter.FilePath)を差し替える。
        /// YMM4は1キャラ設定=1PSDだが、アイテム単位のFilePathは書き換えられるので、
        /// これで「同じ話者のまま立ち絵だけ別PSDに切替」を実現する。</summary>
        static void OverrideTachieFilePath(object tachieItem, string? psdPath, Action<string> log)
        {
            if (psdPath == null) { return; }
            try
            {
                var tp = FindProp(tachieItem.GetType(), "TachieItemParameter");
                var param = tp != null ? tp.GetValue(tachieItem) : null;
                if (param == null) { log(Tr.T("log.i.psdSwapNoParam")); return; }
                var fp = param.GetType().GetProperty("FilePath");
                if (fp == null || !fp.CanWrite) { log(Tr.T("log.i.psdSwapNoWrite", param.GetType().Name)); return; }
                fp.SetValue(param, psdPath);
                log(Tr.T("log.i.psdSwap", Path.GetFileName(psdPath)));
            }
            catch (Exception ex) { log(Tr.T("log.i.psdSwapFail", ex.Message)); }
        }

        /// <summary>PsdPreset の一括ID解決(PSDプラグインの static ResolvePresets(filePath, presets) を呼ぶ)。
        /// 成功したら解決済みプリセットのリスト、失敗なら null</summary>
        static async System.Threading.Tasks.Task<List<object>?> ResolvePresetBatchAsync(
            string psdPath, List<object> presets, Type presetType, Action<string> log)
        {
            try
            {
                var vmType = FindLoadedType("YukkuriMovieMaker.Plugin.Tachie.Psd.PsdPresetEditorViewModel");
                if (vmType == null) { log(Tr.T("log.i.idNotFound", "PsdPresetEditorViewModel")); return null; }
                var resolveM = vmType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m2 => m2.Name == "ResolvePresets" && m2.GetParameters().Length == 2);
                if (resolveM == null) { log(Tr.T("log.i.idNotFound", "ResolvePresets")); return null; }
                var arr = Array.CreateInstance(presetType, presets.Count);
                for (var i = 0; i < presets.Count; i++) arr.SetValue(presets[i], i);
                var res = resolveM.Invoke(null, new object?[] { psdPath, arr });
                if (res is System.Threading.Tasks.Task task)
                {
                    await task;
                    var rp = res.GetType().GetProperty("Result");
                    res = rp != null ? rp.GetValue(task) : null;
                }
                if (res is System.Collections.IEnumerable en && !(res is string))
                {
                    var list = new List<object>();
                    foreach (var x in en) if (x != null) list.Add(x);
                    if (list.Count > 0) return list;
                }
                // 戻り値なし(その場で書き換え)の可能性: 元のリストを返す
                return presets;
            }
            catch (Exception ex)
            {
                log(Tr.T("log.i.idFail", ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                return null;
            }
        }

        /// <summary>YMM4実形式のレイヤーパス群を内部IDへ解決する。戻り値: (Layers, LayerPaths)。失敗時 null</summary>
        static async System.Threading.Tasks.Task<Tuple<object?, object?>?> ResolveLayersAsync(
            string? psdPath, List<string> ymm4Paths, Action<string> log)
        {
            if (string.IsNullOrEmpty(psdPath) || !File.Exists(psdPath))
            {
                log(Tr.T("log.i.faceNoPsdPath"));
                return null;
            }
            var presetType = FindLoadedType("YukkuriMovieMaker.Plugin.Tachie.Psd.PsdPreset");
            if (presetType == null) return null;
            var preset = Activator.CreateInstance(presetType);
            if (preset == null) return null;
            var nameProp = presetType.GetProperty("Name");
            if (nameProp != null && nameProp.CanWrite) nameProp.SetValue(preset, "(解決用)");
            var lpProp = presetType.GetProperty("LayerPaths");
            if (lpProp != null && lpProp.CanWrite) lpProp.SetValue(preset, MakeStringList(lpProp.PropertyType, ymm4Paths));
            var lyProp = presetType.GetProperty("Layers");
            if (lyProp != null && lyProp.CanWrite) lyProp.SetValue(preset, MakeStringList(lyProp.PropertyType, new List<string>()));
            var resolved = await ResolvePresetBatchAsync(psdPath!, new List<object> { preset }, presetType, log);
            var target = resolved != null && resolved.Count > 0 ? resolved[0] : null;
            if (target == null) return null;
            return Tuple.Create(GetPropValue(target, "Layers"), GetPropValue(target, "LayerPaths"));
        }

        /// <summary>YMM4のPSDレイヤーパスの区切り文字を、キャラ既定パラメータの実サンプルから検出する。
        /// (実機ログでは各階層の後ろに区切りが付く形式。文字の正体もログに出す)</summary>
        static char? DetectLayerPathSeparator(object character, Action<string> log)
        {
            try
            {
                var dp = character.GetType().GetProperty("TachieDefaultItemParameter");
                var def = dp != null ? dp.GetValue(character) : null;
                var pp = def != null ? def.GetType().GetProperty("EnableLayerPaths") : null;
                var list = pp != null ? pp.GetValue(def) as System.Collections.IEnumerable : null;
                if (list == null) return null;
                var counts = new Dictionary<char, int>();
                var total = 0;
                foreach (var o in list)
                {
                    var s = o as string;
                    if (string.IsNullOrEmpty(s)) continue;
                    var c = s[s.Length - 1];
                    int n;
                    counts.TryGetValue(c, out n);
                    counts[c] = n + 1;
                    total++;
                    if (total >= 20) break;
                }
                if (total == 0) return null;
                var best = counts.OrderByDescending(kv => kv.Value).First();
                if (best.Value < total * 0.8) { log(Tr.T("log.i.faceNoSeparator")); return null; }
                log(Tr.T("log.i.faceSeparator", ((int)best.Key).ToString("X4"), best.Key));
                return best.Key;
            }
            catch { return null; }
        }

        /// <summary>表情アイテムに「全身の表示状態」を設定する。
        /// YMM4の表情パラメータの EnableLayers は差分ではなく全レイヤー表示状態の完全リストで、
        /// FilePath はキャラの TachieCharacterParameter.FilePath と完全一致が必要(逆コンパイルで確認 2026-07-26)</summary>
        static async System.Threading.Tasks.Task SetFaceParameter(object faceItem, object character, List<string>? layers, List<int>? layerIds, Action<string> log)
        {
            try
            {
                var dp = character.GetType().GetProperty("TachieDefaultFaceParameter");
                var def = dp != null ? dp.GetValue(character) : null;
                if (def == null) { log(Tr.T("log.i.faceNoDefault")); return; }
                var clone = CloneObject(def, log);
                if (clone == null) { log(Tr.T("log.i.faceNoClone")); return; }

                // FilePath: キャラの TachieCharacterParameter.FilePath と完全一致が描画条件
                var charParam = GetPropValue(character, "TachieCharacterParameter");
                var charPsd = charParam != null ? GetPropValue(charParam, "FilePath") as string : null;
                var fpProp = clone.GetType().GetProperty("FilePath");
                if (fpProp != null && fpProp.CanWrite) fpProp.SetValue(clone, charPsd);

                if (layers != null && layers.Count > 0)
                {
                    var state = await BuildFullFaceState(character, layers, layerIds, log);
                    if (state != null)
                    {
                        var pathsProp = clone.GetType().GetProperty("EnableLayerPaths");
                        var namesProp = clone.GetType().GetProperty("EnableLayers");
                        object names = System.Collections.Immutable.ImmutableList.CreateRange(state.Item1);
                        object paths = System.Collections.Immutable.ImmutableList.CreateRange(state.Item2);
                        if (pathsProp != null && pathsProp.CanWrite && pathsProp.PropertyType.IsInstanceOfType(paths))
                            pathsProp.SetValue(clone, paths);
                        if (namesProp != null && namesProp.CanWrite && namesProp.PropertyType.IsInstanceOfType(names))
                            namesProp.SetValue(clone, names);
                        log(Tr.T("log.i.faceState", state.Item1.Count, layers.Count));
                    }
                }

                var fp = FindProp(faceItem.GetType(), "TachieFaceParameter");
                if (fp == null || !fp.CanWrite) { log(Tr.T("log.i.faceNoWrite")); return; }
                if (!fp.PropertyType.IsInstanceOfType(clone))
                {
                    log(Tr.T("log.i.faceTypeMismatch", clone.GetType().Name, fp.PropertyType.Name));
                    return;
                }
                fp.SetValue(faceItem, clone);
            }
            catch (Exception ex) { log(Tr.T("log.i.faceParamFail", ex.Message)); }
        }

        static List<string> ToStringList(object? listObj)
        {
            var result = new List<string>();
            if (listObj is System.Collections.IEnumerable en)
                foreach (var o in en)
                    if (o is string s2) result.Add(s2);
            return result;
        }

        /// <summary>キャラ既定の全身表示状態(TachieDefaultItemParameter)をベースに、
        /// 指定レイヤー(表情)を入れ替えた完全リストを作る。* 始まりは同フォルダのラジオ兄弟を外す。
        /// 返り値: (EnableLayers(nID), EnableLayerPaths)</summary>
        static async System.Threading.Tasks.Task<Tuple<List<string>, List<string>>?> BuildFullFaceState(
            object character, List<string> layers, List<int>? layerIds, Action<string> log)
        {
            // スキーマ確定(2026-07-26): psd.layers は「全身の表示状態の完全リスト」
            // (書き庭の表情プリセット保存形式そのもの)なので、IDと共にそのまま使う
            var converted = layers.Select(l => ToYmm4LayerPath(l)).ToList();
            List<string>? newIds = null;
            if (layerIds != null && layerIds.Count == layers.Count)
                newIds = layerIds.Select(n2 => "n" + n2).ToList();
            else
            {
                var itemDef = GetPropValue(character, "TachieDefaultItemParameter");
                var psdPath = itemDef != null ? GetPropValue(itemDef, "FilePath") as string : null;
                var r2 = await ResolveLayersAsync(psdPath, converted, log);
                if (r2 != null) newIds = ToStringList(r2.Item1);
            }
            if (newIds == null || newIds.Count != converted.Count)
            {
                log(Tr.T("log.i.faceNoLayerIds"));
                return null;
            }
            return Tuple.Create(newIds, converted);
        }

        /// <summary>プロパティを名前で探す(明示的インターフェイス実装 "IVideoItem.X" にも対応)</summary>
        static PropertyInfo? FindProp(Type type, string name)
        {
            var p = type.GetProperty(name);
            if (p != null) return p;
            return type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(x => x.Name.EndsWith("." + name, StringComparison.Ordinal));
        }

        /// <summary>アニメーション型プロパティ(X/Y/Zoom等)に一定値を設定する。
        /// From/To 型と Values[].Value 型の両対応</summary>
        static bool SetAnim(object item, string name, double value, Action<string> log)
        {
            // ★有限でない値(Infinity/NaN)は YMM4 に渡さない。0 除算や巨大な倍率で生まれ、
            //   描画やプロジェクトの保存を壊しうる(2026-10-20c)。上流で防いでいても入口で断る
            if (!double.IsFinite(value))
            {
                log(Tr.T("log.i.animNotFinite", name));
                return false;
            }
            try
            {
                var pi = FindProp(item.GetType(), name);
                var anim = pi != null ? pi.GetValue(item) : null;
                if (anim == null)
                {
                    // 素の数値プロパティだった場合
                    if (pi != null && pi.CanWrite)
                    {
                        pi.SetValue(item, Convert.ChangeType(value, Nullable.GetUnderlyingType(pi.PropertyType) ?? pi.PropertyType));
                        return true;
                    }
                    log(Tr.T("log.i.propNotFound", name));
                    return false;
                }

                var fromProp = anim.GetType().GetProperty("From");
                var toProp = anim.GetType().GetProperty("To");
                if (fromProp != null && fromProp.CanWrite)
                {
                    fromProp.SetValue(anim, Convert.ChangeType(value, fromProp.PropertyType));
                    if (toProp != null && toProp.CanWrite)
                        toProp.SetValue(anim, Convert.ChangeType(value, toProp.PropertyType));
                    return true;
                }

                var valuesProp = anim.GetType().GetProperty("Values");
                var values = valuesProp != null ? valuesProp.GetValue(anim) as System.Collections.IEnumerable : null;
                if (values != null)
                {
                    var any = false;
                    foreach (var v in values)
                    {
                        if (v == null) continue;
                        var vp = v.GetType().GetProperty("Value");
                        if (vp != null && vp.CanWrite)
                        {
                            vp.SetValue(v, Convert.ChangeType(value, Nullable.GetUnderlyingType(vp.PropertyType) ?? vp.PropertyType));
                            any = true;
                        }
                    }
                    if (any) return true;
                }
                log(Tr.T("log.i.animUnknown", name, anim.GetType().Name));
                return false;
            }
            catch (Exception ex)
            {
                log(Tr.T("log.i.setFail", name, ex.Message));
                return false;
            }
        }

        /// <summary>型が一致する場合のみインスタンスを設定(YMM4のキャラモデル等・変換なし)</summary>
        static void TrySetInstance(object item, string name, object value, Action<string> log)
        {
            try
            {
                var pi = item.GetType().GetProperty(name);
                if (pi == null || !pi.CanWrite) { log(Tr.T("log.i.itemPropMissing", item.GetType().Name, name)); return; }
                if (!pi.PropertyType.IsInstanceOfType(value))
                {
                    log(Tr.T("log.i.itemTypeMismatch", item.GetType().Name, name, value.GetType().Name, pi.PropertyType.Name));
                    return;
                }
                pi.SetValue(item, value);
            }
            catch (Exception ex)
            {
                log(Tr.T("log.i.itemSetFail", item.GetType().Name, name, ex.Message));
            }
        }

        /// <summary>プロパティ型の食い違いに耐えるためリフレクションで設定(int/double/bool/enum は変換)</summary>
        // ---------- アイテムテンプレート適用 ----------
        // YMM4 の「アイテムテンプレート」(ItemSettings.Default.Templates)を名前で探し、
        // その中の該当型アイテムを GetClone() で独立コピーして返す。内容(FilePath/Frame/Layer/
        // Length/Text 等)は呼び出し側が SetProps で上書きするので、テンプレはスタイル
        // (フォント・色・エフェクト・登場アニメ等)を運ぶ役目。
        // 未登録・型不一致・失敗時は null を返し、呼び出し側は既定の new XxxItem() にフォールバック。
        // GetClone() は通常のメソッドなので実行中アプリのコンテキストを必要としない(共有参照も生じない)。
        static BaseItem? TemplateItem(Type wantType, string? templateName, Action<string> log)
        {
            if (string.IsNullOrEmpty(templateName)) return null;
            ObservableCollection<ItemTemplate>? templates;
            try { templates = ItemSettings.Default?.Templates; }
            catch (Exception ex) { log(Tr.T("log.i.tmplListFail", ex.Message)); return null; }
            if (templates == null || templates.Count == 0) return null;
            var tmpl = templates.FirstOrDefault(t => t != null && t.Name == templateName);
            if (tmpl == null)
            {
                log(Tr.T("log.i.tmplMissing", templateName));
                return null;
            }
            var rep = tmpl.Items?.FirstOrDefault(it => it != null && wantType.IsInstanceOfType(it));
            if (rep == null)
            {
                log(Tr.T("log.i.tmplNoType", templateName, wantType.Name));
                return null;
            }
            try
            {
                var clone = rep.GetClone() as BaseItem;      // 独立コピー(共有参照回避)
                if (clone == null) return null;
                log(Tr.T("log.i.tmplApplied", templateName, wantType.Name));
                return clone;
            }
            catch (Exception ex) { log(Tr.T("log.i.tmplCloneFail", templateName, ex.Message)); return null; }
        }

        // wantType のテンプレアイテムがあればそれ、無ければ fallback() を新規生成して返す
        static BaseItem MakeItem(Type wantType, string? templateName, Action<string> log, Func<BaseItem> fallback)
        {
            return TemplateItem(wantType, templateName, log) ?? fallback();
        }

        static void SetProps(object item, Action<string> log, params Tuple<string, object>[] props)
        {
            foreach (var prop in props)
            {
                var name = prop.Item1;
                var value = prop.Item2;
                try
                {
                    var pi = item.GetType().GetProperty(name);
                    if (pi == null || !pi.CanWrite)
                    {
                        log(Tr.T("log.i.itemPropMissing2", item.GetType().Name, name));
                        continue;
                    }
                    var target = Nullable.GetUnderlyingType(pi.PropertyType) ?? pi.PropertyType;
                    object converted = value;
                    if (!target.IsInstanceOfType(value))
                        converted = target.IsEnum
                            ? Enum.ToObject(target, Convert.ToInt32(value))
                            : Convert.ChangeType(value, target);
                    pi.SetValue(item, converted);
                }
                catch (Exception ex)
                {
                    log(Tr.T("log.i.itemSetFail", item.GetType().Name, name, ex.Message));
                }
            }
        }

        /// <summary>Timeline.AddItems / TryAddItems をリフレクションで探して呼ぶ(シグネチャをログに残す)</summary>
        /// <param name="logCandidates">候補メソッドの一覧を出すか。小分けに呼ぶとき2回目以降は省く</param>
        static void AddItems(Timeline timeline, List<BaseItem> items, Action<string> log, bool logCandidates = true)
        {
            if (items.Count == 0) { log(Tr.T("log.i.noItems")); return; }

            var candidates = typeof(Timeline)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "AddItems" || m.Name == "TryAddItems")
                .ToArray();
            if (logCandidates)
                foreach (var m in candidates)
                    log(Tr.T("log.i.candidate", m.Name, string.Join(", ",
                        m.GetParameters().Select(pp => pp.ParameterType.Name + " " + pp.Name))));

            foreach (var m in candidates.OrderBy(m => m.Name == "AddItems" ? 0 : 1)
                                        .ThenBy(m => m.GetParameters().Length))
            {
                var ps = m.GetParameters();
                if (ps.Length == 0 || !ps[0].ParameterType.IsInstanceOfType(items)) continue;
                var args = new object?[ps.Length];
                args[0] = items;
                var ok = true;
                for (var i = 1; i < ps.Length; i++)
                {
                    if (ps[i].HasDefaultValue) args[i] = ps[i].DefaultValue;
                    else if (ps[i].ParameterType.IsValueType) args[i] = Activator.CreateInstance(ps[i].ParameterType);
                    else { ok = false; break; }
                }
                if (!ok) continue;
                try
                {
                    var result = m.Invoke(timeline, args);
                    if (logCandidates)
                    {
                        log(Tr.T("log.i.run", m.Name, result == null ? Tr.T("log.i.done") : result.ToString()));
                        log(Tr.T("log.i.placed", items.Count));
                    }
                    return;
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                    log(Tr.T("log.i.methodFail", m.Name, inner));
                }
            }
            log(Tr.T("log.i.addItemsNg"));
        }
    }
}
