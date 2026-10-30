using System;
using System.Collections.Generic;
using System.Text;

namespace HomeBottomBarMod
{
    /// <summary>
    /// 主页底栏的功能列表重排规则。
    ///
    /// 底栏不是固定预制体：<c>BottomMenuPanel</c> 把 Home 面板配置里的
    /// <c>ShowFunction</c> 交给 <c>GList</c> 逐项渲染。功能 6 是公告，功能 1 是设置，
    /// 功能 0 是退出。目标排列是：
    ///   任务 好友 公会 图鉴 仓库 邮件 设置 退出
    ///
    /// 这个类不引用 FairyGUI / 游戏类型，方便离线单测。
    /// </summary>
    public static class HomeBarLayout
    {
        public const int ExitFunction = 0;
        public const int SettingFunction = 1;
        public const int NoticeFunction = 6;

        /// <summary>
        /// 生成目标列表：移除公告与旧退出，把设置放到倒数第二，末尾追加退出。
        /// 如果原列表里没有设置，则不凭空添加设置，只保证退出在最右。
        /// </summary>
        public static List<int> Desired(IReadOnlyList<int> source)
        {
            var result = new List<int>((source == null ? 0 : source.Count) + 1);
            bool hasSetting = false;

            if (source != null)
            {
                for (int i = 0; i < source.Count; i++)
                {
                    int function = source[i];
                    if (function == NoticeFunction || function == ExitFunction) continue;
                    if (function == SettingFunction)
                    {
                        hasSetting = true;
                        continue;
                    }
                    result.Add(function);
                }
            }

            if (hasSetting) result.Add(SettingFunction);
            result.Add(ExitFunction);
            return result;
        }

        /// <summary>当前列表是否已经是目标排列（幂等判断）。</summary>
        public static bool IsDesired(IReadOnlyList<int> source)
        {
            if (source == null) return false;
            var desired = Desired(source);
            if (source.Count != desired.Count) return false;
            for (int i = 0; i < source.Count; i++)
            {
                if (source[i] != desired[i]) return false;
            }
            return true;
        }

        /// <summary>日志用，例如 [8,7,9,5,2,3,1,0]。</summary>
        public static string Describe(IReadOnlyList<int> source)
        {
            var builder = new StringBuilder("[");
            if (source != null)
            {
                for (int i = 0; i < source.Count; i++)
                {
                    if (i > 0) builder.Append(',');
                    builder.Append(source[i]);
                }
            }
            builder.Append(']');
            return builder.ToString();
        }
    }
}
