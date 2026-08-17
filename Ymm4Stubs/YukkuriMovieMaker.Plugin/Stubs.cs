// CI(GitHub Actions)用の YMM4 型スタブ。
// 目的: YMM4 本体が無い環境で KakiniwaYmm4Import.Tests をコンパイル・型ロードさせるだけ。
// テストは純ロジックのみで YMM4 実体には触れないため、メンバー本体は実装しない
// (呼ばれることがあれば NotImplementedException で即分かる)。
// 実 YMM4 での挙動確認はローカル(YMM4_DIR=実インストール先)で行う。
//
// 型の namespace は実 YMM4 と同じにしてある。どのスタブ DLL に属するかは
// 厳密でなくてよい(Tests の Reference は HintPath 直指定)ので、全型をこの
// 1 アセンブリに集約し、残り 3 つは空アセンブリにしている。

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace YukkuriMovieMaker.Plugin
{
    public interface IToolPlugin
    {
        string Name { get; }
        Type ViewModelType { get; }
        Type ViewType { get; }
    }

    public class ToolState
    {
    }

    public class CreateNewToolViewRequestedEventArgs : EventArgs
    {
    }

    public interface IToolViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        string Title { get; }
        bool CanSuspend { get; }
        event EventHandler<CreateNewToolViewRequestedEventArgs>? CreateNewToolViewRequested;
        ToolState SaveState();
        void LoadState(ToolState state);
    }
}

namespace YukkuriMovieMaker.Project
{
    /// <summary>実体はリフレクション経由でしか触らないので空でよい</summary>
    public class Timeline
    {
    }
}

namespace YukkuriMovieMaker.Project.Items
{
    public abstract class BaseItem
    {
        public virtual object GetClone() => throw new NotImplementedException("CI stub");
    }

    public class ImageItem : BaseItem { }
    public class AudioItem : BaseItem { }
    public class TextItem : BaseItem { }
    public class VideoItem : BaseItem { }
    public class VoiceItem : BaseItem { }
    public class TachieItem : BaseItem { }
    public class TachieFaceItem : BaseItem { }
}

namespace YukkuriMovieMaker.Settings
{
    public class ItemTemplate
    {
        public string? Name { get; set; }
        public List<YukkuriMovieMaker.Project.Items.BaseItem>? Items { get; set; }
    }

    public class ItemSettings
    {
        public static ItemSettings? Default => null;
        public ObservableCollection<ItemTemplate>? Templates => null;
    }
}
