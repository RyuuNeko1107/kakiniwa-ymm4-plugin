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
            var ok = new Button { Content = "実行", Padding = new Thickness(16, 4, 16, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var cancel = new Button { Content = "キャンセル", Padding = new Thickness(12, 4, 12, 4), IsCancel = true };
            ok.Click += (_, __) => { win.DialogResult = true; };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(buttons);

            var list = new StackPanel();
            var checks = new List<CheckBox>();
            foreach (var n in names)
            {
                var cb = new CheckBox { Content = n, IsChecked = true, Margin = new Thickness(0, 3, 0, 3) };
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
        readonly TextBox logBox;
        readonly StackPanel mappingPanel;
        readonly Button placeButton;
        readonly Button pauseButton;
        readonly Button stopButton;
        ImportControl? running;
        readonly TextBox baseLayerBox;
        readonly ComboBox startAtCombo;
        readonly Button registerPresetsButton;
        readonly List<Tuple<string, ComboBox>> combos = new List<Tuple<string, ComboBox>>();
        readonly ImportSettings settings = SettingsStore.Load(null);
        Pack? pack;
        string packDir = "";

        public ImportToolView()
        {
            MinWidth = 360;
            MinHeight = 300;

            var root = new DockPanel { Margin = new Thickness(8) };

            var top = new StackPanel();
            top.Children.Add(new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "β版です。取り込むと、いま開いているプロジェクトのタイムラインに項目が追加されます。元に戻す操作は確認できていないので、先にプロジェクトを保存しておいてください。",
            });
            var selectButton = new Button
            {
                Content = "1. 受け渡しパックのフォルダを選ぶ",
                Margin = new Thickness(0, 8, 0, 4),
                Padding = new Thickness(10, 6, 10, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            selectButton.Click += OnSelectClick;
            top.Children.Add(selectButton);

            top.Children.Add(new TextBlock
            {
                Text = "2. 話者の割り当て(パックのキャラ → YMM4のキャラクター)",
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
                Text = "配置先: 開始レイヤー ",
                VerticalAlignment = VerticalAlignment.Center,
            });
            baseLayerBox = new TextBox { Width = 44, Text = settings.BaseLayer.ToString() };
            destRow.Children.Add(baseLayerBox);
            destRow.Children.Add(new TextBlock
            {
                Text = "  開始位置 ",
                VerticalAlignment = VerticalAlignment.Center,
            });
            startAtCombo = new ComboBox();
            startAtCombo.Items.Add("先頭(0秒)");
            startAtCombo.Items.Add("カーソル位置から");
            startAtCombo.SelectedIndex = settings.StartAt == "cursor" ? 1 : 0;
            destRow.Children.Add(startAtCombo);
            top.Children.Add(destRow);

            placeButton = new Button
            {
                Content = "3. タイムラインへ配置",
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
                Content = "一時停止",
                Margin = new Thickness(8, 4, 0, 8),
                Padding = new Thickness(10, 6, 10, 6),
                IsEnabled = false,
                ToolTip = "配置を一時停止します(もう一度押すと再開)",
            };
            pauseButton.Click += OnPauseClick;
            stopButton = new Button
            {
                Content = "停止",
                Margin = new Thickness(8, 4, 0, 8),
                Padding = new Thickness(10, 6, 10, 6),
                IsEnabled = false,
                ToolTip = "配置をやめます。ここまで置いたアイテムはタイムラインに残ります",
            };
            stopButton.Click += OnStopClick;
            var placeRow = new StackPanel { Orientation = Orientation.Horizontal };
            placeRow.Children.Add(placeButton);
            placeRow.Children.Add(pauseButton);
            placeRow.Children.Add(stopButton);
            top.Children.Add(placeRow);

            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            var toolsRow = new StackPanel { Orientation = Orientation.Horizontal };
            var exportButton = new Button
            {
                Content = "YMM4のキャラ設定を書き出す(書き庭連携用)",
                Margin = new Thickness(0, 0, 8, 8),
                Padding = new Thickness(10, 4, 10, 4),
            };
            exportButton.Click += OnExportCharactersClick;
            toolsRow.Children.Add(exportButton);
            registerPresetsButton = new Button
            {
                Content = "書き庭の表情をYMM4のプリセットへ登録",
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
        }

        void OnExportCharactersClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var chars = Importer.GetYmm4Characters(Log);
                if (chars.Count == 0)
                {
                    Log("YMM4のキャラが取得できません(プロジェクトを開いていますか?)");
                    return;
                }
                var selected = SelectListDialog.Show("書き出すキャラを選ぶ", chars.Select(c => c.Name));
                if (selected == null || selected.Count == 0) return;
                var dialog = new SaveFileDialog
                {
                    Title = "書き庭連携用のキャラ設定JSONの保存先",
                    FileName = "ymm4キャラ設定.json",
                    Filter = "JSON|*.json",
                };
                if (dialog.ShowDialog() != true) return;
                Importer.ExportCharacters(dialog.FileName, selected, Log);
            }
            catch (Exception ex)
            {
                Log("例外: " + ex);
            }
        }

        void Log(string message)
        {
            logBox.AppendText(message + Environment.NewLine);
            logBox.ScrollToEnd();
        }

        void OnSelectClick(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "書き庭の受け渡しパック(timeline.json のあるフォルダ)を選択",
            };
            if (dialog.ShowDialog() != true) return;

            logBox.Clear();
            try
            {
                packDir = dialog.FolderName;
                pack = Importer.LoadPack(packDir, Log);
                if (pack == null) return;
                BuildMappingRows();
            }
            catch (Exception ex)
            {
                Log("例外: " + ex);
            }
        }

        void BuildMappingRows()
        {
            mappingPanel.Children.Clear();
            combos.Clear();
            if (pack == null) return;

            var options = Importer.GetYmm4Characters(Log);
            Log("YMM4登録キャラ: " + (options.Count == 0 ? "(取得できず)" : string.Join(", ", options.Select(o => o.Name))));

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
                    Display = "(名前そのまま: " + fallbackName + ")",
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
                        if (cc.Name == saved) { combo.SelectedIndex = i; found = true; break; }
                    }
                }
                if (!found)
                {
                    for (var i = 1; i < combo.Items.Count; i++)
                    {
                        var cc = (CharacterChoice)combo.Items[i]!;
                        if (cc.Name == fallbackName) { combo.SelectedIndex = i; break; }
                    }
                }
                row.Children.Add(combo);
                var warn = new TextBlock
                {
                    Text = " ⚠ 未割り当て(ボイスが付きません)",
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
                            Text = "└ 立ち絵「" + p.Id + "」 → ",
                            VerticalAlignment = VerticalAlignment.Center,
                            MinWidth = 90,
                        });
                        var pcombo = new ComboBox { MinWidth = 200 };
                        pcombo.Items.Add(new CharacterChoice
                        {
                            Display = "(上と同じキャラでPSD差し替え)",
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
                                if (cc.Name == pdefault) { pcombo.SelectedIndex = i; break; }
                            }
                        }
                        prow.Children.Add(pcombo);
                        mappingPanel.Children.Add(prow);
                        combos.Add(Tuple.Create(key, pcombo));
                    }
                }
            }
            placeButton.IsEnabled = true;
            registerPresetsButton.IsEnabled = true;
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
                    Log("PSDレイヤー付きの表情プリセットを持つキャラがパックにいません");
                    return;
                }
                var selected = SelectListDialog.Show("プリセットを反映するキャラを選ぶ", eligible);
                if (selected == null || selected.Count == 0) return;
                await Importer.RegisterExpressionPresets(pack, packDir, map, selected, Log);
            }
            catch (Exception ex)
            {
                Log("例外: " + ex);
            }
        }

        void OnPauseClick(object sender, RoutedEventArgs e)
        {
            var c = running;
            if (c == null) return;
            if (c.IsPaused)
            {
                c.Resume();
                pauseButton.Content = "一時停止";
                Log("再開しました");
            }
            else
            {
                c.Pause();
                pauseButton.Content = "再開";
                Log("一時停止しました(次の区切りで止まります。「再開」で続き、「停止」でやめます)");
            }
        }

        void OnStopClick(object sender, RoutedEventArgs e)
        {
            var c = running;
            if (c == null) return;
            c.Stop();
            stopButton.IsEnabled = false;
            pauseButton.IsEnabled = false;
            Log("停止します(次の区切りで止まります)…");
        }

        /// <summary>配置中の押せる/押せないを切り替える。二重実行(配置ボタン連打)も防ぐ。</summary>
        void SetRunning(bool on)
        {
            placeButton.IsEnabled = !on && pack != null;
            pauseButton.IsEnabled = on;
            stopButton.IsEnabled = on;
            pauseButton.Content = "一時停止";
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
                int parsedLayer;
                settings.BaseLayer = int.TryParse(baseLayerBox.Text, out parsedLayer)
                    ? Math.Max(0, parsedLayer) : 0;
                settings.StartAt = startAtCombo.SelectedIndex == 1 ? "cursor" : "zero";
                await Importer.Run(pack, packDir, map, settings, Log, control);

                // 今回の割り当てを記憶(次回は最初から選択済みにする)。
                // 「名前そのまま」(未割り当て)は記憶しない=次回また選び直せる
                foreach (var pair in map)
                {
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
                try
                {
                    System.Windows.MessageBox.Show(
                        "取り込みを停止しました。\n\n"
                        + "タイムラインには途中まで配置されたアイテムが残っています。"
                        + "残す必要が無ければ Ctrl+Z で戻すか、プロジェクトを保存せずに開き直してください。",
                        "書き庭の台本を取り込む",
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
                Log("例外: " + ex);
                try
                {
                    System.Windows.MessageBox.Show(
                        "取り込みの途中で失敗しました。\n\n"
                        + "タイムラインには途中まで配置されたアイテムが残っています。"
                        + "Ctrl+Z で戻すか、プロジェクトを保存せずに開き直してください。\n\n"
                        + "詳しい内容はダイアログ下部のログに出ています。\n\n"
                        + ex.Message,
                        "書き庭の台本を取り込む",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }
                catch { /* 通知の失敗で二重に落ちない */ }
            }
            finally
            {
                running = null;
                SetRunning(false);
            }
        }
    }
}
