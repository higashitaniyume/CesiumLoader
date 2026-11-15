using System.Reflection;
using CesiumLoader.SDK.Gameplay;
using FairyGUI;
using GameLogic;
using Tools;
using UI;
using UnityEngine;

namespace PvePanelMod
{
    /// <summary>
    /// 官方的商店窗口、战斗（投牌）窗口现在是不是开着。左栏会挡住它们，开着时自动收起左栏。
    /// 读 UIManager 里已经建好的窗口字段（照 CombatOddsMod 读 _cachedFightWindow 的做法），
    /// 不用 UIManager.Fight / landShop 属性：属性在窗口还没建时可能顺手把它建出来。读不到一律当作「没开」。
    /// </summary>
    internal static class GameWindows
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo FightField = typeof(UIManager).GetField("_cachedFightWindow", Private);
        private static readonly FieldInfo ShopField = typeof(UIManager).GetField("_cachedLandShopWindow", Private);
        // 转账窗口：从商店里点转账时，商店窗口先关、这个窗口再开；只有自己转账时才会出现
        private static readonly FieldInfo AtmField = typeof(UIManager).GetField("_cachedATMWindow", Private);
        // 商店窗口自己记着正在逛的人
        private static readonly FieldInfo ShopperField = typeof(LandShopWindow).GetField("shoppingPlayer", Private | BindingFlags.Public);

        // 右上角那列官方按钮（表情、聊天、查找、标记）和它们弹出的表情列表、聊天菜单都在 ExpressionWindow 里
        private static readonly FieldInfo ExprField = typeof(UIManager).GetField("_cachedExpressionWindow", Private);
        private static readonly string[] ExprButtonNames = { "_btn_OpenExpr", "_btn_OpenChat", "_btn_MapChat", "_btn_MapTool" };
        private static FieldInfo[] _exprButtons;

        /// <summary>
        /// 右上角那列官方按钮所在的窗口，以及这几个按钮合起来在界面坐标（GRoot）里的范围。
        /// 窗口没开、按钮都看不见或读不到时返回 false（右栏退回原来贴屏幕右边的位置）。
        /// </summary>
        public static bool ExpressionButtons(out Window window, out Rect area, System.Action<string> log = null)
        {
            window = null;
            area = default(Rect);
            try
            {
                var ui = UIManager.inst;
                var root = GRoot.inst;
                if (ui == null || root == null || ExprField == null) return false;
                var w = ExprField.GetValue(ui) as Window;
                if (w == null || w.isDisposed || !w.isShowing || w.parent != root) return false;
                if (_exprButtons == null)
                {
                    var found = new FieldInfo[ExprButtonNames.Length];
                    for (int i = 0; i < ExprButtonNames.Length; i++)
                        found[i] = typeof(ExpressionWindow).GetField(ExprButtonNames[i], Private | BindingFlags.Public);
                    _exprButtons = found;
                }
                bool any = false;
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                var note = log != null ? new System.Text.StringBuilder() : null;
                for (int i = 0; i < _exprButtons.Length; i++)
                {
                    var b = _exprButtons[i]?.GetValue(w) as GObject;
                    if (b == null || b.isDisposed) { note?.Append(ExprButtonNames[i]).Append("=无 "); continue; }
                    // 官方这几个按钮开了「锚点即坐标」（锚点在右边中间）：这时按钮自己坐标系的 (0,0) 是锚点、不是左上角，
                    // 要先退回左上角（实测：不退的话算出来的范围往右偏一个按钮宽、往下偏半个按钮高）
                    // 另外鼠标悬停时按钮会绕锚点缩小：按它在父节点里的位置和原始大小算（不含它自己的缩放），右栏才不会跟着抖
                    var parent = b.parent;
                    if (parent == null) continue;
                    Vector2 tl = new Vector2(b.x, b.y);
                    if (b.pivotAsAnchor) tl -= new Vector2(b.width * b.pivotX, b.height * b.pivotY);
                    Vector2 p = root.GlobalToLocal(parent.LocalToGlobal(tl));
                    Vector2 q = root.GlobalToLocal(parent.LocalToGlobal(tl + new Vector2(b.width, b.height)));
                    bool shown = b.onStage && Shown(b);
                    note?.Append(ExprButtonNames[i]).Append('(').Append(b.name).Append(")=")
                        .Append(Mathf.RoundToInt(Mathf.Min(p.x, q.x))).Append(',').Append(Mathf.RoundToInt(Mathf.Min(p.y, q.y)))
                        .Append(' ').Append(Mathf.RoundToInt(Mathf.Abs(q.x - p.x))).Append('x').Append(Mathf.RoundToInt(Mathf.Abs(q.y - p.y)))
                        .Append(b.pivotAsAnchor ? " 锚点" + b.pivotX.ToString("0.##") + "," + b.pivotY.ToString("0.##") : "")
                        .Append(shown ? "" : "(隐藏)").Append(' ');
                    if (!shown) continue;
                    x0 = Mathf.Min(x0, Mathf.Min(p.x, q.x)); x1 = Mathf.Max(x1, Mathf.Max(p.x, q.x));
                    y0 = Mathf.Min(y0, Mathf.Min(p.y, q.y)); y1 = Mathf.Max(y1, Mathf.Max(p.y, q.y));
                    any = true;
                }
                if (note != null)
                {
                    string s = note.Append("界面 ").Append(Mathf.RoundToInt(root.width)).Append('x').Append(Mathf.RoundToInt(root.height)).ToString();
                    if (s != _lastButtonsNote) { _lastButtonsNote = s; log("PVE 面板右栏：右上角按钮 " + s); }
                }
                if (!any) return false;
                window = w;
                area = Rect.MinMaxRect(x0, y0, x1, y1);
                return true;
            }
            catch { return false; }
        }

