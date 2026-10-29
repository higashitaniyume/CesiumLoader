using System.Collections.Generic;

namespace QuickChatPlusMod
{
    /// <summary>
    /// 一条聊天配置的只读投影，供 <see cref="PhrasePlanner"/> 使用。
    /// 不引用游戏类型，便于离线单测。
    /// </summary>
    public sealed class PhraseEntry
    {
        /// <summary>ChatID：既是发送时交给服务器的 Index，也是 STRChat 本地化表的主键。</summary>
        public int ChatId;

        /// <summary>ChatType.InGame：战斗中快捷短语。</summary>
        public bool InGame;

        /// <summary>ChatType.Room：房间等待快捷短语。</summary>
        public bool Room;

        /// <summary>ChatType.Result：结算快捷短语。</summary>
        public bool Result;

        /// <summary>MapModeType 列表（空表示没有指定模式）。</summary>
        public int[] Modes;
    }

    /// <summary>一条待追加的克隆：让 <see cref="Mode"/> 模式的列表多显示一条 <see cref="ChatId"/>。</summary>
    public struct PhraseAddition
    {
        public int Mode;
        public int EntryIndex;
        public int ChatId;
    }

    /// <summary>
    /// 计算“每个目标模式还缺哪些短语”。
    ///
    /// 游戏用 ChatConfigure.GetBattleChats(mode, mapId) 从 Chat 配置里筛：
    /// ChatType 含 InGame、MapModeType 含当前模式、MapLimit 为空或含当前地图。
    /// 所以房间里看不到的短语，只是被 ChatType / MapModeType 过滤掉了；
    /// 追加一份 ChatType=InGame、MapModeType=目标模式的克隆，就能让它们重新出现在列表里。
    ///
    /// 同一模式内按 ChatId 去重：原列表已有的不重复追加（20012 这类跨类别复用的 ID 很常见）。
    /// MapLimit 不参与“已可见”判断（当前配置没有按地图限制的条目；若以后出现，宁可少加不重复）。
    /// </summary>
    public static class PhrasePlanner
    {
        public static List<PhraseAddition> Plan(IList<PhraseEntry> entries, int[] targetModes,
            bool includeRoom, bool includeResult)
        {
            var additions = new List<PhraseAddition>();
            if (entries == null || targetModes == null) return additions;

            for (int m = 0; m < targetModes.Length; m++)
            {
                int mode = targetModes[m];

                // 该模式当前已可见的 ChatID。
                var visible = new HashSet<int>();
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (entry == null || !entry.InGame) continue;
                    if (Contains(entry.Modes, mode)) visible.Add(entry.ChatId);
                }

                // 同一模式里一个 ChatID 只补一次；保持配置中的先后顺序。
                var planned = new HashSet<int>();
                for (int i = 0; i < entries.Count; i++)
                {
                    var entry = entries[i];
                    if (entry == null) continue;
                    if (!entry.InGame && !(includeRoom && entry.Room) && !(includeResult && entry.Result)) continue;
                    if (visible.Contains(entry.ChatId)) continue;
                    if (!planned.Add(entry.ChatId)) continue;
                    additions.Add(new PhraseAddition { Mode = mode, EntryIndex = i, ChatId = entry.ChatId });
                }
            }
            return additions;
        }

        public static bool Contains(int[] values, int value)
        {
            if (values == null) return false;
            for (int i = 0; i < values.Length; i++)
                if (values[i] == value) return true;
            return false;
        }
    }
}
