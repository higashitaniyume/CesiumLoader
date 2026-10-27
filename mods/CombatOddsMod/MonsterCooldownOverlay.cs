using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Logging;
using FairyGUI;
using GameLogic;
using Tools;
using UnityEngine;

namespace CombatOddsMod
{
    /// <summary>
    /// 把怪物的主动技能冷却显示在游戏自己的怪物名字/信息条(<c>UICom_AttrInfo</c>)旁边。
    ///
    /// 为什么不是另起一个悬浮标签: 游戏已经为每只怪物在棋盘上挂了名字、编号、血量、攻防和
    /// buff 图标, 直接写进这个组件可以跟着它一起移动、一起隐藏, 不额外占屏。
    ///
    /// 数据: 见 <see cref="MonsterCooldownTable"/> —— 服务器只在怪物释放技能时下发本次冷却,
    /// 之后按轮 -1 由本地推算; 因此这是"释放时校准 + 轮次推算"的显示, 不是逐帧服务器值。
    /// </summary>
    internal sealed class MonsterCooldownOverlay
    {
        private const float TickInterval = 0.1f;
        private const float Gap = 6f;
        private const int DefaultNameFontSize = 26;

        private sealed class Badge
        {
            public long PlayerId;
            public GObject Parent;
            public GTextField Text;
            public int LastRemaining = int.MinValue;
            public int LastCap = int.MinValue;
            public bool GeometryLogged;
        }

        private readonly MonsterCooldownTable _table = new MonsterCooldownTable();
        private readonly Dictionary<long, Badge> _badges = new Dictionary<long, Badge>();
        private readonly object _gate = new object();
        private readonly List<KeyValuePair<long, int>> _pendingCasts = new List<KeyValuePair<long, int>>();

        private BattleLogic _battle;
        private int _lastRound;
        private bool _enabled = true;
        private bool _showAll;
        private bool _started;
        private bool _warned;
        private bool _logDetail;
        private float _offsetX;
        private float _offsetY;

        public void Start()
        {
            if (_started) return;
            try { GameEvents.SkillUsed += OnSkillUsed; }
            catch (Exception e) { WarnOnce("怪物 CD 事件订阅失败: " + e.Message); }
            Timers.inst.Add(TickInterval, 0, Refresh);
            _started = true;
        }

        public void Dispose()
        {
            if (_started) Timers.inst.Remove(Refresh);
            _started = false;
            try { GameEvents.SkillUsed -= OnSkillUsed; } catch { }
            lock (_gate) _pendingCasts.Clear();
            ClearBadges();
            _table.Clear();
            _battle = null;
        }

        /// <summary>开关只改标记: 配置热重载可能来自后台线程, 真正的隐藏发生在主线程刷新里。</summary>
        public void SetEnabled(bool enabled) { _enabled = enabled; }

        /// <summary>是否连普通/精英怪物的战斗技能也显示(默认只显示 BOSS 与带主动技能的怪物)。</summary>
        public void SetShowAll(bool showAll) { _showAll = showAll; }

        /// <summary>把释放/显示值与几何信息写进加载器日志(排查位置/数值用)。</summary>
        public void SetLogDetail(bool enabled) { _logDetail = enabled; }

        /// <summary>相对"名字/血量那一行"右侧的微调(像素)。</summary>
        public void SetOffset(double x, double y)
        {
            _offsetX = (float)x;
            _offsetY = (float)y;
        }

        private void OnSkillUsed(long playerId, int skillId)
        {
            if (playerId == 0) return;
            lock (_gate) _pendingCasts.Add(new KeyValuePair<long, int>(playerId, skillId));
        }

        private void Refresh(object unused)
        {
            try { RefreshCore(); }
            catch (Exception e) { WarnOnce("怪物 CD 显示失败: " + e.Message); }
        }

