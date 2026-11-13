using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using CesiumLoader.SDK.Gameplay;
using UI;

namespace PvePanelMod
{
    /// <summary>悬停小字的内容：名字 + 游戏里的说明（说明里的 {0}{1}… 用配置表的 Params 填上）。读不到就只给名字，绝不抛异常。</summary>
    internal static class TipText
    {
        private const string Gray = "#8a90a6";

        public static Tip Buff(int buffId, int layers, int keepRound)
        {
            string name = "buff " + buffId, desc = null;
            try
            {
                BuffInfoConfigure cfg;
                if (StaticConfigure.Buff != null && StaticConfigure.Buff.InfoDict.TryGetValue(buffId, out cfg) && cfg != null)
                {
                    name = Local(cfg.NameId, UIStringType.Buff) ?? name;
                    desc = Fill(Local(cfg.DescId, UIStringType.Buff), null);
                }
            }
            catch { }
            var extra = new List<string>();
            if (layers > 1) extra.Add(layers + " 层");
            if (keepRound > 0) extra.Add("还剩 " + keepRound + " 回合");
            return new Tip(name, Join(desc, extra));
        }

        public static Tip Card(int cardId, bool bought)
        {
            string name = Names.Card(cardId, false), desc = null;
            try
            {
                CardInfoConfigure cfg;
                if (StaticConfigure.Card != null && StaticConfigure.Card.InfoDict.TryGetValue(cardId, out cfg) && cfg != null)
                    desc = Fill(Local(cfg.DescId, UIStringType.Card), cfg.Params);
            }
            catch { }
            var extra = new List<string>();
            if (bought) extra.Add("已买");
            return new Tip(name, Join(desc, extra));
        }

        public static Tip Relic(int relicId)
        {
            string name = Names.Relic(relicId, false), desc = null, quality = null;
            try
            {
                RelicInfoConfigure cfg;
                if (StaticConfigure.Relic != null && StaticConfigure.Relic.InfoDict.TryGetValue(relicId, out cfg) && cfg != null)
                {
                    desc = Fill(Local(cfg.DescID, UIStringType.Relic), cfg.Params);
                    string q = cfg.RelicQualityType.ToString();
                    quality = q == "Blue" ? "普通" : q == "Purple" ? "稀有" : q == "Orange" ? "高级" : null;
                }
            }
            catch { }
            var extra = new List<string>();
            if (quality != null) extra.Add(quality);
            return new Tip(name, Join(desc, extra));
        }

        public static Tip Monster(string name, int count)
        {
            return new Tip(string.IsNullOrEmpty(name) ? "怪物" : name, count > 1 ? "[color=" + Gray + "]× " + count + "[/color]" : null);
        }

        private static string Join(string desc, List<string> extra)
        {
            string tail = extra.Count > 0 ? "[color=" + Gray + "]" + string.Join(" · ", extra.ToArray()) + "[/color]" : null;
            if (string.IsNullOrEmpty(desc)) return tail;
            return tail == null ? desc : desc + "\n" + tail;
        }

        private static string Local(int id, UIStringType type)
        {
            if (id == 0) return null;
            try { var s = UIHelper.GetLocal(id, type); return string.IsNullOrEmpty(s) ? null : s; } catch { return null; }
        }

        private static readonly Regex Named = new Regex(@"\{\s*[A-Za-z_][A-Za-z0-9_]*\s*=\s*([^{}]*?)\s*\}");

        /// <summary>
        /// 说明里的占位符有两种：{0}{1}… 用配置 Params 填；{range=6} 这种「参数名=数值」只留数值。
        /// 说明里还可能嵌 &lt;img src='ui://…'/&gt; 小图标，交给 Tooltip 的富文本控件去画。
        /// </summary>
        private static string Fill(string text, IList<int> ps)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0) return text;
            if (ps != null)
                for (int i = 0; i < ps.Count && i < 10; i++) text = text.Replace("{" + i + "}", ps[i].ToString());
            return Named.Replace(text, "$1");
        }
    }
}
