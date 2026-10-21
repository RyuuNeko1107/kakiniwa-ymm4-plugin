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
        // ★版数を名前に含める。YMM4のプラグイン一覧・ツールメニューでどの版が
        //   入っているか確認できるように(利用者から「本体でバージョン確認できるように」)
        public string Name { get { Tr.Preferred = SettingsStore.Load(null).Language; return Tr.T("menu.name", PackSchema.PluginVersion); } }
        public Type ViewModelType { get { return typeof(ImportToolViewModel); } }
        public Type ViewType { get { return typeof(ImportToolView); } }
    }

    public class ImportToolViewModel : IToolViewModel
    {
        // ★窓のタイトルバーに版数を出す。どこで撮ったスクリーンショットにも版が写るように
        public string Title { get { Tr.Preferred = SettingsStore.Load(null).Language; return Tr.T("window.title", PackSchema.PluginVersion); } }
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
        /// <summary>true にすると、ボイスの追加を従来の方式(MainModel.AddVoiceItemAsync・生成が非同期に進む)に戻す。
        /// 既定の false は公式の台本編集と同じ手順(タイムラインの外で作り、ResetLength で実尺を確定してから置く)。
        /// 窓には出さない保険の設定(うまくいかないときに kakiniwa-import.json で切り替える)</summary>
        public bool LegacyVoicePath { get; set; } = false;
        /// <summary>セリフのボイスを作る同時数。0=自動(既定。配置の前に全部を並列で作る。同時数は
        /// エンジンの種類ごとの表に従う・VoicePrefetch.cs)/ 1=1行ずつ(従来の、作っては置くの繰り返し)/
        /// 2以上=同時数の上限。窓には出さない保険の設定(kakiniwa-import.json で切り替える)</summary>
        public int VoiceConcurrency { get; set; } = KakiniwaYmm4Import.VoiceConcurrency.Auto;
        /// <summary>窓の表示言語: auto(YMM4に合わせる・既定) / 言語コード(ja・en・lang/ に置いた言語)</summary>
        public string Language { get; set; } = "auto";
        /// <summary>開始位置: "zero"=先頭(0秒) / "cursor"=タイムラインのカーソル位置</summary>
        public string StartAt { get; set; } = "zero";
        /// <summary>セリフのレイヤーの置き方: perCharacter(キャラごと・既定) / single(全員で1つ) / custom(キャラごとに指定)</summary>
        public string VoiceLayerMode { get; set; } = KakiniwaYmm4Import.VoiceLayerMode.PerCharacter;
        /// <summary>custom のとき: パックのキャラid → レイヤー番号(開始レイヤーを 0 とした相対番号)</summary>
        public Dictionary<string, int> VoiceLayers { get; set; } = new Dictionary<string, int>();
        /// <summary>セリフ間の隙間: pack(パックの値に従う・既定) / none(なし) / custom(秒数を指定)</summary>
        public string SerifGapMode { get; set; } = KakiniwaYmm4Import.SerifGapMode.Pack;
        /// <summary>SerifGapMode が custom のときの隙間(秒)</summary>
        public double SerifGapSeconds { get; set; } = 0.2;
        /// <summary>素材(アセット名)ごとのレイヤー固定。種類ごとの指定より優先。無い素材は種類の指定か自動</summary>
        public Dictionary<string, int> AssetLayers { get; set; } = new Dictionary<string, int>();
        /// <summary>素材の種類ごとのレイヤー固定: bg / bgm / se / stage / prop-slot / effect / window → 番号。無い種類は自動</summary>
        public Dictionary<string, int> KindLayers { get; set; } = new Dictionary<string, int>();
        /// <summary>書き庭が PSD のサイドカーへ書いた表情プリセットの記録(PSD の絶対パス|名前)。
        /// 同名のプリセットは、ここに載っているものだけ置き換える(利用者が YMM4 で作ったものを消さない)</summary>
        public List<string> OwnedPresets { get; set; } = new List<string>();
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

        /// <summary>直近の取り込みログ(伏せ字済み)の保存先。毎回上書きする(溜めない)。</summary>
        public static string LastLogPath()
            => Path.Combine(Path.GetDirectoryName(PathOf())!, "kakiniwa-import-last.log");

        /// <summary>取り込みログを伏せ字にして保存する。症状を報告するときに、このファイルを渡せるようにするため。</summary>
        public static void SaveLastLog(string log, Action<string>? onError)
        {
            try
            {
                var header = "書き庭 台本インポート プラグイン v" + PackSchema.PluginVersion
                    + " / " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                File.WriteAllText(LastLogPath(), LogRedactor.Compose(header, log), new System.Text.UTF8Encoding(true));
            }
            catch (Exception ex) { if (onError != null) onError("ログの保存に失敗: " + ex.Message); }
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
            catch (Exception ex) { if (log != null) log(Tr.T("log.settings.loadFail", ex.Message)); }
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
                log(Tr.T("log.settings.saved", path));
            }
            catch (Exception ex) { log(Tr.T("log.settings.saveFail", ex.Message)); }
        }
    }
}