        private static string _lastButtonsNote;

        /// <summary>自己和每一层父节点都可见（按钮自己 visible=true，但整组被藏起来时也算看不见，例如手机版那套按钮）。</summary>
        private static bool Shown(GObject o)
        {
            for (var g = o; g != null && !(g is GRoot); g = g.parent)
                if (!g.visible || g.alpha <= 0.01f) return false;
            return true;
        }

        /// <summary>转账窗口开着（只有自己能打开）。</summary>
        public static bool AtmOpen() { return Showing(AtmField); }

        /// <summary>
        /// 自己的战斗窗口开着。别人打架时这个窗口也会弹出来（观战），所以还要看自己是不是攻方或守方
        /// （照 CombatOddsMod 读 GameLogicManager.fight.battleFightData 的做法）。
        /// </summary>
        public static bool FightOpen()
        {
            if (!Showing(FightField)) return false;
            try
            {
                var live = SimpleSingletonProvider<GameLogicManager>.inst?.fight?.battleFightData;
                if (live == null || live.isEnd) return false;
                return (live.attackerInfo != null && Players.IsSelf(live.attackerInfo.PlayerId))
                    || (live.defenderInfo != null && Players.IsSelf(live.defenderInfo.PlayerId));
            }
            catch { return false; }
        }

        /// <summary>
        /// 自己的商店开着。别人进店时这个窗口也会弹出来，所以还要看在逛的是不是自己：
        /// 先看商店窗口自己的 shoppingPlayer；读不到再看右栏收到的最近一次进店消息（<paramref name="lastShopper"/>，商店一回合只有一个人在逛）。
        /// </summary>
        public static bool ShopOpen(long lastShopper)
        {
            if (!Showing(ShopField)) return false;
            long shopper = 0;
            try
            {
                var window = ShopField.GetValue(UIManager.inst);
                var pd = window != null && ShopperField != null ? ShopperField.GetValue(window) as BattlePlayerData : null;
                if (pd?.player != null) shopper = pd.player.Id;
            }
            catch { }
            if (shopper == 0) shopper = lastShopper;
            return shopper != 0 && Players.IsSelf(shopper);
        }

        private static bool Showing(FieldInfo field)
        {
            try
            {
                var ui = UIManager.inst;
                if (ui == null || field == null) return false;
                var window = field.GetValue(ui) as Window;
                return window != null && !window.isDisposed && window.isShowing;
            }
            catch { return false; }
        }
    }
}
