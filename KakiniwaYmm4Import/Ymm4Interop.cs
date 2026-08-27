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
                if (list == null) { log("Characters 一覧が取得できない"); return result; }

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
                log("キャラ一覧取得失敗: " + ex.Message);
            }
            return result;
        }

        /// <summary>YMM4の登録キャラ設定を書き庭連携用JSONへ書き出す(名前・声・立ち絵PSD・既定表情)。
        /// レイヤーパスは書き庭側の「/」区切りへ逆変換して出す</summary>
        public static void ExportCharacters(string path, HashSet<string> only, Action<string> log)
        {
            var chars = GetYmm4Characters(log);
            if (chars.Count == 0) { log("書き出すキャラがいません(プロジェクトを開いていますか?)"); return; }
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
            log("キャラ設定を書き出し: " + list.Count + "人 → " + path);
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
                    log("注意: このプラグインの動作確認は YMM4 v" + string.Join("/", TestedYmm4) + "(実行中は v" + v.ToString(3)
                        + ")。挙動がおかしいときはプラグインの新しい版が無いか確認してください");
            }
            catch { }
        }

        static object? LoadPsdFileSettings(string psdPath, Action<string> log)
        {
            var type = FindLoadedType("YukkuriMovieMaker.Plugin.Tachie.Psd.PsdFileSettings");
            if (type == null) { log("  PsdFileSettings 型が見つからない"); return null; }
            // 引数の型(string 1つ)まで確認して選ぶ(バージョン差でのオーバーロード違い対策)
            var m = FindMethodByShape(type, "LoadFromPsdFilePath", new[] { typeof(string) });
            if (m == null) { log("  LoadFromPsdFilePath が見つからない"); return null; }
            try { return m.Invoke(null, new object?[] { psdPath }); }
            catch (Exception ex) { log("  プリセット読込失敗: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message)); return null; }
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
                    if (v != null) { log("MainModel 取得: フィールド " + t.Name + "." + f.Name); return v; }
                }
            }
            foreach (var p in mainVm.GetType().GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
            {
                if (p.PropertyType.FullName != TargetType || p.GetIndexParameters().Length > 0) continue;
                var v = p.GetValue(mainVm);
                if (v != null) { log("MainModel 取得: プロパティ " + p.Name); return v; }
            }
            log("MainModel が見つからない → ボイスはファクトリ経路で配置");
            return null;
        }

        /// <summary>UIのセリフ追加と同じ完全経路 MainModel.AddVoiceItemAsync(frame, layer, character, serif, decorations)。
        /// タイムラインへの追加までやるので、成功時は items へ入れないこと</summary>
        static async System.Threading.Tasks.Task<Tuple<bool, VoiceItem?>> AddVoiceViaMainModel(
            object mainModel, int frame, int layer, object character, string serif, Action<string> log)
        {
            var shortSerif = serif.Length > 12 ? serif.Substring(0, 12) + "…" : serif;
            try
            {
                var m = mainModel.GetType()
                    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(x => x.Name == "AddVoiceItemAsync" && x.GetParameters().Length == 5);
                if (m == null)
                {
                    log("  ボイス: MainModel.AddVoiceItemAsync(5引数) が見つからない。YMM4の更新で中身が変わった可能性があります。プラグインの新しい版が出ていないか確認してください");
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
                    log("  ボイス(UI経路で追加)「" + shortSerif + "」 VoiceLength=" + DumpValue(read("VoiceLength"))
                        + " VoiceCache=" + (read("VoiceCache") == null ? "null" : "あり"));
                }
                else
                {
                    log("  ボイス(UI経路で追加)「" + shortSerif + "」(戻り値からアイテム参照は取れず)");
                }
                return Tuple.Create(true, item);
            }
            catch (Exception ex)
            {
                log("  ボイス: UI経路での追加失敗「" + shortSerif + "」: "
                    + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                return Tuple.Create(false, (VoiceItem?)null);
            }
        }

        /// <summary>YMM4の正規ファクトリ static VoiceItem.CreateVoiceItemAsync(character, serif, …) で
        /// ボイスアイテムを生成する(UIのセリフ追加と同じ経路)。生成結果の実態もログに出す</summary>
        static async System.Threading.Tasks.Task<VoiceItem?> CreateVoiceItemViaFactory(
            object character, string serif, Action<string> log)
        {
            try
            {
                var m = typeof(VoiceItem)
                    .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(x => x.Name == "CreateVoiceItemAsync");
                if (m == null) { log("  ボイス: 正規ファクトリが見つからない(YMM4更新で変わった?)"); return null; }
                var ps = m.GetParameters();
                var args = new object?[ps.Length];
                args[0] = character;
                args[1] = serif;
                for (var i = 2; i < ps.Length; i++)
                    args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue
                        : (ps[i].ParameterType.IsValueType ? Activator.CreateInstance(ps[i].ParameterType) : null);
                var taskObj = m.Invoke(null, args);
                VoiceItem? item = null;
                if (taskObj is System.Threading.Tasks.Task task)
                {
                    await task;
                    var rp = taskObj.GetType().GetProperty("Result");
                    item = rp != null ? rp.GetValue(taskObj) as VoiceItem : null;
                }
                else item = taskObj as VoiceItem;
                if (item == null) { log("  ボイス: 正規ファクトリが null を返した"); return null; }

                var shortSerif = serif.Length > 12 ? serif.Substring(0, 12) + "…" : serif;
                Func<string, object?> read = name =>
                {
                    var p2 = FindProp(item.GetType(), name);
                    return p2 != null ? p2.GetValue(item) : null;
                };
                log("  ボイス(正規生成)「" + shortSerif + "」 VoiceLength=" + DumpValue(read("VoiceLength"))
                    + " VoiceCache=" + (read("VoiceCache") == null ? "null" : "あり")
                    + " Length=" + DumpValue(read("Length")));
                return item;
            }
            catch (Exception ex)
            {
                log("  ボイス: 正規ファクトリでの生成失敗: "
                    + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                return null;
            }
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
            if (mainVm == null) { log("NG: MainWindow.DataContext が null"); return null; }
            log("MainViewModel: " + mainVm.GetType().FullName);

            var tlProp = mainVm.GetType().GetProperty("ActiveTimelineViewModel");
            var tlVm = tlProp != null ? tlProp.GetValue(mainVm) : null;
            if (tlVm == null) { log("NG: ActiveTimelineViewModel が null(プロジェクトが開かれていない?)"); return null; }
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
                            log("Timeline 取得: フィールド " + t.Name + "." + f.Name);
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
                            log("Timeline 取得: プロパティ " + t.Name + "." + pr.Name);
                            return tl;
                        }
                    }
                }
            }
            log("NG: TimelineViewModel から Timeline モデルを見つけられなかった");
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
            catch (Exception ex) { log(name + "取得失敗(" + ex.Message + ")→ " + fallback); }
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
            log("  キャラ既定値コピー: " + copied + "項目 (" + item.GetType().Name + ")");
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
                log("  クローン失敗(" + type.Name + "): " + ex.Message);
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
                if (def == null) { log("  立ち絵: キャラの TachieDefaultItemParameter が null(立ち絵未設定?)"); return false; }
                var clone = CloneObject(def, log) ?? def; // 書き換えないので共有でも実害はないがクローン優先
                var tp = FindProp(tachieItem.GetType(), "TachieItemParameter");
                if (tp == null || !tp.CanWrite) { log("  立ち絵: TachieItemParameter が書けない"); return false; }
                if (!tp.PropertyType.IsInstanceOfType(clone))
                {
                    log("  立ち絵: 型不一致 " + clone.GetType().Name + " → " + tp.PropertyType.Name);
                    return false;
                }
                tp.SetValue(tachieItem, clone);
                log("  立ち絵: パラメータ設定OK(" + clone.GetType().Name + ")");
                return true;
            }
            catch (Exception ex) { log("  立ち絵パラメータ設定失敗: " + ex.Message); return false; }
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
                if (param == null) { log("  立ち絵PSD差し替え: パラメータが取れない"); return; }
                var fp = param.GetType().GetProperty("FilePath");
                if (fp == null || !fp.CanWrite) { log("  立ち絵PSD差し替え: FilePath が書けない(" + param.GetType().Name + ")"); return; }
                fp.SetValue(param, psdPath);
                log("  立ち絵PSD差し替え: " + Path.GetFileName(psdPath));
            }
            catch (Exception ex) { log("  立ち絵PSD差し替え失敗: " + ex.Message); }
        }

        /// <summary>PsdPreset の一括ID解決(PSDプラグインの static ResolvePresets(filePath, presets) を呼ぶ)。
        /// 成功したら解決済みプリセットのリスト、失敗なら null</summary>
        static async System.Threading.Tasks.Task<List<object>?> ResolvePresetBatchAsync(
            string psdPath, List<object> presets, Type presetType, Action<string> log)
        {
            try
            {
                var vmType = FindLoadedType("YukkuriMovieMaker.Plugin.Tachie.Psd.PsdPresetEditorViewModel");
                if (vmType == null) { log("  ID解決: PsdPresetEditorViewModel が見つからない"); return null; }
                var resolveM = vmType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                    .FirstOrDefault(m2 => m2.Name == "ResolvePresets" && m2.GetParameters().Length == 2);
                if (resolveM == null) { log("  ID解決: ResolvePresets が見つからない"); return null; }
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
                log("  ID解決失敗: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                return null;
            }
        }

        /// <summary>YMM4実形式のレイヤーパス群を内部IDへ解決する。戻り値: (Layers, LayerPaths)。失敗時 null</summary>
        static async System.Threading.Tasks.Task<Tuple<object?, object?>?> ResolveLayersAsync(
            string? psdPath, List<string> ymm4Paths, Action<string> log)
        {
            if (string.IsNullOrEmpty(psdPath) || !File.Exists(psdPath))
            {
                log("  表情: PSDパス不明のためID解決できない");
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
                if (best.Value < total * 0.8) { log("  表情: 区切り文字を特定できず(末尾がバラバラ)"); return null; }
                log("  表情: レイヤーパス区切り文字を検出 = U+" + ((int)best.Key).ToString("X4") + " (表示: '" + best.Key + "')");
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
                if (def == null) { log("  表情: キャラの TachieDefaultFaceParameter が null(立ち絵未設定?)"); return; }
                var clone = CloneObject(def, log);
                if (clone == null) { log("  表情: クローン不可のため既定表情のまま"); return; }

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
                        log("  表情: 全身状態 " + state.Item1.Count + "枚で設定(入替指定 " + layers.Count + "点)");
                    }
                }

                var fp = FindProp(faceItem.GetType(), "TachieFaceParameter");
                if (fp == null || !fp.CanWrite) { log("  表情: TachieFaceParameter が書けない"); return; }
                if (!fp.PropertyType.IsInstanceOfType(clone))
                {
                    log("  表情: 型不一致 " + clone.GetType().Name + " → " + fp.PropertyType.Name);
                    return;
                }
                fp.SetValue(faceItem, clone);
            }
            catch (Exception ex) { log("  表情パラメータ設定失敗: " + ex.Message); }
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
                log("  表情: レイヤーIDが解決できず設定不可(書き庭のパック書き出しなら layerIds が自動で付きます)");
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
                    log("  " + name + ": プロパティが見つからない");
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
                log("  " + name + ": " + anim.GetType().Name + " の設定方法が分からない(From/To も Values も無し)");
                return false;
            }
            catch (Exception ex)
            {
                log("  " + name + " 設定失敗: " + ex.Message);
                return false;
            }
        }

        /// <summary>型が一致する場合のみインスタンスを設定(YMM4のキャラモデル等・変換なし)</summary>
        static void TrySetInstance(object item, string name, object value, Action<string> log)
        {
            try
            {
                var pi = item.GetType().GetProperty(name);
                if (pi == null || !pi.CanWrite) { log("  [" + item.GetType().Name + "] " + name + " が無い/書けない"); return; }
                if (!pi.PropertyType.IsInstanceOfType(value))
                {
                    log("  [" + item.GetType().Name + "] " + name + " 型不一致: " + value.GetType().Name + " → " + pi.PropertyType.Name);
                    return;
                }
                pi.SetValue(item, value);
            }
            catch (Exception ex)
            {
                log("  [" + item.GetType().Name + "] " + name + " 設定失敗: " + ex.Message);
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
            catch (Exception ex) { log("  テンプレ一覧の取得に失敗: " + ex.Message); return null; }
            if (templates == null || templates.Count == 0) return null;
            var tmpl = templates.FirstOrDefault(t => t != null && t.Name == templateName);
            if (tmpl == null)
            {
                log("  テンプレ「" + templateName + "」はYMM4に未登録。既定で配置します");
                return null;
            }
            var rep = tmpl.Items?.FirstOrDefault(it => it != null && wantType.IsInstanceOfType(it));
            if (rep == null)
            {
                log("  テンプレ「" + templateName + "」に " + wantType.Name + " が無いため既定で配置します");
                return null;
            }
            try
            {
                var clone = rep.GetClone() as BaseItem;      // 独立コピー(共有参照回避)
                if (clone == null) return null;
                log("  テンプレ「" + templateName + "」を適用(" + wantType.Name + ")");
                return clone;
            }
            catch (Exception ex) { log("  テンプレ「" + templateName + "」複製失敗: " + ex.Message); return null; }
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
                        log("  [" + item.GetType().Name + "] プロパティ " + name + " が無い/書けない");
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
                    log("  [" + item.GetType().Name + "] " + name + " 設定失敗: " + ex.Message);
                }
            }
        }

        /// <summary>Timeline.AddItems / TryAddItems をリフレクションで探して呼ぶ(シグネチャをログに残す)</summary>
        /// <param name="logCandidates">候補メソッドの一覧を出すか。小分けに呼ぶとき2回目以降は省く</param>
        static void AddItems(Timeline timeline, List<BaseItem> items, Action<string> log, bool logCandidates = true)
        {
            if (items.Count == 0) { log("追加するアイテムがありません"); return; }

            var candidates = typeof(Timeline)
                .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(m => m.Name == "AddItems" || m.Name == "TryAddItems")
                .ToArray();
            if (logCandidates)
                foreach (var m in candidates)
                    log("候補メソッド: " + m.Name + "(" + string.Join(", ",
                        m.GetParameters().Select(pp => pp.ParameterType.Name + " " + pp.Name)) + ")");

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
                        log("実行: " + m.Name + " → " + (result == null ? "完了" : result.ToString()));
                        log("配置完了: " + items.Count + "アイテム");
                    }
                    return;
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                    log(m.Name + " 失敗: " + inner);
                }
            }
            log("NG: タイムラインへの一括配置(AddItems)が呼べませんでした。YMM4の更新で中身が変わった可能性があります。プラグインの新しい版が出ていないか確認してください(動作確認済み: YMM4 v4.54)");
        }
    }
}