        private void RefreshCore()
        {
            var battle = CurrentBattle();
            if (!ReferenceEquals(battle, _battle))
            {
                _battle = battle;
                _lastRound = 0;
                _table.Clear();
                ClearBadges();
                lock (_gate) _pendingCasts.Clear();
            }
            if (battle == null || battle.PlayerDatas == null)
            {
                HideAll();
                return;
            }

            int round = CurrentRound();
            // 同一局内轮次只增不减; 变小说明已经换局(同一个 BattleLogic 实例被复用)。
            if (_lastRound > 0 && round < _lastRound)
            {
                _table.Clear();
                ClearBadges();
                lock (_gate) _pendingCasts.Clear();
            }
            _lastRound = round;
            ResolvePendingCasts(battle, round);

            if (!_enabled)
            {
                HideAll();
                return;
            }

            var seen = new HashSet<long>();
            var players = battle.PlayerDatas;
            for (int i = 0; i < players.Count; i++)
            {
                var data = players[i];
                if (data == null || data.player == null || data.Property == null) continue;
                if (data.characterType != CharacterType.Monster) continue;

                long id = data.player.Id;
                if (id == 0) continue;
                if (SafeHp(data) <= 0)
                {
                    _table.Remove(id);
                    HideBadge(id);
                    continue;
                }
                // 普通/精英怪物的"战斗技能"多数就是它的普通攻击(例如玩具恐龙的杂音攻击),
                // 拿它当 CD 会出现"没开大 CD 也在变"。默认只显示 BOSS 和真正带主动技能的怪物,
                // 想看全部可以在配置里打开 ShowMonsterCdForAllMonsters。
                if (!_showAll && !ShouldShow(data))
                {
                    _table.Remove(id);
                    HideBadge(id);
                    continue;
                }

                int skillId = SafeSkillId(data);
                var skill = SkillConfig(skillId);
                // 没有主动技能或配表没有冷却: 不显示(没东西可倒数)。
                if (skill == null || skill.Round <= 0 || skill.Round > SkillCooldownRules.MaxPlausible)
                {
                    _table.Remove(id);
                    HideBadge(id);
                    continue;
                }

                // 没有释放记录时按"就绪"起步: 不用 Hero.SkillCds 当种子 —— 那份快照在客户端
                // 不会随释放更新, 拿它会让怪物没开技能也显示一个在倒数的 CD。
                if (!_table.Has(id)) _table.Seed(id, skillId, 0, skill.Round, round);
                int remaining, cap;
                if (!_table.TryGet(id, round, out remaining, out cap))
                {
                    HideBadge(id);
                    continue;
                }

                var info = AttrInfoOf(data);
                if (info == null)
                {
                    HideBadge(id);
                    continue;
                }

                seen.Add(id);
                Show(id, info, remaining, cap, round);
            }

            if (_badges.Count > 0)
            {
                var stale = new List<long>();
                foreach (var pair in _badges)
                    if (!seen.Contains(pair.Key)) stale.Add(pair.Key);
                for (int i = 0; i < stale.Count; i++) HideBadge(stale[i]);
            }
        }

        private void ResolvePendingCasts(BattleLogic battle, int round)
        {
            KeyValuePair<long, int>[] casts;
            lock (_gate)
            {
                if (_pendingCasts.Count == 0) return;
                casts = _pendingCasts.ToArray();
                _pendingCasts.Clear();
            }

            for (int i = 0; i < casts.Length; i++)
            {
                long id = casts[i].Key;
                BattlePlayerData data;
                try { data = battle.GetPlayerDataById(id); }
                catch { continue; }
                if (data == null || data.player == null || data.characterType != CharacterType.Monster) continue;

                int castSkillId = casts[i].Value;
                // 只认该怪物当前战斗主动技能的释放帧。怪物还会用别的技能(召唤/追击等),
                // 那些帧不会重置主动技能冷却; 以前把它们也记成"开大", 会出现没开大 CD 却变了。
                int activeSkillId = SafeSkillId(data);
                if (activeSkillId == 0 || castSkillId != activeSkillId)
                {
#if COMBATODDS_DEBUG_PROBES
                    SdkLog.Info("CombatOdds", "[怪物CD] 忽略释放 player=" + id + " cast=" + castSkillId +
                        " active=" + activeSkillId + " round=" + round);
#endif
                    continue;
                }

                var skill = SkillConfig(activeSkillId);
                int applied = SafeActiveCd(data);
                if (applied <= 0 && skill != null) applied = skill.Round;
                if (applied <= 0 || applied > SkillCooldownRules.MaxPlausible) continue;

                _table.ApplyCast(id, activeSkillId, applied, round);
                Log("释放 player=" + id + " skill=" + activeSkillId + " applied=" + applied + " round=" + round);
#if COMBATODDS_DEBUG_PROBES
                SdkLog.Info("CombatOdds", "[怪物CD] 开大 player=" + id + " skill=" + activeSkillId +
                    " applied=" + applied + " round=" + round);
#endif
            }
        }

        private void Show(long id, UI.UICom_AttrInfo info, int remaining, int cap, int round)
        {
            var badge = EnsureBadge(id, info);
            if (badge == null) return;

            badge.Text.text = SkillCooldownText.Format(remaining, cap);
            badge.Text.visible = true;
            Position(badge, info);

            if (badge.LastRemaining != remaining || badge.LastCap != cap)
            {
                badge.LastRemaining = remaining;
                badge.LastCap = cap;
                Log("显示 player=" + id + " value=" + remaining + "/" + cap + " round=" + round);
#if COMBATODDS_DEBUG_PROBES
                SdkLog.Info("CombatOdds", "[怪物CD] 数值 player=" + id + " value=" + remaining + "/" + cap +
                    " round=" + round);
#endif
            }
        }

