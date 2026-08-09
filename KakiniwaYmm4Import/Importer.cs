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
        public static async System.Threading.Tasks.Task RegisterExpressionPresets(Pack pack, string packDir,
            Dictionary<string, CharacterChoice> speakerMap, HashSet<string> only, Action<string> log)
        {
            var presetType = FindLoadedType("YukkuriMovieMaker.Plugin.Tachie.Psd.PsdPreset");
            if (presetType == null) { log("PsdPreset 型が見つからない(PSDプラグイン未ロード?)"); return; }
            LogYmm4VersionIfUntested(log);
            var total = 0;
            List<CharacterChoice>? ymm4Chars = null; // 必要になったら一度だけ取得
            foreach (var chara in pack.Characters)
            {
                if (!only.Contains(chara.Name)) continue;
                CharacterChoice? choice;
                if (!speakerMap.TryGetValue(chara.Id, out choice) || choice == null || choice.Model == null)
                {
                    log("  " + chara.Name + ": 話者未割り当てのためスキップ");
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
                var groups = new List<Tuple<string, string, List<PackExpression>>>(); // (psdPath, 説明, 表情)
                var claimed = new HashSet<string>();
                if (chara.Portraits != null)
                    foreach (var p in chara.Portraits)
                        foreach (var x in p.Expressions ?? new List<PackExpression>())
                            claimed.Add(x.Id);
                var flat = chara.Expressions
                    .Where(x => x.Psd != null && x.Psd.Layers.Count > 0 && !claimed.Contains(x.Id))
                    .ToList();
                if (flat.Count > 0)
                {
                    if (!string.IsNullOrEmpty(defaultPsd) && File.Exists(defaultPsd))
                        groups.Add(Tuple.Create(defaultPsd!, "既定", flat));
                    else
                        log("  " + chara.Name + "(→" + choice.Name + "): PSD立ち絵未設定のため既定の表情をスキップ");
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
                        // 立ち絵ごとの割り当て(取り込みダイアログの立ち絵行。複合キー charId/portraitId)を
                        // 最優先。基底キャラと同じ選択なら従来の Default/サイドカー経路へ。
                        CharacterChoice? selP;
                        var explicitSel = speakerMap.TryGetValue(chara.Id + "/" + p.Id, out selP)
                            && selP != null && selP.Model != null ? selP : null;
                        if (explicitSel != null && explicitSel.Name != choice.Name)
                        {
                            var ipS = GetPropValue(explicitSel.Model!, "TachieDefaultItemParameter");
                            target = ipS != null ? GetPropValue(ipS, "FilePath") as string : null;
                            desc += " → " + explicitSel.Name;
                            if (string.IsNullOrEmpty(target) || !File.Exists(target))
                            {
                                log("  " + chara.Name + "/" + desc + ": 割り当て先YMM4キャラのPSDが見つからずスキップ");
                                continue;
                            }
                        }
                        else if (explicitSel == null && !string.IsNullOrEmpty(p.Ymm4Name))
                        {
                            // 別のYMM4キャラに割り当てた立ち絵: そのキャラの既定PSDへ
                            if (ymm4Chars == null) ymm4Chars = GetYmm4Characters(_ => { });
                            var m = ymm4Chars.FirstOrDefault(c => c.Name == p.Ymm4Name && c.Model != null);
                            var ip = m != null ? GetPropValue(m.Model!, "TachieDefaultItemParameter") : null;
                            target = ip != null ? GetPropValue(ip, "FilePath") as string : null;
                            desc += " → " + p.Ymm4Name;
                            if (string.IsNullOrEmpty(target) || !File.Exists(target))
                            {
                                log("  " + chara.Name + "/" + desc + ": 割り当て先YMM4キャラのPSDが見つからずスキップ");
                                continue;
                            }
                        }
                        else if (p.Default && !string.IsNullOrEmpty(defaultPsd) && File.Exists(defaultPsd))
                        {
                            // 既定の立ち絵(割り当て先の指定なし)= このキャラのYMM4登録PSDへ
                            target = defaultPsd;
                        }
                        else
                        {
                            // 同じYMM4キャラのままPSDだけ差し替える立ち絵: パック内のそのPSDのサイドカーへ
                            var rel = p.Psd != null ? p.Psd.Path : exprsP[0].Psd!.Path;
                            target = ResolveInPack(packDir, rel);
                            if (target == null || !File.Exists(target))
                            {
                                log("  " + chara.Name + "/" + desc + ": 立ち絵PSDが見つからずスキップ");
                                continue;
                            }
                        }
                        groups.Add(Tuple.Create(target!, desc, exprsP));
                    }
                }
                foreach (var g in groups)
                {
                    var seen = new HashSet<string>();
                    var exprs = g.Item3.Where(x => seen.Add(x.Id)).ToList();
                    total += await RegisterPresetsInto(presetType, g.Item1, exprs, choice,
                        chara.Name + "(" + g.Item2 + "→" + choice.Name + ")", log);
                }
            }
            log(total > 0
                ? "表情プリセット登録完了: 計" + total + "件(YMM4のPSD素材マネージャを開き直すと反映)"
                : "登録できるプリセットがありませんでした");
        }

        /// <summary>表情プリセット一式を、指定PSDのサイドカー(-ymm.json)へ登録する。返り値=登録件数</summary>
        static async System.Threading.Tasks.Task<int> RegisterPresetsInto(Type presetType, string psdPath,
            List<PackExpression> exprs, CharacterChoice choice, string label, Action<string> log)
        {
            var settings = LoadPsdFileSettings(psdPath, log);
            if (settings == null) return 0;
            // Presets は ImmutableList のため、作業用リストで組み直してプロパティごと差し替える
            var working = new List<object>();
            if (GetPropValue(settings, "Presets") is System.Collections.IEnumerable existingList)
                foreach (var x in existingList)
                    if (x != null) working.Add(x);
            var added = 0;
            var newOnes = new List<object>();
            foreach (var expr in exprs)
            {
                var name = expr.Label ?? expr.Id;
                // 同名プリセットは置き換え
                working.RemoveAll(x => GetPropValue(x, "Name") as string == name);
                var preset = Activator.CreateInstance(presetType);
                if (preset == null) continue;
                var nameProp = presetType.GetProperty("Name");
                if (nameProp != null && nameProp.CanWrite) nameProp.SetValue(preset, name);
                var state = await BuildFullFaceState(choice.Model!, expr.Psd!.Layers, expr.Psd!.LayerIds, log);
                if (state == null) { log("  プリセット「" + name + "」: 全身状態を組めずスキップ"); continue; }
                var pathsProp = presetType.GetProperty("LayerPaths");
                if (pathsProp != null && pathsProp.CanWrite)
                    pathsProp.SetValue(preset, MakeStringList(pathsProp.PropertyType, state.Item2));
                var layersProp = presetType.GetProperty("Layers");
                if (layersProp != null && layersProp.CanWrite)
                    layersProp.SetValue(preset, MakeStringList(layersProp.PropertyType, state.Item1));
                newOnes.Add(preset);
                added++;
            }
            working.AddRange(newOnes);
            try
            {
                // ImmutableList<PsdPreset> を組み立てて Presets を差し替え
                var presetsProp = settings.GetType().GetProperty("Presets");
                if (presetsProp == null || !presetsProp.CanWrite)
                {
                    log("  " + label + ": Presets プロパティが書き替えられない");
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
                if (save == null) { log("  " + label + ": SaveFromPsdFilePath が見つからない"); return 0; }
                save.Invoke(settings, new object?[] { psdPath });
                log("  " + label + ": " + added + "件のプリセットを登録 → "
                    + Path.GetFileNameWithoutExtension(psdPath) + "-ymm.json");
                return added;
            }
            catch (Exception ex)
            {
                log("  " + label + ": 保存失敗: " + (ex.InnerException != null ? ex.InnerException.Message : ex.Message));
                return 0;
            }
        }

        public static async System.Threading.Tasks.Task Run(Pack pack, string packDir,
            Dictionary<string, CharacterChoice> speakerMap,
            ImportSettings settings, Action<string> log)
        {
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
                if (preExisting.Count > 0) log("既存アイテム " + preExisting.Count + " 件は再配置の対象外にします");
            }
            catch (Exception ex) { log("既存アイテムの記録に失敗(続行): " + ex.Message); }

            var fps = ReadFps(timeline, log);
            var projW = ReadVideoInfoValue(timeline, "Width", 1920, log);
            var projH = ReadVideoInfoValue(timeline, "Height", 1080, log);
            Func<double, int> F = sec => (int)Math.Round(sec * fps); // 尺(長さ)用

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
                log("配置先: レイヤー" + layerOffset + "から / " + (frameOffset > 0 ? frameOffset + "フレーム目から" : "先頭から"));
            Func<double, int> FrameAt = sec => F(sec) + frameOffset; // 開始位置用
            Func<int, int> L = n => n + layerOffset;

            var evs = pack.Events;
            var totalEnd = evs.Count == 0 ? 0 : evs.Max(ev2 => ev2.Start + ev2.Seconds) + 1.0;

            // レイヤー割り当て(2パス): 実際に使うレーンだけ数えて0から隙間なく詰める。
            // 重なり順: 背景 < BGM < SE < ト書き < キャラ(立ち絵/表情) < 小物 < エフェクト < 窓 < ボイス(字幕)
            var lanes = new Dictionary<string, int>();
            var nextLane = 0;
            Action<string> addLane = key => { if (!lanes.ContainsKey(key)) lanes[key] = nextLane++; };
            Func<string, int> laneOf = key =>
            {
                int l;
                if (!lanes.TryGetValue(key, out l)) { l = nextLane++; lanes[key] = l; }
                return l;
            };
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
            var propSlot = new Dictionary<int, int>(); // 小物イベントの index → スロット番号
            {
                Func<int, double> propEnd = i =>
                {
                    var nm = evs[i].Asset!.Name;
                    for (var j = i + 1; j < evs.Count; j++)
                        if (evs[j].Type == "prop_stop"
                            && (evs[j].Name == nm || (evs[j].Asset != null && evs[j].Asset!.Name == nm)))
                            return evs[j].Start;
                    return totalEnd;
                };
                var propIdx = new List<int>();
                for (var i = 0; i < evs.Count; i++)
                    if (evs[i].Type == "prop" && assetExists(evs[i].Asset)) propIdx.Add(i);
                propIdx.Sort((a, b) => evs[a].Start.CompareTo(evs[b].Start));
                propSlot = AssignGreedySlots(propIdx, i2 => evs[i2].Start, propEnd);
            }

            if (evs.Any(e2 => e2.Type == "bg" && assetExists(e2.Asset))) addLane("bg");
            if (evs.Any(e2 => e2.Type == "bgm" && assetExists(e2.Asset))) addLane("bgm");
            if (evs.Any(e2 => e2.Type == "se" && assetExists(e2.Asset))
                || evs.Any(e2 => e2.Type == "serif" && e2.Se.Any(s2 => assetExists(s2)))) addLane("se");
            if (evs.Any(e2 => e2.Type == "stage")) addLane("stage");
            foreach (var id in charOrder) addLane("chara:" + id);
            for (var i = 0; i < evs.Count; i++)
                if (evs[i].Type == "prop" && assetExists(evs[i].Asset)) addLane("prop-slot:" + propSlot[i]);
            foreach (var e2 in evs)
                if (e2.Type == "effect" && assetExists(e2.Asset)) addLane("effect:" + e2.Asset!.Name);
            if (evs.Any(e2 => e2.Type == "window" && assetExists(e2.Asset))) addLane("window");
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
            Func<string, PackPortrait?> activePortraitOf = (charId) =>
            {
                // 保険込みの解決(FindPortrait): 書き庭側は id へ正規化して送るが、古い書き庭が
                // 作ったパックでは表示名・YMM4名のまま入っていることがある。表情は元から両対応
                // なので、立ち絵だけ id 限定で「切替が黙って効かない」になるのを防ぐ。
                if (portraitsById.TryGetValue(charId, out var m)
                    && activePortrait.TryGetValue(charId, out var pid))
                    return FindPortrait(m, pid);
                return null;
            };

            // 対応する停止イベント(prop_stop / window_stop)までの時刻を探す
            Func<int, string, string?, double> nextStopOf = (i, stopType, name) =>
            {
                for (var j = i + 1; j < evs.Count; j++)
                {
                    if (evs[j].Type != stopType) continue;
                    if (name == null || evs[j].Name == name
                        || (evs[j].Asset != null && evs[j].Asset!.Name == name)) return evs[j].Start;
                }
                return totalEnd;
            };

            Func<int, string[], double> nextStartOfType = (i, types) =>
            {
                for (var j = i + 1; j < evs.Count; j++)
                    if (types.Contains(evs[j].Type)) return evs[j].Start;
                return totalEnd;
            };

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

            for (var i = 0; i < evs.Count; i++)
            {
                var ev = evs[i];
                switch (ev.Type)
                {
                    case "bg":
                    {
                        var path = ResolveAsset(packDir, ev.Asset, skipped, ev);
                        if (path == null) break;
                        var item = MakeItem(typeof(ImageItem), ev.Template, log, () => new ImageItem());
                        SetProps(item, log,
                            P("FilePath", path), P("Frame", FrameAt(ev.Start)), P("Layer", L(laneOf("bg"))),
                            P("Length", F(nextStartOfType(i, new[] { "bg" }) - ev.Start)),
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
                            P("FilePath", path), P("Frame", FrameAt(ev.Start)), P("Layer", L(laneOf("bgm"))),
                            P("Length", F(nextStartOfType(i, new[] { "bgm", "bgm_stop" }) - ev.Start)),
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
                        SetProps(item, log,
                            P("FilePath", path), P("Frame", FrameAt(ev.Start)), P("Layer", L(laneOf("se"))),
                            P("Length", F(AudioDurationSeconds(path) ?? 1.0)),
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
                                        log("  退場: " + pos.Speaker + "(立ち絵を " + ev.Start + "秒で終了)");
                                    }
                                    else
                                    {
                                        log("  退場: " + pos.Speaker + "(立ち絵未配置のためスキップ)");
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
                                log(string.Format("  配置更新: {0} x={1}% height={2}",
                                    pos.Speaker, pos.X, pos.Height != null ? pos.Height + "%" : "既定"));
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
                            emptySerifSkipped++;
                            break;
                        }
                        if (!string.IsNullOrEmpty(ev.Template) && !serifTemplateWarned)
                        {
                            serifTemplateWarned = true;
                            log("  ※ セリフのアイテムテンプレート適用は現状未対応です(実機検証後に対応予定)。"
                                + "ボイスは既定の設定で配置します");
                        }
                        var chara = pack.Characters.FirstOrDefault(c => c.Id == ev.Speaker);
                        var charId = ev.Speaker ?? "?";
                        // レーンは laneOf で解決
                        var slotIndex = slotOf(charId); // 登場順: 0=左,1=右,…

                        // 話者割り当て(UIで選択済み)。無ければ ymm4Name → name → 台本表記の順
                        CharacterChoice? choice = null;
                        if (ev.Speaker != null) speakerMap.TryGetValue(ev.Speaker, out choice);
                        var ymm4Name = (choice != null ? choice.Name : null)
                                       ?? (chara != null ? (chara.Ymm4Name ?? chara.Name) : null)
                                       ?? ev.SpeakerName ?? "";

                        // ★アクティブ立ち絵の割り当て先キャラ解決(立ち絵・表情・ボイス共通)。
                        //   別PSD立ち絵がアクティブな間のセリフは、ボイスもそのYMM4キャラの
                        //   声設定で生成する(音声話者も変えないと表情アイテム・口パクの対応が
                        //   切れる)。割り当てなし/解決不能なら従来どおり基底キャラ。
                        //   レーン(voice:charId)は書き庭キャラ単位のまま。
                        var apSerif = activePortraitOf(charId);
                        var serifHost = choice != null && choice.Model != null
                            ? resolveTachieHost(charId, choice, apSerif) : null;
                        var voiceChoice = serifHost != null ? serifHost.Item1 : choice;
                        var voiceName = serifHost != null ? serifHost.Item1.Name : ymm4Name;

                        // 本命: MainModel.AddVoiceItemAsync(UIのセリフ追加の完全経路。
                        // タイムライン追加+ボイス生成トリガーまでYMM4自身がやる)
                        VoiceItem? voice = null;
                        var addedByModel = false;
                        if (voiceChoice != null && voiceChoice.Model != null && mainModel != null)
                        {
                            var r = await AddVoiceViaMainModel(mainModel,
                                FrameAt(ev.Start), L(laneOf("voice:" + charId)), voiceChoice.Model, ev.Text ?? "", log);
                            addedByModel = r.Item1;
                            voice = r.Item2;
                            if (addedByModel)
                                directPlacements.Add(Tuple.Create(L(laneOf("voice:" + charId)),
                                    "VoiceItem ボイス「" + (ev.Text ?? "").Substring(0, Math.Min(10, (ev.Text ?? "").Length)) + "」"));
                        }
                        if (!addedByModel)
                        {
                            // 保険1: 正規ファクトリで生成して自前配置
                            if (voiceChoice != null && voiceChoice.Model != null)
                                voice = await CreateVoiceItemViaFactory(voiceChoice.Model, ev.Text ?? "", log);
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
                        if (voice != null && !string.IsNullOrEmpty(ev.Reading))
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
                                    log("  立ち絵「" + (apTachie != null ? apTachie.Id : "?")
                                        + "」の割り当て先YMM4キャラ「" + PortraitAssignedName(apTachie)
                                        + "」が見つからないため、パックの画像で仮置きします");
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
                                        log("  立ち絵の画像が見つからないので、大きさの自動調整を省略しました: "
                                            + (chara != null ? chara.Name : charId));
                                        SetAnim(tachie, "X", xOf(charId, slotIndex), log);
                                        SetAnim(tachie, "Y", yOffsetOf(charId), log);
                                    }
                                    items.Add(tachie);
                                }
                                else
                                {
                                    tachieUnavailable.Add(charId);
                                    log("  → 「" + hostChoice.Name + "」はYMM4側に立ち絵が無いので、パックの画像で仮置きします");
                                }
                                }
                            }
                            if (tachieUnavailable.Contains(charId))
                            {
                                // 立ち絵未設定キャラ: パックの表情画像/PSDをセリフ区間に仮置き
                                var portrait = ResolvePortrait(packDir, chara, activePortraitOf(charId), ev.Expression);
                                if (portrait == null)
                                    log("  仮置きの立ち絵画像が見つかりません: "
                                        + (chara != null ? chara.Name : charId));
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
                                    log("  表情「" + ev.Expression + "」: PSDレイヤー指定が無いためスキップ");
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
                                        log("  表情「" + ev.Expression + "」: 立ち絵の割り当て先YMM4キャラが"
                                            + "見つからないためスキップ");
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
                                log("⚠ 「" + (chara != null ? chara.Name : ev.SpeakerName ?? charId) + "」→「" + ymm4Name
                                    + "」はYMM4未登録の名前です。ボイス・立ち絵は仮配置になります"
                                    + "(話者の割り当てドロップダウンで登録キャラを選ぶと本配置)");
                            // 未割り当てキャラ: パック内の画像/PSDを ImageItem で仮置き(従来動作)
                            var portrait = ResolvePortrait(packDir, chara, activePortraitOf(charId), ev.Expression);
                            if (portrait == null)
                                log("  仮置きの立ち絵画像が見つかりません: "
                                    + (chara != null ? chara.Name : charId));
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

                        foreach (var se in ev.Se)
                        {
                            var sePath = ResolveAsset(packDir, se, skipped, ev);
                            if (sePath == null) continue;
                            var item = new AudioItem();
                            SetProps(item, log,
                                P("FilePath", sePath), P("Frame", FrameAt(ev.Start)), P("Layer", L(laneOf("se"))),
                                P("Length", F(AudioDurationSeconds(sePath) ?? 1.0)), P("Remark", "SE: " + se.Name));
                            items.Add(item);
                        }
                        break;
                    }
                    case "window":
                    case "prop":
                    {
                        var path = ResolveAsset(packDir, ev.Asset, skipped, ev);
                        if (path == null) break;
                        var isWindow = ev.Type == "window";
                        var stopAt = nextStopOf(i, isWindow ? "window_stop" : "prop_stop",
                            isWindow ? null : ev.Asset!.Name);
                        var item = MakeItem(typeof(ImageItem), ev.Template, log, () => new ImageItem());
                        SetProps(item, log,
                            P("FilePath", path), P("Frame", FrameAt(ev.Start)),
                            P("Layer", L(laneOf(isWindow ? "window" : "prop-slot:" + propSlot[i]))),
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
                        var isVideo = ext == ".mp4" || ext == ".webm" || ext == ".avi" || ext == ".mov";
                        var wantType = isVideo ? typeof(VideoItem) : typeof(ImageItem);
                        BaseItem item = MakeItem(wantType, ev.Template, log,
                            () => isVideo ? (BaseItem)new VideoItem() : new ImageItem());
                        var seconds = ev.Seconds > 0 ? ev.Seconds : 2.0;
                        SetProps(item, log,
                            P("FilePath", path), P("Frame", FrameAt(ev.Start)),
                            P("Layer", L(laneOf("effect:" + ev.Asset!.Name))),
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
                            log("  立ち絵切替: 「" + ev.Name + "」は登録されていない立ち絵名のためスキップ("
                                + sp + " は現在の立ち絵のまま)");
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
                                    log("  立ち絵「" + (apNew != null ? apNew.Id : plan.PortraitId)
                                        + "」の割り当て先YMM4キャラ「" + PortraitAssignedName(apNew)
                                        + "」が見つからないため、次のセリフまで表示をスキップ");
                                    log("  立ち絵切替: " + sp + " → " + ev.Name);
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
                            log("  立ち絵切替の置き直しに失敗(続行): " + ex.Message);
                        }
                        log("  立ち絵切替: " + sp + " → " + ev.Name);
                        break;
                    }
                    case "bgm_stop":
                    case "prop_stop":
                    case "window_stop":
                    case "custom":
                        break; // アイテム化しない(*_stop は終端計算で消費済み)
                    default:
                        skipped.Add(string.Format("no.{0}: 未知の type '{1}'", ev.No, ev.Type));
                        break;
                }
            }

            log(string.Format("アイテム生成: {0}件 (fps={1})", items.Count, fps));
            foreach (var s in skipped) log("  スキップ: " + s);
            // 飛ばしたものがあるなら、末尾でもう一度まとめて言う。1行ずつのスキップは
            // 大量のログに埋もれて気づけず、「配置はできたのに素材が抜けている」まま
            // 動画を作り進めてしまう。何件・何が足りないかを最後に一目で分かるようにする。
            if (emptySerifSkipped > 0)
                log(string.Format("  中身の無い行(SE や 間 だけ)を {0}件、ボイスにしませんでした", emptySerifSkipped));
            if (skipped.Count > 0)
            {
                log("");
                log(string.Format("※ {0}件を飛ばしました(素材が見つからない等)。上の「スキップ」行を確認してください", skipped.Count));
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
                    layerSummary.Add(Tuple.Create(layer,
                        it.GetType().Name + (string.IsNullOrEmpty(remark) ? "" : " " + remark)));
                }
                catch { }
            }
            log("── 配置レイヤー一覧(番号大=手前のつもり) ──");
            foreach (var t in layerSummary.OrderBy(t2 => t2.Item1))
                log("  L" + t.Item1.ToString().PadLeft(2) + ": " + t.Item2);

            AddItems(timeline, items, log);

            // 診断: ボイスアイテムがキャラへ解決できているか(ボイス割り当て問題の切り分け)
            var firstVoice = items.OfType<VoiceItem>().FirstOrDefault();
            if (firstVoice != null)
            {
                try
                {
                    var cp = FindProp(firstVoice.GetType(), "Character");
                    var c = cp != null ? cp.GetValue(firstVoice) : null;
                    var np = c != null ? c.GetType().GetProperty("Name") : null;
                    log("診断: 先頭ボイスの Character = "
                        + (c == null ? "null(この名前のキャラがYMM4に未登録)" : (np != null ? np.GetValue(c) : c)));
                    var vp = c != null ? c.GetType().GetProperty("Voice") : null;
                    var v = vp != null && c != null ? vp.GetValue(c) : null;
                    if (c != null) log("診断: キャラの声設定 = " + (v == null ? "null(キャラ設定で声が未設定)" : v.GetType().Name));
                }
                catch (Exception ex) { log("診断失敗: " + ex.Message); }
            }

            // ボイスは AddVoiceItemAsync が非同期(バックグラウンド)で生成する。実尺が揃う前に
            // 再配置すると「据え置き」になり推定尺のまま=ボイス間に間隔が残る。生成完了(実尺確定)を
            // 待ってから再配置する。
            await WaitForVoiceLengths(timeline, preExisting, log);

            // セリフの尺は推定(charsPerSecond)なので、実ボイス長とズレて「間延び/被り」が出る。
            // 配置後の実尺でセリフを詰め直す(YMM4の ResolveAllItemsCollision は重なりを散らすだけで
            // 隙間を詰めない)。実尺が未確定で再配置を見送ったときだけ、従来の衝突解決を保険で呼ぶ。
            if (!ResequenceByVoiceLength(timeline, fps, pack.Timing.SerifGap, log, intendedLayer, propItems, propExplicitEnd, preExisting, voicePause, portraitEndSync))
            {
                try
                {
                    var resolve = typeof(Timeline).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .FirstOrDefault(m2 => m2.Name == "ResolveAllItemsCollision" && m2.GetParameters().Length == 0);
                    if (resolve != null) { resolve.Invoke(timeline, null); log("(保険)衝突解決を実行: 重なりのみ整理"); }
                }
                catch (Exception ex) { log("衝突解決失敗: " + ex.Message); }
            }

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
                    var hi = frameOffset + F(totalEnd) + 1;
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
                            log("  ボイス表情オフ不可(パラメータnull): " + (vname ?? "?"));
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
                                log("  ボイス表情オフ不可(IsEnabledが無い: " + fparam.GetType().Name + "): " + (vname ?? "?"));
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
                        if (fr3 < frameOffset || fr3 > frameOffset + F(totalEnd) + 1) continue;
                        var prm = GetPropValue(tfi, "TachieFaceParameter");
                        log("診断[表情アイテム@" + fr3 + "]: param="
                            + (prm == null ? "null" : prm.GetType().Name)
                            + " IDs=" + DumpValue(prm != null ? GetPropValue(prm, "EnableLayers") : null)
                            + " Paths=" + DumpValue(prm != null ? GetPropValue(prm, "EnableLayerPaths") : null)
                            + " File=" + DumpValue(prm != null ? GetPropValue(prm, "FilePath") : null)
                            + " IsEnabled=" + DumpValue(prm != null ? GetPropValue(prm, "IsEnabled") : null));
                    }
                }

                if (hasWindow) log("テキスト窓があるため、字幕を最前面に設定: " + jimakuCount + "件");
                if (faceOffCount > 0)
                    log("ボイス側の表情変更を無効化: " + faceOffCount + "件(表情は表情レイヤーが支配)");
            }
            catch (Exception ex) { log("ボイス後処理失敗: " + ex.Message); }

            // 書き庭側の声設定の案内。YMM4 の声設定はエンジン統合も単位も別物なので
            // 自動では反映しない(壊すより案内)。パック同梱の「声の設定メモ.txt」と同内容。
            try
            {
                var tunedChars = pack.Characters.Where(c2 => c2.Voice != null).ToList();
                if (tunedChars.Count > 0)
                {
                    log("── 声の設定メモ(書き庭の試聴と同じ声にする場合、YMM4のキャラ設定を以下に) ──");
                    foreach (var c2 in tunedChars)
                    {
                        var v3 = c2.Voice!;
                        var line = "  " + (c2.Ymm4Name ?? c2.Name) + ": " + v3.Engine
                            + (string.IsNullOrEmpty(v3.Preset) ? "" : " / 話者 " + v3.Preset);
                        if (v3.Params != null && v3.Params.Count > 0)
                            line += " / " + string.Join(" ", v3.Params.Select(kv => kv.Key + "=" + kv.Value));
                        log(line);
                    }
                }
            }
            catch { /* 案内表示の失敗は取り込み結果に影響させない */ }
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
        internal static int ShiftDeltaAt(List<Tuple<int, int>> shifts, int f)
        {
            var d = 0; foreach (var s in shifts) { if (s.Item1 <= f) d = s.Item2; else break; } return d;
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
            Func<double, int> F = sec => (int)Math.Round(sec * fps);
            var plan = new PortraitSwitchPlan
            {
                PortraitId = pr.Id,
                Replace = placedStartSec != null,
                NewStartFrame = F(evStartSec) + frameOffset,
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
                log("  立ち絵サイズ不明のためレイアウト省略: " + Path.GetFileName(path));
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
            log(string.Format("  立ち絵レイアウト: {0} zoom={1:F1}% x={2:F0} y={3:F0} ({4})",
                Path.GetFileName(path), zoom, x, y,
                (ok1 && ok2 && ok3) ? "OK" : "一部失敗"));
        }

        /// <summary>ボイス生成(非同期)の完了を待つ。VoiceItem.Length が実尺(>1)になるまで最大約20秒ポーリング。
        /// 半数以上が確定したら抜ける(残りが遅れても再配置は安全側で据え置く)。await Task.Delay で
        /// UIスレッドを塞がず生成を進めさせる。</summary>
        static async System.Threading.Tasks.Task WaitForVoiceLengths(Timeline timeline, HashSet<object> preExisting, Action<string> log)
        {
            try
            {
                var itemsEnum = GetPropValue(timeline, "Items") as System.Collections.IEnumerable;
                if (itemsEnum == null) return;
                // ★今回置いたボイスだけを数える。既存アイテムのあるプロジェクトでは、実尺確定済みの
                //   既存ボイスだけで「半数」を満たして即抜けし、今回分の生成を一切待たずに再配置が
                //   据え置き→保険の衝突解決がタイムライン全体に走って既存が散る、があった。
                var voices = itemsEnum.Cast<object>().OfType<VoiceItem>()
                    .Where(v => !preExisting.Contains(v)).ToList();
                if (voices.Count == 0) return;
                int LenOf(object it)
                {
                    var p = FindProp(it.GetType(), "Length");
                    return p != null ? Convert.ToInt32(p.GetValue(it)) : 0;
                }
                var need = (voices.Count + 1) / 2;
                for (var i = 0; i < 100; i++) // 100 * 200ms = 最大20秒
                {
                    var ready = voices.Count(v => LenOf(v) > 1);
                    if (ready >= need)
                    {
                        if (i > 0) log("ボイス生成待ち: " + ready + "/" + voices.Count + "件が実尺確定(" + (i * 200) + "ms)");
                        return;
                    }
                    await System.Threading.Tasks.Task.Delay(200);
                }
                log("ボイス生成待ち: タイムアウト(実尺が揃わないまま続行)");
            }
            catch (Exception ex) { log("ボイス生成待ちで例外(続行): " + ex.Message); }
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
                if (itemsEnum == null) { log("再配置: Items が取得できずスキップ"); return false; }
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
                    log("再配置: ボイス実尺が未確定(音声合成待ち?)のため据え置き");
                    return false;
                }

                var gap = Math.Max(0, (int)Math.Round(gapSec * fps));
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
                        pauseFrames = (int)Math.Round(ps * fps);
                    cursor += Math.Max(1, v.len) + pauseFrames + gap;
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
                    foreach (var kv in portraitEndSync)
                    {
                        var it = kv.Key;
                        if (!all.Contains(it)) continue;
                        var lp2 = FindProp(it.GetType(), "Length");
                        if (lp2 == null || !lp2.CanWrite) continue;
                        var nm2 = GetPropValue(it, "CharacterName") as string ?? "";
                        var vMatch = voices.FirstOrDefault(v => v.frame == kv.Value
                            && (GetPropValue(v.item, "CharacterName") as string ?? "") == nm2);
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
                    foreach (var p in props)
                    {
                        if (!propExplicitEnd.Contains(p)) continue;
                        var lp = FindProp(p.GetType(), "Length");
                        if (lp == null || !lp.CanWrite) continue;
                        var pf = FrameOf(p);
                        var nextStart = int.MaxValue;
                        foreach (var q in props) { var qf = FrameOf(q); if (qf > pf && qf < nextStart) nextStart = qf; }
                        if (nextStart != int.MaxValue && pf + LenOf(p) > nextStart)
                        {
                            SetProps(p, log, P("Length", Math.Max(1, nextStart - pf)));
                            swapClamped++;
                        }
                    }
                }

                log("実尺で再配置: ボイス " + voices.Count + "件(移動 " + moved + ")・他 " + others
                    + "件移動/" + stretched + "件伸縮/" + clamped + "件終端打切/" + laneClamped + "件レーン内打切/"
                    + swapClamped + "件入替打切・表情実尺 " + faceSynced + "件・立ち絵切替終端 " + portraitSynced
                    + "件・レーン集約 " + relaned + "件・gap " + gap + "f");
                return true;
            }
            catch (Exception ex) { log("再配置失敗(据え置き): " + ex.Message); return false; }
        }
    }
}
