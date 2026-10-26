using System;

namespace CombatOddsMod
{
    /// <summary>
    /// 手牌剩余数的口径与图标地址解析。纯逻辑, 不碰游戏/UI 类型, 便于离线测试。
    /// </summary>
    internal static class HandCountRules
    {
        /// <summary>
        /// 取一名玩家当前的手牌数。
        ///
        /// <paramref name="serverCardCount"/> 是游戏自己的 <c>CardContainer.CardCount</c>
        /// (玩家信息面板显示的就是它): 队友的牌被服务器掩码, 但数量仍是真实值, 也是随
        /// 1040 属性同步一起刷新的那个。
        ///
        /// 自己的手牌列表 <paramref name="selfHandCount"/> 是本机实时列表: 有些同步消息
        /// (如 5056 手牌变更) 只更新列表而不回写 <c>CardCount</c>, 所以自己的牌以列表长度为准。
        /// 列表不可用时传负数。
        /// </summary>
        internal static int Resolve(int serverCardCount, int selfHandCount, bool isSelf)
        {
            if (isSelf && selfHandCount >= 0) return selfHandCount;
            return serverCardCount < 0 ? 0 : serverCardCount;
        }

        /// <summary>
        /// 从游戏自己的文字模板里取内联图标地址, 例如 <c>[img]ui://vflhnh8dabcde[/img]</c>
        /// 或 <c>&lt;img src='ui://vflhnh8dabcde'/&gt;</c>。取不到返回 null。
        /// </summary>
        internal static string ExtractIconUrl(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            int start = text.IndexOf("ui://", StringComparison.Ordinal);
            if (start < 0) return null;
            int end = start + 5;
            while (end < text.Length && IsUrlChar(text[end])) end++;
            return end > start + 5 ? text.Substring(start, end - start) : null;
        }

        /// <summary>显示用文本; 未知(负数)显示问号, 绝不显示负数。</summary>
        internal static string Format(int count)
        {
            return count < 0 ? "?" : count.ToString();
        }

        /// <summary>FairyGUI 资源 ID 只含小写字母与数字。</summary>
        private static bool IsUrlChar(char c)
        {
            return (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
        }
    }
}
