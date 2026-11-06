using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace CesiumLoader.Overlay
{
    /// <summary>
    /// 把 ShopRelicPeekMod 产出的 FairyGUI BBCode 渲染成 WPF Run 序列。
    /// 支持标签：[color=#RRGGBB] [color=red] [b] [/b] [/color]，其它未知标签原样剥除保留文本。
    /// </summary>
    public static class BbCodeParser
    {
        private static readonly Regex TagRx = new Regex(@"\[(/?)([a-zA-Z]+)(?:=([^\]]*))?\]", RegexOptions.Compiled);

        private sealed class Style
        {
            public string Color;
            public bool Bold;
        }

        public static void SetRichText(RichTextBox box, string bbText, int fontSize)
        {
            var doc = new FlowDocument();
            doc.PagePadding = new Thickness(0);
            doc.FontSize = Math.Max(12, Math.Min(28, fontSize));
            doc.FontFamily = new FontFamily("Microsoft YaHei, Segoe UI");
            doc.Foreground = Brushes.White;
            doc.Background = Brushes.Transparent;
            doc.LineHeight = fontSize * 1.35;

            if (string.IsNullOrEmpty(bbText))
            {
                box.Document = doc;
                return;
            }

            // 按行切分，每行一个 Paragraph；空行保留小间距
            string[] lines = bbText.Replace("\r\n", "\n").Split('\n');
            foreach (string rawLine in lines)
            {
                var p = new Paragraph();
                p.Margin = new Thickness(0);
                p.LineHeight = fontSize * 1.35;
                if (rawLine.Length == 0)
                {
                    p.Inlines.Add(new Run(" "));
                }
                else
                {
                    AppendLineRuns(p, rawLine);
                }
                doc.Blocks.Add(p);
            }
            box.Document = doc;
        }

        private static void AppendLineRuns(Paragraph p, string line)
        {
            var styleStack = new Stack<Style>();
            styleStack.Push(new Style { Color = "#FFFFFF", Bold = false });

            int pos = 0;
            foreach (Match m in TagRx.Matches(line))
            {
                if (m.Index > pos)
                {
                    var cur = styleStack.Peek();
                    AddRun(p, line.Substring(pos, m.Index - pos), cur);
                }
                ApplyTag(m, styleStack);
                pos = m.Index + m.Length;
            }
            if (pos < line.Length)
            {
                AddRun(p, line.Substring(pos), styleStack.Peek());
            }
            if (p.Inlines.Count == 0) p.Inlines.Add(new Run(" "));
        }

        private static void AddRun(Paragraph p, string text, Style s)
        {
            // FairyGUI 下两格全角空格是行首缩进，WPF 照原样保留可读性
            var run = new Run(text);
            run.Foreground = ParseBrush(s.Color);
            run.FontWeight = s.Bold ? FontWeights.Bold : FontWeights.Normal;
            p.Inlines.Add(run);
        }

        private static void ApplyTag(Match m, Stack<Style> stack)
        {
            bool closing = m.Groups[1].Value == "/";
            string name = m.Groups[2].Value.ToLowerInvariant();
            string arg = m.Groups.Count > 3 ? m.Groups[3].Value.Trim() : null;
            if (name == "b")
            {
                if (!closing)
                {
                    var cur = Clone(stack.Peek());
                    cur.Bold = true;
                    stack.Push(cur);
                }
                else if (stack.Count > 1)
                {
                    // 找到最近的 bold 标记弹出（简单处理：顶栈若是 bold 则弹）
                    if (stack.Peek().Bold) stack.Pop();
                }
            }
            else if (name == "color")
            {
                if (!closing)
                {
                    var cur = Clone(stack.Peek());
                    if (!string.IsNullOrEmpty(arg)) cur.Color = arg;
                    stack.Push(cur);
                }
                else if (stack.Count > 1)
                {
                    stack.Pop();
                }
            }
        }

        private static Style Clone(Style s) => new Style { Color = s.Color, Bold = s.Bold };

        private static Brush ParseBrush(string color)
        {
            if (string.IsNullOrWhiteSpace(color)) return Brushes.White;
            try
            {
                // #RRGGBB 或 #AARRGGBB，或 CSS 命名色
                var conv = new BrushConverter();
                return (Brush)conv.ConvertFromInvariantString(color.Trim());
            }
            catch { return Brushes.White; }
        }
    }
}
