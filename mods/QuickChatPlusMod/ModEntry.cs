using System;
using System.Collections.Generic;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Manifests;
using CesiumLoader.SDK.Mods;

namespace QuickChatPlusMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            SdkManifest.ExportSidecar();
            ModBase.Run(new QuickChatPlus());
        }
    }

    /// <summary>
    /// 把 Chat 配置里“属于其他类别/其他模式”的既有短语补进战斗内快捷短语列表。
    ///
    /// 游戏打开列表时调用 ChatConfigure.GetBattleChats(room.MapType, room.MapId)，
    /// 只返回 ChatType 含 InGame、MapModeType 含当前模式的条目；房间等待与结算短语
    /// （以及只在别的模式里登记的条目）因此不会出现。这里给每个目标模式追加一份
    /// ChatType=InGame、MapModeType=该模式的克隆，文本仍由游戏用 ChatID 查 STRChat，
    /// 所以其他玩家无需安装本模组也能正常显示。
    ///
    /// 只改本地静态配置，不发送任何消息、不挂钩 RPC；发送仍走原版按钮。
    /// </summary>
    public sealed class QuickChatPlus : ModBase
    {
        private static readonly int[] PveModes =
        {
            (int)MapModeType.Pve,            // PVE
            (int)MapModeType.MutatorPve,     // 词条 PVE
            (int)MapModeType.CampaignPve,    // 战役 PVE
            (int)MapModeType.PracticePve,    // 练习 PVE
        };

        private static readonly int[] PvpModes =
        {
            (int)MapModeType.Standard,
            (int)MapModeType.CampaignPvp,
            (int)MapModeType.AsymmetricalBattle,
            (int)MapModeType.PracticePvp,
            (int)MapModeType.Ultra,
            (int)MapModeType.LuckyStarBattle,
        };

        private bool _enabled = true;
        private bool _includeRoom = true;
        private bool _includeResult = true;
        private bool _includePvp;

        private ChatConfigure _applied;
        private ChatListMutation _mutation;
        private bool _dirty = true;
        private DateTime _retryAfter = DateTime.MinValue;
        private DateTime _nextFitWarn = DateTime.MinValue;
        private int _maxVisibleItems = 16;
        private readonly ChatListFitter _fitter;

        public QuickChatPlus()
        {
            _fitter = new ChatListFitter(message => Log.Warn(message));
        }

        public override string Name { get { return "快捷短语扩展"; } }
        public override string Version { get { return "1.0.0"; } }
        public override string Author { get { return "CesiumLoader"; } }
        public override string Description { get { return "把房间、结算与其他模式的既有快捷短语补进战斗列表，列表超屏时自动限高并支持滚动。"; } }

        public override void OnInitialize()
        {
            var config = Config;
            if (config != null)
            {
                if (!config.Has("enabled")) config.Set("enabled", true);
                if (!config.Has("includeRoomChats")) config.Set("includeRoomChats", true);
                if (!config.Has("includeResultChats")) config.Set("includeResultChats", true);
                if (!config.Has("includePvpModes")) config.Set("includePvpModes", false);
                config.SetDefault("maxVisibleItems", 16);
                QuickChatPlusConfigDocs.Apply(config);
                config.SaveIfDirty();
            }
            ReadConfig();
            Log.Info("已初始化：战斗内快捷短语会补入 Chat 配置里的全部既有短语，文本与发送沿用游戏原版。");
        }

        public override void OnConfigChanged()
        {
            ReadConfig();
            Log.Info("配置已热重载: enabled=" + _enabled + ", room=" + _includeRoom +
                     ", result=" + _includeResult + ", pvp=" + _includePvp +
                     ", maxVisibleItems=" + _maxVisibleItems);
        }

        public override void OnUpdate()
        {
            if (!_enabled)
            {
                if (_applied != null)
                {
                    Restore();
                    Log.Info("已停用，战斗内快捷短语恢复原版列表。");
                }
                _fitter.Restore();
                return;
            }

            try
            {
                _fitter.Tick(_maxVisibleItems);
            }
            catch (Exception e)
            {
                if (DateTime.UtcNow >= _nextFitWarn)
                {
                    _nextFitWarn = DateTime.UtcNow.AddSeconds(10);
                    Log.Warn("快捷短语列表滚动适配失败: " + e.Message);
                }
            }

            var chat = StaticConfigure.Chat;
            if (chat == null) return;
            if (!_dirty && ReferenceEquals(chat, _applied)) return;
            if (DateTime.UtcNow < _retryAfter) return;

            try
            {
                Apply(chat);
            }
            catch (Exception e)
            {
                _retryAfter = DateTime.UtcNow.AddSeconds(10);
                _dirty = true;
                Restore();
                Log.Warn("扩展快捷短语失败，10 秒后重试: " + e.Message);
            }
        }

        private void Apply(ChatConfigure chat)
        {
            // 配置还没加载完：先不动，下一帧再试。
            if (chat.Infos == null || chat.Infos.Count == 0) return;

            // 先移除上一轮追加（配置对象被替换时也按引用尝试，安全）。
            Restore();

            // 先记录 applied：万一追加中途抛异常，catch 里的 Restore 才能按引用回滚。
            _applied = chat;
            _mutation = ChatListExpander.Expand(chat, BuildTargetModes(), _includeRoom, _includeResult);
            _dirty = false;
            _retryAfter = DateTime.MinValue;
            if (_mutation.IsEmpty)
            {
                Log.Info("快捷短语无需补充（配置里没有可追加的条目）。");
                return;
            }

            Log.Info("快捷短语已扩展：新增 " + _mutation.Added + " 条" +
                     (_mutation.Unlimited > 0 ? "，放开 " + _mutation.Unlimited + " 条地图限定" : "") +
                     "；" + _mutation.Summary);
        }

        private int[] BuildTargetModes()
        {
            var modes = new List<int>(PveModes);
            if (_includePvp) modes.AddRange(PvpModes);
            return modes.ToArray();
        }

        private void ReadConfig()
        {
            var config = Config;
            _enabled = config == null || config.GetBool("enabled", true);
            _includeRoom = config == null || config.GetBool("includeRoomChats", true);
            _includeResult = config == null || config.GetBool("includeResultChats", true);
            _includePvp = config != null && config.GetBool("includePvpModes", false);
            int maxVisible = config != null ? config.GetInt("maxVisibleItems", 16) : 16;
            if (maxVisible < 0) maxVisible = 0;
            if (maxVisible > 60) maxVisible = 60;
            _maxVisibleItems = maxVisible;
            _dirty = true;
            _retryAfter = DateTime.MinValue;
        }

        private void Restore()
        {
            ChatListExpander.Restore(_applied, _mutation);
            _mutation = null;
            _applied = null;
        }

        public override void OnUnload()
        {
            _fitter.Restore();
            Restore();
        }
    }
}