        private Badge EnsureBadge(long id, UI.UICom_AttrInfo info)
        {
            Badge badge;
            if (_badges.TryGetValue(id, out badge))
            {
                if (badge.Text != null && !badge.Text.isDisposed && ReferenceEquals(badge.Parent, info) &&
                    ReferenceEquals(badge.Text.parent, info))
                    return badge;
                DisposeBadge(badge);
            }

            try
            {
                var text = new GTextField
                {
                    name = "cesium-monster-cd",
                    touchable = false,
                    autoSize = AutoSizeType.Both,
                    UBBEnabled = true,
                };
                var format = new TextFormat();
                var source = info.txt_Name != null && !info.txt_Name.isDisposed ? info.txt_Name.textFormat : null;
                if (source != null && !string.IsNullOrEmpty(source.font)) format.font = source.font;
                // 比怪物名字小一号, 白字黑描边, 保证压在棋盘/血条上也清晰。
                int nameSize = source != null && source.size > 0 ? source.size : DefaultNameFontSize;
                format.size = Math.Max(12, (int)Math.Round(nameSize * 0.65f));
                format.bold = true;
                format.color = Color.white;
                format.outline = 2;
                format.outlineColor = Color.black;
                text.textFormat = format;
                info.AddChild(text);
                text.SetXY(0f, 0f);

                badge = new Badge { PlayerId = id, Parent = info, Text = text };
                _badges[id] = badge;
                return badge;
            }
            catch (Exception e)
            {
                WarnOnce("创建怪物 CD 标签失败: " + e.Message);
                return null;
            }
        }

        private void Position(Badge badge, UI.UICom_AttrInfo info)
        {
            try
            {
                float left, top, right, bottom;
                if (!TryRowRect(info, badge.Text, out left, out top, out right, out bottom)) return;
                float x = right + Gap + _offsetX;
                float y = top + (bottom - top - badge.Text.height) * 0.5f + _offsetY;
                badge.Text.SetXY(x, y);
                LogGeometry(badge, info, left, top, right, bottom);
            }
            catch { }
        }

        /// <summary>
        /// 记录名字行的实际几何(排查位置用)。Debug 构建每次怪物首次刷新都会写一行,
        /// Release 只在 LogCdDetail 打开时写。
        /// </summary>
        private void LogGeometry(Badge badge, UI.UICom_AttrInfo info, float left, float top, float right, float bottom)
        {
            if (badge.GeometryLogged) return;
#if COMBATODDS_DEBUG_PROBES
            badge.GeometryLogged = true;
#else
            if (!_logDetail) return;
            badge.GeometryLogged = true;
#endif
            try
            {
                var name = info.txt_Name;
                var hp = info.txt_HP;
                SdkLog.Info("CombatOdds", "[怪物CD] 几何 player=" + badge.PlayerId +
                    " row=(" + Fmt(left) + "," + Fmt(top) + "," + Fmt(right) + "," + Fmt(bottom) + ")" +
                    " name=(" + Fmt(name.x) + "," + Fmt(name.y) + "," + Fmt(name.width) + "," + Fmt(name.height) + ")" +
                    " hp=(" + Fmt(hp.x) + "," + Fmt(hp.y) + "," + Fmt(hp.width) + "," + Fmt(hp.height) + ")" +
                    " com=(" + Fmt(info.width) + "," + Fmt(info.height) + ")" +
                    " label=(" + Fmt(badge.Text.x) + "," + Fmt(badge.Text.y) + "," + Fmt(badge.Text.width) + "," + Fmt(badge.Text.height) + ")");
            }
            catch { }
        }

        private static string Fmt(float value)
        {
            return ((int)Math.Round(value)).ToString();
        }

        /// <summary>
        /// 取"怪物名字所在那一行"的可见范围: 以 <c>txt_Name</c> 为基准, 把同一行上可见的文字
        /// (编号/血量等)并进来, 得到右边界, 冷却文本放在它右侧。
        /// </summary>
        private static bool TryRowRect(UI.UICom_AttrInfo info, GObject ignore, out float left, out float top, out float right, out float bottom)
        {
            left = top = right = bottom = 0f;
            var name = info.txt_Name;
            if (name == null || name.isDisposed) return false;
            VisualRect(name, out left, out top, out right, out bottom);
            float nameTop = top;
            float rowHeight = bottom - top;
            // 只并进与名字"顶边对齐"的文字(编号/同排血量)。血量通常在名字下一行,
            // 按中心距离判断会把两行并成一大块, 标签被推到两行之间。
            float topTolerance = Math.Max(6f, rowHeight * 0.5f);

            int count = info.numChildren;
            for (int i = 0; i < count; i++)
            {
                var child = info.GetChildAt(i);
                if (child == null || ReferenceEquals(child, ignore) || ReferenceEquals(child, name)) continue;
                if (!(child is GTextField) || !child.visible) continue;

                float l, t, r, b;
                VisualRect(child, out l, out t, out r, out b);
                if (Math.Abs(t - nameTop) > topTolerance) continue;

                if (r > right) right = r;
                if (l < left) left = l;
                if (b > bottom) bottom = b;
            }
            return true;
        }

