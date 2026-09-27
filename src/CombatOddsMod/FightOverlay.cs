using System;

namespace CombatOddsMod
{
    /// <summary>
    /// FightWindow 覆盖层的反射抽象。真实实现走 FairyGUI 反射(不可离线验证),
    /// 编排逻辑(何时建/复用/换父/隐藏)抽到 <see cref="FightOverlayController"/> 用假实现单测。
    /// </summary>
    public interface IFightOverlayReflector
    {
        /// <summary>打怪投牌窗口当前是否可见。</summary>
        bool IsFightShowing();
        /// <summary>取 FightWindow 的 FairyGUI 根容器(contentPane); 取不到返回 null。</summary>
        object GetFightPane();
        /// <summary>新建并初步配置一个文本标签(GTextField); 失败返回 null。</summary>
        object CreateLabel();
        /// <summary>标签当前是否挂在 pane 下。</summary>
        bool LabelInPane(object label, object pane);
        /// <summary>把标签挂到 pane。</summary>
        void AddChildToPane(object pane, object label);
        /// <summary>设置标签文本。</summary>
        void SetText(object label, string text);
        /// <summary>显隐标签。</summary>
        void SetVisible(object label, bool visible);
    }

    /// <summary>
    /// 覆盖层状态机(纯逻辑, 可离线单测):
    ///   · 未启用 / 窗口不可见 / 取不到容器 → 隐藏已有标签, 不新建;
    ///   · 首次可见 → 建标签并挂到容器; 之后复用同一标签;
    ///   · 容器变了(重开窗口)或标签掉出容器 → 重新挂;
    ///   · 建标签失败(null) → 本次跳过, 下次重试(不抛异常)。
    /// 这样即便真实 FairyGUI 绑定要进游戏才能目视确认, 编排本身是被测过的。
    /// </summary>
    public sealed class FightOverlayController
    {
        private readonly IFightOverlayReflector _r;
        private object _label;
        private object _pane;

        public FightOverlayController(IFightOverlayReflector reflector)
        {
            if (reflector == null) throw new ArgumentNullException(nameof(reflector));
            _r = reflector;
        }

        /// <summary>标签是否已创建(测试/诊断用)。</summary>
        public bool HasLabel { get { return _label != null; } }

        /// <summary>每次战斗更新调用。enabled=false 或窗口不可见即隐藏。</summary>
        public void Update(bool enabled, string text)
        {
            if (!enabled) { HideInternal(); return; }
            if (!_r.IsFightShowing()) { HideInternal(); return; }

            var pane = _r.GetFightPane();
            if (pane == null) { HideInternal(); return; }

            // 标签一旦不再挂在当前容器上(被游戏在战斗子阶段之间移除/销毁, 或容器换了),
            // 就丢弃旧引用重建一个新的——不能复用可能已被 dispose 的对象(否则"只显示一次")。
            if (_label != null && (!ReferenceEquals(pane, _pane) || !_r.LabelInPane(_label, pane)))
            {
                _label = null;
                _pane = null;
            }

            if (_label == null)
            {
                _label = _r.CreateLabel();
                if (_label == null) { _pane = null; return; }   // 建失败: 下次重试
                _r.AddChildToPane(pane, _label);
                _pane = pane;
            }

            _r.SetText(_label, text);
            _r.SetVisible(_label, true);
        }

        /// <summary>主动隐藏(战斗结束/关窗)。</summary>
        public void Hide() { HideInternal(); }

        private void HideInternal()
        {
            if (_label != null) _r.SetVisible(_label, false);
        }
    }
}
