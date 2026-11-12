using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Gameplay;
using Core.Unit;
using FairyGUI;
using GameLogic;
using Tools;
using UI;

namespace PvePanelMod
{
    /// <summary>左栏一行（一个玩家）要显示的数据。</summary>
    internal sealed class PlayerRow
    {
        public long Id;
        public int Slot;              // 0 起：0 = 一楼
        public bool IsSelf;
        public string Nick;
        public string HeadUrl;        // 借官方左栏的头像地址（带皮肤后缀）
        public int Hp, MaxHp, Gold, Hand, Star, SkillCd;
        public bool Knocked;          // 被击倒：头像变灰
        public bool Away;             // 离线 / 挂机：buff 栏前加电脑标记
        public string StateDebug;     // 击倒 / 离线判断用到的原始字段，状态变化时写进日志核对
        public readonly List<BuffIcon> Buffs = new List<BuffIcon>();

        /// <summary>用来判断「有没有变化」：没变就不重画。</summary>
        public string Key()
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(Id).Append('|').Append(Slot).Append('|').Append(HeadUrl).Append('|').Append(Hp).Append('/').Append(MaxHp)
              .Append('|').Append(Gold).Append('|').Append(Hand).Append('|').Append(Star).Append('|').Append(SkillCd)
              .Append('|').Append(Knocked).Append('|').Append(Away);
            foreach (var b in Buffs) sb.Append('|').Append(b.Icon).Append('x').Append(b.Layers).Append('r').Append(b.KeepRound);
            return sb.ToString();
        }
    }

    internal struct BuffIcon
    {
        public int Id;
        public string Icon;
        public int KeepRound;
        public int Layers;
    }

    /// <summary>
    /// 从游戏里读左栏数据。数据来源都在 2026-11-12 用探针实测过（见 pve-panel/信息收集.md「探针实测结果」）：
    ///   星级 = Property.level，技能 CD = BattlePlayerData.SkillCD，血量/星币 = Property.HP/maxHP/gold，
    ///   buff 名字图标 = StaticConfigure.Buff（只取 IsShow 的），头像 = 官方左栏 loader_Icon 的 url。
    /// 所有读取都兜底：读不到就给默认值，绝不抛异常。
    /// </summary>
    internal static class PanelData
    {
        /// <summary>当前是不是该显示面板：PVE、已经进入对局。</summary>
        public static bool ShouldShow()
        {
            Players.MatchInfo m;
            return Players.TryGetMatch(out m) && m.IsPve && m.InBattle;
        }

        /// <summary>按楼层（Slot）排好的 4 个玩家。怪物没有昵称，排除掉。</summary>
        public static List<PlayerRow> Read(Dictionary<int, string> headBySlot)
        {
            var rows = new List<PlayerRow>();
            foreach (var pd in Players.All())
            {
                try
                {
                    if (pd?.player == null) continue;
                    string nick = Players.SafeNick(pd);
                    if (nick == null) continue;
                    var p = pd.player;
                    var prop = pd.Property;
                    var row = new PlayerRow
                    {
                        Id = p.Id,
                        Slot = p.Slot,
                        Nick = nick,
                        IsSelf = Players.IsSelf(p.Id),
                        Hp = SafeInt(() => prop.HP.Value),
                        MaxHp = SafeInt(() => prop.maxHP),
                        Gold = SafeInt(() => prop.gold.Value),
                        Star = SafeInt(() => prop.level.Value),
                        SkillCd = SafeInt(() => pd.SkillCD),
                        Hand = Players.HandCount(p.Id),
                    };
                    if (row.MaxHp <= 0) { row.Hp = SafeInt(() => p.Hero.Hp); row.MaxHp = SafeInt(() => p.Hero.MaxHp); }
                    row.Knocked = SafeBool(() => prop.HealthState == HealthState.Dead) || (row.MaxHp > 0 && row.Hp <= 0);
                    // 只看实时字段：Player.OffLine、Property.Online。Hero.OfflineType 是「曾经掉过线」的历史记录，
                    // 重连回来也不清（2026-11-12 实测：重连后 OfflineType=WithReconnect，但 Online=True、OffLine=False），不能用。
                    row.Away = SafeBool(() => p.OffLine) || SafeBool(() => !prop.Online.Value);
                    row.StateDebug = "Hp=" + row.Hp + "/" + row.MaxHp + " HealthState=" + SafeStr(() => prop.HealthState.ToString())
                        + " Online=" + SafeStr(() => prop.Online.ToString()) + " Player.OffLine=" + SafeStr(() => p.OffLine.ToString())
                        + " OfflineType=" + SafeStr(() => p.Hero.OfflineType.ToString()) + " TimeOutNum=" + SafeStr(() => p.Hero.TimeOutNum.ToString())
                        + " ReviveRoundNum=" + SafeStr(() => p.Hero.ReviveRoundNum.ToString())
                        + " TotalDie=" + SafeStr(() => pd.TotalDie.ToString());
                    string head;
                    if (headBySlot != null && headBySlot.TryGetValue(row.Slot, out head)) row.HeadUrl = head;
                    else row.HeadUrl = "UT_Hero_ProfilePhoto_" + SafeInt(() => p.Hero.HeroId);
                    ReadBuffs(row);
                    rows.Add(row);
                }
                catch { }
            }
            rows.Sort((a, b) => a.Slot.CompareTo(b.Slot));
            return rows;
        }

        private static void ReadBuffs(PlayerRow row)
        {
            foreach (var b in Players.BuffsOf(row.Id))
            {
                try
                {
                    BuffInfoConfigure cfg;
                    if (StaticConfigure.Buff == null || !StaticConfigure.Buff.InfoDict.TryGetValue(b.BuffId, out cfg) || cfg == null) continue;
                    // 游戏自己不显示的 buff（大多是筹码的内部计数）我们也不显示
                    if (!cfg.IsShow || string.IsNullOrEmpty(cfg.Icon)) continue;
                    row.Buffs.Add(new BuffIcon { Id = b.BuffId, Icon = cfg.Icon, Layers = b.Layers, KeepRound = b.KeepRound });
                }
                catch { }
            }
        }

        /// <summary>
        /// 官方左栏每个玩家的头像地址，按楼层编号。官方每行有 txt_Slot（"1st"…"4th"）和 com_Head/loader_Icon。
        /// </summary>
        public static Dictionary<int, string> ReadOfficialHeads(List<GComponent> officialRows)
        {
            var map = new Dictionary<int, string>();
            foreach (var r in officialRows)
            {
                try
                {
                    var slotText = (r.GetChild("txt_Slot") as GTextField)?.text;
                    int slot = SlotFromText(slotText);
                    if (slot < 0) continue;
                    var icon = FindByName(r, "loader_Icon", 0) as GLoader;
                    if (icon != null && !string.IsNullOrEmpty(icon.url)) map[slot] = icon.url;
                }
                catch { }
            }
            return map;
        }

        /// <summary>官方左栏里的玩家行：同时有 list_Level 和 txt_PlayerName 的组件。</summary>
        public static List<GComponent> FindOfficialRows()
        {
            var list = new List<GComponent>();
            var panel = FindPanel();
            if (panel != null) Collect(panel, list, 0);
            return list;
        }

        public static UIBattleInfoPanel FindPanel()
        {
            var root = GRoot.inst;
            if (root == null) return null;
            for (int i = 0; i < root.numChildren; i++)
                if (root.GetChildAt(i) is UIBattleInfoPanel p) return p;
            return null;
        }

        private static void Collect(GObject o, List<GComponent> list, int depth)
        {
            if (!(o is GComponent c) || depth > 9) return;
            if (c.GetChild("list_Level") != null && c.GetChild("txt_PlayerName") != null) { list.Add(c); return; }
            for (int i = 0; i < c.numChildren; i++) Collect(c.GetChildAt(i), list, depth + 1);
        }

        private static GObject FindByName(GObject o, string name, int depth)
        {
            if (o == null || depth > 6) return null;
            if (o.name == name) return o;
            if (o is GComponent c)
                for (int i = 0; i < c.numChildren; i++)
                {
                    var r = FindByName(c.GetChildAt(i), name, depth + 1);
                    if (r != null) return r;
                }
            return null;
        }

        private static int SlotFromText(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            char c = s[0];
            return c >= '1' && c <= '4' ? c - '1' : -1;
        }

        private static int SafeInt(Func<int> f) { try { return f(); } catch { return 0; } }
        private static bool SafeBool(Func<bool> f) { try { return f(); } catch { return false; } }
        private static string SafeStr(Func<string> f) { try { return f(); } catch { return "?"; } }
    }
}