        private static void VisualRect(GObject o, out float left, out float top, out float right, out float bottom)
        {
            float pivotX = o.pivotAsAnchor ? o.pivotX : 0f;
            float pivotY = o.pivotAsAnchor ? o.pivotY : 0f;
            float width = o.width;
            float height = o.height;
            if (width <= 0f && o is GTextField) width = ((GTextField)o).textWidth;
            if (height <= 0f && o is GTextField) height = ((GTextField)o).textHeight;
            left = o.x - width * pivotX;
            top = o.y - height * pivotY;
            right = left + width;
            bottom = top + height;
        }

        private void HideBadge(long id)
        {
            Badge badge;
            if (_badges.TryGetValue(id, out badge))
            {
                _badges.Remove(id);
                DisposeBadge(badge);
            }
        }

        private void HideAll()
        {
            foreach (var badge in _badges.Values)
                if (badge.Text != null && !badge.Text.isDisposed) badge.Text.visible = false;
        }

        private void ClearBadges()
        {
            foreach (var badge in _badges.Values) DisposeBadge(badge);
            _badges.Clear();
        }

        private static void DisposeBadge(Badge badge)
        {
            if (badge == null || badge.Text == null) return;
            try
            {
                if (badge.Text.parent != null) badge.Text.parent.RemoveChild(badge.Text, true);
                else if (!badge.Text.isDisposed) badge.Text.Dispose();
            }
            catch { }
            badge.Text = null;
        }

        private static BattleLogic CurrentBattle()
        {
            try { return SimpleSingletonProvider<GameLogicManager>.inst?.battle; }
            catch { return null; }
        }

        private static int CurrentRound()
        {
            try
            {
                var room = SimpleSingletonProvider<GameLogicManager>.inst?.room;
                var info = room != null ? room.curRoomInfo : null;
                return info != null ? info.Round : 1;
            }
            catch { return 1; }
        }

        private static UI.UICom_AttrInfo AttrInfoOf(BattlePlayerData data)
        {
            try
            {
                var character = data.CharacterInst;
                var window = character != null ? character.window : null;
                var info = window != null ? window.Com_AttrInfo : null;
                if (info == null || info.isDisposed) return null;
                return info;
            }
            catch { return null; }
        }

        private static int SafeHp(BattlePlayerData data)
        {
            try { return data.Property.HP != null ? data.Property.HP.Value : 0; }
            catch { return 0; }
        }

        private static int SafeActiveCd(BattlePlayerData data)
        {
            try { return data.Property.activeSkillCD != null ? data.Property.activeSkillCD.Value : 0; }
            catch { return 0; }
        }

        private static int SafeSkillId(BattlePlayerData data)
        {
            try { return data.player.GetBattleActiveSkillId(); }
            catch { return 0; }
        }

        /// <summary>
        /// 默认是否值得显示: BOSS 一定显示; 其它怪物只有在配表里<b>真的配了主动技能</b>
        /// (<c>MonsterInfoConfigure.ActiveSkill != 0</c>)时才显示。普通/精英怪的
        /// <c>PveActiveSkill</c> 往往只是它的攻击手段, 不是"开大"。
        /// </summary>
        private static bool ShouldShow(BattlePlayerData data)
        {
            try
            {
                var config = data.player.characterConfig;
                if (config != null && config.MonsterType == MonsterType.Boss) return true;

                int heroId = data.player.Hero != null ? data.player.Hero.HeroId : 0;
                MonsterInfoConfigure info;
                if (heroId != 0 && StaticConfigure.Monster != null &&
                    StaticConfigure.Monster.InfoDict.TryGetValue(heroId, out info) && info.ActiveSkill != 0)
                    return true;
            }
            catch { }
            return false;
        }

        private static SkillInfoConfigure SkillConfig(int skillId)
        {
            try
            {
                if (skillId == 0 || StaticConfigure.Skill == null) return null;
                SkillInfoConfigure skill;
                return StaticConfigure.Skill.InfoDict.TryGetValue(skillId, out skill) ? skill : null;
            }
            catch { return null; }
        }

        private void Log(string message)
        {
            if (!_logDetail || string.IsNullOrEmpty(message)) return;
            SdkLog.Info("CombatOdds", "[怪物CD] " + message);
        }

        private void WarnOnce(string message)
        {
            if (_warned) return;
            _warned = true;
            SdkLog.Warn("CombatOdds", message);
        }
    }
}
