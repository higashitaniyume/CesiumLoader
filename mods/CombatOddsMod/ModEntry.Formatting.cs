using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace CombatOddsMod
{
    public static partial class ModEntry
    {
        private static string Pct(double p) => (p * 100.0).ToString("0.#") + "%";

        // 右对齐到 5 位("100%"/" 62%"/"  8%"), 让防御/闪避两行的被击倒率竖排对齐, 一眼比大小。
        private static string PadPct(double p)
        {
            string s = Math.Round(p * 100.0).ToString("0") + "%";
            return s.Length >= 4 ? s : new string(' ', 4 - s.Length) + s;
        }

        private static string Fmt1(double v) => v.ToString("0.#");

        // ============================== 颜色/排版(UBB) ==============================
        // FairyGUI GTextField 开启 ubbEnabled 后支持 [color=#rgb]/[size=n]/[b]。
        // ColorHighlight=false 或送控制台时, 一律降级为纯文本(StripUbb)。
        private const string Blue = "5AA9FF";
        private const string Green = "5BE37A";
        private const string Gray = "BFBFBF";
        private const string Orange = "FFB454";
        private const string Red = "FF6B6B";
        private const string Yellow = "FFD24A";

        private static bool Color => _cfg != null && _cfg.ColorHighlight;

        /// <summary>按配置缩放字号(默认已是放大版)。</summary>
        private static int Sz(int baseSize)
        {
            double s = _cfg != null && _cfg.OverlayFontScale > 0 ? _cfg.OverlayFontScale : 1.0;
            int v = (int)Math.Round(baseSize * s);
            return v < 10 ? 10 : v;
        }

        private static string C(string text, string hex) => Color ? ("[color=#" + hex + "]" + text + "[/color]") : text;
        private static string Big(string text) => Color ? ("[size=" + Sz(40) + "]" + text + "[/size]") : text;
        private static string Dim(string text) => Color ? ("[size=" + Sz(22) + "][color=#" + Gray + "]" + text + "[/color][/size]") : text;

        /// <summary>被击倒率彩片: 颜色按危险度(绿低/黄中/红高), 推荐项(更安全)前加 ★。</summary>
        private static string KnockChip(double p, bool recommended)
        {
            string hex = p < 0.20 ? Green : (p < 0.50 ? Yellow : Red);
            string s = (recommended ? "★" : "") + PctPlain(p);
            return C(s, hex);
        }

        /// <summary>击杀率颜色: 越高越绿(对攻方是好事)。</summary>
        private static string KillColor(double p) => p >= 0.60 ? Green : (p >= 0.30 ? Yellow : Red);

        private static string PctPlain(double p) => (p * 100.0).ToString("0.#") + "%";

        /// <summary>buff 尾注: " (标记x2 / 狂暴)" —— 最多两项, 简短。</summary>
        private static string BuffTail(List<string> applied)
        {
            if (applied == null || applied.Count == 0) return string.Empty;
            var names = new List<string>();
            foreach (var a in applied)
            {
                // a 形如 "标记x2 +2" / "狂暴 +1"; 只取名字部分。
                int sp = a.IndexOf(' ');
                names.Add(sp > 0 ? a.Substring(0, sp) : a);
                if (names.Count >= 2) break;
            }
            return "  (" + string.Join(" / ", names.ToArray()) + ")";
        }

        private static readonly Regex _ubb =
            new Regex(@"\[/?(color|size|b|i|u)(=[^\]]*)?\]",
                RegexOptions.IgnoreCase);

        /// <summary>去掉 UBB 标签(送控制台/通知时用, 免得日志里全是 [color] 标记)。</summary>
        private static string StripUbb(string s) => string.IsNullOrEmpty(s) ? s : _ubb.Replace(s, string.Empty);

        private static string Indent(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return "│ " + s.Replace("\n", "\n│ ");
        }
    }
}
