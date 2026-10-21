// 配置本体: タイムラインへのアイテム配置(時間計算・レーン割り当て・実尺での再配置)と表情プリセット登録。

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
    // ---------- 配置ロジック ----------
    public static partial class Importer
    {
        /// <summary>書き庭の表情プリセット(パックの expressions[].psd.layers)を、
        /// 割り当て先YMM4キャラのPSDサイドカー(<PSD名>-ymm.json)へプリセット登録する</summary>

        /// <summary>キャラの表情すべて(flat + 立ち絵ごと)。書き庭は flat にまとめて送るが、
        /// 古い書き庭のパックでは立ち絵側にしか入っていないことがある。ここを flat だけで
        /// 見ると「プリセット登録が黙って何もしない」になるので、両方から集める。</summary>
        public static List<PackExpression> AllExpressions(PackCharacter c)
        {
            var seen = new HashSet<string>();
            var all = new List<PackExpression>();
            foreach (var e in c.Expressions) { if (seen.Add(e.Id)) all.Add(e); }
            if (c.Portraits != null)
                foreach (var p in c.Portraits)
                    foreach (var e in p.Expressions) { if (seen.Add(e.Id)) all.Add(e); }
            return all;
        }
        /// <summary>プリセットの書き先1つ(PSD 1つ)。書く前に利用者へ一覧で見せて確認を取る。</summary>
        public sealed class PresetTarget
        {
            public string PsdPath = "";
            /// <summary>記録用の説明(キャラ名(既定/立ち絵 X → YMM4キャラ))</summary>
            public string Label = "";
            public List<PackExpression> Expressions = new List<PackExpression>();
            public CharacterChoice Choice = null!;
            /// <summary>この書き先を決めた取り込み窓の行(キャラ id か「キャラid/立ち絵id」)</summary>
            public string MapKey = "";
            /// <summary>パックに同梱された PSD か(false=利用者の PC にある YMM4 キャラの PSD)</summary>
            public bool InPack;
        }

        /// <summary>立ち絵の表情プリセットの書き先の決め方(純ロジック)。</summary>
        internal enum PortraitPresetKind
        {
            /// <summary>取り込み窓の立ち絵の行で、基底と別の YMM4 キャラが選ばれている → そのキャラの PSD</summary>
            RowSelection,
            /// <summary>パックの ymm4Name だけが別キャラを指している → 書かない(行で選んでもらう)</summary>
            NeedRowSelection,
            /// <summary>既定の立ち絵 → 基底キャラの YMM4 登録 PSD</summary>
            DefaultPsd,
            /// <summary>同じキャラのまま PSD だけ差し替える立ち絵 → パック内のその PSD</summary>
            InPackPsd,
        }

        /// <summary>立ち絵の表情プリセットをどこへ書くか。
        /// ★パックの外の PSD(=利用者の PC にある別キャラの PSD)へは、利用者が窓の立ち絵の行で
        /// 選んだときだけ書く。以前はパックの portraits[].ymm4Name だけで書き先が決まり、
        /// 細工パックが他キャラのサイドカーの同名プリセットを消せた(2026-10-20c)。</summary>
        internal static PortraitPresetKind DecidePortraitPresetTarget(
            string? rowSelectedName, string baseName, string? packYmm4Name,
            bool isDefaultPortrait, bool defaultPsdUsable)
        {
            if (!string.IsNullOrEmpty(rowSelectedName) && rowSelectedName != baseName)
                return PortraitPresetKind.RowSelection;
            if (string.IsNullOrEmpty(rowSelectedName) && !string.IsNullOrEmpty(packYmm4Name) && packYmm4Name != baseName)
                return PortraitPresetKind.NeedRowSelection;
            if (isDefaultPortrait && defaultPsdUsable) return PortraitPresetKind.DefaultPsd;
            return PortraitPresetKind.InPackPsd;
        }

        /// <summary>この窓で利用者が選んだのではない割り当て(名前の一致で自動で付いた・前回の記憶で付いた)。
        /// プリセットの書き先の確認で、パックの外の PSD に最初から印を付けない対象。</summary>
        internal static HashSet<string> NotChosenInWindow(IEnumerable<string> autoPicked, IEnumerable<string> rememberedPicked)
        {
            var s = new HashSet<string>(autoPicked);
            s.UnionWith(rememberedPicked);
            return s;
        }

        /// <summary>書き先の確認で、最初から印を付けておくか。パック内の PSD は付ける。
        /// パックの外の PSD は、その行を利用者が選んだとき(=名前の一致で自動で付いただけではない)だけ。</summary>
        internal static bool PresetTargetCheckedByDefault(PresetTarget t, ISet<string> autoPicked)
            => t.InPack || !autoPicked.Contains(t.MapKey);

        /// <summary>「書き庭が作ったプリセット」の記録のキー(PSD の絶対パス + 名前)。</summary>
        internal static string OwnedPresetKey(string psdPath, string name)
            => Path.GetFullPath(psdPath).ToLowerInvariant() + "|" + name;

        /// <summary>この名前のプリセットを書いてよいか。同名が無ければ新規で書く。
        /// 同名があるときは、書き庭が作った記録のあるものだけ置き換える
        /// (利用者が YMM4 で作った同名のプリセットを黙って消さない・2026-10-20c)。</summary>
        internal static bool MayWritePreset(string psdPath, string name,
            ICollection<string> existingNames, ISet<string> owned)
            => !existingNames.Contains(name) || owned.Contains(OwnedPresetKey(psdPath, name));

        /// <summary>PSD のサイドカー(表情プリセットの保存先)の候補。
        /// ★名前の付き方は「PSD名-ymm.json」と「PSD名.psd-ymm.json」の両方を候補にする
        /// (YMM4 の実際の付け方は実機未確認。控えを取りこぼさない側に倒す)。</summary>
        internal static List<string> SidecarCandidates(string psdPath)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(psdPath)) ?? "";
            return new List<string>
            {
                Path.Combine(dir, Path.GetFileNameWithoutExtension(psdPath) + "-ymm.json"),
                Path.Combine(dir, Path.GetFileName(psdPath) + "-ymm.json"),
            };
        }

        /// <summary>サイドカーとその控え(.bak)がリンク(シンボリックリンク・ジャンクション)なら true。
        /// リンクのまま書く・写すと、書き込みがリンク先(パックの外)へ出る。</summary>
        internal static bool SidecarIsLinked(string psdPath)
        {
            foreach (var s in SidecarCandidates(psdPath))
            {
                if (IsLink(s) || IsLink(s + ".bak")) return true;
            }
            return false;
        }

        /// <summary>書き換える前に、いまあるサイドカーを .bak へ写す。返り値=写した数。
        /// 写せなければ例外(呼び出し側は書き込みをやめる)。</summary>
        internal static int BackupSidecars(string psdPath)
        {
            var n = 0;
            foreach (var s in SidecarCandidates(psdPath))
            {
                if (!File.Exists(s)) continue;
                File.Copy(s, s + ".bak", true);
                n++;
            }
            return n;
        }

        /// <summary>表情プリセットの書き先を集める(まだ何も書かない)。</summary>
        internal static List<PresetTarget> PlanPresetTargets(Pack pack, string packDir,
            Dictionary<string, CharacterChoice> speakerMap, HashSet<string> only, Action<string> log)
        {
            var targets = new List<PresetTarget>();
            foreach (var chara in pack.Characters)
            {
                if (!only.Contains(chara.Name)) continue;
                CharacterChoice? choice;
                if (!speakerMap.TryGetValue(chara.Id, out choice) || choice == null || choice.Model == null)
                {
                    log(Tr.T("log.psd.noSpeaker", chara.Name));
                    continue;
                }
                var itemParam = GetPropValue(choice.Model, "TachieDefaultItemParameter");
                var defaultPsd = itemParam != null ? GetPropValue(itemParam, "FilePath") as string : null;

                // ── 書き込み先PSD(サイドカー)ごとに表情をまとめる ──
                // ★以前はキャラの全表情をID重複除去して「既定PSD」1つへ書いていた。複数立ち絵で
                //   同名(同ID)の表情を持つと2つ目以降が黙って落ち、別立ち絵用の表情まで既定PSDの
                //   サイドカーへ混ざっていた(2026-08 監査)。表情は「属する立ち絵のPSD」へ書き、
                //   重複IDの除去も同じPSD内だけで行う。
                // 注: 書き庭は characters[].expressions(flat)へ立ち絵側の表情も合流させて送る。
                //     flat をそのまま既定PSDへ書くと混入が残るため、立ち絵が抱えるIDを除外する。
                var claimed = new HashSet<string>();
                if (chara.Portraits != null)
                    foreach (var p in chara.Portraits)
                        foreach (var x in p.Expressions ?? new List<PackExpression>())
                            claimed.Add(x.Id);
                var flat = chara.Expressions
                    .Where(x => x.Psd != null && x.Psd.Layers.Count > 0 && !claimed.Contains(x.Id))
                    .ToList();
                var defaultPsdUsable = !string.IsNullOrEmpty(defaultPsd) && File.Exists(defaultPsd);
                if (flat.Count > 0)
                {
                    if (defaultPsdUsable)
                        targets.Add(new PresetTarget
                        {
                            PsdPath = defaultPsd!, Label = chara.Name + "(既定→" + choice.Name + ")",
                            Expressions = flat, Choice = choice, MapKey = chara.Id, InPack = false,
                        });
                    else
                        log(Tr.T("log.psd.noTachie", chara.Name, choice.Name));
                }
                if (chara.Portraits != null)
                {
                    foreach (var p in chara.Portraits)
                    {
                        var exprsP = (p.Expressions ?? new List<PackExpression>())
                            .Where(x => x.Psd != null && x.Psd.Layers.Count > 0).ToList();
                        if (exprsP.Count == 0) continue;
                        string? target;
                        var desc = "立ち絵 " + p.Id;
                        var mapKey = chara.Id;
                        var inPack = false;
                        // 立ち絵ごとの割り当て(取り込みダイアログの立ち絵行。複合キー charId/portraitId)を
                        // 最優先。基底キャラと同じ選択なら従来の Default/サイドカー経路へ。
                        CharacterChoice? selP;
                        var explicitSel = speakerMap.TryGetValue(chara.Id + "/" + p.Id, out selP)
                            && selP != null && selP.Model != null ? selP : null;
                        var kind = DecidePortraitPresetTarget(explicitSel != null ? explicitSel.Name : null,
                            choice.Name, p.Ymm4Name, p.Default, defaultPsdUsable);
                        if (kind == PortraitPresetKind.RowSelection)
                        {
                            var ipS = GetPropValue(explicitSel!.Model!, "TachieDefaultItemParameter");
                            target = ipS != null ? GetPropValue(ipS, "FilePath") as string : null;
                            desc += " → " + explicitSel.Name;
                            mapKey = chara.Id + "/" + p.Id;
                            if (string.IsNullOrEmpty(target) || !File.Exists(target))
                            {
                                log(Tr.T("log.psd.noHostPsd", chara.Name, desc));
                                continue;
                            }
                        }
                        else if (kind == PortraitPresetKind.NeedRowSelection)
                        {
                            // ★パックの ymm4Name だけでは、パックの外(別キャラ)の PSD に書かない
                            log(Tr.T("log.psd.needRow", chara.Name, desc, p.Ymm4Name));
                            continue;
                        }
                        else if (kind == PortraitPresetKind.DefaultPsd)
                        {
                            // 既定の立ち絵(割り当て先の指定なし)= このキャラのYMM4登録PSDへ
                            target = defaultPsd;
                        }
                        else
                        {
                            // 同じYMM4キャラのままPSDだけ差し替える立ち絵: パック内のそのPSDのサイドカーへ
                            var rel = p.Psd != null ? p.Psd.Path : exprsP[0].Psd!.Path;
                            // ★ここは解決したパスの隣にサイドカーを「書く」ので、絶対パスは受け付けない
                            //   (パック外の実在 PSD のサイドカーを細工パックで上書きされる)。同梱PSDのみ。
                            if (!string.IsNullOrEmpty(rel) && Path.IsPathRooted(rel))
                            {
                                log(Tr.T("log.psd.notBundled", chara.Name, desc, rel));
                                continue;
                            }
                            target = ResolveInPackForWrite(packDir, rel);
                            if (target == null || !File.Exists(target))
                            {
                                log(Tr.T("log.psd.noPsd", chara.Name, desc));
                                continue;
                            }
                            inPack = true;
                        }
                        targets.Add(new PresetTarget
                        {
                            PsdPath = target!, Label = chara.Name + "(" + desc + "→" + choice.Name + ")",
                            Expressions = exprsP, Choice = choice, MapKey = mapKey, InPack = inPack,
                        });
                    }
                }
            }
            foreach (var t in targets)
            {
                // 重複IDの除去は同じPSD内だけで行う
                var seen = new HashSet<string>();
                t.Expressions = t.Expressions.Where(x => seen.Add(x.Id)).ToList();
            }
            return targets;
        }

        /// <param name="confirm">書く前に書き先の一覧を利用者に見せ、書いてよいものだけを返す
        /// (null=取りやめ)。</param>
        /// <param name="owned">書き庭が作ったプリセットの記録(OwnedPresetKey)。書いたものを足す。</param>
        public static async System.Threading.Tasks.Task RegisterExpressionPresets(Pack pack, string packDir,
            Dictionary<string, CharacterChoice> speakerMap, HashSet<string> only, Action<string> log,
            Func<IReadOnlyList<PresetTarget>, ICollection<PresetTarget>?> confirm, ISet<string> owned)
        {
            var presetType = FindLoadedType("YukkuriMovieMaker.Plugin.Tachie.Psd.PsdPreset");
            if (presetType == null) { log(Tr.T("log.psd.noPresetType")); return; }
            LogYmm4VersionIfUntested(log);
            var targets = PlanPresetTargets(pack, packDir, speakerMap, only, log);
            if (targets.Count == 0) { log(Tr.T("log.psd.none")); return; }
            // ★書く前に、書き先の PSD を一覧で見せて確認を取る(2026-10-20c)
            var approved = confirm(targets);
            if (approved == null || approved.Count == 0) { log(Tr.T("log.psd.cancelled")); return; }
            var total = 0;
            foreach (var t in targets)
            {
                if (!approved.Contains(t)) continue;
                total += await RegisterPresetsInto(presetType, t.PsdPath, t.Expressions, t.Choice, t.Label, owned, log);
            }
            log(total > 0
                ? Tr.T("log.psd.done", total)
                : Tr.T("log.psd.none"));
        }

        /// <summary>表情プリセット一式を、指定PSDのサイドカー(-ymm.json)へ登録する。返り値=登録件数</summary>
        static async System.Threading.Tasks.Task<int> RegisterPresetsInto(Type presetType, string psdPath,
            List<PackExpression> exprs, CharacterChoice choice, string label, ISet<string> owned, Action<string> log)
        {
            // ★サイドカーや控えがリンクなら書かない(書き込みがリンク先へ出る)
            if (SidecarIsLinked(psdPath)) { log(Tr.T("log.psd.linked", label)); return 0; }
            var settings = LoadPsdFileSettings(psdPath, log);
            if (settings == null) return 0;
            // Presets は ImmutableList のため、作業用リストで組み直してプロパティごと差し替える
            var working = new List<object>();
            if (GetPropValue(settings, "Presets") is System.Collections.IEnumerable existingList)
                foreach (var x in existingList)
                    if (x != null) working.Add(x);
            var existingNames = new HashSet<string>(working
                .Select(x => GetPropValue(x, "Name") as string).Where(n => n != null).Select(n => n!));
            var added = 0;
            var newOnes = new List<object>();
            var written = new List<string>();
            foreach (var expr in exprs)
            {
                var name = expr.Label ?? expr.Id;
                // ★同名プリセットの置き換えは、書き庭が作った記録のあるものだけ
                if (!MayWritePreset(psdPath, name, existingNames, owned))
                {
                    log(Tr.T("log.psd.keptForeign", label, name));
                    continue;
                }
                var preset = Activator.CreateInstance(presetType);
                if (preset == null) continue;
                var nameProp = presetType.GetProperty("Name");
                if (nameProp != null && nameProp.CanWrite) nameProp.SetValue(preset, name);
                var state = await BuildFullFaceState(choice.Model!, expr.Psd!.Layers, expr.Psd!.LayerIds, log);
                if (state == null) { log(Tr.T("log.psd.noState", name)); continue; }
                var pathsProp = presetType.GetProperty("LayerPaths");
                if (pathsProp != null && pathsProp.CanWrite)
                    pathsProp.SetValue(preset, MakeStringList(pathsProp.PropertyType, state.Item2));
                var layersProp = presetType.GetProperty("Layers");
                if (layersProp != null && layersProp.CanWrite)
                    layersProp.SetValue(preset, MakeStringList(layersProp.PropertyType, state.Item1));
                // 置き換えは新しいものが組めてから(組めずに古いものだけ消える、を起こさない)
                working.RemoveAll(x => GetPropValue(x, "Name") as string == name);
                newOnes.RemoveAll(x => GetPropValue(x, "Name") as string == name);
                newOnes.Add(preset);
                written.Add(name);
                added++;
            }
            if (newOnes.Count == 0) return 0; // 書くものが無ければファイルに触らない
            working.AddRange(newOnes);
            // ★書き換える前に、いまのサイドカーを .bak に写す。写せなければ書かない
            try { BackupSidecars(psdPath); }
            catch (Exception ex)
            {
                log(Tr.T("log.psd.backupFail", label, ex.Message));
                return 0;
            }
            try
            {
                // ImmutableList<PsdPreset> を組み立てて Presets を差し替え
                var presetsProp = settings.GetType().GetProperty("Presets");
                if (presetsProp == null || !presetsProp.CanWrite)
                {
                    log(Tr.T("log.psd.noPresetsProp", label));
                    return 0;
                }
                var arr = Array.CreateInstance(presetType, working.Count);
                for (var i = 0; i < working.Count; i++) arr.SetValue(working[i], i);
                var createRange = typeof(System.Collections.Immutable.ImmutableList)
                    .GetMethods(BindingFlags.Static | BindingFlags.Public)
                    .First(m2 => m2.Name == "CreateRange" && m2.GetParameters().Length == 1)
                    .MakeGenericMethod(presetType);
                presetsProp.SetValue(settings, createRange.Invoke(null, new object[] { arr }));
                // ★引数の型(string 1つ)まで確認して選ぶ。名前+引数個数だけだと
                //   YMM4側にオーバーロードが増えた版で別物を呼びかねない
                var save = FindMethodByShape(settings.GetType(), "SaveFromPsdFilePath", new[] { typeof(string) });
                if (save == null) { log(Tr.T("log.psd.noSave", label)); return 0; }
                save.Invoke(settings, new object?[] { psdPath });
                foreach (var nm in written) owned.Add(OwnedPresetKey(psdPath, nm));
                log(Tr.T("log.psd.saved", label, added, Path.GetFileNameWithoutExtension(psdPath)));
                return added;
            }
            catch (Exception ex)
            {
                log(Tr.T("log.psd.saveFail", label, ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                return 0;
            }
        }

        /// <summary>AddItems を小分けにする件数。「停止」を押したとき、まだ置いていない分を
        /// 置かずに済ませるため(一括だと全部置かれてから止まる)。</summary>
        public const int PlaceChunkSize = 100;

        /// <summary>count 件を size ごとに [start, end) の範囲に分ける(純ロジック・テスト対象)。</summary>
        public static List<int[]> ChunkRanges(int count, int size)
        {
            var ranges = new List<int[]>();
            if (count <= 0 || size <= 0) return ranges;
            for (var start = 0; start < count; start += size)
                ranges.Add(new[] { start, Math.Min(count, start + size) });
            return ranges;
        }

        public static System.Threading.Tasks.Task Run(Pack pack, string packDir,
            Dictionary<string, CharacterChoice> speakerMap,
            ImportSettings settings, Action<string> log)
        {
            return Run(pack, packDir, speakerMap, settings, log, null);
        }

        /// <param name="control">一時停止・停止(null なら止められない従来どおり)。
        /// 停止すると ImportStoppedException を投げて抜ける。置いた分は残る。</param>
        public static async System.Threading.Tasks.Task Run(Pack pack, string packDir,
            Dictionary<string, CharacterChoice> speakerMap,
            ImportSettings settings, Action<string> log, ImportControl? control,
            IProgress<ImportProgressReport>? progress = null, Func<string, bool>? confirm = null)
        {
            control ??= new ImportControl();
            // 進捗バーと残り時間の目安(ImportProgress.cs)。progress は UI スレッドへ渡す Progress<T> を想定
            var progressWatch = System.Diagnostics.Stopwatch.StartNew();
            Action<ImportStage, double, int, int, double?> report = (st, f, d, t, rem) =>
            {
                if (progress == null) return;
                progress.Report(new ImportProgressReport
                {
                    Stage = st, Fraction = ImportProgressMath.Overall(st, f), Done = d, Total = t,
                    RemainingSeconds = rem, ElapsedSeconds = progressWatch.Elapsed.TotalSeconds,
                });
            };
            report(ImportStage.Prepare, 0, 0, 0, null);
            LogYmm4VersionIfUntested(log);
            var timeline = FindActiveTimeline(log);
            if (timeline == null) return;
            var mainModel = FindMainModel(log);

            // 取り込み前に既存アイテムを控える(参照ベース)。後段の再配置・打ち切り・レーン畳みは
            // 「今回置いた分」だけを対象にする。これが無いと、既にセリフを置いたプロジェクトへの
            // 取り込みや同じパックの2回目取り込みで、既存アイテムまで再整列・切り詰めされて壊れる。
            var preExisting = new HashSet<object>();
            try
            {
                var pre = GetPropValue(timeline, "Items") as System.Collections.IEnumerable;
                if (pre != null) foreach (var it in pre) preExisting.Add(it);
                if (preExisting.Count > 0) log(Tr.T("log.pre.excluded", preExisting.Count));
            }
            catch (Exception ex) { log(Tr.T("log.pre.fail", ex.Message)); }

            var fps = ReadFps(timeline, log);
            // ボイスの実尺を測る(ResetLength)ための動画情報。取れなければ従来どおり後の取り直しに任せる
            var voiceVideoInfo = GetPropValue(timeline, "VideoInfo");
            // 公式方式で1本も作れなかった(声が出ない・版の違い)ときは、残りの行も待たされないよう従来の方式へ切り替える
            var officialVoiceBroken = false;
            // 取り込み全体の所要時間(ログの最後に出す。1行ずつ/並列の比較と、遅いという報告の手がかり)
            var runWatch = System.Diagnostics.Stopwatch.StartNew();
            // 段階ごとの所要時間(ログの最後に長い順で出す)
            var stages = new StageTimer();
            long loopVoiceMs = 0; // 配置のループの中で音声を作るのにかかった時間(1行ずつ・作り直し)
            var voiceLines = 0;
            // 本命(公式の台本と同じ手順)で作り、生成の完了と実尺の確定まで済んでいるボイス
            var officialVoices = new HashSet<object>();
            var projW = ReadVideoInfoValue(timeline, "Width", 1920, log);
            var projH = ReadVideoInfoValue(timeline, "Height", 1080, log);
            // ★飽和させる。(int) への素の変換は、あふれると負の値になる(2026-10-20c)
            Func<double, int> F = sec => SecondsToFrames(sec, fps); // 尺(長さ)用

            // 配置先(設定): 開始レイヤーのシフト+開始フレーム(先頭 or カーソル位置)
            var layerOffset = Math.Max(0, settings.BaseLayer);
            var frameOffset = 0;
            if (settings.StartAt == "cursor")
            {
                try
                {
                    var cf = FindProp(timeline.GetType(), "CurrentFrame");
                    if (cf != null) frameOffset = Convert.ToInt32(cf.GetValue(timeline));
                }
                catch { }
            }
            if (layerOffset > 0 || frameOffset > 0)
                log(Tr.T("log.dest", layerOffset, frameOffset > 0 ? Tr.T("log.dest.frame", frameOffset) : Tr.T("log.dest.start")));
            Func<double, int> FrameAt = sec => AddFramesSaturating(F(sec), frameOffset); // 開始位置用
            Func<int, int> L = n => n + layerOffset;

            var evs = pack.Events;
            var totalEnd = evs.Count == 0 ? 0 : evs.Max(ev2 => ev2.Start + ev2.Seconds) + 1.0;

            // レイヤー割り当て(2パス): 実際に使うレーンだけ数えて0から隙間なく詰める。
            // 重なり順: 背景 < BGM < SE < ト書き < キャラ(立ち絵/表情) < 小物 < エフェクト < 窓 < ボイス(字幕)
            // 設定で番号を固定した種類(セリフ・背景など)はその番号、残りは空いている番号へ登場順に詰める。
            // セリフ「全員で1つ」のときは voice:* に寄せる(LanePlan.CanonicalKey)。
            var lanePlan = new LanePlan(LanePlan.FixedFromSettings(settings.VoiceLayerMode, settings.VoiceLayers, settings.KindLayers, settings.AssetLayers));
            Func<string, string?, string> assetKey = (defKey, name) => LanePlan.AssetKey(defKey, name, settings.AssetLayers);
            Action<string> addLane = key => lanePlan.Add(LanePlan.CanonicalKey(key, settings.VoiceLayerMode));
            Func<string, int> laneOf = key => lanePlan.Of(LanePlan.CanonicalKey(key, settings.VoiceLayerMode));
            // 封じ込め検証を通した存在確認(絶対/UNC/.. はここで false=UNC を File.Exists に到達させない)。
            Func<PackAssetRef?, bool> assetExists = a =>
                a != null && ResolveInPack(packDir, a.Path) != null;

            // 話者の登場順(立ち絵の左右振り分けにも使う)
            var charOrder = new List<string>();
            foreach (var e2 in evs)
                if (e2.Type == "serif" && e2.Speaker != null && !charOrder.Contains(e2.Speaker))
                    charOrder.Add(e2.Speaker);
            Func<string, int> slotOf = id =>
            {
                var idx = charOrder.IndexOf(id);
                if (idx < 0) { charOrder.Add(id); idx = charOrder.Count - 1; }
                return idx;
            };

            // 小物のレーン割り当て: 「時間が重ならない小物は同じレーンに畳む」(区間グラフの貪欲彩色)。
            // 退場を書いて終わる小物どうし(=入れ替わり)は1レーンに背中合わせで入り、名前ごとに別レーンへ
            // 散らばらない。逆に、退場が無く最後まで残る小物どうしは時間が重なるので別レーンへ分かれる。
            // ★「次の停止・次の同種」は前もって1回の逆走査で引けるようにしておく。
            //   イベントごとに後ろを全部なめると件数の二乗になり、大きなパックで固まる(2026-10-20c)
            var nextPropStop = NextNamedStopStarts(evs, "prop_stop", totalEnd);
            var nextWindowStop = NextStartOfTypes(evs, new[] { "window_stop" }, totalEnd);
            var nextBg = NextStartOfTypes(evs, new[] { "bg" }, totalEnd);
            var nextBgm = NextStartOfTypes(evs, new[] { "bgm", "bgm_stop" }, totalEnd);
            var nextWindow = NextStartOfTypes(evs, new[] { "window" }, totalEnd);
            var propSlot = new Dictionary<int, int>(); // 小物イベントの index → スロット番号
            {
                Func<int, double> propEnd = i => nextPropStop[i];
                var propIdx = new List<int>();
                for (var i = 0; i < evs.Count; i++)
                    if (evs[i].Type == "prop" && assetExists(evs[i].Asset)) propIdx.Add(i);
                propIdx.Sort((a, b) => evs[a].Start.CompareTo(evs[b].Start));
                propSlot = AssignGreedySlots(propIdx, i2 => evs[i2].Start, propEnd);
            }

            foreach (var e2 in evs)
                if (e2.Type == "bg" && assetExists(e2.Asset)) addLane(assetKey("bg", e2.Asset!.Name));
            foreach (var e2 in evs)
                if (e2.Type == "bgm" && assetExists(e2.Asset)) addLane(assetKey("bgm", e2.Asset!.Name));
            foreach (var e2 in evs)
            {
                if (e2.Type == "se" && assetExists(e2.Asset)) addLane(assetKey("se", e2.Asset!.Name));
                if (e2.Type == "serif")
                    foreach (var s2 in e2.Se) if (assetExists(s2)) addLane(assetKey("se", s2.Name));
            }
            if (evs.Any(e2 => e2.Type == "stage")) addLane("stage");
            foreach (var id in charOrder) addLane("chara:" + id);
            for (var i = 0; i < evs.Count; i++)
                if (evs[i].Type == "prop" && assetExists(evs[i].Asset)) addLane(assetKey("prop-slot:" + propSlot[i], evs[i].Asset!.Name));
            foreach (var e2 in evs)
                if (e2.Type == "effect" && assetExists(e2.Asset)) addLane(assetKey("effect:" + e2.Asset!.Name, e2.Asset!.Name));
            foreach (var e2 in evs)
                if (e2.Type == "window" && assetExists(e2.Asset)) addLane(assetKey("window", e2.Asset!.Name));
            foreach (var id in charOrder) addLane("voice:" + id);
            // 表情アイテムはボイスより下(大きい番号)に。同時刻の表情制御は下の行が勝つため、
            // 表情レイヤーがセリフの表情変更(無効化済みだが保険)より必ず優先される
            foreach (var id in charOrder)
            {
                CharacterChoice? ch;
                var mapped2 = speakerMap.TryGetValue(id, out ch) && ch != null && ch.Model != null;
                var hasTachie = mapped2 && GetPropValue(ch!.Model!, "TachieDefaultItemParameter") != null;
                var hasExpr = evs.Any(e2 => e2.Type == "serif" && e2.Speaker == id && e2.Expression != null);
                if (hasTachie && hasExpr) addLane("face:" + id);
            }
            lanePlan.Resolve();
            if (VoiceLayerMode.Normalize(settings.VoiceLayerMode) != VoiceLayerMode.PerCharacter
                || (settings.KindLayers != null && settings.KindLayers.Count > 0))
                log(Tr.T("log.layers", VoiceLayerMode.Normalize(settings.VoiceLayerMode),
                    settings.KindLayers != null && settings.KindLayers.Count > 0
                        ? Tr.T("log.layers.fixed", string.Join(",", settings.KindLayers.Select(kv => kv.Key + ":" + kv.Value))) : ""));

            // 現在の立ち位置(layout イベントで更新され、以降の配置に適用)
            var currentLayout = new Dictionary<string, PackPosition>();
            Func<string, int, double> xOf = (charId, slotIndex) =>
            {
                PackPosition? pos;
                if (currentLayout.TryGetValue(charId, out pos) && pos != null)
                    return projW * pos.X / 100.0;
                return (slotIndex % 2 == 0 ? -1 : 1) * projW * settings.PortraitXRatio;
            };
            Func<string, double> heightRatioOf = charId =>
            {
                PackPosition? pos;
                if (currentLayout.TryGetValue(charId, out pos) && pos != null && pos.Height != null)
                    return pos.Height.Value / 100.0;
                return settings.PortraitHeightRatio;
            };
            // 書き庭が教えてくれた「いま出ている立ち絵のPSD実高(px)」。
            Func<string, double?> psdHeightOf = charId =>
            {
                PackPosition? pos;
                if (currentLayout.TryGetValue(charId, out pos) && pos != null && pos.PsdHeight != null)
                {
                    var h = pos.PsdHeight.Value;
                    // 壊れた値は使わない(手編集パック対策)
                    if (double.IsFinite(h) && h > 0 && h <= 100000) return h;
                }
                return null;
            };
            // 縦オフセット(YMM4 の Y はピクセル・正=下)。台本 layout の y は正=上なので符号反転。
            // 未指定は 0(下端揃えのまま)。
            Func<string, double> yOffsetOf = charId =>
            {
                PackPosition? pos;
                if (currentLayout.TryGetValue(charId, out pos) && pos != null && pos.Y != null)
                    return -(projH * pos.Y.Value / 100.0);
                return 0.0;
            };
            var tachiePlaced = new HashSet<string>();
            var tachieUnavailable = new HashSet<string>();
            var warnedUnmapped = new HashSet<string>();
            // 配置済み立ち絵アイテム(退場時に尺を切り詰めるための参照): charId → (アイテム, 開始秒)
            var tachieItems = new Dictionary<string, Tuple<BaseItem, double>>();
            // portrait 切替で置き直した立ち絵アイテム(行内立ち絵)。これが後続の portrait で
            // 切られたら「セリフ終了時刻(推定)で切られた」ので、実尺再配置での終端合わせ対象になる。
            var portraitPlacedItems = new HashSet<object>();
            // 終端合わせ対象: アイテム → 対応ボイスの旧開始フレーム(アンカー)
            var portraitEndSync = new Dictionary<object, int>();

            // 複数立ち絵(別PSD切替): charId → (立ち絵id → PackPortrait)、現在の立ち絵id
            var portraitsById = new Dictionary<string, Dictionary<string, PackPortrait>>();
            var activePortrait = new Dictionary<string, string>();
            foreach (var pc in pack.Characters)
            {
                if (pc.Portraits == null || pc.Portraits.Count == 0) continue;
                var map = new Dictionary<string, PackPortrait>();
                foreach (var pr in pc.Portraits) map[pr.Id] = pr;
                portraitsById[pc.Id] = map;
                var def = pc.Portraits.FirstOrDefault(p => p.Default) ?? pc.Portraits[0];
                activePortrait[pc.Id] = def.Id;
            }
            // アクティブ立ち絵に対する「配置用キャラ」解決。
            // ★割り当て済み(Ymm4Name あり)のYMM4キャラへ OverrideTachieFilePath で別PSDを
            //   強制すると、立ち絵タイプ不一致等でYMM4ごと落ちる(実機クラッシュ 2026-08-09)。
            //   Ymm4Name がある立ち絵は「そのYMM4キャラ自身の既定立ち絵」で置き、上書きしない。
            //   返り値: Item1=配置キャラ, Item2=PSD上書き(Override)してよいか。
            //   null=割り当て先YMM4キャラが見つからない(呼び出し側でスキップ/仮置き)。
            //   優先順: ①取り込みダイアログの立ち絵ごとの割り当て(speakerMap の複合キー
            //   "charId/portraitId")→ ②パックの Ymm4Name → ③基底キャラ+Override(従来)。
            //   ①②で基底キャラと同じ名前が選ばれている場合は従来どおりPSD差し替えで置く。
            List<CharacterChoice>? ymm4CharsForHost = null; // 必要になったら一度だけ取得
            Func<string, CharacterChoice, PackPortrait?, Tuple<CharacterChoice, bool>?> resolveTachieHost =
                (charId0, baseChoice, portrait) =>
            {
                if (portrait == null) return Tuple.Create(baseChoice, true); // 単一立ち絵: 従来
                CharacterChoice? sel;
                var hasSel = speakerMap.TryGetValue(charId0 + "/" + portrait.Id, out sel)
                    && sel != null && sel.Model != null;
                var decision = DecideTachieHostName(
                    hasSel ? sel!.Name : null, PortraitAssignedName(portrait), baseChoice.Name);
                if (decision.Item1 == null) return Tuple.Create(baseChoice, true); // 基底+Override
                if (hasSel && sel!.Name == decision.Item1) return Tuple.Create(sel, false);
                if (ymm4CharsForHost == null) ymm4CharsForHost = GetYmm4Characters(_ => { });
                var m = FindYmm4CharacterByName(ymm4CharsForHost, decision.Item1);
                return m != null ? Tuple.Create(m, false) : null;
            };
            // ★ボイスも同じ解決結果で生成する(音声話者も変えないと表情アイテム・口パクの
            //   対応が切れる)。これでボイスと立ち絵/表情の CharacterName は常に一致し、
            //   実尺再配置の突き合わせも従来の CharacterName ベースのままで整合する。
            // 現在アクティブな立ち絵(複数立ち絵キャラのみ。単一立ち絵は null)
            // state を引数にしているのは、ボイスを先にまとめて作るとき(下の「先に作る」)に、
            // 配置のループと同じ規則で「その行の時点の立ち絵」を先読みするため。
            Func<Dictionary<string, string>, string, PackPortrait?> portraitIn = (state, charId) =>
            {
                // 保険込みの解決(FindPortrait): 書き庭側は id へ正規化して送るが、古い書き庭が
                // 作ったパックでは表示名・YMM4名のまま入っていることがある。表情は元から両対応
                // なので、立ち絵だけ id 限定で「切替が黙って効かない」になるのを防ぐ。
                if (portraitsById.TryGetValue(charId, out var m)
                    && state.TryGetValue(charId, out var pid))
                    return FindPortrait(m, pid);
                return null;
            };
            Func<string, PackPortrait?> activePortraitOf = (charId) => portraitIn(activePortrait, charId);
            // セリフの話者割り当てと、アクティブ立ち絵の割り当て先キャラ(ボイスもこのキャラの声で作る)。
            // Item1=取り込み窓で選んだキャラ(無ければ null)、Item2=resolveTachieHost の結果。
            Func<PackEvent, PackPortrait?, Tuple<CharacterChoice?, Tuple<CharacterChoice, bool>?>> serifHostOf = (e, ap) =>
            {
                CharacterChoice? ch = null;
                if (e.Speaker != null) speakerMap.TryGetValue(e.Speaker, out ch);
                var host = ch != null && ch.Model != null ? resolveTachieHost(e.Speaker ?? "?", ch, ap) : null;
                return Tuple.Create(ch, host);
            };

            // 対応する停止イベント(prop_stop / window_stop)までの時刻を探す

            var items = new List<BaseItem>();
            var skipped = new List<string>();
            // 小物の入れ替わり検出用: 全小物と、そのうち「退場(prop_stop)が明示された小物」。
            // 退場が書かれた小物は、次の小物の登場と重ならないよう再配置後に詰める(レーンをまたいで効く)。
            var propItems = new List<object>();
            var propExplicitEnd = new HashSet<object>();
            var serifTemplateWarned = false; // セリフ(VoiceItem)のテンプレ適用は未対応: 一度だけ警告
            var emptySerifSkipped = 0; // 中身の無い行(SE/間だけ)でボイスを置かなかった数
            // セリフのボイス → その行に書かれた「間」(秒)。実尺で並べ直すときに後ろへ足す
            var voicePause = new Dictionary<object, double>();
            object 直前のボイス = null; // 中身の無い行の「間」を足す先(下の説明)
            // MainModel経由で直接追加したボイス(items に入らない)のレイヤー記録
            var directPlacements = new List<Tuple<int, string>>();

            // セリフ行に埋めた SE(`[SE:…]`)をその行の開始位置に置く。本文のある行・空の行の両方から使う
            Action<PackEvent> placeInlineSe = (e) =>
            {
                foreach (var se in e.Se)
                {
                    var sePath = ResolveAsset(packDir, se, skipped, e);
                    if (sePath == null) continue;
                    var seItem = new AudioItem();
                    SetProps(seItem, log,
                        P("FilePath", sePath), P("Frame", FrameAt(e.Start)), P("Layer", L(laneOf(assetKey("se", se.Name)))),
                        P("Length", F(InlineSeLength(sePath, se.Name, log))), P("Remark", "SE: " + se.Name));
                    items.Add(seItem);
                }
            };

            // ── セリフのボイスを先にまとめて作る(公式の台本機能と同じ流れ)──
            // 公式は全セリフのボイスを並列で作り終えてから順に並べる。以前の取り込みは1行ずつ
            // 作るのを待ってから次へ進んでいたので、行数ぶん待ちが積み上がっていた。
            // ここでは「作る」だけを先に済ませ、並べ方(位置・隙間・被りの調整)とログの順番は
            // 下のループのまま変えない。作れなかった行は、ループの中で今までの経路で作り直す。
            // 同時数はエンジンの種類ごと(VoicePrefetch.cs の表)。設定 VoiceConcurrency=1 で従来の1行ずつ。
            Dictionary<int, VoiceJob>? prefetched = null;
            stages.Lap(Tr.T("stage.prep"));
            // セリフごとの声のキャラとエンジンを先に読む(先にまとめて作る・料金のかかる声の確認に使う)
            var serifPlan = new List<VoiceJob>();
            {
                // 立ち絵の切替(portrait)はセリフの声のキャラを変えうるので、ループと同じ規則で先読みする。
                // ★先読みが食い違っても、ループ側で「キャラ・本文・読み」を突き合わせ、違えば使わない。
                var simPortrait = new Dictionary<string, string>(activePortrait);
                for (var j = 0; j < evs.Count; j++)
                {
                    var e = evs[j];
                    if (!EventFitsFrames(e.Start, e.Seconds, fps, frameOffset)) continue;
                    if (e.Type == "portrait")
                    {
                        Dictionary<string, PackPortrait>? pm;
                        if (e.Speaker != null && !string.IsNullOrEmpty(e.Name)
                            && portraitsById.TryGetValue(e.Speaker, out pm) && pm != null)
                        {
                            var pr = FindPortrait(pm, e.Name!);
                            if (pr != null) simPortrait[e.Speaker] = pr.Id;
                        }
                        continue;
                    }
                    if (e.Type != "serif" || string.IsNullOrWhiteSpace(e.Text) || e.NoVoice) continue;
                    var vh = serifHostOf(e, portraitIn(simPortrait, e.Speaker ?? "?"));
                    var vc = vh.Item2 != null ? vh.Item2.Item1 : vh.Item1;
                    if (vc == null || vc.Model == null) continue;
                    serifPlan.Add(new VoiceJob
                    {
                        Index = j, Model = vc.Model, Text = e.Text ?? "",
                        Reading = string.IsNullOrEmpty(e.Reading) ? null : e.Reading,
                        Engine = SpeakerTypeName(vc.Model),
                    });
                }
            }
            // ★料金・回数の制限がかかる声(オンライン)と、利用者の外部コマンドで作る声は、作る前に件数と文字数を見せて確かめる
            //   (細工したパックで、利用者の課金枠を黙って使わせない・2026-10-22 監査)
            var costly = CostlyVoiceSummary(serifPlan.Select(x => Tuple.Create(x.Engine, x.Text)));
            if (costly.Count > 0)
            {
                var lines = string.Join("\n", costly.Select(c => Tr.T("confirm.costly.line", ShortTypeName(c.Item1), c.Item2, c.Item3)));
                log(Tr.T("log.costly", lines.Replace("\n", " / ")));
                if (confirm != null && !confirm(Tr.T("confirm.costly", lines)))
                {
                    log(Tr.T("log.costly.cancelled"));
                    return;
                }
            }
            if (VoiceConcurrency.UsePrefetch(settings.VoiceConcurrency, settings.LegacyVoicePath))
            {
                var jobs = serifPlan.ToList();
                // 先に作らないエンジン(表に無い外部の音声プラグイン・SAPI の外部の声など)は、ループで今までどおり作る
                var cpu0 = Environment.ProcessorCount;
                var skippedEngines = jobs.Where(x => VoiceConcurrency.LimitFor(x.Engine, cpu0, settings.VoiceConcurrency) == VoiceConcurrency.NotPrefetched)
                    .Select(x => x.Engine).Distinct().ToList();
                if (skippedEngines.Count > 0)
                {
                    log(Tr.T("log.prefetch.skipEngine", string.Join(", ", skippedEngines.Select(ShortTypeName))));
                    jobs = jobs.Where(x => VoiceConcurrency.LimitFor(x.Engine, cpu0, settings.VoiceConcurrency) != VoiceConcurrency.NotPrefetched).ToList();
                }
                if (jobs.Count > 1)
                {
                    var cpu = Environment.ProcessorCount;
                    var globalLimit = VoiceConcurrency.GlobalLimit(cpu, settings.VoiceConcurrency);
                    log(Tr.T("log.prefetch.start", jobs.Count, globalLimit, string.Join(", ",
                        jobs.GroupBy(x => x.Engine).Select(g =>
                            ShortTypeName(g.Key) + "×" + g.Count() + "(" + VoiceConcurrency.LimitFor(g.Key, cpu, settings.VoiceConcurrency) + ")"))));
                    var pw = System.Diagnostics.Stopwatch.StartNew();
                    // ★別スレッドで作るので、表示言語(ログの言語と YMM4 の文言)を UI スレッドに揃える
                    var uiCulture = System.Globalization.CultureInfo.CurrentUICulture;
                    var culture = System.Globalization.CultureInfo.CurrentCulture;
                    // 残り時間の目安: 試しの1本目が終わった時点を基準に、そこからの速さで割り出す
                    // (1本目は1本だけで作るので、並べて作る速さとは違う)
                    var etaLock = new object();
                    Tuple<int, double, double>? firstEta = null; // (何件目で, いつ, 目安の秒)
                    double etaRefAt = -1;
                    var maxLimit = jobs.Select(x => VoiceConcurrency.LimitFor(x.Engine, cpu, settings.VoiceConcurrency)).Max();
                    var minSamples = ImportProgressMath.MinSamples(Math.Min(maxLimit, globalLimit), jobs.Count - 1);
                    var etaWatch = System.Diagnostics.Stopwatch.StartNew();
                    report(ImportStage.Voices, 0, 0, jobs.Count, null);
                    Action<int, int> onVoiceDone = (d, t) =>
                    {
                        double? rem = null;
                        lock (etaLock)
                        {
                            var now = etaWatch.Elapsed.TotalSeconds;
                            if (d == 1 || etaRefAt < 0) etaRefAt = now;
                            else rem = ImportProgressMath.EstimateRemaining(d - 1, now - etaRefAt, t - d, minSamples);
                            if (rem != null && firstEta == null) firstEta = Tuple.Create(d, now, rem.Value);
                        }
                        report(ImportStage.Voices, (double)d / t, d, t, rem);
                    };
                    var res = await VoicePrefetch.RunAsync<VoiceJob, VoiceItem>(jobs,
                        x => x.Engine,
                        g => VoiceConcurrency.LimitFor(g, cpu, settings.VoiceConcurrency),
                        globalLimit,
                        x =>
                        {
                            x.Ran = true;
                            System.Globalization.CultureInfo.CurrentUICulture = uiCulture;
                            System.Globalization.CultureInfo.CurrentCulture = culture;
                            // ★ログは行ごとに溜め、ループがその行に来たときに出す(並列の順で混ざらない)
                            return CreateVoiceItemOfficial(x.Model, x.Text, x.Reading, voiceVideoInfo, s => x.Log.Add(s));
                        },
                        (d, t) => control.CheckpointAsync(Tr.T("log.prefetch.progress", d, t)),
                        probeFirst: true,
                        onVoiceDone);
                    for (var k = 0; k < jobs.Count; k++)
                    {
                        jobs[k].Result = res.Items[k];
                        jobs[k].Error = res.Errors[k];
                    }
                    log(Tr.T("log.prefetch.done", res.Items.Count(x => x != null), jobs.Count, pw.ElapsedMilliseconds));
                    // 残り時間の目安の当たり具合(最初に出した目安と、実際に残っていた時間)
                    if (firstEta != null)
                        log(Tr.T("log.eta.check", firstEta.Item1, jobs.Count, Math.Round(firstEta.Item3, 1),
                            Math.Round(etaWatch.Elapsed.TotalSeconds - firstEta.Item2, 1)));
                    if (res.ProbeFailed) log(Tr.T("log.prefetch.probeFailed"));
                    prefetched = jobs.ToDictionary(x => x.Index);
                }
            }
            stages.Lap(Tr.T("stage.prefetch"));
            // 配置のループの進み具合。先に作ったなら「配置」、1行ずつ作るなら「ボイスの作成」として数える
            var serifTotal = evs.Count(e => e.Type == "serif" && !string.IsNullOrWhiteSpace(e.Text));
            var loopWatch = System.Diagnostics.Stopwatch.StartNew();
            var loopVoicesDone = 0;
            Tuple<int, double, double>? loopFirstEta = null; // 1行ずつのときの残り時間の目安の当たり具合
            if (prefetched == null && serifTotal > 0) report(ImportStage.Voices, 0, 0, serifTotal, null);

            for (var i = 0; i < evs.Count; i++)
            {
                var ev = evs[i];
                // ★フレームに直すとあふれる時刻のイベントは置かない(負の位置・長さで YMM4 に渡さない)
                if (!EventFitsFrames(ev.Start, ev.Seconds, fps, frameOffset))
                {
                    skipped.Add(Tr.T("log.skipped.frameOverflow", ev.No));
                    continue;
                }
                switch (ev.Type)
                {
                    case "bg":
                    {
                        var path = ResolveAsset(packDir, ev.Asset, skipped, ev);
                        if (path == null) break;
                        var item = MakeItem(typeof(ImageItem), ev.Template, log, () => new ImageItem());
                        SetProps(item, log,
                            P("FilePath", path), P("Frame", FrameAt(ev.Start)), P("Layer", L(laneOf(assetKey("bg", ev.Asset!.Name)))),
                            // ★1フレーム未満にしない。@背景 が同時刻に並ぶと次の開始が同じで Length=0 になる
                            //   (小物・窓の退場は Math.Max(1, …) で守っているのに、ここだけ無かった)
                            P("Length", Math.Max(1, F(nextBg[i] - ev.Start))),
                            P("Remark", "背景: " + ev.Asset!.Name));
                        items.Add(item);
                        break;
                    }
                    case "bgm":
                    {
                        var path = ResolveAsset(packDir, ev.Asset, skipped, ev);
                        if (path == null) break;
                        var item = MakeItem(typeof(AudioItem), ev.Template, log, () => new AudioItem());
                        SetProps(item, log,
                            P("FilePath", path), P("Frame", FrameAt(ev.Start)), P("Layer", L(laneOf(assetKey("bgm", ev.Asset!.Name)))),
                            P("Length", Math.Max(1, F(nextBgm[i] - ev.Start))),
                            P("Remark", "BGM: " + ev.Asset!.Name));
                        items.Add(item);
                        break;
                    }
                    case "se":
                    {
                        var path = ResolveAsset(packDir, ev.Asset, skipped, ev);
                        if (path == null) break;
                        var item = MakeItem(typeof(AudioItem), ev.Template, log, () => new AudioItem());
                        // ★尺は実ファイル長(読めないときだけ従来の1秒)。1秒固定だと長い効果音が途中で切れる
                        // ★読めなかったことを黙らない。尺を測れるのは wav/mp3/ogg/opus だけで、
                        //   素材として通る m4a/aac/flac は必ず1秒に切られる(MediaProbe.cs)。
                        //   無言だと「wav に変換すると直る」という説明のつかない挙動になる(2026-08-16 監査)。
                        var seLen = AudioDurationSeconds(path);
                        if (seLen == null)
                            log(Tr.T("log.se.noLength", ev.Asset!.Name));
                        SetProps(item, log,
                            P("FilePath", path), P("Frame", FrameAt(ev.Start)), P("Layer", L(laneOf(assetKey("se", ev.Asset!.Name)))),
                            P("Length", F(seLen ?? 1.0)),
                            P("Remark", "SE: " + ev.Asset!.Name));
                        items.Add(item);
                        break;
                    }
                    case "stage":
                    {
                        var item = MakeItem(typeof(TextItem), ev.Template, log, () => new TextItem());
                        SetProps(item, log,
                            P("Text", "(" + ev.Text + ")"), P("Frame", FrameAt(ev.Start)), P("Layer", L(laneOf("stage"))),
                            P("Length", F(ev.Seconds)), P("IsHidden", true),
                            P("Remark", "ト書き"));
                        items.Add(item);
                        break;
                    }
    case "layout":
                    {
                        if (ev.Positions != null)
                        {
                            foreach (var pos in ev.Positions)
                            {
                                if (pos.Exit)
                                {
                                    // 退場: 置いてある立ち絵をこの時点で切る。以降のセリフで再登場できる
                                    Tuple<BaseItem, double>? placed;
                                    if (tachieItems.TryGetValue(pos.Speaker, out placed) && placed != null)
                                    {
                                        SetProps(placed.Item1, log,
                                            P("Length", Math.Max(1, F(ev.Start - placed.Item2))));
                                        tachieItems.Remove(pos.Speaker);
                                        tachiePlaced.Remove(pos.Speaker);
                                        log(Tr.T("log.exit", LF(pos.Speaker), ev.Start));
                                    }
                                    else
                                    {
                                        log(Tr.T("log.exit.skip", LF(pos.Speaker)));
                                    }
                                    continue;
                                }
                                currentLayout[pos.Speaker] = pos;
                                // ★すでに置いてある立ち絵にも、その場で反映する。以前は
                                //   「次に新しく置くとき」しか見ておらず、場面の途中で @配置 を
                                //   書いても YMM4 では最初の位置・大きさのままだった
                                //   (書き庭の配置プレビューは動くので食い違う・2026-08-08 監査)。
                                if (tachieItems.TryGetValue(pos.Speaker, out var 配置済み) && 配置済み != null)
                                {
                                    var slotIdx2 = slotOf(pos.Speaker);
                                    var already = ResolvePortrait(packDir,
                                        pack.Characters.FirstOrDefault(c => c.Id == pos.Speaker),
                                        activePortraitOf(pos.Speaker), null);
                                    if (already != null)
                                        ApplyPortraitLayout(配置済み.Item1, already,
                                            xOf(pos.Speaker, slotIdx2), projH, heightRatioOf(pos.Speaker), log,
                                            yOffsetOf(pos.Speaker), psdHeightOf(pos.Speaker));
                                    else
                                    {
                                        SetAnim(配置済み.Item1, "X", xOf(pos.Speaker, slotIdx2), log);
                                        SetAnim(配置済み.Item1, "Y", yOffsetOf(pos.Speaker), log);
                                    }
                                }
                                log(Tr.T("log.layoutUpdate",
                                    LF(pos.Speaker), pos.X, pos.Height != null ? pos.Height + "%" : Tr.T("log.default")));
                            }
                        }
                        break;
                    }
                    case "serif":
                    {
                        // ★中身の無い行(SE や 間 だけの行)でボイスを置かない。置くと
                        //   YMM4 に「セリフが空のアイテム」が並び、手で消す手間になる
                        //   (書き庭は SE/間 のために行そのものは出している・2026-08-08 監査)。
                        if (string.IsNullOrWhiteSpace(ev.Text))
                        {
                            // ★「間」だけは取りこぼさない。ボイスを置かないと、その行に
                            //   書かれた [間:N] を足す先が無くなり、同じコミットで直したはずの
                            //   「間が詰まる」がここで復活する(2026-08-08 自己監査)。
                            //   溜めは直前のセリフの後ろに足す=台本の見た目どおりになる。
                            if (ev.Pause > 0 && 直前のボイス != null)
                            {
                                voicePause.TryGetValue(直前のボイス, out var 既存);
                                voicePause[直前のボイス] = 既存 + ev.Pause;
                            }
                            // ★SE は本文が空でも置く。「ソラ: [SE:ドンッ]」のように SE だけを運ぶ行は
                            //   仕様(pack-format.md)で認められている。ここで break すると SE が黙って消える
                            placeInlineSe(ev);
                            emptySerifSkipped++;
                            break;
                        }
                        if (!string.IsNullOrEmpty(ev.Template) && !serifTemplateWarned)
                        {
                            serifTemplateWarned = true;
                            log(Tr.T("log.serifTemplate"));
                        }
                        var chara = pack.Characters.FirstOrDefault(c => c.Id == ev.Speaker);
                        var charId = ev.Speaker ?? "?";
                        // レーンは laneOf で解決
                        var slotIndex = slotOf(charId); // 登場順: 0=左,1=右,…

                        // 話者割り当て(UIで選択済み)。無ければ ymm4Name → name → 台本表記の順
                        var apSerif = activePortraitOf(charId);
                        var hostOf = serifHostOf(ev, apSerif);
                        var choice = hostOf.Item1;
                        var ymm4Name = (choice != null ? choice.Name : null)
                                       ?? (chara != null ? (chara.Ymm4Name ?? chara.Name) : null)
                                       ?? ev.SpeakerName ?? "";

                        // ★アクティブ立ち絵の割り当て先キャラ解決(立ち絵・表情・ボイス共通)。
                        //   別PSD立ち絵がアクティブな間のセリフは、ボイスもそのYMM4キャラの
                        //   声設定で生成する(音声話者も変えないと表情アイテム・口パクの対応が
                        //   切れる)。割り当てなし/解決不能なら従来どおり基底キャラ。
                        //   レーン(voice:charId)は書き庭キャラ単位のまま。
                        var serifHost = hostOf.Item2;
                        var voiceChoice0 = serifHost != null ? serifHost.Item1 : choice;
                        var voiceName = serifHost != null ? serifHost.Item1.Name : ymm4Name;
                        // ★セリフがファイルの場所の形の行は、声を作らない(YMM4 がその場所の音声を読み込むため)
                        var voiceChoice = ev.NoVoice ? null : voiceChoice0;

                        // 本命: 公式の台本編集と同じ手順。ボイスをタイムラインの外で作り(生成の完了を待つ)、
                        // ResetLength で実尺を確定してから、こちらで位置を決めて置く。
                        // ★従来の MainModel.AddVoiceItemAsync は「タイムラインに置いたあとで、生成が非同期に進む」
                        //   ので、尺が確定する前に並べてしまう余地があった(設定 LegacyVoicePath で戻せる)。
                        VoiceItem? voice = null;
                        var addedByModel = false;
                        var readingApplied = false;
                        var voiceWatch = System.Diagnostics.Stopwatch.StartNew();
                        voiceLines++;
                        // 先にまとめて作った分があれば使う(作れなかった行・食い違った行は下の今までの経路へ)
                        var useOneByOne = true;
                        if (prefetched != null && voiceChoice != null && voiceChoice.Model != null)
                        {
                            VoiceJob? pj;
                            prefetched.TryGetValue(i, out pj);
                            var keyMatches = pj != null && ReferenceEquals(pj.Model, voiceChoice.Model)
                                && pj.Text == (ev.Text ?? "")
                                && pj.Reading == (string.IsNullOrEmpty(ev.Reading) ? null : ev.Reading);
                            if (pj != null && pj.Ran)
                            {
                                foreach (var line in pj.Log) log(line);
                                if (pj.Error != null)
                                    log(Tr.T("log.i.officialFail", FirstChars(SingleLine(pj.Text), 12), pj.Error.Message));
                            }
                            switch (VoicePrefetch.Decide(pj != null && pj.Ran, keyMatches, pj != null && pj.Result != null))
                            {
                                case PrefetchUse.UseItem:
                                    voice = pj!.Result;
                                    readingApplied = !string.IsNullOrEmpty(ev.Reading);
                                    officialVoices.Add(voice!);
                                    useOneByOne = false;
                                    break;
                                case PrefetchUse.RetryOneByOne:
                                    log(Tr.T("log.prefetch.retry", FirstChars(SingleLine(ev.Text ?? ""), 12)));
                                    break;
                                case PrefetchUse.Mismatch:
                                    log(Tr.T("log.prefetch.mismatch", FirstChars(SingleLine(ev.Text ?? ""), 12)));
                                    break;
                            }
                        }
                        if (useOneByOne && !settings.LegacyVoicePath && !officialVoiceBroken && voiceChoice != null && voiceChoice.Model != null)
                        {
                            voice = await CreateVoiceItemOfficial(voiceChoice.Model, ev.Text ?? "",
                                string.IsNullOrEmpty(ev.Reading) ? null : ev.Reading, voiceVideoInfo, log);
                            readingApplied = voice != null && !string.IsNullOrEmpty(ev.Reading);
                            if (voice != null) officialVoices.Add(voice);
                            if (voice == null)
                            {
                                officialVoiceBroken = true;
                                log(Tr.T("log.voice.legacyNext"));
                            }
                        }
                        if (voice == null && voiceChoice != null && voiceChoice.Model != null && mainModel != null)
                        {
                            var voicesBefore = new HashSet<object>(VoiceItemsOf(timeline).Cast<object>());
                            var r = await AddVoiceViaMainModel(mainModel,
                                FrameAt(ev.Start), L(laneOf("voice:" + charId)), voiceChoice.Model, ev.Text ?? "", log);
                            addedByModel = r.Item1;
                            voice = r.Item2;
                            // ★戻り値からアイテムが取れない版(YMM4 v4.56 など)では voice が null になり、
                            //   [間:N] と読み指定が黙って落ちていた。追加の前後の差分で今回のアイテムを特定する。
                            if (addedByModel && voice == null)
                                voice = VoiceItemsOf(timeline).FirstOrDefault(v2 => !voicesBefore.Contains(v2));
                            if (addedByModel)
                                directPlacements.Add(Tuple.Create(L(laneOf("voice:" + charId)),
                                    // 記録は1行ずつ読むもの。折り返しをそのまま出すと途中で行が割れる
                                    "VoiceItem ボイス「" + FirstChars(SingleLine(ev.Text ?? ""), 10) + "」"));
                        }
                        if (!addedByModel)
                        {
                            // 保険1: 正規ファクトリで生成して自前配置(上の本命で作れていればそのまま使う)
                            if (voice == null && voiceChoice != null && voiceChoice.Model != null)
                                voice = await CreateVoiceItemViaFactory(voiceChoice.Model, ev.Text ?? "", log,
                                    null, voiceVideoInfo);
                            if (voice == null)
                            {
                                // 保険2(未割り当て含む): 手組みプレースホルダ
                                voice = new VoiceItem();
                                SetProps(voice, log,
                                    P("CharacterName", voiceName), P("Serif", ev.Text ?? ""),
                                    P("Length", F(ev.Seconds)));
                                if (voiceChoice != null && voiceChoice.Model != null)
                                {
                                    TrySetInstance(voice, "Character", voiceChoice.Model, log);
                                    CopyCharacterDefaults(voice, voiceChoice.Model, log);
                                }
                            }
                            SetProps(voice, log, P("Frame", FrameAt(ev.Start)), P("Layer", L(laneOf("voice:" + charId))));
                            items.Add(voice);
                        }
                        loopVoiceMs += voiceWatch.ElapsedMilliseconds;
                        loopVoicesDone++;
                        if (prefetched == null)
                        {
                            var remLoop = ImportProgressMath.EstimateRemaining(loopVoicesDone, loopWatch.Elapsed.TotalSeconds,
                                serifTotal - loopVoicesDone, ImportProgressMath.MinSamples(1, serifTotal));
                            if (remLoop != null && loopFirstEta == null && loopVoicesDone < serifTotal)
                                loopFirstEta = Tuple.Create(loopVoicesDone, loopWatch.Elapsed.TotalSeconds, remLoop.Value);
                            report(ImportStage.Voices, (double)loopVoicesDone / Math.Max(1, serifTotal), loopVoicesDone, serifTotal, remLoop);
                        }
                        else if (loopVoicesDone % 20 == 0)
                            report(ImportStage.Place, 0.5 * loopVoicesDone / Math.Max(1, serifTotal), loopVoicesDone, serifTotal, null);
                        if (voice != null && !readingApplied && !string.IsNullOrEmpty(ev.Reading))
                            SetProps(voice, log, P("Pronounce", ev.Reading!));
                        // 台本に書かれた「間」を覚えておく(実尺での再配置で使う)
                        if (voice != null && ev.Pause > 0) voicePause[voice] = ev.Pause;
                        if (voice != null) 直前のボイス = voice;

                        var mapped = choice != null && choice.Model != null;
                        if (mapped)
                        {
                            // YMM4登録キャラ: 立ち絵アイテム1本+表情アイテムで切り替え(YMM4の流儀)
                            if (!tachiePlaced.Contains(charId) && !tachieUnavailable.Contains(charId))
                            {
                                var apTachie = apSerif;
                                var host = serifHost;
                                if (host == null)
                                {
                                    tachieUnavailable.Add(charId);
                                    log(Tr.T("log.tachie.noHost", apTachie != null ? LF(apTachie.Id) : "?", LF(PortraitAssignedName(apTachie))));
                                }
                                else
                                {
                                var hostChoice = host.Item1;
                                var tachie = new TachieItem();
                                SetProps(tachie, log,
                                    P("CharacterName", hostChoice.Name), P("Frame", FrameAt(ev.Start)),
                                    P("Layer", L(laneOf("chara:" + charId))), P("Length", F(totalEnd - ev.Start)),
                                    P("Remark", "立ち絵: " + hostChoice.Name));
                                TrySetInstance(tachie, "Character", hostChoice.Model!, log);
                                if (SetTachieParameter(tachie, hostChoice.Model!, log))
                                {
                                    tachiePlaced.Add(charId);
                                    tachieItems[charId] = Tuple.Create((BaseItem)tachie, ev.Start);
                                    // 複数立ち絵: 同じYMM4キャラのままの立ち絵だけPSDを差し替える。
                                    // ★別YMM4キャラに割り当てた立ち絵へは OverrideTachieFilePath しない。
                                    //   立ち絵設定済みキャラへの強制上書きはYMM4ごと落ちる
                                    //   (実機クラッシュ 2026-08-09)。そのキャラ自身の既定立ち絵が正。
                                    if (host.Item2 && apTachie?.Psd != null)
                                        OverrideTachieFilePath(tachie, ResolvePackPath(packDir, apTachie.Psd.Path), log);
                                    // サイズ・位置: パックの立ち絵実寸が分かればフィット、無ければX位置のみ
                                    var defaultPortrait = ResolvePortrait(packDir, chara, apTachie, null);
                                    if (defaultPortrait != null)
                                        ApplyPortraitLayout(tachie, defaultPortrait,
                                            xOf(charId, slotIndex), projH, heightRatioOf(charId), log,
                                            yOffsetOf(charId), psdHeightOf(charId));
                                    else
                                    {
                                        // 実寸不明でフィットできない場合も、横位置と縦オフセットは反映する。
                                        // ★何が起きたかを残す。以前は無言で、立ち絵の大きさだけが
                                        //   合わない理由が分からなかった(2026-08-08 監査)。
                                        log(Tr.T("log.tachie.noImage", chara != null ? chara.Name : charId));
                                        SetAnim(tachie, "X", xOf(charId, slotIndex), log);
                                        SetAnim(tachie, "Y", yOffsetOf(charId), log);
                                    }
                                    items.Add(tachie);
                                }
                                else
                                {
                                    tachieUnavailable.Add(charId);
                                    log(Tr.T("log.tachie.noYmm4", hostChoice.Name));
                                }
                                }
                            }
                            if (tachieUnavailable.Contains(charId))
                            {
                                // 立ち絵未設定キャラ: パックの表情画像/PSDをセリフ区間に仮置き
                                var portrait = ResolvePortrait(packDir, chara, activePortraitOf(charId), ev.Expression);
                                if (portrait == null)
                                    log(Tr.T("log.tachie.noStandin", chara != null ? chara.Name : charId));
                                if (portrait != null)
                                {
                                    var img = new ImageItem();
                                    SetProps(img, log,
                                        P("FilePath", portrait), P("Frame", FrameAt(ev.Start)),
                                        P("Layer", L(laneOf("chara:" + charId))), P("Length", F(ev.Seconds)),
                                        P("Remark", (chara != null ? chara.Name : "?") + "(" + (ev.Expression ?? "既定") + ")"));
                                    ApplyPortraitLayout(img, portrait, xOf(charId, slotIndex), projH,
                                        heightRatioOf(charId), log, yOffsetOf(charId), psdHeightOf(charId));
                                    items.Add(img);
                                }
                            }
                            else if (ev.Expression != null)
                            {
                                // 表情は表情レイヤーで「状態」として管理(ユーザー方針 2026-07-26)。
                                // 次の表情変化・退場・末尾まで持続させる。ボイス側の表情変更は
                                // 後処理で無効化する(しないとセリフに上書きされて効かない)
                                // 複数立ち絵なら現在の立ち絵の表情、無ければキャラ既定の表情
                                var apFace = activePortraitOf(charId);
                                var exprSource = apFace != null && apFace.Expressions.Count > 0
                                    ? apFace.Expressions : chara?.Expressions;
                                var packExpr = exprSource?.FirstOrDefault(
                                    x => x.Id == ev.Expression || x.Label == ev.Expression);
                                var layers = packExpr?.Psd?.Layers;
                                var layerIds = packExpr?.Psd?.LayerIds;
                                if (layers == null || layers.Count == 0)
                                {
                                    log(Tr.T("log.face.noLayers", LF(ev.Expression)));
                                }
                                else
                                {
                                    var untilSec = totalEnd;
                                    for (var j = i + 1; j < evs.Count; j++)
                                    {
                                        var e3 = evs[j];
                                        if (e3.Type == "serif" && e3.Speaker == ev.Speaker && e3.Expression != null)
                                        {
                                            untilSec = e3.Start;
                                            break;
                                        }
                                        if (e3.Type == "layout" && e3.Positions != null
                                            && e3.Positions.Any(p3 => p3.Exit && p3.Speaker == ev.Speaker))
                                        {
                                            untilSec = e3.Start;
                                            break;
                                        }
                                    }
                                    // ★表情アイテムの対象キャラは、立ち絵・ボイスと同じ解決結果に合わせる。
                                    //   別YMM4キャラの立ち絵に基底キャラ宛の表情を出してもズレて効かない。
                                    var hostF = serifHost;
                                    if (hostF == null)
                                    {
                                        log(Tr.T("log.face.noHost", LF(ev.Expression)));
                                    }
                                    else
                                    {
                                        var face = new TachieFaceItem();
                                        SetProps(face, log,
                                            P("CharacterName", hostF.Item1.Name), P("Frame", FrameAt(ev.Start)),
                                            P("Layer", L(laneOf("face:" + charId))),
                                            P("Length", Math.Max(1, F(untilSec - ev.Start))),
                                            P("Remark", "表情: " + ev.Expression));
                                        TrySetInstance(face, "Character", hostF.Item1.Model!, log);
                                        await SetFaceParameter(face, hostF.Item1.Model!, layers, layerIds, log);
                                        items.Add(face);
                                    }
                                }
                            }
                        }
                        else
                        {
                            if (warnedUnmapped.Add(charId))
                                log(Tr.T("log.unmapped", chara != null ? chara.Name : ev.SpeakerName ?? charId, ymm4Name));
                            // 未割り当てキャラ: パック内の画像/PSDを ImageItem で仮置き(従来動作)
                            var portrait = ResolvePortrait(packDir, chara, activePortraitOf(charId), ev.Expression);
                            if (portrait == null)
                                log(Tr.T("log.tachie.noStandin", chara != null ? chara.Name : charId));
                            if (portrait != null)
                            {
                                var img = new ImageItem();
                                SetProps(img, log,
                                    P("FilePath", portrait), P("Frame", FrameAt(ev.Start)),
                                    P("Layer", L(laneOf("chara:" + charId))),
                                    P("Length", F(ev.Seconds)),
                                    P("Remark", (chara != null ? chara.Name : "?") + "(" + (ev.Expression ?? "既定") + ")"));
                                ApplyPortraitLayout(img, portrait, xOf(charId, slotIndex), projH,
                                    heightRatioOf(charId), log, yOffsetOf(charId), psdHeightOf(charId));
                                items.Add(img);
                            }
                        }

                        placeInlineSe(ev);
                        break;
                    }
                    case "window":
                    case "prop":
                    {
                        var path = ResolveAsset(packDir, ev.Asset, skipped, ev);
                        if (path == null) break;
                        var isWindow = ev.Type == "window";
                        var stopAt = isWindow ? nextWindowStop[i] : nextPropStop[i];
                        // ★窓は最後の指定だけが出る(プレビューも同じ)。window_stop が無くても、次の @窓 が
                        //   来たらそこで前の窓を終える。終えないと両方が同じレーンで重なる
                        if (isWindow) stopAt = Math.Min(stopAt, nextWindow[i]);
                        var item = MakeItem(typeof(ImageItem), ev.Template, log, () => new ImageItem());
                        SetProps(item, log,
                            P("FilePath", path), P("Frame", FrameAt(ev.Start)),
                            P("Layer", L(laneOf(isWindow ? assetKey("window", ev.Asset!.Name) : assetKey("prop-slot:" + propSlot[i], ev.Asset!.Name)))),
                            P("Length", Math.Max(1, F(stopAt - ev.Start))),
                            P("Remark", (isWindow ? "テキスト窓: " : "小物: ") + ev.Asset!.Name));
                        var size = ReadImageSize(path);
                        if (size != null)
                        {
                            if (isWindow)
                            {
                                // 画面下・幅95%フィット(細部はYMM4で調整する前提の仮配置)
                                var zoom = projW * 0.95 / size.Item1 * 100.0;
                                SetAnim(item, "Zoom", zoom, log);
                                SetAnim(item, "Y", projH / 2.0 - size.Item2 * zoom / 100.0 / 2.0 - projH * 0.02, log);
                            }
                            else
                            {
                                var zoom = (ev.Height ?? 25) / 100.0 * projH / size.Item2 * 100.0;
                                SetAnim(item, "Zoom", zoom, log);
                                SetAnim(item, "X", projW * (ev.X ?? 0) / 100.0, log);
                                SetAnim(item, "Y", projH * (ev.Y ?? 0) / 100.0, log);
                            }
                        }
                        if (!isWindow)
                        {
                            propItems.Add(item);
                            if (stopAt < totalEnd) propExplicitEnd.Add(item); // 退場が明示されている
                        }
                        items.Add(item);
                        break;
                    }
                    case "effect":
                    {
                        var path = ResolveAsset(packDir, ev.Asset, skipped, ev);
                        if (path == null) break;
                        var ext = Path.GetExtension(path).ToLowerInvariant();
                        // ★.mkv は素材の許可拡張子(PackModel.AbsAllowedExts)には入っているのに
                        //   ここに無く、読めない画像アイテムとして置かれていた(2026-08-16 監査)。
                        var isVideo = ext == ".mp4" || ext == ".webm" || ext == ".avi" || ext == ".mov"
                            || ext == ".mkv";
                        var wantType = isVideo ? typeof(VideoItem) : typeof(ImageItem);
                        BaseItem item = MakeItem(wantType, ev.Template, log,
                            () => isVideo ? (BaseItem)new VideoItem() : new ImageItem());
                        var seconds = ev.Seconds > 0 ? ev.Seconds : 2.0;
                        SetProps(item, log,
                            P("FilePath", path), P("Frame", FrameAt(ev.Start)),
                            P("Layer", L(laneOf(assetKey("effect:" + ev.Asset!.Name, ev.Asset!.Name)))),
                            P("Length", F(seconds)),
                            P("Remark", "エフェクト: " + ev.Asset!.Name));
                        var size = ReadImageSize(path);
                        if (size != null)
                        {
                            var zoom = (ev.Height ?? 100) / 100.0 * projH / size.Item2 * 100.0;
                            SetAnim(item, "Zoom", zoom, log);
                            SetAnim(item, "X", projW * (ev.X ?? 0) / 100.0, log);
                            SetAnim(item, "Y", projH * (ev.Y ?? 0) / 100.0, log);
                        }
                        items.Add(item);
                        break;
                    }
                    case "portrait":
                    {
                        // 立ち絵切替: 現在の立ち絵アイテムをこの時刻で終了し(尺を切る)、キャラが
                        // 画面に居るなら新しい立ち絵(別PSD)のアイテムを同位置・同レーンで即置き直す
                        // (行内立ち絵: 1セリフだけの切替に、次の同キャラ serif を待たず追随する)。
                        // 尺は末尾までの暫定で、次の portrait/退場イベントが切る(serif 側の作りと同じ)。
                        // ボイスは主キャラのまま。
                        var sp = ev.Speaker;
                        Dictionary<string, PackPortrait>? pmap;
                        if (sp == null || !portraitsById.TryGetValue(sp, out pmap) || pmap == null
                            || string.IsNullOrEmpty(ev.Name))
                            break;
                        Tuple<BaseItem, double>? placed;
                        tachieItems.TryGetValue(sp, out placed);
                        var plan = PlanPortraitSwitch(pmap, ev.Name!,
                            placed != null ? (double?)placed.Item2 : null, ev.Start, totalEnd, fps, frameOffset);
                        if (plan == null)
                        {
                            // ★未登録の立ち絵名: 状態もアイテムも触らない(ログのみ)。
                            //   触ると activePortraitOf が null になり、既定PSDフォールバックで
                            //   無駄な切断・別の顔での置き直しが起きる(2026-08 監査)。
                            log(Tr.T("log.switch.unknown", LF(ev.Name), LF(sp)));
                            break;
                        }
                        activePortrait[sp] = plan.PortraitId;
                        if (placed != null)
                        try
                        {
                            SetProps(placed.Item1, log, P("Length", plan.CutLength));
                            // portrait 由来で置いたアイテムを portrait で切った=セリフ終了時刻(推定)での
                            // 切断。実尺再配置で対応ボイスの実終端に合わせるためアンカーを覚える。
                            if (portraitPlacedItems.Contains(placed.Item1))
                                portraitEndSync[placed.Item1] = FrameAt(placed.Item2);
                            tachieItems.Remove(sp);
                            tachiePlaced.Remove(sp);
                            // 置き直し: キャラが画面に居るので、新しい立ち絵をこの時刻から置く
                            CharacterChoice? chP;
                            if (speakerMap.TryGetValue(sp, out chP) && chP != null && chP.Model != null)
                            {
                                var charaP = pack.Characters.FirstOrDefault(c => c.Id == sp);
                                // ★切替先立ち絵が別YMM4キャラ割り当てなら、そのキャラで置く
                                //   (以降のセリフのボイスも serif 側で同じ解決結果になり、
                                //   CharacterName はボイスと常に一致する)
                                //   (基底キャラへのPSD強制上書きはYMM4クラッシュの原因・2026-08-09)
                                var apNew = activePortraitOf(sp);
                                var hostP = resolveTachieHost(sp, chP, apNew);
                                if (hostP == null)
                                {
                                    log(Tr.T("log.switch.noHost", LF(apNew != null ? apNew.Id : plan.PortraitId), LF(PortraitAssignedName(apNew))));
                                    log(Tr.T("log.switch", LF(sp), LF(ev.Name)));
                                    break;
                                }
                                var tachie = new TachieItem();
                                SetProps(tachie, log,
                                    P("CharacterName", hostP.Item1.Name), P("Frame", plan.NewStartFrame),
                                    P("Layer", L(laneOf("chara:" + sp))), P("Length", plan.NewLength),
                                    P("Remark", "立ち絵: " + hostP.Item1.Name + "(" + plan.PortraitId + ")"));
                                TrySetInstance(tachie, "Character", hostP.Item1.Model!, log);
                                if (SetTachieParameter(tachie, hostP.Item1.Model!, log))
                                {
                                    if (hostP.Item2 && apNew?.Psd != null)
                                        OverrideTachieFilePath(tachie,
                                            ResolvePackPath(packDir, apNew.Psd.Path), log);
                                    var slotIdxP = slotOf(sp);
                                    var newPortrait = ResolvePortrait(packDir, charaP, apNew, null);
                                    if (newPortrait != null)
                                        ApplyPortraitLayout(tachie, newPortrait, xOf(sp, slotIdxP), projH,
                                            heightRatioOf(sp), log, yOffsetOf(sp), psdHeightOf(sp));
                                    else
                                    {
                                        SetAnim(tachie, "X", xOf(sp, slotIdxP), log);
                                        SetAnim(tachie, "Y", yOffsetOf(sp), log);
                                    }
                                    items.Add(tachie);
                                    tachiePlaced.Add(sp);
                                    tachieItems[sp] = Tuple.Create((BaseItem)tachie, ev.Start);
                                    portraitPlacedItems.Add(tachie);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            // ★プラグインの都合でYMM4を巻き込まない。置き直しに失敗しても
                            //   切替(状態)は済んでいるので、従来どおり次のセリフで復帰できる
                            log(Tr.T("log.switch.replaceFail", ex.Message));
                        }
                        log(Tr.T("log.switch", LF(sp), LF(ev.Name)));
                        break;
                    }
                    case "bgm_stop":
                    case "prop_stop":
                    case "window_stop":
                    case "custom":
                        break; // アイテム化しない(*_stop は終端計算で消費済み)
                    default:
                        skipped.Add(Tr.T("log.skipped.unknownType", ev.No, LF(ev.Type, 40)));
                        break;
                }
            }

            if (loopFirstEta != null)
                log(Tr.T("log.eta.check", loopFirstEta.Item1, serifTotal, Math.Round(loopFirstEta.Item3, 1),
                    Math.Round(loopWatch.Elapsed.TotalSeconds - loopFirstEta.Item2, 1)));
            stages.Lap(Tr.T("stage.loop"));
            stages.Move(Tr.T("stage.loop"), Tr.T("stage.loopVoice"), loopVoiceMs);
            log(Tr.T("log.items", items.Count, fps));
            foreach (var s in skipped) log(Tr.T("log.skipped", s));
            // 飛ばしたものがあるなら、末尾でもう一度まとめて言う。1行ずつのスキップは
            // 大量のログに埋もれて気づけず、「配置はできたのに素材が抜けている」まま
            // 動画を作り進めてしまう。何件・何が足りないかを最後に一目で分かるようにする。
            if (emptySerifSkipped > 0)
                log(Tr.T("log.emptySerif", emptySerifSkipped));
            if (skipped.Count > 0)
            {
                log("");
                log(Tr.T("log.skippedTotal", skipped.Count));
            }

            // 配置レイヤーの一覧(重なり順の検証用。意図: 番号が大きいほど手前)
            // あわせて「意図レーン」を記録し、AddItems/音声合成で別レーンへ弾かれた分を再配置後に戻す。
            var layerSummary = new List<Tuple<int, string>>(directPlacements);
            var intendedLayer = new Dictionary<object, int>();
            foreach (var it in items)
            {
                try
                {
                    var lp = FindProp(it.GetType(), "Layer");
                    var layer = lp != null ? Convert.ToInt32(lp.GetValue(it)) : -1;
                    if (layer >= 0) intendedLayer[it] = layer;
                    var rp = FindProp(it.GetType(), "Remark");
                    var remark = rp != null ? rp.GetValue(it) as string : null;
                    // ★備考(素材名・キャラ名が入る)は「」で括る。保存ログの伏せ字が「」の中を文字数にする(2026-10-22 監査)
                    layerSummary.Add(Tuple.Create(layer,
                        it.GetType().Name + (string.IsNullOrEmpty(remark) ? "" : " 「" + SingleLine(remark!) + "」")));
                }
                catch { }
            }
            log(Tr.T("log.layerList"));
            foreach (var t in layerSummary.OrderBy(t2 => t2.Item1))
                log("  L" + t.Item1.ToString().PadLeft(2) + ": " + t.Item2);

            stages.Lap(Tr.T("stage.misc"));
            // ★小分けに置く。合間で一時停止・停止を見る(停止なら置いた分を残して抜ける)
            var ranges = ChunkRanges(items.Count, PlaceChunkSize);
            for (var ci = 0; ci < ranges.Count; ci++)
            {
                try { await control.CheckpointAsync(Tr.T("log.placing.progress", ranges[ci][0], items.Count)); }
                catch (ImportStoppedException)
                {
                    log(Tr.T("log.stopped", ranges[ci][0], items.Count));
                    throw;
                }
                var chunk = items.GetRange(ranges[ci][0], ranges[ci][1] - ranges[ci][0]);
                AddItems(timeline, chunk, log, ci == 0);
                report(ImportStage.Place, 0.5 + 0.5 * ranges[ci][1] / Math.Max(1, items.Count), ranges[ci][1], items.Count, null);
                if (ranges.Count > 1) log(Tr.T("log.placing", ranges[ci][1], items.Count));
            }

            stages.Lap(Tr.T("stage.addItems"));
            // 診断: ボイスアイテムがキャラへ解決できているか(ボイス割り当て問題の切り分け)
            var firstVoice = items.OfType<VoiceItem>().FirstOrDefault();
            if (firstVoice != null)
            {
                try
                {
                    var cp = FindProp(firstVoice.GetType(), "Character");
                    var c = cp != null ? cp.GetValue(firstVoice) : null;
                    var np = c != null ? c.GetType().GetProperty("Name") : null;
                    log(Tr.T("log.diag.char",
                        c == null ? Tr.T("log.diag.charNull") : (np != null ? np.GetValue(c) : c)));
                    var vp = c != null ? c.GetType().GetProperty("Voice") : null;
                    var v = vp != null && c != null ? vp.GetValue(c) : null;
                    if (c != null) log(Tr.T("log.diag.voice", v == null ? Tr.T("log.diag.voiceNull") : v.GetType().Name));
                }
                catch (Exception ex) { log(Tr.T("log.diag.fail", ex.Message)); }
            }

            // ボイスは AddVoiceItemAsync が非同期(バックグラウンド)で生成する。実尺が揃う前に
            // 再配置すると「据え置き」になり推定尺のまま=ボイス間に間隔が残る。生成完了(実尺確定)を
            // 待ってから再配置する。
            // ★本命の手順で作ったボイスは、作る時点で生成の完了を待ち、実尺も確定済み。全部がそうなら
            //   生成待ち(200ms ごとに見て、3回続けて変わらないまで=最低 600ms)を通さない。
            //   1本でも従来の追加方式(タイムラインに置いてから生成が進む)や未確定のものがあれば今までどおり待つ
            report(ImportStage.Wait, 0, 0, 0, null);
            var newVoices = VoiceItemsOf(timeline).Where(v => !preExisting.Contains(v)).ToList();
            if (CanSkipVoiceWait(newVoices.Count, newVoices.Count(v => officialVoices.Contains(v) && VoiceReadiness(v) > 0)))
                log(Tr.T("log.wait.skipped", newVoices.Count));
            else
                await WaitForVoiceLengths(timeline, preExisting, log, control);
            stages.Lap(Tr.T("stage.wait"));
            RefreshVoiceLengths(timeline, preExisting, log);
            stages.Lap(Tr.T("stage.refresh"));
            report(ImportStage.Relayout, 0, 0, 0, null);
            await control.CheckpointAsync(Tr.T("log.beforeRelayout"));

            // セリフの尺は推定(charsPerSecond)なので、実ボイス長とズレて「間延び/被り」が出る。
            // 配置後の実尺でセリフを詰め直す(YMM4の ResolveAllItemsCollision は重なりを散らすだけで
            // 隙間を詰めない)。実尺が未確定で再配置を見送ったときだけ、従来の衝突解決を保険で呼ぶ。
            var serifGap = SerifGapMode.Resolve(settings.SerifGapMode, settings.SerifGapSeconds, pack.Timing.SerifGap);
            log(Tr.T("log.gap", serifGap.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                SerifGapMode.Normalize(settings.SerifGapMode) == SerifGapMode.Pack ? Tr.T("log.gap.pack") : Tr.T("log.gap.plugin")));
            if (!ResequenceByVoiceLength(timeline, fps, serifGap, log, intendedLayer, propItems, propExplicitEnd, preExisting, voicePause, portraitEndSync))
            {
                try
                {
                    var resolve = typeof(Timeline).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(m2 => m2.Name == "ResolveAllItemsCollision" && m2.GetParameters().Length == 0);
                    if (resolve != null) { resolve.Invoke(timeline, null); log(Tr.T("log.collision.run")); }
                }
                catch (Exception ex) { log(Tr.T("log.collision.fail", ex.Message)); }
            }
            stages.Lap(Tr.T("stage.resequence"));

            // 配置後のボイスアイテムへの後処理:
            //   ①テキスト窓がある場合の字幕最前面化
            //   ②ボイス側の表情変更を無効化(表情は表情レイヤーで管理するため、セリフに奪わせない)
            try
            {
                var hasWindow = evs.Any(e2 => e2.Type == "window");
                var jimakuCount = 0;
                var faceOffCount = 0;
                var timelineItems = GetPropValue(timeline, "Items") as System.Collections.IEnumerable;
                if (timelineItems != null)
                {
                    var lo = frameOffset;
                    var hi = AddFramesSaturating(AddFramesSaturating(frameOffset, F(totalEnd)), 1);
                    foreach (var it in timelineItems)
                    {
                        var v2 = it as VoiceItem;
                        if (v2 == null) continue;
                        var fp2 = FindProp(v2.GetType(), "Frame");
                        var fr = fp2 != null ? Convert.ToInt32(fp2.GetValue(v2)) : -1;
                        if (fr < lo || fr > hi) continue;
                        if (hasWindow)
                        {
                            SetProps(v2, log, P("JimakuIsAlwaysOnTop", true));
                            jimakuCount++;
                        }
                        var vname = GetPropValue(v2, "CharacterName") as string;
                        var fparam = GetPropValue(v2, "TachieFaceParameter");
                        if (fparam == null)
                        {
                            log(Tr.T("log.faceOff.nullParam", vname ?? "?"));
                        }
                        else
                        {
                            var ie = fparam.GetType().GetProperty("IsEnabled");
                            if (ie != null && ie.CanWrite)
                            {
                                ie.SetValue(fparam, false);
                                faceOffCount++;
                            }
                            else
                            {
                                log(Tr.T("log.faceOff.noEnabled", fparam.GetType().Name, vname ?? "?"));
                            }
                        }
                    }
                }
                // 診断: 配置後の表情アイテムの実態(設定値がAddItems後も残っているか)
                var timelineItems2 = GetPropValue(timeline, "Items") as System.Collections.IEnumerable;
                if (timelineItems2 != null)
                {
                    foreach (var it in timelineItems2)
                    {
                        if (!(it is TachieFaceItem tfi)) continue;
                        var fp3 = FindProp(tfi.GetType(), "Frame");
                        var fr3 = fp3 != null ? Convert.ToInt32(fp3.GetValue(tfi)) : -1;
                        if (fr3 < frameOffset || fr3 > AddFramesSaturating(AddFramesSaturating(frameOffset, F(totalEnd)), 1)) continue;
                        var prm = GetPropValue(tfi, "TachieFaceParameter");
                        log(Tr.T("log.diag.faceItem", fr3)
                            + (prm == null ? "null" : prm.GetType().Name)
                            + " IDs=" + DumpValue(prm != null ? GetPropValue(prm, "EnableLayers") : null)
                            + " Paths=" + DumpValue(prm != null ? GetPropValue(prm, "EnableLayerPaths") : null)
                            + " File=" + DumpValue(prm != null ? GetPropValue(prm, "FilePath") : null)
                            + " IsEnabled=" + DumpValue(prm != null ? GetPropValue(prm, "IsEnabled") : null));
                    }
                }

                if (hasWindow) log(Tr.T("log.jimaku", jimakuCount));
                if (faceOffCount > 0)
                    log(Tr.T("log.faceOff", faceOffCount));
            }
            catch (Exception ex) { log(Tr.T("log.post.fail", ex.Message)); }

            // 診断: 置いたボイスの最終的な位置と長さ(時刻順)。隙間・被りの報告が来たときに、
            // どの行がどこに並んだかをログだけで追えるように。1行ずつ/並列で並びが同じかの比較にも使う
            try
            {
                var placed = new List<Tuple<int, int, int, string>>();
                var all = GetPropValue(timeline, "Items") as System.Collections.IEnumerable;
                if (all != null)
                    foreach (var it in all)
                    {
                        if (!(it is VoiceItem v4) || preExisting.Contains(it)) continue;
                        var fr = Convert.ToInt32(GetPropValue(v4, "Frame") ?? 0);
                        var ln = Convert.ToInt32(GetPropValue(v4, "Length") ?? 0);
                        var ly = Convert.ToInt32(GetPropValue(v4, "Layer") ?? 0);
                        placed.Add(Tuple.Create(fr, ln, ly, FirstChars(SingleLine(GetPropValue(v4, "Serif") as string ?? ""), 10)));
                    }
                foreach (var p4 in placed.OrderBy(x => x.Item1).ThenBy(x => x.Item3))
                    log(Tr.T("log.placedVoice", p4.Item1, p4.Item2, p4.Item3, p4.Item4));
            }
            catch (Exception ex) { log(Tr.T("log.post.fail", ex.Message)); }

            // 書き庭側の声設定の案内。YMM4 の声設定はエンジン統合も単位も別物なので
            // 自動では反映しない(壊すより案内)。パック同梱の「声の設定メモ.txt」と同内容。
            try
            {
                var tunedChars = pack.Characters.Where(c2 => c2.Voice != null).ToList();
                if (tunedChars.Count > 0)
                {
                    log(Tr.T("log.voiceMemo"));
                    foreach (var c2 in tunedChars)
                    {
                        var v3 = c2.Voice!;
                        var line = "  " + (c2.Ymm4Name ?? c2.Name) + ": " + v3.Engine
                            + (string.IsNullOrEmpty(v3.Preset) ? "" : Tr.T("log.voiceMemo.speaker", v3.Preset));
                        if (v3.Params != null && v3.Params.Count > 0)
                            line += " / " + string.Join(" ", v3.Params.Select(kv => kv.Key + "=" + kv.Value));
                        log(line);
                    }
                }
            }
            catch { /* 案内表示の失敗は取り込み結果に影響させない */ }
            stages.Lap(Tr.T("stage.post"));
            report(ImportStage.Done, 1, 0, 0, null);
            log(Tr.T("log.elapsed", runWatch.ElapsedMilliseconds));
            foreach (var kv in stages.Sorted())
                log(Tr.T("log.stage", kv.Key, kv.Value));
            var voiceTotal = stages.Of(Tr.T("stage.prefetch")) + loopVoiceMs;
            if (voiceLines > 0) log(Tr.T("log.stage.perVoice", voiceTotal, voiceLines, voiceTotal / voiceLines));
        }

        /// <summary>料金・回数の制限がかかる声(オンラインのサービス)と、利用者が登録した外部コマンドの声。
        /// 取り込みの前に件数と文字数を見せて確かめる対象(VoicePrefetch.cs の表と同じ型の名前)。</summary>
        internal static readonly HashSet<string> CostlyEngines = new HashSet<string>
        {
            "YukkuriMovieMaker.Voice.AmazonPollyVoiceSpeaker",
            "YukkuriMovieMaker.Voice.GoogleTTSVoiceSpeaker",
            "YukkuriMovieMaker.Voice.MicrosoftAzureTTSVoiceSpeaker",
            "YukkuriMovieMaker.Voice.CoeFontVoiceSpeaker",
            "YukkuriMovieMaker.Voice.OpenAI.OpenAIVoiceSpeaker",
            "YukkuriMovieMaker.Plugin.Community.Voice.GeminiTTS.GeminiTTSVoiceSpeaker",
            "YukkuriMovieMaker.Plugin.Community.Voice.GrokTTS.GrokTTSVoiceSpeaker",
            "YukkuriMovieMaker.Plugin.Community.Voice.ElevenLabs.ElevenLabsVoiceSpeaker",
            "YukkuriMovieMaker.Plugin.Community.Voice.Kotodama.KotodamaVoiceSpeaker",
            "YukkuriMovieMaker.Plugin.Community.Voice.LivetoonTTS.LivetoonTTSSpeaker",
            "YukkuriMovieMaker.Plugin.Community.Voice.AivisCloudAPI.AivisCloudAPIVoiceSpeaker",
            "YukkuriMovieMaker.Voice.CommandLineVoiceSpeaker",
        };

        /// <summary>料金のかかる声ごとの (エンジン, 行数, 文字数)。無ければ空。
        /// 声を先にまとめて作るのは PC の中で完結する声だけなので、ここに数える行は1回ずつしか作らない。</summary>
        internal static List<Tuple<string, int, int>> CostlyVoiceSummary(IEnumerable<Tuple<string, string>> engineAndText)
            => engineAndText.Where(x => CostlyEngines.Contains(x.Item1))
                .GroupBy(x => x.Item1)
                .Select(g => Tuple.Create(g.Key, g.Count(), g.Sum(x => (x.Item2 ?? "").Length)))
                .ToList();

        /// <summary>セリフや読みが「ファイルの場所」(C:\…・\\server\…・file://…)の形か。
        /// YMM4 は読みが音声ファイルの場所だと、合成せずにそのファイルを声として読み込む(VoiceItem.IsCustomVoice)。
        /// パックからそれを渡すと、利用者の PC やネットワーク上の任意の音声を引き込める(2026-10-22 監査)。</summary>
        internal static bool LooksLikeFileLocation(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            return Uri.TryCreate(s!.Trim(), UriKind.Absolute, out var u) && u.IsFile;
        }

        /// <summary>ログへ出すパック由来の文字列を1行に収める(LogRedactor.Field の短い別名)</summary>
        static string LF(string? s, int max = 80) => LogRedactor.Field(s, max);

        /// <summary>ボイスの生成待ちを省いてよいか: 今回置いたボイスが1本以上あり、その全部が
        /// 本命の手順で作られ(生成の完了まで待ってある)、尺も入っているとき。</summary>
        internal static bool CanSkipVoiceWait(int newVoices, int officialReady)
            => newVoices > 0 && officialReady == newVoices;

        /// <summary>先にまとめて作るボイス1行ぶん(パックのイベントの番号で引く)。</summary>
        internal sealed class VoiceJob
        {
            public int Index;
            public object Model = null!;
            public string Text = "";
            public string? Reading;
            /// <summary>声のエンジン(IVoiceSpeaker の実装の型の完全名。読めなければ空)</summary>
            public string Engine = "";
            /// <summary>作る間のログ。並列で作るので溜めておき、配置のループがその行に来たときに出す</summary>
            public List<string> Log = new List<string>();
            public bool Ran;
            public VoiceItem? Result;
            public Exception? Error;
        }

        /// <summary>キャラの声のエンジン(Character.Voice.Speaker の型の完全名)。読めなければ空。</summary>
        static string SpeakerTypeName(object character)
        {
            try
            {
                var v = GetPropValue(character, "Voice");
                var sp = v != null ? GetPropValue(v, "Speaker") : null;
                if (sp == null) return "";
                // SAPI5 は声ごとに分ける(Microsoft の声だけ並べてよい・VoicePrefetch.cs)
                return VoiceConcurrency.SapiEngineKey(sp.GetType().FullName ?? "", GetPropValue(sp, "ID") as string);
            }
            catch { return ""; } // swallow-ok: 読めなければ「知らないエンジン」=同時数 1 の安全側に倒れる
        }

        /// <summary>ログ用の短い型名(名前空間を落とす。SAPI の外部の声の印は残す)。</summary>
        internal static string ShortTypeName(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return "?";
            var plain = fullName.Split('#')[0];
            var suffix = fullName.Length > plain.Length ? fullName.Substring(plain.Length) : "";
            var k = plain.LastIndexOf('.');
            return (k >= 0 ? plain.Substring(k + 1) : plain) + suffix;
        }

        // ---------- 純アルゴリズム(YMM4 非依存・テスト対象) ----------

        /// <summary>小物のレーン割り当て(区間グラフの貪欲彩色)。sortedIdx は開始時刻昇順の
        /// イベント index 列。時間が重ならない小物は空いた最小スロットへ畳む。
        /// 返り値: index → スロット番号。</summary>
        internal static Dictionary<int, int> AssignGreedySlots(
            List<int> sortedIdx, Func<int, double> startOf, Func<int, double> endOf)
        {
            var propSlot = new Dictionary<int, int>();
            var laneEnds = new List<double>(); // 各スロットが空く時刻
            foreach (var i in sortedIdx)
            {
                var start = startOf(i);
                var slot = -1;
                for (var s = 0; s < laneEnds.Count; s++)
                    if (laneEnds[s] <= start + 1e-6) { slot = s; break; } // このスロットは空いている
                if (slot < 0) { slot = laneEnds.Count; laneEnds.Add(0); }
                laneEnds[slot] = endOf(i);
                propSlot[i] = slot;
            }
            return propSlot;
        }

        /// <summary>再配置の階段関数: 旧Frame f のズレ量(f 以下で最大の旧Frameを持つボイスの delta)。
        /// shifts は (旧Frame, delta) を旧Frame昇順に並べたもの。該当なし(区間の前)は 0。</summary>
        /// <summary>先頭 n 文字(UTF-16)。絵文字などのペアの真ん中で切らない(ログに孤立サロゲートを出さない)。</summary>
        internal static string FirstChars(string s, int n)
        {
            if (s.Length <= n) return s;
            if (n > 0 && char.IsHighSurrogate(s[n - 1])) n--;
            return s.Substring(0, n);
        }

        internal static int ShiftDeltaAt(List<Tuple<int, int>> shifts, int f)
        {
            // ★二分探索。アイテムごとに全ボイスをなめると、件数の二乗で固まる(2026-10-20c)。
            //   同じ旧Frameが並ぶときは、従来どおり最後のものを採る。
            int lo = 0, hi = shifts.Count; // shifts[lo..hi) のうち Item1 > f の最初の位置を探す
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (shifts[mid].Item1 <= f) lo = mid + 1; else hi = mid;
            }
            return lo == 0 ? 0 : shifts[lo - 1].Item2;
        }

        /// <summary>フレームの上限。位置と長さを足し合わせても int があふれない余裕を残す。</summary>
        internal const int MaxFrame = int.MaxValue / 4;

        /// <summary>秒→フレーム(四捨五入)。int の範囲で飽和させる(素の (int) 変換はあふれると負になる)。
        /// NaN は 0。</summary>
        internal static int SecondsToFrames(double sec, double fps)
        {
            var v = Math.Round(sec * fps);
            if (double.IsNaN(v)) return 0;
            if (v >= int.MaxValue) return int.MaxValue;
            if (v <= int.MinValue) return int.MinValue;
            return (int)v;
        }

        /// <summary>フレームの足し算(int の範囲で飽和)。</summary>
        internal static int AddFramesSaturating(int a, int b)
            => (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, (long)a + b));

        /// <summary>このイベントの開始・終わりがフレームの上限に収まるか(収まらなければ置かない)。</summary>
        internal static bool EventFitsFrames(double startSec, double seconds, double fps, int frameOffset)
        {
            var end = Math.Round(startSec * fps) + Math.Round(Math.Max(0, seconds) * fps) + (double)frameOffset;
            return double.IsFinite(end) && startSec >= 0 && end <= MaxFrame;
        }

        /// <summary>各イベント i について「i より後で、types のどれかの種類の最初のイベントの開始」。
        /// 無ければ fallback。1回の逆走査で全部求める。</summary>
        internal static double[] NextStartOfTypes(List<PackEvent> evs, string[] types, double fallback)
        {
            var r = new double[evs.Count];
            var next = fallback;
            for (var i = evs.Count - 1; i >= 0; i--)
            {
                r[i] = next;
                if (Array.IndexOf(types, evs[i].Type) >= 0) next = evs[i].Start;
            }
            return r;
        }

        /// <summary>各イベント i について「i より後で、i の素材名(asset.name)を止める stopType の
        /// 最初のイベントの開始」(停止側は name か asset.name が一致すればよい)。無ければ fallback。</summary>
        internal static double[] NextNamedStopStarts(List<PackEvent> evs, string stopType, double fallback)
        {
            var r = new double[evs.Count];
            var latest = new Dictionary<string, double>();
            for (var i = evs.Count - 1; i >= 0; i--)
            {
                var key = evs[i].Asset != null ? evs[i].Asset!.Name : null;
                double s;
                r[i] = key != null && latest.TryGetValue(key, out s) ? s : fallback;
                if (evs[i].Type == stopType)
                {
                    if (evs[i].Name != null) latest[evs[i].Name!] = evs[i].Start;
                    if (evs[i].Asset != null && evs[i].Asset!.Name != null) latest[evs[i].Asset!.Name] = evs[i].Start;
                }
            }
            return r;
        }

        /// <summary>昇順の配列で value より大きい最初の位置(無ければ Length)。</summary>
        internal static int UpperBound(int[] sorted, int value)
        {
            int lo = 0, hi = sorted.Length;
            while (lo < hi)
            {
                var mid = lo + (hi - lo) / 2;
                if (sorted[mid] <= value) lo = mid + 1; else hi = mid;
            }
            return lo;
        }

        /// <summary>立ち絵名の解決(id 優先・Ymm4Name は古い書き庭パックの保険)。無ければ null。</summary>
        internal static PackPortrait? FindPortrait(Dictionary<string, PackPortrait> portraits, string name)
        {
            PackPortrait? pr;
            if (portraits.TryGetValue(name, out pr)) return pr;
            foreach (var kv in portraits)
                if (kv.Value.Ymm4Name == name) return kv.Value;
            return null;
        }

        /// <summary>アクティブ立ち絵の配置先判定(純ロジック部)。Ymm4Name が空でなければ
        /// その名前(=そのYMM4キャラで置く。PSD上書きはしない)、空なら null
        /// (=基底キャラ+OverrideTachieFilePath でPSD差し替えの従来動作)。</summary>
        internal static string? PortraitAssignedName(PackPortrait? portrait)
        {
            var n = portrait != null ? portrait.Ymm4Name : null;
            return string.IsNullOrEmpty(n) ? null : n;
        }

        /// <summary>YMM4登録キャラ一覧から名前一致(Model 必須)を引く。無ければ null。</summary>
        internal static CharacterChoice? FindYmm4CharacterByName(List<CharacterChoice> chars, string name)
            => chars.FirstOrDefault(c => c.Name == name && c.Model != null);

        /// <summary>立ち絵の配置キャラ決定(純ロジック部)。優先順: 明示選択(ダイアログの
        /// 立ち絵行)→ パックの Ymm4Name → 基底キャラ。基底と同名の指定は「基底+PSD差し替え」
        /// (従来動作)へ畳む。返り値: Item1=別キャラ名(null=基底キャラで置く),
        /// Item2=OverrideTachieFilePath でPSD上書きしてよいか。
        /// ボイス・立ち絵・表情アイテムすべてこの決定に従う(話者もこのキャラで生成しないと
        /// 表情アイテム・口パクの対応が切れる)。</summary>
        internal static Tuple<string?, bool> DecideTachieHostName(
            string? explicitName, string? assignedName, string baseName)
        {
            var name = !string.IsNullOrEmpty(explicitName) ? explicitName : assignedName;
            if (string.IsNullOrEmpty(name) || name == baseName)
                return Tuple.Create((string?)null, true); // 基底キャラ+PSD差し替え(従来)
            return Tuple.Create((string?)name, false);    // 別YMM4キャラで置く(上書きなし)
        }

        /// <summary>portrait(立ち絵切替)イベントの適用計画。</summary>
        internal sealed class PortraitSwitchPlan
        {
            /// <summary>解決済みの立ち絵 id(activePortrait に入れる正規キー)</summary>
            public string PortraitId = "";
            /// <summary>表示中の立ち絵があり、切断+置き直しをするか</summary>
            public bool Replace;
            /// <summary>切断する既存アイテムの新 Length(フレーム)。Replace=false なら 0</summary>
            public int CutLength;
            /// <summary>置き直すアイテムの開始フレーム(frameOffset 込み)</summary>
            public int NewStartFrame;
            /// <summary>置き直すアイテムの暫定 Length(末尾まで。次のイベントが切る前提)</summary>
            public int NewLength;
        }

        /// <summary>portrait イベントの純ロジック部: 名前解決と切断/置き直しのフレーム計算。
        /// 解決できない name は null(状態もアイテムも触らない=既定PSDへの無駄な切断を防ぐ)。
        /// placedStartSec=null は「そのキャラの立ち絵が画面に無い」= 状態更新のみ(Replace=false)。</summary>
        internal static PortraitSwitchPlan? PlanPortraitSwitch(
            Dictionary<string, PackPortrait> portraits, string name,
            double? placedStartSec, double evStartSec, double totalEndSec,
            double fps, int frameOffset)
        {
            var pr = FindPortrait(portraits, name);
            if (pr == null) return null;
            Func<double, int> F = sec => SecondsToFrames(sec, fps);
            var plan = new PortraitSwitchPlan
            {
                PortraitId = pr.Id,
                Replace = placedStartSec != null,
                NewStartFrame = AddFramesSaturating(F(evStartSec), frameOffset),
                NewLength = Math.Max(1, F(totalEndSec - evStartSec)),
            };
            if (placedStartSec != null)
                plan.CutLength = Math.Max(1, F(evStartSec - placedStartSec.Value));
            return plan;
        }

        /// <summary>実尺再配置での「portrait で切られた立ち絵」の終端合わせ。
        /// 切断はセリフの終了時刻(推定)で行われるが、シフトの階段はボイス開始フレームでしか
        /// 変わらないため、再配置後も終端が推定尺のまま残る。対応するボイス(同キャラ・同開始)の
        /// 実終端(voiceNewFrame + voiceLen)に合わせた新 Length を返す。
        /// null = 合わせない(対応ボイスなし・実尺未確定・退化した1フレーム項目・逆転)。</summary>
        internal static int? PortraitCutTachieNewLength(
            int itemNewFrame, int itemLen, int? voiceNewFrame, int voiceLen)
        {
            if (voiceNewFrame == null) return null;   // 対応ボイスなし
            if (voiceLen <= 1) return null;           // 実尺未確定(音声合成待ち)
            if (itemLen <= 1) return null;            // 戻しと次の切替が同時刻のときの1フレーム残骸は触らない
            var newLen = voiceNewFrame.Value + voiceLen - itemNewFrame;
            return newLen >= 1 ? newLen : (int?)null;
        }

        // ---------- 立ち絵の自動レイアウト ----------

        /// <summary>立ち絵をフィット(高さ比指定)+横位置指定+下端揃え(+縦オフセット)にする。
        /// yOffset は YMM4 の Y ピクセル(正=下)。既定 0 で下端揃えのまま。</summary>
        static void ApplyPortraitLayout(object img, string path, double x,
            double projH, double heightRatio, Action<string> log, double yOffset = 0.0,
            double? psdHeight = null)
        {
            // 実寸は「書き庭が教えてくれた、いま出ている立ち絵のPSD高」を優先する。
            // ★こちらが自分で読めるのは既定立ち絵のPSDだけなので、立ち絵を切り替えた場面では
            //   こちらの実測が別のPSDのものになる(2026-08-08 監査)。
            var size = ReadImageSize(path);
            double? 実高 = (psdHeight != null && psdHeight > 0) ? psdHeight : (size != null ? (double?)size.Item2 : null);
            if (実高 == null)
            {
                log(Tr.T("log.tachie.noSize", Path.GetFileName(path)));
                return;
            }
            // 拡大率は「このプロジェクトの高さ」から出す(解像度に依存するので必ずここで計算)
            var zoom = projH * heightRatio / 実高.Value * 100.0;
            var scaledH = 実高.Value * zoom / 100.0;
            // 下端を画面下に揃え(原点=画面中央)、そこから縦オフセットを足す
            var y = projH / 2.0 - scaledH / 2.0 + yOffset;
            var ok1 = SetAnim(img, "Zoom", zoom, log);
            var ok2 = SetAnim(img, "X", x, log);
            var ok3 = SetAnim(img, "Y", y, log);
            log(Tr.T("log.tachie.layout",
                Path.GetFileName(path), zoom, x, y,
                (ok1 && ok2 && ok3) ? "OK" : Tr.T("log.partialFail")));
        }

        static List<VoiceItem> VoiceItemsOf(Timeline timeline)
        {
            var itemsEnum = GetPropValue(timeline, "Items") as System.Collections.IEnumerable;
            return itemsEnum == null ? new List<VoiceItem>() : itemsEnum.Cast<object>().OfType<VoiceItem>().ToList();
        }

        /// <summary>ボイス1本の「生成が済んだか」の目安(0=未完了)。
        /// 実際の音声の長さ VoiceLength(ミリ秒)があればそれ、無い版では Length(フレーム)が1より大きいときだけ。
        /// ★Length は推定の値のまま居座ることがある(公式の台本編集も、生成後に ResetLength で取り直している)。
        ///   VoiceLength は音声ができて初めて入るので、こちらを完了の印にする。</summary>
        static long VoiceReadiness(object voice)
        {
            var vp = FindProp(voice.GetType(), "VoiceLength");
            if (vp != null)
            {
                var v = vp.GetValue(voice);
                if (v is TimeSpan ts) return (long)Math.Round(ts.TotalMilliseconds);
            }
            var lp = FindProp(voice.GetType(), "Length");
            var len = lp != null ? Convert.ToInt64(lp.GetValue(voice)) : 0;
            return len > 1 ? len : 0;
        }

        /// <summary>ボイス生成の完了とみなせるか。全件が完了(>0)で、前回の測定から1件も変わっていないとき。
        /// 「半数が確定すれば抜ける」だと、SAPI5 のように生成が遅い音声で未完了のまま並べ直してしまい、
        /// 後から長さが確定して隙間・被りになる。</summary>
        internal static bool VoiceLengthsSettled(IReadOnlyList<long> now, IReadOnlyList<long>? previous)
        {
            if (previous == null || previous.Count != now.Count) return false;
            for (var i = 0; i < now.Count; i++)
                if (now[i] <= 0 || now[i] != previous[i]) return false;
            return true;
        }

        /// <summary>ボイス生成(非同期)の完了を待つ。全件の VoiceLength が確定し、3回(600ms)続けて
        /// 変わらなくなるまで最大60秒ポーリングする。await Task.Delay で UI スレッドを塞がず生成を進めさせる。</summary>
        static async System.Threading.Tasks.Task WaitForVoiceLengths(Timeline timeline, HashSet<object> preExisting, Action<string> log, ImportControl? control = null)
        {
            try
            {
                // ★今回置いたボイスだけを数える。既存アイテムのあるプロジェクトでは、確定済みの
                //   既存ボイスだけで即抜けし、今回分の生成を一切待たずに再配置が据え置き→保険の衝突解決が
                //   タイムライン全体に走って既存が散る、があった。
                var voices = VoiceItemsOf(timeline).Where(v => !preExisting.Contains(v)).ToList();
                if (voices.Count == 0) return;
                long[]? prev = null;
                var stable = 0;
                for (var i = 0; i < 300; i++) // 300 * 200ms = 最大60秒
                {
                    var now = voices.Select(v => VoiceReadiness(v)).ToArray();
                    var ready = now.Count(l => l > 0);
                    stable = VoiceLengthsSettled(now, prev) ? stable + 1 : 0;
                    prev = now;
                    if (stable >= 3)
                    {
                        log(Tr.T("log.wait.settled", ready, voices.Count, i * 200));
                        return;
                    }
                    await System.Threading.Tasks.Task.Delay(200);
                    // 一時停止・停止はここでも見る(ボイス生成待ちは最大60秒あり、待つ間に止めたい)
                    if (control != null) await control.CheckpointAsync(Tr.T("log.wait.progress", ready, voices.Count));
                }
                var notReady = voices.Count(v => VoiceReadiness(v) <= 0);
                log(Tr.T("log.wait.timeout", notReady, voices.Count));
            }
            catch (ImportStoppedException) { throw; }
            catch (Exception ex) { log(Tr.T("log.wait.exception", ex.Message)); }
        }

        /// <summary>確定したボイスの尺を、公式の台本編集と同じ ItemEx.ResetLength で取り直す。
        /// Length が推定や途中の値のまま残っていると、再配置が1フレームや過大な尺で詰めてしまう。</summary>
        static void RefreshVoiceLengths(Timeline timeline, HashSet<object> preExisting, Action<string> log)
        {
            try
            {
                var voices = VoiceItemsOf(timeline).Where(v => !preExisting.Contains(v)).ToList();
                if (voices.Count == 0) return;
                var videoInfo = GetPropValue(timeline, "VideoInfo");
                if (videoInfo == null)
                {
                    log(Tr.T("log.reset.noInfo"));
                    return;
                }
                var changed = 0;
                var failed = 0;
                foreach (var v in voices)
                {
                    if (VoiceReadiness(v) <= 0) continue; // 未確定のものは触らない
                    try
                    {
                        var lp = FindProp(v.GetType(), "Length");
                        var before = lp != null ? Convert.ToInt32(lp.GetValue(v)) : 0;
                        if (!ResetVoiceLength(v, videoInfo, log)) { failed++; continue; }
                        var after = lp != null ? Convert.ToInt32(lp.GetValue(v)) : 0;
                        if (after != before) changed++;
                    }
                    catch (Exception ex) { failed++; if (failed <= 2) log(Tr.T("log.reset.fail", ex.Message)); }
                }
                log(Tr.T("log.reset.done", voices.Count, changed, failed > 0 ? Tr.T("log.reset.failed", failed) : ""));
            }
            catch (Exception ex) { log(Tr.T("log.reset.exception", ex.Message)); }
        }

        /// <summary>timeline.Items のボイス(VoiceItem)を Frame 昇順に並べ、実尺(Length)で back-to-back に再配置。
        /// 先頭ボイスは動かさない(シーン先頭に固定)。ボイス以外のアイテムは、直前ボイスのズレ量だけ
        /// 平行移動して同期を保つ(表情・セリフ内SE はセリフと同じ位置に置かれているため一緒に動く)。
        /// ボイス実尺が未確定(音声合成待ちで Length が 1 以下ばかり)のときは安全のため据え置く。</summary>
        static bool ResequenceByVoiceLength(Timeline timeline, double fps, double gapSec, Action<string> log,
            IDictionary<object, int> intendedLayer = null,
            List<object> propItems = null, HashSet<object> propExplicitEnd = null,
            HashSet<object> preExisting = null,
            IDictionary<object, double> voicePause = null,
            IDictionary<object, int> portraitEndSync = null)
        {
            try
            {
                var itemsEnum = GetPropValue(timeline, "Items") as System.Collections.IEnumerable;
                if (itemsEnum == null) { log(Tr.T("log.relayout.noItems")); return false; }
                // 今回の取り込みで置いたアイテムだけを対象にする(preExisting=取り込み前の既存分)。
                // 全件を対象にすると、既存プロジェクトのボイスまで back-to-back に再整列し、
                // contentEnd 打ち切りで既存アイテムを切り詰めてしまう。
                var all = itemsEnum.Cast<object>()
                    .Where(it => preExisting == null || !preExisting.Contains(it))
                    .ToList();

                int FrameOf(object it)
                {
                    var p = FindProp(it.GetType(), "Frame");
                    return p != null ? Convert.ToInt32(p.GetValue(it)) : 0;
                }
                int LenOf(object it)
                {
                    var p = FindProp(it.GetType(), "Length");
                    return p != null ? Convert.ToInt32(p.GetValue(it)) : 0;
                }

                var voices = all.OfType<VoiceItem>()
                    .Select(v => new { item = (object)v, frame = FrameOf(v), len = LenOf(v) })
                    .OrderBy(v => v.frame)
                    .ToList();
                if (voices.Count == 0) return false;

                // 実尺が妥当か。半数以上が 1 フレーム以下なら合成未完了とみなし据え置く(安全側)。
                if (voices.Count(v => v.len > 1) < (voices.Count + 1) / 2)
                {
                    log(Tr.T("log.relayout.unsettled"));
                    return false;
                }

                var gap = Math.Max(0, SecondsToFrames(gapSec, fps));
                // (旧Frame, ズレ量) の階段関数。ボイス以外のアイテムを直前ボイスのズレ量で移動するのに使う。
                var shifts = new List<Tuple<int, int>>();
                var cursor = voices[0].frame; // 先頭ボイスは動かさない
                var moved = 0;
                foreach (var v in voices)
                {
                    var delta = cursor - v.frame;
                    SetProps(v.item, log, P("Frame", cursor));
                    shifts.Add(Tuple.Create(v.frame, delta));
                    if (delta != 0) moved++;
                    // ★台本に書かれた「間」をボイスの後ろに足す。実尺で並べ直すと
                    //   見積りの尺は捨てられるので、ここで足さないと指定した溜めが消える
                    //   (2026-08-08 監査)。
                    var pauseFrames = 0;
                    if (voicePause != null && voicePause.TryGetValue(v.item, out var ps) && ps > 0)
                        pauseFrames = SecondsToFrames(ps, fps);
                    // ★足し続けても int があふれないよう上限で止める(間は1行24時間まで・行は2万まで)
                    cursor = (int)Math.Min(MaxFrame, (long)cursor + Math.Max(1, v.len) + pauseFrames + gap);
                }

                // 旧Frame f のズレ量。位置と長さの写像に使う(本体は ShiftDeltaAt に抽出済み)。
                int DeltaAt(int f) => ShiftDeltaAt(shifts, f);

                var voiceSet = new HashSet<object>(voices.Select(v => v.item));
                var others = 0;
                var stretched = 0;
                foreach (var it in all)
                {
                    if (voiceSet.Contains(it)) continue;
                    var fp = FindProp(it.GetType(), "Frame");
                    if (fp == null || !fp.CanWrite) continue;
                    var f = Convert.ToInt32(fp.GetValue(it)); // 旧Frame
                    var dStart = DeltaAt(f);
                    // 長さ: 旧終端(f+len)の移動量に合わせて伸縮。立ち絵/背景/BGM/窓/小物 等の「後の
                    // イベントまで持続」するアイテムが、実尺のタイムラインに追随する=ボイス基準になる。
                    // 表情アイテムはスパンでなく「1セリフ=1ボイス」なので、ここではいじらず下で実尺に合わせる。
                    var lp = FindProp(it.GetType(), "Length");
                    if (lp != null && lp.CanWrite && !(it is TachieFaceItem))
                    {
                        var len = Convert.ToInt32(lp.GetValue(it));
                        if (len > 1)
                        {
                            var newLen = len + DeltaAt(f + len) - dStart;
                            if (newLen >= 1 && newLen != len) { SetProps(it, log, P("Length", newLen)); stretched++; }
                        }
                    }
                    if (dStart != 0) { SetProps(it, log, P("Frame", f + dStart)); others++; }
                }

                // 表情アイテムはボイスの実尺そのものに合わせる。移動後は同じ CharacterName+Frame に
                // その表情のボイスが来ているので、ボイスの実 Length を引いて採用する。
                var voiceLenByKey = new Dictionary<string, int>();
                foreach (var v in voices)
                {
                    var nm = GetPropValue(v.item, "CharacterName") as string ?? "";
                    voiceLenByKey[nm + "|" + FrameOf(v.item)] = LenOf(v.item);
                }
                var faceSynced = 0;
                foreach (var it in all)
                {
                    if (!(it is TachieFaceItem)) continue;
                    var lp = FindProp(it.GetType(), "Length");
                    if (lp == null || !lp.CanWrite) continue;
                    var nm = GetPropValue(it, "CharacterName") as string ?? "";
                    if (voiceLenByKey.TryGetValue(nm + "|" + FrameOf(it), out var vlen) && vlen > 1)
                    {
                        SetProps(it, log, P("Length", vlen));
                        faceSynced++;
                    }
                }

                // portrait で切られた立ち絵の終端合わせ: 切断はセリフの「終了時刻(推定)」で行われて
                // いるが、shifts の階段はボイス開始フレームでしか変わらないため、上の伸縮では終端が
                // 推定尺のまま残る。対応するボイス(同キャラ・同旧開始フレーム=アンカー)の実終端
                // (Frame+実Length)に合わせ直す。
                var portraitSynced = 0;
                if (portraitEndSync != null && portraitEndSync.Count > 0)
                {
                    // ★索引を引いてから探す(切替ごとに全アイテム・全ボイスをなめると二乗になる)
                    var allSet = new HashSet<object>(all);
                    var voicesByOldFrame = voices.ToLookup(v => v.frame);
                    foreach (var kv in portraitEndSync)
                    {
                        var it = kv.Key;
                        if (!allSet.Contains(it)) continue;
                        var lp2 = FindProp(it.GetType(), "Length");
                        if (lp2 == null || !lp2.CanWrite) continue;
                        var nm2 = GetPropValue(it, "CharacterName") as string ?? "";
                        var vMatch = voicesByOldFrame[kv.Value].FirstOrDefault(
                            v => (GetPropValue(v.item, "CharacterName") as string ?? "") == nm2);
                        var newLen = PortraitCutTachieNewLength(FrameOf(it), LenOf(it),
                            vMatch != null ? (int?)FrameOf(vMatch.item) : null,
                            vMatch != null ? LenOf(vMatch.item) : 0);
                        if (newLen != null) { SetProps(it, log, P("Length", newLen.Value)); portraitSynced++; }
                    }
                }

                // スパン系(立ち絵/背景/BGM等)が最後のボイスより後ろへ伸びて末尾に無音が残るのを防ぐ。
                // 実コンテンツ終端(最後のボイスの終わり)を超えて伸びるアイテムは、そこで打ち切る。
                var contentEnd = voices.Max(v => FrameOf(v.item) + Math.Max(1, LenOf(v.item)));
                var clamped = 0;
                foreach (var it in all)
                {
                    if (voiceSet.Contains(it)) continue;
                    var fp = FindProp(it.GetType(), "Frame");
                    var lp = FindProp(it.GetType(), "Length");
                    if (fp == null || lp == null || !lp.CanWrite) continue;
                    var f = Convert.ToInt32(fp.GetValue(it));
                    var len = Convert.ToInt32(lp.GetValue(it));
                    if (f < contentEnd && f + len > contentEnd)
                    {
                        SetProps(it, log, P("Length", contentEnd - f));
                        clamped++;
                    }
                }
                // 別レーンへ弾かれた同種アイテムを1レーンへ畳み直す。音声合成でボイス実尺が伸びて
                // 一瞬重なるとYMM4が別レーンへ退避し、位置を直してもその退避レーンが残る(=別レイヤに散る)。
                // 再配置後は時間軸上 back-to-back で重ならないので、種類×キャラごとに最小レーンへ集約する。
                // ただし重なりが1つでも残るグループは不正状態を避けて畳まない(安全側)。
                int LayerOf(object it) { var p = FindProp(it.GetType(), "Layer"); return p != null ? Convert.ToInt32(p.GetValue(it)) : 0; }
                var relaned = 0;
                foreach (var grp in all.Where(it => it is VoiceItem || it is TachieFaceItem)
                    .GroupBy(it => it.GetType().Name + "|" + (GetPropValue(it, "CharacterName") as string ?? "")))
                {
                    var members = grp.OrderBy(FrameOf).ToList();
                    if (members.Count < 2) continue;
                    var overlap = false;
                    for (int i = 0; i + 1 < members.Count; i++)
                        if (FrameOf(members[i]) + Math.Max(1, LenOf(members[i])) > FrameOf(members[i + 1])) { overlap = true; break; }
                    if (overlap) continue; // 重なりが残るなら畳まない
                    var target = members.Min(LayerOf);
                    foreach (var it in members)
                        if (LayerOf(it) != target) { SetProps(it, log, P("Layer", target)); relaned++; }
                }

                // 配置アイテム(小物/背景/立ち絵/窓/エフェクト等)を意図レーンへ戻し、同一レーン内で
                // 連続するアイテムが境界で重ならないよう打ち切る。小物の退場→登場が並ぶ境界の
                // 1フレーム被り・別レーン散りを解消する。表情は上で実尺同期済みなので長さは触らない。
                var laneClamped = 0;
                if (intendedLayer != null && intendedLayer.Count > 0)
                {
                    foreach (var kv in intendedLayer)
                        if (LayerOf(kv.Key) != kv.Value) { SetProps(kv.Key, log, P("Layer", kv.Value)); relaned++; }
                    foreach (var grp in intendedLayer.Keys.GroupBy(it => intendedLayer[it]))
                    {
                        var lane = grp.OrderBy(FrameOf).ToList();
                        for (int i = 0; i + 1 < lane.Count; i++)
                        {
                            var it = lane[i];
                            if (it is TachieFaceItem) continue;
                            var lp = FindProp(it.GetType(), "Length");
                            if (lp == null || !lp.CanWrite) continue;
                            var nextFrame = FrameOf(lane[i + 1]);
                            if (FrameOf(it) + LenOf(it) > nextFrame)
                            {
                                SetProps(it, log, P("Length", Math.Max(1, nextFrame - FrameOf(it))));
                                laneClamped++;
                            }
                        }
                    }
                }

                // 退場(prop_stop)が明示された小物は、次の小物の登場と重ならないよう詰める。
                // 「明確に入れ替わりが書かれている場合は重ねない」= 作者が退場を書いた小物だけ、
                // レーンをまたいで次の小物の開始フレームで打ち切る(退場を書いていない小物は積み上げ可のまま)。
                var swapClamped = 0;
                if (propItems != null && propExplicitEnd != null && propExplicitEnd.Count > 0)
                {
                    var props = propItems.Where(p => FindProp(p.GetType(), "Frame") != null)
                        .OrderBy(FrameOf).ToList();
                    // ★開始フレームの昇順の配列を二分探索で引く(小物ごとに全小物をなめると二乗になる)
                    var propFrames = props.Select(FrameOf).ToArray();
                    foreach (var p in props)
                    {
                        if (!propExplicitEnd.Contains(p)) continue;
                        var lp = FindProp(p.GetType(), "Length");
                        if (lp == null || !lp.CanWrite) continue;
                        var pf = FrameOf(p);
                        var ub = UpperBound(propFrames, pf);
                        var nextStart = ub < propFrames.Length ? propFrames[ub] : int.MaxValue;
                        if (nextStart != int.MaxValue && pf + LenOf(p) > nextStart)
                        {
                            SetProps(p, log, P("Length", Math.Max(1, nextStart - pf)));
                            swapClamped++;
                        }
                    }
                }

                log(Tr.T("log.relayout.done", voices.Count, moved, others, stretched, clamped, laneClamped,
                    swapClamped, faceSynced, portraitSynced, relaned, gap));
                return true;
            }
            catch (Exception ex) { log(Tr.T("log.relayout.fail", ex.Message)); return false; }
        }
    }
}
