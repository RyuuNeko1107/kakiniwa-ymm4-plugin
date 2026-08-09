// YMM4のPSDレイヤーパス実形式と、書き庭の「/」区切りパスの相互変換。

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
        // YMM4のPSDレイヤーパスの実形式(実機のymm4キャラ設定書き出しで確認):
        //   各階層 = U+001C + レイヤー名 + U+001D + 兄弟番号(既定0) を連結
        const char LayerNameOpen = '\u001C';
        const char LayerNameClose = '\u001D';
        static readonly System.Text.RegularExpressions.Regex LayerSegRe =
            new System.Text.RegularExpressions.Regex("\u001C([^\u001D]*)\u001D\\d*");

        /// <summary>書き庭の「/」区切りパス → YMM4実形式</summary>
        static string ToYmm4LayerPath(string slashPath)
        {
            return string.Concat(slashPath.Split('/')
                .Select(seg => LayerNameOpen + seg + LayerNameClose + "0"));
        }

        /// <summary>YMM4実形式 → 書き庭の「/」区切り(形式外の文字列はそのまま返す)</summary>
        static string FromYmm4LayerPath(string raw)
        {
            var ms = LayerSegRe.Matches(raw);
            if (ms.Count == 0) return raw;
            return string.Join("/", ms.Cast<System.Text.RegularExpressions.Match>()
                .Select(m2 => m2.Groups[1].Value));
        }

        /// <summary>YMM4形式のレイヤーパス一覧を書き庭の「/」区切りへ逆変換</summary>
        static List<string> ConvertPathsToSlash(object? listObj)
        {
            var result = new List<string>();
            var en = listObj as System.Collections.IEnumerable;
            if (en == null) return result;
            foreach (var o in en)
            {
                var s = o as string;
                if (string.IsNullOrEmpty(s)) continue;
                result.Add(FromYmm4LayerPath(s!));
            }
            return result;
        }

        static int CountSegments(string ymm4Path)
        {
            var c = 0;
            foreach (var ch in ymm4Path)
                if (ch == LayerNameOpen) c++;
            return c;
        }

        static string LeafNameOf(string ymm4Path)
        {
            var open = ymm4Path.LastIndexOf(LayerNameOpen);
            var close = ymm4Path.LastIndexOf(LayerNameClose);
            return open >= 0 && close > open ? ymm4Path.Substring(open + 1, close - open - 1) : ymm4Path;
        }
    }
}
