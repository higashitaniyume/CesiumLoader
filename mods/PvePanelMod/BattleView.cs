using System;
using System.Collections.Generic;
using System.Reflection;
using CesiumLoader.SDK.Runtime;
using FairyGUI;
using UnityEngine;

namespace PvePanelMod
{
    /// <summary>从战斗胜率助手的「信箱」CombatOddsMod.BattleOddsFeed 取出的一次战斗读数（拷贝成本 mod 自己的类型）。</summary>
    internal sealed class BattleOdds
    {
        public long Version;
        public string AttackerName = "", DefenderName = "";
        public bool SelfIsAttacker, SelfIsDefender;
        public bool ShowDefender, AttackerRolled, DefendBetter, DodgeBetter, DodgeSure, CannotFightBack;
        public int DefenderHp, AttackerFinalAtk;
        public double DefendKnockProb, DodgeKnockProb, WorstKnockProb;
        public bool ShowAttacker;
        public double KillProb;
        public int TargetHp, TypicalDamage;
        public readonly List<string> Notes = new List<string>();
    }

    /// <summary>
    /// 按名字反射读 CombatOddsMod.BattleOddsFeed，不声明硬依赖：战斗胜率助手没装 / 被关掉时，这里什么都读不到，面板其余部分照常工作。
    /// 计算全部由战斗胜率助手完成，本 mod 只负责换个样子显示（被击倒概率 → 存活概率）。
    /// </summary>
    internal sealed class BattleFeedReader
    {
        private Type _feed;
        private PropertyInfo _latest;
        private MethodInfo _claim;
        private DateTime _nextResolve = DateTime.MinValue;
        private readonly Dictionary<string, FieldInfo> _fields = new Dictionary<string, FieldInfo>();
        private BattleOdds _cached;
        private long _cachedVersion = -1;

        public bool Available => _latest != null;

        public BattleOdds Read()
        {
            if (!Resolve()) return null;
            object s;
            try { s = _latest.GetValue(null, null); } catch { return null; }
            if (s == null) { _cachedVersion = -1; _cached = null; return null; }
            long v = Get<long>(s, "Version");
            if (v == _cachedVersion && _cached != null) return _cached;
            var o = new BattleOdds
            {
                Version = v,
                AttackerName = Get<string>(s, "AttackerName") ?? "",
                DefenderName = Get<string>(s, "DefenderName") ?? "",
                SelfIsAttacker = Get<bool>(s, "SelfIsAttacker"),
                SelfIsDefender = Get<bool>(s, "SelfIsDefender"),
                ShowDefender = Get<bool>(s, "ShowDefender"),
                AttackerRolled = Get<bool>(s, "AttackerRolled"),
                DefendBetter = Get<bool>(s, "DefendBetter"),
                DodgeBetter = Get<bool>(s, "DodgeBetter"),
                DodgeSure = Get<bool>(s, "DodgeSure"),
                CannotFightBack = Get<bool>(s, "CannotFightBack"),
                DefenderHp = Get<int>(s, "DefenderHp"),
                AttackerFinalAtk = Get<int>(s, "AttackerFinalAtk"),
                DefendKnockProb = Get<double>(s, "DefendKnockProb"),
                DodgeKnockProb = Get<double>(s, "DodgeKnockProb"),
                WorstKnockProb = Get<double>(s, "WorstKnockProb"),
                ShowAttacker = Get<bool>(s, "ShowAttacker"),
                KillProb = Get<double>(s, "KillProb"),
                TargetHp = Get<int>(s, "TargetHp"),
                TypicalDamage = Get<int>(s, "TypicalDamage"),
            };
            var notes = Get<System.Collections.IEnumerable>(s, "Notes");
            if (notes != null) foreach (var n in notes) if (n != null) o.Notes.Add(n.ToString());
            _cached = o;
            _cachedVersion = v;
            return o;
        }

        /// <summary>告诉战斗胜率助手「战斗读数由我显示」，它就不画自己那个框（3 秒内没再声明会自动恢复）。</summary>
        public void Claim()
        {
            if (_claim == null) return;
            try { _claim.Invoke(null, null); } catch { }
        }

        private bool Resolve()
        {
            if (_latest != null) return true;
            if (DateTime.UtcNow < _nextResolve) return false;
            _nextResolve = DateTime.UtcNow.AddSeconds(5);   // 没装战斗胜率助手时，每 5 秒才再找一次
            try
            {
                _feed = RuntimeAssemblyService.FindTypeBySimpleName("BattleOddsFeed");
                if (_feed == null || _feed.Namespace != "CombatOddsMod") { _feed = null; return false; }
                _latest = _feed.GetProperty("Latest", BindingFlags.Public | BindingFlags.Static);
                _claim = _feed.GetMethod("ClaimDisplay", BindingFlags.Public | BindingFlags.Static);
            }
            catch { _latest = null; }
            return _latest != null;
        }

