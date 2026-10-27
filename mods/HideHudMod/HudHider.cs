using System.Collections.Generic;
using FairyGUI;
using UI;

namespace HideHudMod
{
    /// <summary>
    /// 只操作游戏自己的 FairyGUI 对象，不创建新界面，也不改游戏数据。
    ///
    /// 隐藏方式：把目标容器设为不可见，并在隐藏期间每帧保持；
    /// 恢复时只还原“由本 mod 隐藏且仍在舞台上”的对象。
    /// </summary>
    internal sealed class HudHider
    {
        private sealed class HiddenTarget
        {
            public GObject Target;
        }

        private readonly List<HiddenTarget> _hidden = new List<HiddenTarget>();

        private UIBattleInfoPanel _panel;
        private UIHandCardPanel _hand;
        private UIOperateTimeWindow _timer;
        private UIGameInfoWindow _gameInfo;
        private UIBattleHintWindow _battleHint;

        private GObject[] _otherTargets;
        private UIBattleInfoPanel _otherPanel;
        private int _otherChildCount;
        private float _nextScan;

        /// <summary>当前由本 mod 隐藏的对象数量（诊断/日志用）。</summary>
        public int HiddenCount { get { return _hidden.Count; } }

        /// <summary>隐藏或恢复。隐藏期间每帧调用以压住游戏后续的 visible 写入。</summary>
        public void Apply(HideHudSettings settings, bool hiding)
        {
            if (!hiding)
            {
                Clear();
                return;
            }

            EnsureReferences();
            bool all = settings.HideAllHud;

            SetHidden(_panel, all);
            SetHidden(_hand, all || settings.HideHandCards);
            SetHidden(_timer, all || settings.HideOther);
            SetHidden(_gameInfo, all || settings.HideOther);
            SetHidden(_battleHint, all || settings.HideOther);

            if (_panel != null)
            {
                SetHidden(_panel.com_BattlePlayer, all || settings.HideAvatars);
                SetHidden(_panel.com_AttrInfos, all || settings.HideMonsterDisplay);

                bool other = all || settings.HideOther;
                GObject[] targets = GetOtherTargets();
                for (int i = 0; i < targets.Length; i++) SetHidden(targets[i], other);
            }
        }

        /// <summary>恢复全部目标的可见状态，并丢掉旧容器引用。</summary>
        public void Clear()
        {
            for (int i = 0; i < _hidden.Count; i++)
            {
                GObject target = _hidden[i].Target;
                try
                {
                    if (target != null && !target.isDisposed && target.parent != null)
                        target.visible = true;
                }
                catch { }
            }
            _hidden.Clear();
        }

        /// <summary>切场景/面板重建时调用：先恢复，再重新扫描。</summary>
        public void Reset()
        {
            Clear();
            _panel = null;
            _hand = null;
            _timer = null;
            _gameInfo = null;
            _battleHint = null;
            _otherTargets = null;
            _otherPanel = null;
            _otherChildCount = 0;
            _nextScan = 0f;
        }

        private void EnsureReferences()
        {
            float now = CesiumLoader.SDK.Engine.UnityTime.RealtimeSinceStartup;
            if (now < _nextScan) return;
            _nextScan = now + 0.5f;

            UIBattleInfoPanel panel = FindFirst<UIBattleInfoPanel>(GRoot.inst);
            UIHandCardPanel hand = FindFirst<UIHandCardPanel>(GRoot.inst);
            UIOperateTimeWindow timer = FindFirst<UIOperateTimeWindow>(GRoot.inst);
            UIGameInfoWindow gameInfo = FindFirst<UIGameInfoWindow>(GRoot.inst);
            UIBattleHintWindow battleHint = FindFirst<UIBattleHintWindow>(GRoot.inst);

            bool changed = !ReferenceEquals(panel, _panel) || !ReferenceEquals(hand, _hand)
                || !ReferenceEquals(timer, _timer) || !ReferenceEquals(gameInfo, _gameInfo)
                || !ReferenceEquals(battleHint, _battleHint);

            _panel = panel;
            _hand = hand;
            _timer = timer;
            _gameInfo = gameInfo;
            _battleHint = battleHint;

            if (changed)
            {
                // 旧面板/旧窗口可能已经被游戏关闭，Clear 会跳过已不在舞台上的对象。
                Clear();
                if (!ReferenceEquals(panel, _otherPanel)) _otherTargets = null;
            }
        }

        private GObject[] GetOtherTargets()
        {
            if (_panel == null) return EmptyTargets;
            if (_otherTargets != null && ReferenceEquals(_panel, _otherPanel)
                && _otherChildCount == _panel.numChildren) return _otherTargets;

            _otherPanel = _panel;
            _otherChildCount = _panel.numChildren;

            // “其他”= 主 HUD 里除怪物信息条、玩家头像列表和全屏演出视频之外的所有直接子项。
            // 按实际子项枚举，而不是硬编码字段，UI 增删装饰图/按钮时也不会漏。
            var targets = new List<GObject>(_otherChildCount);
            for (int i = 0; i < _panel.numChildren; i++)
            {
                GObject child = _panel.GetChildAt(i);
                if (child == null) continue;
                if (ReferenceEquals(child, _panel.com_AttrInfos)) continue;
                if (ReferenceEquals(child, _panel.com_BattlePlayer)) continue;
                if (ReferenceEquals(child, _panel.graph_ShowTime)) continue;
                targets.Add(child);
            }
            _otherTargets = targets.ToArray();
            return _otherTargets;
        }

        private static readonly GObject[] EmptyTargets = new GObject[0];

        private void SetHidden(GObject target, bool hide)
        {
            if (!CanTouch(target))
            {
                Remove(target);
                return;
            }

            HiddenTarget entry = Find(target);
            if (hide)
            {
                if (entry != null)
                {
                    // 游戏可能在我们隐藏后又把它设回可见，这里持续压住。
                    if (target.visible) target.visible = false;
                    return;
                }

                // 本来就不可见的目标不碰，避免恢复时把游戏自己隐藏的元素误显示出来。
                if (!target.visible) return;

                target.visible = false;
                _hidden.Add(new HiddenTarget { Target = target });
                return;
            }

            if (entry == null) return;
            // 只恢复由本 mod 隐藏、且仍在舞台上的对象。
            if (!target.visible) target.visible = true;
            _hidden.Remove(entry);
        }

        private static bool CanTouch(GObject target)
        {
            if (target == null) return false;
            try { return !target.isDisposed && target.parent != null; }
            catch { return false; }
        }

        private HiddenTarget Find(GObject target)
        {
            for (int i = 0; i < _hidden.Count; i++)
                if (ReferenceEquals(_hidden[i].Target, target)) return _hidden[i];
            return null;
        }

        private void Remove(GObject target)
        {
            for (int i = _hidden.Count - 1; i >= 0; i--)
                if (ReferenceEquals(_hidden[i].Target, target)) _hidden.RemoveAt(i);
        }

        private static T FindFirst<T>(GObject node) where T : GObject
        {
            return FindFirst<T>(node, 0);
        }

        private static T FindFirst<T>(GObject node, int depth) where T : GObject
        {
            if (node == null || depth > 20) return null;
            try { if (node.isDisposed) return null; } catch { return null; }

            T hit = node as T;
            if (hit != null) return hit;

            GComponent component = node as GComponent;
            if (component == null) return null;
            for (int i = 0; i < component.numChildren; i++)
            {
                T found = FindFirst<T>(component.GetChildAt(i), depth + 1);
                if (found != null) return found;
            }
            return null;
        }
    }
}
