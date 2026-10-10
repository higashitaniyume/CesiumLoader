namespace CombatOddsMod
{
    /// <summary>战斗胜率助手配置(configs/CombatOddsMod.json, 全部可选)。</summary>
    public class CombatOddsConfig
    {
        /// <summary>总开关。</summary>
        public bool Enabled = true;
        /// <summary>是否也为非自己的战斗显示(观战/队友打怪与防御)。默认开启, 满足"别人打怪防御也要有显示"。</summary>
        public bool ShowForOthers = true;
        /// <summary>是否弹出游戏内通知(有渲染后端时可见; 无后端时进日志)。</summary>
        public bool PopupNotification = true;
        /// <summary>通知存活秒数。</summary>
        public float NotificationTtl = 8f;
        /// <summary>闪避失败时是否仍保留基础防御(InitDef)减伤(默认否, 按承受全额攻击算, 更保守)。</summary>
        public bool DodgeKeepsBaseDefense = false;
        /// <summary>是否用颜色高亮结论与关键数字(FightWindow 覆盖层富文本; 控制台自动降级为纯文本)。</summary>
        public bool ColorHighlight = true;
        /// <summary>是否显示目标身上"受到伤害±"类 buff 明细(标记/狂暴/护盾/蓝海晴印记等), 并把它们计入伤害与击倒率。</summary>
        public bool ShowBuffBreakdown = true;
        /// <summary>是否把读数以 FairyGUI 覆盖层显示在 FightWindow 上(真机验证; 失败自动降级到控制台/通知)。</summary>
        public bool InGameOverlay = true;
        /// <summary>覆盖层字号缩放(1.0=默认已放大版; 想更大调 1.2~1.5, 想更小调 0.8)。</summary>
        public double OverlayFontScale = 1.0;
        /// <summary>覆盖层锚点: "left-center"(默认, 靠左竖直居中) / "top-left" / "top-center" / "top-right" / "right-center"。</summary>
        public string OverlayAnchor = "left-center";
        /// <summary>覆盖层水平微调(像素, 正=右移)。</summary>
        public double OverlayOffsetX = 0;
        /// <summary>覆盖层垂直微调(像素, 正=下移; 靠左居中时可用负值上移)。</summary>
        public double OverlayOffsetY = 0;
        /// <summary>是否在主 HUD(左下角)的攻击力后面追加显示星币锤/手电筒/美工刀带来的攻击力加成。</summary>
        public bool ShowRelicAtkBonus = true;
        /// <summary>是否在棋盘上每个玩家头顶显示实时攻击/防御, 悬停查看详情。</summary>
        public bool ShowBoardPlayerAttrs = true;
        /// <summary>把每次攻击的攻击力加成明细打进加载器日志(星币/治愈/星光/血量/持有筹码 + 各筹码加成), 便于与结算伤害对拍。</summary>
        public bool LogAtkBonusDetail = true;
    }
}