        private T Get<T>(object o, string name)
        {
            try
            {
                FieldInfo f;
                if (!_fields.TryGetValue(name, out f)) { f = o.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance); _fields[name] = f; }
                if (f == null) return default(T);
                var v = f.GetValue(o);
                return v is T t ? t : default(T);
            }
            catch { return default(T); }
        }
    }

    /// <summary>
    /// 战斗面板：挂在左栏正下方、同样从屏幕左边伸出来，只在战斗时出现（设计决定）。
    /// 原模组显示「被击倒概率」，这里换成「挺住（存活）概率」= 1 − 被击倒概率。
    /// 不用表情符号：游戏字体不一定有这些字形。
    /// </summary>
    internal sealed class BattleView : IDisposable
    {
        private const float Width = 380f, Pad = 14f, Border = 3f;
        private static readonly Color PanelColor = new Color(0.086f, 0.098f, 0.157f, 0.94f);
        private static readonly Color BorderColor = new Color(1f, 1f, 1f, 0.16f);
        private static readonly Color Well = new Color(0.051f, 0.059f, 0.094f, 1f);
        private static readonly Color Line = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color Soft = new Color(0.776f, 0.804f, 0.859f, 1f);
        private static readonly Color Muted = new Color(0.541f, 0.565f, 0.651f, 1f);
        private static readonly Color Blue = new Color(0.384f, 0.659f, 1f);
        private static readonly Color Green = new Color(0.357f, 0.89f, 0.478f);
        private static readonly Color Yellow = new Color(1f, 0.749f, 0.235f);
        private static readonly Color Red = new Color(1f, 0.42f, 0.42f);
        private static readonly Color Orange = new Color(1f, 0.706f, 0.329f);

        private GComponent _layer, _box;
        private float _k;
        private long _shownVersion = -1;

        public void Hide()
        {
            if (_layer != null && !_layer.isDisposed) _layer.visible = false;
        }

        public void Render(BattleOdds o, float topY)
        {
            var root = GRoot.inst;
            if (root == null || o == null) { Hide(); return; }
            float k = PanelScale.K(root);
            if (_layer == null || _layer.isDisposed || _layer.parent != root || Math.Abs(k - _k) > 0.001f)
            {
                Dispose();
                _k = k;
                _layer = new GComponent { name = "PvePanelBattle", sortingOrder = 25010, touchable = false };
                root.AddChild(_layer);
                _box = new GComponent { touchable = false };
                _layer.AddChild(_box);
                _shownVersion = -1;
            }
            if (o.Version != _shownVersion) { _shownVersion = o.Version; Draw(o); }
            _box.SetXY(0f, PanelScale.FitBottom(topY, _box.height));   // 放大后不超出屏幕底边
            if (!_layer.visible) _layer.visible = true;
        }

        private void Draw(BattleOdds o)
        {
            float k = _k, x0 = Pad * k;
            _box.RemoveChildren(0, -1, true);
            var items = new List<GObject>();
            var seps = new List<GGraph>();
            string atkName = ShortName(o.AttackerName), defName = ShortName(o.DefenderName);
            float y = 10f * k;

            // 顶行小字：谁打谁
            string who = o.SelfIsDefender ? "战斗 · " + defName + " 防守"
                       : o.SelfIsAttacker ? "战斗 · " + atkName + " 进攻"
                       : "观战 · " + atkName + " 打 " + defName;
            y = Row(items, who, 14f, Muted, false, x0, y) + 2f * k;

            if (o.ShowDefender)
            {
                if (o.AttackerRolled)
                {
                    string big = o.DefendBetter ? "该防御" : o.DodgeBetter ? (o.DodgeSure ? "必闪" : "该闪避") : "防闪都行";
                    Color bc = o.DefendBetter ? Blue : o.DodgeBetter ? Green : Soft;
                    y = Row(items, big, 28f, bc, true, x0, y) + 4f * k;
                    float cx = Chip(items, "防  挺住 ", 1 - o.DefendKnockProb, o.DefendBetter, x0, y);
                    Chip(items, "闪  挺住 ", 1 - o.DodgeKnockProb, o.DodgeBetter, cx + 10f * k, y);
                    y += 36f * k;
                    y = Row(items, defName + " " + o.DefenderHp + " 血  <  " + atkName + " " + o.AttackerFinalAtk + " 攻", 14f, Muted, false, x0, y);
                }
                else
                {
                    y = Row(items, "等对方投骰", 24f, Soft, true, x0, y) + 2f * k;
                    y = Row(items, defName + " " + o.DefenderHp + " 血，最坏情况挺住 " + Pct(1 - o.WorstKnockProb), 14f, Muted, false, x0, y);
                }
            }

            if (o.ShowDefender && o.ShowAttacker)
            {
                y += 6f * k;
                var sep = new GGraph { touchable = false };
                sep.SetXY(x0, y);
                items.Add(sep);
                seps.Add(sep);
                y += 8f * k;
            }

            if (o.ShowAttacker)
            {
                var kill = LeftPanel.Text(22f * k, Color.white, true);
                kill.UBBEnabled = true;
                kill.text = "击杀 [color=#" + Hex(KillColor(o.KillProb)) + "]" + Pct(o.KillProb) + "[/color]  [size=" + Mathf.RoundToInt(14f * k) + "][color=#8a90a6]（目标防御）[/color][/size]";
                kill.SetXY(x0, y);
                items.Add(kill);
                y += kill.textHeight + 2f * k;
                y = Row(items, atkName + " > " + defName + " " + o.TargetHp + " 血 · 预计伤害 " + o.TypicalDamage, 14f, Muted, false, x0, y);
            }

            if (o.CannotFightBack) y = Row(items, "（本场无法反击）", 14f, Muted, false, x0, y);
            foreach (var n in o.Notes) y = Row(items, n, 14f, Orange, false, x0, y);
            y += 10f * k;

            // 宽度跟着内容走：最少 Width，哪行字长就撑宽，不会再超出面板
            float w = Width * k;
            foreach (var it in items)
            {
                float right = it is GTextField tf ? it.x + tf.textWidth : it.x + it.width;
                if (right + x0 > w) w = right + x0;
            }
            foreach (var sep in seps) sep.DrawRect(w - 2 * x0, Mathf.Max(1f, k), 0, Color.clear, Line);

            // 底板：右边圆角，左边贴死屏幕边（和左栏同一种「从边上伸出来」）
            float b = Border * k, r = 18f * k;
            var border = new GGraph { touchable = false };
            border.DrawRoundRect(w, y, BorderColor, new[] { 0f, r, 0f, r });
            _box.AddChild(border);
            var bg = new GGraph { touchable = false };
            bg.SetXY(0f, b);
            bg.DrawRoundRect(w - b, y - 2 * b, PanelColor, new[] { 0f, r - b, 0f, r - b });
            _box.AddChild(bg);
            foreach (var it in items) _box.AddChild(it);
            _box.SetSize(w, y);
        }

        private float Row(List<GObject> items, string text, float size, Color color, bool bold, float x, float y)
        {
            var t = LeftPanel.Text(size * _k, color, bold);
            t.text = text;
            t.SetXY(x, y);
            items.Add(t);
            return y + t.textHeight;
        }

        /// <summary>「防 挺住 82% ★」小片：推荐的那个加绿框和星号；百分比颜色按存活率（高绿、中黄、低红）。返回右边界。</summary>
        private float Chip(List<GObject> items, string label, double survive, bool best, float x, float y)
        {
            float k = _k;
            var t = LeftPanel.Text(17f * k, Soft, true);
            t.UBBEnabled = true;
            t.text = label + "[color=#" + Hex(SurviveColor(survive)) + "]" + Pct(survive) + "[/color]" + (best ? " ★" : "");
            float w = t.textWidth + 20f * k, h = 32f * k;
            if (best)
            {
                var edge = new GGraph { touchable = false };
                edge.SetXY(x - 2f * k, y - 2f * k);
                edge.DrawRoundRect(w + 4f * k, h + 4f * k, Green, new[] { 10f * k, 10f * k, 10f * k, 10f * k });
                items.Add(edge);
            }
            var bg = new GGraph { touchable = false };
            bg.SetXY(x, y);
            bg.DrawRoundRect(w, h, Well, new[] { 8f * k, 8f * k, 8f * k, 8f * k });
            items.Add(bg);
            t.SetXY(x + 10f * k, y + (h - t.textHeight) / 2f);
            items.Add(t);
            return x + w;
        }

        /// <summary>名字超过 7 个字就截断加「…」，长昵称不会把面板撑出屏幕。</summary>
        private static string ShortName(string s) => string.IsNullOrEmpty(s) || s.Length <= 7 ? (s ?? "") : s.Substring(0, 7) + "…";

        private static Color SurviveColor(double p) => p >= 0.8 ? Green : p >= 0.5 ? Yellow : Red;
        private static Color KillColor(double p) => p >= 0.6 ? Green : p >= 0.3 ? Yellow : Red;
        private static string Pct(double p) => Math.Round(Math.Max(0, Math.Min(1, p)) * 100.0).ToString("0") + "%";
        private static string Hex(Color c) => ColorUtility.ToHtmlStringRGB(c);

        public void Dispose()
        {
            if (_layer != null && !_layer.isDisposed) _layer.Dispose();
            _layer = null; _box = null;
        }
    }
}
