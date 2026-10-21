// 取り込みツールのUI(コード構築・XAML不使用): 対象選択ダイアログと話者割り当てビュー。

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
    // ---------- View(コード構築・XAML不使用) ----------

    /// <summary>話者割り当ての選択肢(YMM4登録キャラ or 名前そのまま)</summary>
    public class CharacterChoice
    {
        public string Display = "";
        public string Name = "";     // VoiceItem.CharacterName に入れる値
        public object? Model;        // YMM4 の Character モデル(取れた場合のみ)
        public override string ToString() { return Display; }
    }

    /// <summary>チェックボックス式の対象選択ダイアログ(既定=全選択)。キャンセルで null</summary>
    public static class SelectListDialog
    {
        public static HashSet<string>? Show(string title, IEnumerable<string> names)
            => Show(title, names, null, null);

        /// <param name="message">一覧の上に出す説明(null=無し)</param>
        /// <param name="initiallyChecked">最初に印を付けておくか(null=全部付ける)</param>
        public static HashSet<string>? Show(string title, IEnumerable<string> names,
            string? message, Func<string, bool>? initiallyChecked)
        {
            var win = new Window
            {
                Title = title,
                Width = 360,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current != null ? Application.Current.MainWindow : null,
                ResizeMode = ResizeMode.NoResize,
            };
            var root = new DockPanel { Margin = new Thickness(12) };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 10, 0, 0),
            };
            var ok = new Button { Content = Tr.T("ui.run"), Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = Tr.T("ui.cancel"), Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
            ok.Click += (_, __) => { win.DialogResult = true; };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            if (!string.IsNullOrEmpty(message))
            {
                var msg = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
                DockPanel.SetDock(msg, Dock.Top);
                root.Children.Add(msg);
            }
            var list = new StackPanel();
            var checks = new List<CheckBox>();
            foreach (var n in names)
            {
                var cb = new CheckBox
                {
                    Content = n,
                    IsChecked = initiallyChecked == null || initiallyChecked(n),
                    Margin = new Thickness(0, 3, 0, 3),
                };
                checks.Add(cb);
                list.Children.Add(cb);
            }
            root.Children.Add(new ScrollViewer
            {
                Content = list,
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            });
            win.Content = root;

            if (win.ShowDialog() != true) return null;
            var selected = new HashSet<string>();
            foreach (var cb in checks)
                if (cb.IsChecked == true && cb.Content is string s) selected.Add(s);
            return selected;
        }
    }

    public class ImportToolView : UserControl
    {
        TextBox logBox = null!;
        // 窓に出すログは伏せ字にする(スクリーンショットで共有されるため)。
        // 元の文面はここに持ち、保存するときに1回だけ伏せる(伏せ字を重ねて伏せると文字数表示が壊れる)。
        readonly System.Text.StringBuilder rawLog = new System.Text.StringBuilder();
        StackPanel mappingPanel = null!;
        Button placeButton = null!;
        Button pauseButton = null!;
        Button stopButton = null!;
        // 進捗バーと残り時間の目安(配置中だけ動く)
        ProgressBar progressBar = null!;
        TextBlock progressText = null!;
        ImportControl? running;
        TextBox baseLayerBox = null!;
        ComboBox startAtCombo = null!;
        ComboBox voiceModeCombo = null!;
        ComboBox gapModeCombo = null!;
        TextBox gapSecondsBox = null!;
        // 素材名 → レイヤー番号の入力欄(パックを読んだあとに、パックの素材から作る)
        readonly Dictionary<string, TextBox> assetLayerBoxes = new Dictionary<string, TextBox>();
        StackPanel assetPanel = new StackPanel { Margin = new Thickness(12, 2, 0, 2) };
        // キャラid → レイヤー番号の入力欄(「キャラごとに指定」のときだけ見せる)
        readonly Dictionary<string, TextBox> voiceLayerBoxes = new Dictionary<string, TextBox>();
        // 素材の種類 → レイヤー番号の入力欄(空=自動)
        readonly Dictionary<string, TextBox> kindLayerBoxes = new Dictionary<string, TextBox>();
        Button registerPresetsButton = null!;
        readonly List<Tuple<string, ComboBox>> combos = new List<Tuple<string, ComboBox>>();
        // 選択を「パックの名前(ymm4Name/name)との一致」で自動で付けただけで、利用者が選んでいない行。
        // ★パックの外の PSD へプリセットを書くかの確認で、これらは最初は印を付けない(2026-10-20c)
        readonly HashSet<string> autoPicked = new HashSet<string>();
        // 前回の記憶(settings.SpeakerMap)で付いただけの選択。記憶はパックのキャラ id で引くので、別のパックが
        // 同じ id を名乗ると、そのパックでも利用者の PC の PSD が選ばれた状態で開く。プリセットの書き先の確認では
        // 「この窓で利用者が選んだもの」とは見なさない(2026-10-22 監査)。記憶そのものは便利なので残す
        readonly HashSet<string> rememberedPicked = new HashSet<string>();
        readonly ImportSettings settings = SettingsStore.Load(null);
        Pack? pack;
        string packDir = "";

        // 言語の選択欄(0=自動、以降は Tr.AvailableLanguages の順)
        List<string> languageCodes = new List<string>();
        bool buildingUi;

        public ImportToolView()
        {
            Tr.Preferred = settings.Language;
            BuildContent();
        }

        /// <summary>窓の中身を作る。言語を切り替えたときは、これをもう一度呼んで作り直す。</summary>
        void BuildContent()
        {
            buildingUi = true;
            kindLayerBoxes.Clear();
            voiceLayerBoxes.Clear();
            assetLayerBoxes.Clear();
            assetPanel = new StackPanel { Margin = new Thickness(12, 2, 0, 2) };
            MinWidth = 360;
            MinHeight = 300;

            var root = new DockPanel { Margin = new Thickness(8) };

            var top = new StackPanel();
            // 版数は常に見える場所に置く(ログが流れても、窓の一部だけを撮っても写る)
            top.Children.Add(new TextBlock
            {
                Text = Tr.T("ui.header", PackSchema.PluginVersion,
                    typeof(VoiceItem).Assembly.GetName().Version?.ToString() ?? "?"),
                FontWeight = FontWeights.Bold,
                Margin = new Thickness(0, 0, 0, 4),
            });
            var langRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
            langRow.Children.Add(new TextBlock { Text = Tr.T("ui.language"), VerticalAlignment = VerticalAlignment.Center });
            var langCombo = new ComboBox();
            languageCodes = new List<string> { "auto" };
            languageCodes.AddRange(Tr.AvailableLanguages());
            langCombo.Items.Add(Tr.T("ui.language.auto"));
            foreach (var code in languageCodes.Skip(1)) langCombo.Items.Add(Tr.NativeName(code));
            var chosen = languageCodes.IndexOf(settings.Language ?? "auto");
            langCombo.SelectedIndex = chosen < 0 ? 0 : chosen;
            langCombo.SelectionChanged += (_, __) =>
            {
                if (buildingUi) return;
                if (running != null) { langCombo.SelectedIndex = Math.Max(0, languageCodes.IndexOf(settings.Language ?? "auto")); return; }
                OnLanguageChanged(languageCodes[Math.Max(0, langCombo.SelectedIndex)]);
            };
            langRow.Children.Add(langCombo);
            top.Children.Add(langRow);
            top.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = Tr.T("ui.beta"),
            });
            var selectButton = new Button
            {
                Content = Tr.T("ui.selectPack"),
                Margin = new Thickness(0, 8, 0, 4),
                Padding = new Thickness(10, 6, 10, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            selectButton.Click += OnSelectClick;
            top.Children.Add(selectButton);

            top.Children.Add(new TextBlock
            {
                Text = Tr.T("ui.speakers"),
                Margin = new Thickness(0, 4, 0, 2),
            });
            mappingPanel = new StackPanel { Margin = new Thickness(12, 0, 0, 4) };
            top.Children.Add(mappingPanel);

            var destRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 2, 0, 2),
            };
            destRow.Children.Add(new TextBlock
            {
                Text = Tr.T("ui.baseLayer"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            baseLayerBox = new TextBox { Width = 44, Text = settings.BaseLayer.ToString() };
            destRow.Children.Add(baseLayerBox);
            destRow.Children.Add(new TextBlock
            {
                Text = Tr.T("ui.startAt"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            startAtCombo = new ComboBox();
            startAtCombo.Items.Add(Tr.T("ui.startAt.zero"));
            startAtCombo.Items.Add(Tr.T("ui.startAt.cursor"));
            startAtCombo.SelectedIndex = settings.StartAt == "cursor" ? 1 : 0;
            destRow.Children.Add(startAtCombo);
            top.Children.Add(destRow);

            // レイヤーの置き方: セリフ(キャラごと/1つにまとめる/キャラごとに指定)と、素材の種類ごとの固定番号。
            // 番号は「開始レイヤーを0とした相対番号」。空欄は自動(使うものだけ上から詰める)。
            var voiceRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            voiceRow.Children.Add(new TextBlock { Text = Tr.T("ui.voiceLayer"), VerticalAlignment = VerticalAlignment.Center });
            voiceModeCombo = new ComboBox();
            voiceModeCombo.Items.Add(Tr.T("ui.voice.perCharacter"));
            voiceModeCombo.Items.Add(Tr.T("ui.voice.single"));
            voiceModeCombo.Items.Add(Tr.T("ui.voice.custom"));
            voiceModeCombo.SelectedIndex = VoiceLayerMode.Normalize(settings.VoiceLayerMode) == VoiceLayerMode.Single ? 1
                : VoiceLayerMode.Normalize(settings.VoiceLayerMode) == VoiceLayerMode.Custom ? 2 : 0;
            voiceModeCombo.SelectionChanged += (_, __) => UpdateVoiceLayerBoxes();
            voiceRow.Children.Add(voiceModeCombo);
            top.Children.Add(voiceRow);

            // セリフとセリフの間の隙間: パックの設定に従う / なし(公式の台本編集のように詰める) / 秒数を指定
            var gapRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
            gapRow.Children.Add(new TextBlock { Text = Tr.T("ui.gap"), VerticalAlignment = VerticalAlignment.Center });
            gapModeCombo = new ComboBox();
            gapModeCombo.Items.Add(Tr.T("ui.gap.pack"));
            gapModeCombo.Items.Add(Tr.T("ui.gap.none"));
            gapModeCombo.Items.Add(Tr.T("ui.gap.custom"));
            gapModeCombo.SelectedIndex = SerifGapMode.Normalize(settings.SerifGapMode) == SerifGapMode.None ? 1
                : SerifGapMode.Normalize(settings.SerifGapMode) == SerifGapMode.Custom ? 2 : 0;
            gapRow.Children.Add(gapModeCombo);
            gapSecondsBox = new TextBox
            {
                Width = 44,
                Margin = new Thickness(6, 0, 0, 0),
                Text = settings.SerifGapSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
            };
            var gapUnit = new TextBlock { Text = Tr.T("ui.seconds"), VerticalAlignment = VerticalAlignment.Center };
            gapRow.Children.Add(gapSecondsBox);
            gapRow.Children.Add(gapUnit);
            gapModeCombo.SelectionChanged += (_, __) =>
            {
                var custom = gapModeCombo.SelectedIndex == 2;
                gapSecondsBox.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
                gapUnit.Visibility = gapSecondsBox.Visibility;
            };
            gapSecondsBox.Visibility = gapModeCombo.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            gapUnit.Visibility = gapSecondsBox.Visibility;
            top.Children.Add(gapRow);

            var kindPanel = new StackPanel { Margin = new Thickness(12, 2, 0, 2) };
            var kinds = new[]
            {
                Tuple.Create("bg", Tr.T("ui.kind.bg")), Tuple.Create("bgm", Tr.T("ui.kind.bgm")), Tuple.Create("se", Tr.T("ui.kind.se")),
                Tuple.Create("stage", Tr.T("ui.kind.stage")), Tuple.Create("prop-slot", Tr.T("ui.kind.propSlot")),
                Tuple.Create("effect", Tr.T("ui.kind.effect")), Tuple.Create("window", Tr.T("ui.kind.window")),
            };
            foreach (var k in kinds)
            {
                var krow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                krow.Children.Add(new TextBlock { Text = k.Item2, MinWidth = 110, VerticalAlignment = VerticalAlignment.Center });
                int saved;
                var box = new TextBox
                {
                    Width = 44,
                    Text = settings.KindLayers != null && settings.KindLayers.TryGetValue(k.Item1, out saved) ? saved.ToString() : "",
                };
                krow.Children.Add(box);
                krow.Children.Add(new TextBlock { Text = Tr.T("ui.auto"), VerticalAlignment = VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.Gray });
                kindPanel.Children.Add(krow);
                kindLayerBoxes[k.Item1] = box;
            }
            top.Children.Add(new Expander
            {
                Header = Tr.T("ui.kindsHeader"),
                Content = kindPanel,
                Margin = new Thickness(0, 2, 0, 2),
            });
            top.Children.Add(new Expander
            {
                Header = Tr.T("ui.assetsHeader"),
                Content = assetPanel,
                Margin = new Thickness(0, 2, 0, 2),
            });

            placeButton = new Button
            {
                Content = Tr.T("ui.place"),
                Margin = new Thickness(0, 4, 0, 8),
                Padding = new Thickness(10, 6, 10, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
                IsEnabled = false,
            };
            placeButton.Click += OnPlaceClick;
            // 配置の一時停止・停止(配置中だけ押せる)。長い台本は AddItems → ボイス生成待ち →
            // 再配置と続くので、途中でやめる手段が要る(2026-08-27 要望)
            pauseButton = new Button
            {
                Content = Tr.T("ui.pause"),
                Margin = new Thickness(8, 4, 0, 8),
                Padding = new Thickness(10, 6, 10, 6),
                IsEnabled = false,
                ToolTip = Tr.T("ui.pauseTip"),
            };
            pauseButton.Click += OnPauseClick;
            stopButton = new Button
            {
                Content = Tr.T("ui.stop"),
                Margin = new Thickness(8, 4, 0, 8),
                Padding = new Thickness(10, 6, 10, 6),
                IsEnabled = false,
                ToolTip = Tr.T("ui.stopTip"),
            };
            stopButton.Click += OnStopClick;
            var placeRow = new StackPanel { Orientation = Orientation.Horizontal };
            placeRow.Children.Add(placeButton);
            placeRow.Children.Add(pauseButton);
            placeRow.Children.Add(stopButton);
            // ★同じ行に置く(行を増やさない)。進み具合は ImportProgress.cs が段階の重みで全体に直す
            progressBar = new ProgressBar
            {
                Minimum = 0, Maximum = 1, Value = 0, Width = 160, Height = 14,
                Margin = new Thickness(12, 4, 0, 8), VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            // ★文言は次の行へ(ボタンと同じ行だと、窓の幅によって「残り約…」が切れて見えなかった・実機で確認)
            progressText = new TextBlock
            {
                Margin = new Thickness(0, 0, 0, 6),
                TextWrapping = TextWrapping.Wrap,
            };
            placeRow.Children.Add(progressBar);
            top.Children.Add(placeRow);
            top.Children.Add(progressText);

            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            var toolsRow = new StackPanel { Orientation = Orientation.Horizontal };
            var exportButton = new Button
            {
                Content = Tr.T("ui.exportChars"),
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 4, 10, 4),
            };
            exportButton.Click += OnExportCharactersClick;
            toolsRow.Children.Add(exportButton);
            registerPresetsButton = new Button
            {
                Content = Tr.T("ui.registerPresets"),
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(10, 4, 10, 4),
                IsEnabled = false,
            };
            registerPresetsButton.Click += OnRegisterPresetsClick;
            toolsRow.Children.Add(registerPresetsButton);
            top.Children.Add(toolsRow);

            logBox = new TextBox
            {
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            };
            root.Children.Add(logBox);

            Content = root;
            buildingUi = false;
        }

        /// <summary>窓の入力欄の値を設定へ写す(配置の前・言語の切り替えで作り直す前に使う)。</summary>
        void ReadUiIntoSettings()
        {
            int parsedLayer;
            settings.BaseLayer = int.TryParse(baseLayerBox.Text, out parsedLayer)
                ? Math.Max(0, parsedLayer) : 0;
            settings.StartAt = startAtCombo.SelectedIndex == 1 ? "cursor" : "zero";
            settings.VoiceLayerMode = voiceModeCombo.SelectedIndex == 1 ? VoiceLayerMode.Single
                : voiceModeCombo.SelectedIndex == 2 ? VoiceLayerMode.Custom : VoiceLayerMode.PerCharacter;
            settings.VoiceLayers = ReadLayerBoxes(voiceLayerBoxes);
            settings.KindLayers = ReadLayerBoxes(kindLayerBoxes);
            settings.AssetLayers = ReadLayerBoxes(assetLayerBoxes);
            settings.SerifGapMode = gapModeCombo.SelectedIndex == 1 ? SerifGapMode.None
                : gapModeCombo.SelectedIndex == 2 ? SerifGapMode.Custom : SerifGapMode.Pack;
            double gapSec;
            if (double.TryParse(gapSecondsBox.Text.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out gapSec)
                || double.TryParse(gapSecondsBox.Text.Trim(), out gapSec))
                settings.SerifGapSeconds = SerifGapMode.Resolve(SerifGapMode.Custom, gapSec, 0);
        }

        /// <summary>言語を切り替える。入力済みの値・読み込んだパック・ログはそのまま、文言だけ作り直す。</summary>
        void OnLanguageChanged(string code)
        {
            ReadUiIntoSettings();
            // まだ記憶していない話者の選択も、作り直しのあとに残るよう設定へ写す
            foreach (var pair in combos)
            {
                // 自動で付けただけの選択は記憶しない(作り直しでも同じ名前の一致で付く)
                if (autoPicked.Contains(pair.Item1)) continue;
                var choice = pair.Item2.SelectedItem as CharacterChoice;
                if (choice != null && choice.Model != null) settings.SpeakerMap[pair.Item1] = choice.Name;
            }
            settings.Language = code;
            Tr.Preferred = code;
            SettingsStore.Save(settings, _ => { });
            var hadPack = pack != null;
            BuildContent();
            if (hadPack) BuildMappingRows(true);
            logBox.Text = LogRedactor.RedactForDisplay(rawLog.ToString());
            logBox.ScrollToEnd();
        }

        void OnExportCharactersClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var chars = Importer.GetYmm4Characters(Log);
                if (chars.Count == 0)
                {
                    Log(Tr.T("log.noChars"));
                    return;
                }
                var selected = SelectListDialog.Show(Tr.T("ui.pickExportChars"), chars.Select(c => c.Name));
                if (selected == null || selected.Count == 0) return;
                var dialog = new SaveFileDialog
                {
                    Title = Tr.T("ui.saveCharsTitle"),
                    FileName = Tr.T("ui.saveCharsFile"),
                    Filter = "JSON|*.json",
                };
                if (dialog.ShowDialog() != true) return;
                Importer.ExportCharacters(dialog.FileName, selected, Log);
            }
            catch (Exception ex)
            {
                Log(Tr.T("log.exception", ex));
            }
        }

        void Log(string message)
        {
            rawLog.AppendLine(message);
            logBox.AppendText(LogRedactor.RedactForDisplay(message) + Environment.NewLine);
            logBox.ScrollToEnd();
        }

        void OnSelectClick(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = Tr.T("ui.pickPackTitle"),
            };
            if (dialog.ShowDialog() != true) return;

            logBox.Clear();
            rawLog.Clear();
            try
            {
                packDir = dialog.FolderName;
                var loadWatch = System.Diagnostics.Stopwatch.StartNew();
                pack = Importer.LoadPack(packDir, Log);
                Log(Tr.T("log.stage.load", loadWatch.ElapsedMilliseconds));
                if (pack == null) return;
                BuildMappingRows();
                ApplyPackLayers(pack.Layers);
            }
            catch (Exception ex)
            {
                Log(Tr.T("log.exception", ex));
            }
        }

        void BuildMappingRows(bool quiet = false)
        {
            mappingPanel.Children.Clear();
            combos.Clear();
            autoPicked.Clear();
            rememberedPicked.Clear();
            voiceLayerBoxes.Clear();
            BuildAssetRows();
            if (pack == null) return;

            var options = Importer.GetYmm4Characters(Log);
            if (!quiet)
                Log(Tr.T("log.chars", options.Count == 0 ? Tr.T("log.charsNone") : string.Join(", ", options.Select(o => o.Name))));

            foreach (var c in pack.Characters)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
                row.Children.Add(new TextBlock
                {
                    Text = c.Name + " → ",
                    VerticalAlignment = VerticalAlignment.Center,
                    MinWidth = 90,
                });
                var combo = new ComboBox { MinWidth = 200 };
                var fallbackName = c.Ymm4Name ?? c.Name;
                combo.Items.Add(new CharacterChoice
                {
                    Display = Tr.T("ui.sameName", fallbackName),
                    Name = fallbackName,
                    Model = null,
                });
                foreach (var opt in options) combo.Items.Add(opt);

                // 既定選択: ①前回保存した割り当て → ②ymm4Name(無ければ name)との完全一致
                string? saved;
                settings.SpeakerMap.TryGetValue(c.Id, out saved);
                combo.SelectedIndex = 0;
                var found = false;
                if (saved != null)
                {
                    for (var i = 1; i < combo.Items.Count; i++)
                    {
                        var cc = (CharacterChoice)combo.Items[i]!;
                        if (cc.Name == saved) { combo.SelectedIndex = i; found = true; rememberedPicked.Add(c.Id); break; }
                    }
                }
                if (!found)
                {
                    for (var i = 1; i < combo.Items.Count; i++)
                    {
                        var cc = (CharacterChoice)combo.Items[i]!;
                        if (cc.Name == fallbackName) { combo.SelectedIndex = i; autoPicked.Add(c.Id); break; }
                    }
                }
                var mainKey = c.Id;
                combo.SelectionChanged += (_, __) => { autoPicked.Remove(mainKey); rememberedPicked.Remove(mainKey); };
                row.Children.Add(combo);
                var vbox = new TextBox { Width = 36, Margin = new Thickness(6, 0, 0, 0), ToolTip = Tr.T("ui.voiceBoxTip") };
                int vsaved;
                if (settings.VoiceLayers != null && settings.VoiceLayers.TryGetValue(c.Id, out vsaved)) vbox.Text = vsaved.ToString();
                voiceLayerBoxes[c.Id] = vbox;
                row.Children.Add(vbox);
                var warn = new TextBlock
                {
                    Text = Tr.T("ui.unassigned"),
                    Foreground = System.Windows.Media.Brushes.OrangeRed,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var updateWarn = (Action)(() =>
                {
                    var sel = combo.SelectedItem as CharacterChoice;
                    warn.Visibility = sel != null && sel.Model != null ? Visibility.Collapsed : Visibility.Visible;
                });
                combo.SelectionChanged += (_, __) => updateWarn();
                updateWarn();
                row.Children.Add(warn);
                mappingPanel.Children.Add(row);
                combos.Add(Tuple.Create(c.Id, combo));

                // 立ち絵ごとの割り当て行: PSDファイルが異なる立ち絵を複数持つキャラだけ出す。
                // 別PSDの立ち絵は、その立ち絵タイプの合うYMM4キャラを個別に選ぶ(割り当て済み
                // キャラへのPSD強制上書きはYMM4クラッシュの原因・2026-08-09)。
                // 「上と同じ」を選んだ立ち絵は従来どおりPSD差し替えで配置される。
                var psdPortraits = (c.Portraits ?? new List<PackPortrait>())
                    .Where(p => p.Psd != null && !string.IsNullOrEmpty(p.Psd.Path)).ToList();
                if (psdPortraits.Select(p => p.Psd!.Path).Distinct().Count() >= 2)
                {
                    foreach (var p in psdPortraits)
                    {
                        var key = c.Id + "/" + p.Id;
                        var prow = new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Margin = new Thickness(16, 2, 0, 2),
                        };
                        prow.Children.Add(new TextBlock
                        {
                            Text = Tr.T("ui.portraitRow", p.Id),
                            VerticalAlignment = VerticalAlignment.Center,
                            MinWidth = 90,
                        });
                        var pcombo = new ComboBox { MinWidth = 200 };
                        pcombo.Items.Add(new CharacterChoice
                        {
                            Display = Tr.T("ui.portraitSame"),
                            Name = "",
                            Model = null,
                        });
                        foreach (var opt in options) pcombo.Items.Add(opt);
                        pcombo.SelectedIndex = 0;
                        // 既定選択: ①前回保存 → ②パックの Ymm4Name 一致 → ③上と同じ
                        string? psaved;
                        settings.SpeakerMap.TryGetValue(key, out psaved);
                        var pdefault = psaved ?? p.Ymm4Name;
                        if (!string.IsNullOrEmpty(pdefault))
                        {
                            for (var i = 1; i < pcombo.Items.Count; i++)
                            {
                                var cc = (CharacterChoice)pcombo.Items[i]!;
                                if (cc.Name == pdefault)
                                {
                                    pcombo.SelectedIndex = i;
                                    if (psaved == null) autoPicked.Add(key); // パックの ymm4Name で付けただけ
                                    else rememberedPicked.Add(key);           // 前回の記憶で付いただけ
                                    break;
                                }
                            }
                        }
                        pcombo.SelectionChanged += (_, __) => { autoPicked.Remove(key); rememberedPicked.Remove(key); };
                        prow.Children.Add(pcombo);
                        mappingPanel.Children.Add(prow);
                        combos.Add(Tuple.Create(key, pcombo));
                    }
                }
            }
            placeButton.IsEnabled = true;
            registerPresetsButton.IsEnabled = true;
            UpdateVoiceLayerBoxes();
        }

        /// <summary>パックの `layers`(書き庭側で決めたレイヤー配置)を窓の入力欄の初期値にする。
        /// 窓で変えれば、そちらが取り込みに使われる。`layers` の無い古いパックでは何もしない。</summary>
        void ApplyPackLayers(PackLayers? layers)
        {
            if (layers == null) return;
            var mode = layers.VoiceMode();
            voiceModeCombo.SelectedIndex = mode == VoiceLayerMode.Single ? 1 : mode == VoiceLayerMode.Custom ? 2 : 0;
            var voice = layers.CleanVoiceLayers();
            foreach (var kv in voiceLayerBoxes)
                kv.Value.Text = voice.TryGetValue(kv.Key, out var n) ? n.ToString() : "";
            var kinds = layers.CleanKinds();
            foreach (var kv in kindLayerBoxes)
                kv.Value.Text = kinds.TryGetValue(kv.Key, out var n) ? n.ToString() : "";
            UpdateVoiceLayerBoxes();
            Log(Tr.T("log.layersApplied"));
        }

        /// <summary>パックの素材(背景・BGM・効果音・小物・エフェクト・窓)を1つずつ並べ、レイヤー番号の欄を作る。</summary>
        void BuildAssetRows()
        {
            assetPanel.Children.Clear();
            assetLayerBoxes.Clear();
            if (pack == null) return;
            var seen = new HashSet<string>();
            var kindName = new Dictionary<string, string>
            {
                ["bg"] = Tr.T("ui.kind.bg"), ["bgm"] = Tr.T("ui.kind.bgm"), ["se"] = Tr.T("ui.kind.se"), ["prop"] = Tr.T("ui.kind.prop"),
                ["effect"] = Tr.T("ui.kind.effect"), ["window"] = Tr.T("ui.kind.window"),
            };
            Action<string, string?> add = (kind, name) =>
            {
                if (string.IsNullOrEmpty(name) || !seen.Add(name!)) return;
                var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
                row.Children.Add(new TextBlock { Text = kindName[kind] + ": " + name, MinWidth = 220, VerticalAlignment = VerticalAlignment.Center });
                int saved;
                var box = new TextBox
                {
                    Width = 44,
                    Text = settings.AssetLayers != null && settings.AssetLayers.TryGetValue(name!, out saved) ? saved.ToString() : "",
                };
                row.Children.Add(box);
                row.Children.Add(new TextBlock { Text = Tr.T("ui.auto"), VerticalAlignment = VerticalAlignment.Center, Foreground = System.Windows.Media.Brushes.Gray });
                assetPanel.Children.Add(row);
                assetLayerBoxes[name!] = box;
            };
            foreach (var e in pack.Events)
            {
                if (kindName.ContainsKey(e.Type) && e.Asset != null) add(e.Type, e.Asset.Name);
                if (e.Type == "serif" && e.Se != null) foreach (var s in e.Se) add("se", s.Name);
            }
            if (assetPanel.Children.Count == 0)
                assetPanel.Children.Add(new TextBlock { Text = Tr.T("ui.noAssets"), Foreground = System.Windows.Media.Brushes.Gray });
        }

        /// <summary>「キャラごとに指定する」のときだけ、話者の行にレイヤー番号の欄を出す。</summary>
        void UpdateVoiceLayerBoxes()
        {
            var show = voiceModeCombo.SelectedIndex == 2;
            foreach (var b in voiceLayerBoxes.Values) b.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>入力欄から「番号 ≥ 0 の整数だけ」を取り出す(空・不正は自動=入れない)。</summary>
        static Dictionary<string, int> ReadLayerBoxes(Dictionary<string, TextBox> boxes)
        {
            var d = new Dictionary<string, int>();
            foreach (var kv in boxes)
            {
                int n;
                if (int.TryParse(kv.Value.Text.Trim(), out n) && n >= 0) d[kv.Key] = n;
            }
            return d;
        }

        async void OnRegisterPresetsClick(object sender, RoutedEventArgs e)
        {
            if (pack == null) return;
            try
            {
                var map = new Dictionary<string, CharacterChoice>();
                foreach (var pair in combos)
                {
                    var choice = pair.Item2.SelectedItem as CharacterChoice;
                    if (choice != null) map[pair.Item1] = choice;
                }
                var eligible = pack.Characters
                    .Where(c => Importer.AllExpressions(c).Any(x => x.Psd != null && x.Psd.Layers.Count > 0))
                    .Select(c => c.Name)
                    .ToList();
                if (eligible.Count == 0)
                {
                    Log(Tr.T("log.noPresetChars"));
                    return;
                }
                var selected = SelectListDialog.Show(Tr.T("ui.pickPresetChars"), eligible);
                if (selected == null || selected.Count == 0) return;
                var owned = new HashSet<string>(settings.OwnedPresets ?? new List<string>());
                var picked = Importer.NotChosenInWindow(autoPicked, rememberedPicked);
                await Importer.RegisterExpressionPresets(pack, packDir, map, selected, Log,
                    targets => ConfirmPresetTargets(targets, picked), owned);
                settings.OwnedPresets = owned.OrderBy(x => x, StringComparer.Ordinal).ToList();
                SettingsStore.Save(settings, Log);
            }
            catch (Exception ex)
            {
                Log(Tr.T("log.exception", ex));
            }
        }

        /// <summary>書き先の PSD を一覧で見せ、書いてよいものだけを返す(取りやめは null)。
        /// パックの外の PSD は、利用者が窓の行で選んだものだけ最初から印を付ける。</summary>
        static ICollection<Importer.PresetTarget>? ConfirmPresetTargets(
            IReadOnlyList<Importer.PresetTarget> targets, ISet<string> autoPicked)
        {
            var byLine = new Dictionary<string, Importer.PresetTarget>();
            var lines = new List<string>();
            for (var i = 0; i < targets.Count; i++)
            {
                var t = targets[i];
                var line = (i + 1) + ". " + Path.GetFileName(t.PsdPath) + " — " + t.Label
                    + (t.InPack ? "" : "(" + Tr.T("ui.presetOutsidePack") + ")");
                byLine[line] = t;
                lines.Add(line);
            }
            var chosen = SelectListDialog.Show(Tr.T("ui.confirmPresetTitle"), lines,
                Tr.T("ui.confirmPresetMessage"),
                line => Importer.PresetTargetCheckedByDefault(byLine[line], autoPicked));
            if (chosen == null) return null;
            return chosen.Select(l => byLine[l]).ToList();
        }

        /// <summary>進捗の知らせを表示へ(UI スレッド)。残り時間は分かるときだけ出す。</summary>
        void ShowProgress(ImportProgressReport r)
        {
            // 並列に作ったボイスの知らせは、終わった順と届く順が前後しうる。戻る向きの知らせは捨てる
            if (r.Fraction < progressBar.Value && r.Stage != ImportStage.Prepare) return;
            progressBar.Value = r.Fraction;
            if (r.Stage == ImportStage.Done)
            {
                progressText.Text = Tr.T("progress.done", ImportProgressMath.FormatElapsed(r.ElapsedSeconds ?? 0));
                return;
            }
            var text = Tr.T("progress.stage." + r.Stage);
            if (r.Total > 0) text += " " + r.Done + "/" + r.Total;
            if (r.Stage == ImportStage.Voices && r.Done < r.Total)
            {
                if (r.RemainingSeconds == null) text += Tr.T("progress.estimating");
                else
                {
                    var (kind, m, s) = ImportProgressMath.RoundRemaining(r.RemainingSeconds.Value);
                    text += Tr.T("progress.remaining." + kind, m, s);
                }
            }
            progressText.Text = text;
        }

        void OnPauseClick(object sender, RoutedEventArgs e)
        {
            var c = running;
            if (c == null) return;
            if (c.IsPaused)
            {
                c.Resume();
                pauseButton.Content = Tr.T("ui.pause");
                Log(Tr.T("log.resumed"));
            }
            else
            {
                c.Pause();
                pauseButton.Content = Tr.T("ui.resume");
                Log(Tr.T("log.paused"));
            }
        }

        void OnStopClick(object sender, RoutedEventArgs e)
        {
            var c = running;
            if (c == null) return;
            c.Stop();
            stopButton.IsEnabled = false;
            pauseButton.IsEnabled = false;
            Log(Tr.T("log.stopping"));
        }

        /// <summary>配置中の押せる/押せないを切り替える。二重実行(配置ボタン連打)も防ぐ。</summary>
        void SetRunning(bool on)
        {
            placeButton.IsEnabled = !on && pack != null;
            pauseButton.IsEnabled = on;
            stopButton.IsEnabled = on;
            pauseButton.Content = Tr.T("ui.pause");
        }

        async void OnPlaceClick(object sender, RoutedEventArgs e)
        {
            if (pack == null || running != null) return;
            var control = new ImportControl();
            running = control;
            SetRunning(true);
            try
            {
                var map = new Dictionary<string, CharacterChoice>();
                foreach (var pair in combos)
                {
                    var choice = pair.Item2.SelectedItem as CharacterChoice;
                    if (choice != null) map[pair.Item1] = choice;
                }
                ReadUiIntoSettings();
                progressBar.Value = 0;
                progressBar.Visibility = Visibility.Visible;
                progressText.Text = Tr.T("progress.stage.Prepare");
                // Progress<T> は作ったスレッド(UI)で受け取る。ボイスを並列で作る別スレッドからの知らせもここへ戻る
                var progress = new Progress<ImportProgressReport>(ShowProgress);
                // 料金・回数の制限がかかる声で作る前の確認(はい/いいえ)
                Func<string, bool> confirm = msg => System.Windows.MessageBox.Show(msg, Tr.T("msg.title"),
                    System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;
                await Importer.Run(pack, packDir, map, settings, Log, control, progress, confirm);

                // 今回の割り当てを記憶(次回は最初から選択済みにする)。
                // 「名前そのまま」(未割り当て)は記憶しない=次回また選び直せる
                foreach (var pair in map)
                {
                    // 自動で付けただけの選択は記憶しない(次回も同じ名前の一致で付く)。
                    // ★記憶すると「利用者が選んだ」と区別できなくなる(プリセットの書き先の確認で使う)
                    if (autoPicked.Contains(pair.Key)) continue;
                    if (pair.Value.Model != null) settings.SpeakerMap[pair.Key] = pair.Value.Name;
                    else settings.SpeakerMap.Remove(pair.Key);
                }
                SettingsStore.Save(settings, Log);
            }
            catch (ImportStoppedException ex)
            {
                // 利用者が自分で止めた。失敗ではないので警告にはしないが、
                // 「途中まで置いたものが残る」ことは同じなので、そこは同じ言葉で伝える
                Log(ex.Message);
                progressText.Text = Tr.T("progress.stopped");
                try
                {
                    System.Windows.MessageBox.Show(
                        Tr.T("msg.stopped"),
                        Tr.T("msg.title"),
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
                catch { /* 通知の失敗で二重に落ちない */ }
            }
            catch (Exception ex)
            {
                // ★ログ欄に1行出すだけでは気づけない。ここは取り込み中ずっとメッセージが
                //   流れる欄なので、末尾に「例外: …」が増えても利用者は成功したと思って
                //   閉じてしまう。しかも**半端に配置されたアイテムはタイムラインに残る**。
                //   利用者のプロジェクトを書き換えている以上、失敗ははっきり伝える。
                Log(Tr.T("log.exception", ex));
                try
                {
                    System.Windows.MessageBox.Show(
                        Tr.T("msg.failed", ex.Message),
                        Tr.T("msg.title"),
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }
                catch { /* 通知の失敗で二重に落ちない */ }
            }
            finally
            {
                running = null;
                SetRunning(false);
                // 成功でも失敗でも、直近のログを(伏せ字にして)ファイルに残す
                SettingsStore.SaveLastLog(rawLog.ToString(), null);
            }
        }
    }
}
