// プラグインのエントリ(IToolPlugin 実装・ツール登録)と、取り込み設定の保存・読み込み。
// pwsh の Roslyn(Add-Type)で YMM4 同梱の .NET 10 アセンブリを参照してコンパイルする(build-plugin.ps1)。

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
    // ---------- プラグイン本体 ----------

    public class KakiniwaImportPlugin : IToolPlugin
    {
        public string Name { get { return "書き庭の台本を取り込む(β版)"; } }
        public Type ViewModelType { get { return typeof(ImportToolViewModel); } }
        public Type ViewType { get { return typeof(ImportToolView); } }
    }

    public class ImportToolViewModel : IToolViewModel
    {
        public string Title { get { return "書き庭 台本インポート"; } }
        public bool CanSuspend { get { return true; } }
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public event EventHandler<CreateNewToolViewRequestedEventArgs>? CreateNewToolViewRequested { add { } remove { } }
        public ToolState SaveState() { return new ToolState(); }
        public void LoadState(ToolState state) { }
    }

    // ---------- 設定の記憶(話者割り当て・立ち絵レイアウト) ----------

    public class ImportSettings
    {
        /// <summary>パックのキャラid → 前回選んだYMM4キャラ名</summary>
        public Dictionary<string, string> SpeakerMap { get; set; } = new Dictionary<string, string>();
        /// <summary>立ち絵の高さ(画面高さ比)。手編集可</summary>
        public double PortraitHeightRatio { get; set; } = 0.85;
        /// <summary>立ち絵の左右オフセット(画面幅比)。手編集可</summary>
        public double PortraitXRatio { get; set; } = 0.25;
        /// <summary>配置先の開始レイヤー(全アイテムをこの分だけ下へシフト)</summary>
        public int BaseLayer { get; set; } = 0;
        /// <summary>開始位置: "zero"=先頭(0秒) / "cursor"=タイムラインのカーソル位置</summary>
        public string StartAt { get; set; } = "zero";
    }

    public static class SettingsStore
    {
        static string PathOf()
        {
            // プラグインDLLの場所(<ymm4>\user\plugin\KakiniwaYmm4Import)から user\setting へ
            var dll = Assembly.GetExecutingAssembly().Location;
            var userDir = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(dll)!)!)!;
            return Path.Combine(userDir, "setting", "kakiniwa-import.json");
        }

        public static ImportSettings Load(Action<string>? log)
        {
            try
            {
                var path = PathOf();
                if (File.Exists(path))
                {
                    var s = JsonSerializer.Deserialize<ImportSettings>(File.ReadAllText(path),
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (s != null) return s;
                }
            }
            catch (Exception ex) { if (log != null) log("設定読み込み失敗: " + ex.Message); }
            return new ImportSettings();
        }

        public static void Save(ImportSettings settings, Action<string> log)
        {
            try
            {
                var path = PathOf();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(settings, new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                }));
                log("設定を保存: " + path);
            }
            catch (Exception ex) { log("設定保存失敗: " + ex.Message); }
        }
    }
}
