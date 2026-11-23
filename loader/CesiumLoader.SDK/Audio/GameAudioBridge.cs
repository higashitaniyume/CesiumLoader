using System;
using System.Collections.Generic;
using System.Reflection;
using CesiumLoader.SDK.Runtime;

namespace CesiumLoader.SDK.Audio
{
    /// <summary>
    /// 与《Astral Party》游戏特定内部类型（FairyGUI.Stage、StaticConfigure.Audio、GameSettings、AudioManager）
    /// 进行解耦互操作的适配桥梁。
    /// 
    /// 通过运行时动态查找与反射缓存，保证 SDK 在脱离游戏主程序集（如离线单元测试宿主）
    /// 运行期间不会触发 JIT TypeLoadException 或 FileNotFoundException。
    /// </summary>
    internal static class GameAudioBridge
    {
        private static bool _initialized;
        private static FieldInfo _playSoundCallbackField;
        private static PropertyInfo _stageInstProp;
        private static PropertyInfo _staticAudioProp;
        private static PropertyInfo _eventDictProp;
        private static PropertyInfo _bgmDictProp;
        private static PropertyInfo _bankDictProp;
        private static PropertyInfo _stateDictProp;
        private static MethodInfo _getMasterVol;
        private static MethodInfo _getBgmVol;
        private static MethodInfo _getSfxVol;
        private static MethodInfo _getVoiceVol;
        private static MethodInfo _setRtpcValue;
        private static MethodInfo _isBgmPlayingMethod;
        private static FieldInfo _audioManagerInstField;
        private static PropertyInfo _audioManagerInstProp;
        private static PropertyInfo _audioManagerHasInstanceProp;

        // 背景音乐配表(惰性解析, 只在读取成功后缓存)
        private static readonly object _bgmTableLock = new object();
        private static List<BgmBankInfo> _bgmTable;
        private static Dictionary<int, BgmBankInfo> _bgmByPlayEvent;

        private static void EnsureInit()
        {
            if (_initialized) return;
            _initialized = true;

            try
            {
                Type stageType = RuntimeAssemblyService.FindType("FairyGUI.Stage");
                if (stageType != null)
                {
                    _stageInstProp = stageType.GetProperty("inst", BindingFlags.Public | BindingFlags.Static);
                    _playSoundCallbackField = stageType.GetField("playSoundCallback", BindingFlags.Public | BindingFlags.Instance);
                }

                Type staticConfigType = RuntimeAssemblyService.FindType("StaticConfigure");
                if (staticConfigType != null)
                {
                    _staticAudioProp = staticConfigType.GetProperty("Audio", BindingFlags.Public | BindingFlags.Static);
                }

                Type settingsType = RuntimeAssemblyService.FindType("Core.GameSettings");
                if (settingsType != null)
                {
                    _getMasterVol = settingsType.GetMethod("GetMasterVolume", BindingFlags.Public | BindingFlags.Static);
                    _getBgmVol = settingsType.GetMethod("GetBGMVolume", BindingFlags.Public | BindingFlags.Static);
                    _getSfxVol = settingsType.GetMethod("GetSFXVolume", BindingFlags.Public | BindingFlags.Static);
                    _getVoiceVol = settingsType.GetMethod("GetVoiceVolume", BindingFlags.Public | BindingFlags.Static);
                }

                Type audioMgrType = RuntimeAssemblyService.FindType("Core.AudioManager");
                if (audioMgrType != null)
                {
                    _setRtpcValue = audioMgrType.GetMethod("SetRTPCValue", new Type[] { typeof(string), typeof(float) });
                    _isBgmPlayingMethod = audioMgrType.GetMethod("IsBGMPlaying", new Type[] { typeof(int) });

                    // AudioManager 的基类就是已经实例化好的 Tools.SimpleSingletonProvider<AudioManager>，
                    // 直接用它比 MakeGenericType 稳（HybridCLR 下泛型实例化可能不可用）。
                    Type singletonType = audioMgrType.BaseType;
                    if (singletonType == null || !singletonType.IsGenericType)
                    {
                        try
                        {
                            Type open = RuntimeAssemblyService.FindType("Tools.SimpleSingletonProvider`1");
                            singletonType = open == null ? null : MakeGenericSingleton(open, audioMgrType);
                        }
                        catch { singletonType = null; }
                    }

                    if (singletonType != null)
                    {
                        // 只拿成员句柄, 不在这里读 inst: SimpleSingletonProvider.inst 会就地 new 出实例,
                        // 而 AudioManager.InstanceInit 会把 Stage.playSoundCallback 改回游戏自己的回调。
                        _audioManagerInstField = singletonType.GetField("_inst", BindingFlags.NonPublic | BindingFlags.Static);
                        _audioManagerInstProp = singletonType.GetProperty("inst", BindingFlags.Public | BindingFlags.Static);
                        _audioManagerHasInstanceProp = singletonType.GetProperty("hasInstance", BindingFlags.Public | BindingFlags.Static);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 取 AudioManager 单例。游戏尚未创建时返回 null（<b>不会</b>顺手把它构造出来）。
        /// </summary>
        private static object GetAudioManagerInstance()
        {
            EnsureInit();
            try
            {
                // 优先读私有静态字段 _inst: 它是 inst 属性真正持有的实例, 不会触发构造。
                if (_audioManagerInstField != null)
                {
                    object cached = _audioManagerInstField.GetValue(null);
                    if (cached != null) return cached;
                    return null;
                }

                if (_audioManagerHasInstanceProp != null && !(bool)_audioManagerHasInstanceProp.GetValue(null, null)) return null;
                return _audioManagerInstProp?.GetValue(null, null);
            }
            catch { return null; }
        }

        /// <summary>游戏的 AudioManager 单例是否已存在（存在才可能有 BGM 在播）。</summary>
        internal static bool IsAudioManagerReady
        {
            get { return GetAudioManagerInstance() != null; }
        }

        /// <summary>
        /// 单独一个方法承载 <c>Type.MakeGenericType</c>。
        /// 按 <see cref="CesiumLoader.SDK.Internals.UnityCall"/> 的约定，可能被裁剪的调用必须隔离：
        /// 一旦它在 HybridCLR 下不可用，失败只局限在这个方法，不会带走整个 EnsureInit。
        /// </summary>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static Type MakeGenericSingleton(Type openGeneric, Type argument)
        {
            return openGeneric.MakeGenericType(argument);
        }

        public static object GetStageInstance()
        {
            EnsureInit();
            try { return _stageInstProp?.GetValue(null, null); }
            catch { return null; }
        }

        public static Action<int> GetStagePlaySoundCallback(object stage)
        {
            EnsureInit();
            if (stage == null || _playSoundCallbackField == null) return null;
            try { return _playSoundCallbackField.GetValue(stage) as Action<int>; }
            catch { return null; }
        }

        public static bool SetStagePlaySoundCallback(object stage, Action<int> callback)
        {
            EnsureInit();
            if (stage == null || _playSoundCallbackField == null) return false;
            try
            {
                _playSoundCallbackField.SetValue(stage, callback);
                return true;
            }
            catch { return false; }
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, Tuple<string, uint>> _eventInfoCache =
            new System.Collections.Concurrent.ConcurrentDictionary<int, Tuple<string, uint>>();

        public static bool TryGetAudioEventInfo(int configId, out string eventName, out uint wwiseEventId)
        {
            if (_eventInfoCache.TryGetValue(configId, out var cached))
            {
                eventName = cached.Item1;
                wwiseEventId = cached.Item2;
                return true;
            }

            eventName = string.Empty;
            wwiseEventId = 0;
            EnsureInit();

            try
            {
                object audioConfig = _staticAudioProp?.GetValue(null, null);
                if (audioConfig == null) return false;

                if (_eventDictProp == null)
                    _eventDictProp = audioConfig.GetType().GetProperty("EventDict");

                var dict = _eventDictProp?.GetValue(audioConfig, null) as System.Collections.IDictionary;
                if (dict != null && dict.Contains(configId))
                {
                    object eventObj = dict[configId];
                    if (eventObj != null)
                    {
                        var nameProp = eventObj.GetType().GetProperty("EventName");
                        var idProp = eventObj.GetType().GetProperty("EventID");
                        eventName = nameProp?.GetValue(eventObj, null) as string ?? string.Empty;
                        wwiseEventId = (uint)(idProp?.GetValue(eventObj, null) ?? 0u);
                        _eventInfoCache[configId] = Tuple.Create(eventName, wwiseEventId);
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        public static float GetGameMasterVolume()
        {
            EnsureInit();
            try { return _getMasterVol != null ? (float)_getMasterVol.Invoke(null, null) : 70f; }
            catch { return 70f; }
        }

        public static float GetGameBgmVolume()
        {
            EnsureInit();
            try { return _getBgmVol != null ? (float)_getBgmVol.Invoke(null, null) : 50f; }
            catch { return 50f; }
        }

        public static float GetGameSfxVolume()
        {
            EnsureInit();
            try { return _getSfxVol != null ? (float)_getSfxVol.Invoke(null, null) : 80f; }
            catch { return 80f; }
        }

        public static float GetGameVoiceVolume()
        {
            EnsureInit();
            try { return _getVoiceVol != null ? (float)_getVoiceVol.Invoke(null, null) : 100f; }
            catch { return 100f; }
        }

        public static void SetWwiseRtpc(string rtpcName, float value)
        {
            EnsureInit();
            try
            {
                object audioManager = GetAudioManagerInstance();
                if (audioManager != null && _setRtpcValue != null)
                {
                    _setRtpcValue.Invoke(audioManager, new object[] { rtpcName, value });
                }
            }
            catch { }
        }

        // =====================================================================
        // 背景音乐(BGM)配表与播放状态
        // =====================================================================

        private static object ReadMember(object target, string name)
        {
            if (target == null) return null;
            try
            {
                Type t = target.GetType();
                PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (p != null) return p.GetValue(target, null);
                FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.Instance);
                return f?.GetValue(target);
            }
            catch { return null; }
        }

        private static int ReadInt(object target, string name)
        {
            return ToIntSafe(ReadMember(target, name));
        }

        /// <summary>
        /// 读 Wwise 事件数值 ID。配表里是 `uint`(fixed32)，高位大于 int.MaxValue 的值很常见，
        /// 用 Convert.ToInt32 会溢出，必须按无符号读。
        /// </summary>
        private static uint ReadUInt(object target, string name)
        {
            object v = ReadMember(target, name);
            if (v == null) return 0u;
            try
            {
                if (v is uint u) return u;
                if (v is ulong ul) return (uint)ul;
                return Convert.ToUInt32(v);
            }
            catch { return 0u; }
        }

        /// <summary>
        /// 解析并缓存全部背景音乐银行（按 bank 去重）。
        /// 配表未就绪时返回 false，调用方应等下一轮再试（不会缓存失败结果）。
        /// </summary>
        internal static bool TryGetBgmTable(out List<BgmBankInfo> banks)
        {
            lock (_bgmTableLock)
            {
                if (_bgmTable != null)
                {
                    banks = _bgmTable;
                    return true;
                }
            }

            banks = null;
            EnsureInit();

            try
            {
                object audioConfig = _staticAudioProp?.GetValue(null, null);
                if (audioConfig == null) return false;

                if (_bgmDictProp == null) _bgmDictProp = audioConfig.GetType().GetProperty("BGMDict");
                if (_bankDictProp == null) _bankDictProp = audioConfig.GetType().GetProperty("BankDict");
                if (_stateDictProp == null) _stateDictProp = audioConfig.GetType().GetProperty("StateDict");
                if (_eventDictProp == null) _eventDictProp = audioConfig.GetType().GetProperty("EventDict");

                var bgmDict = _bgmDictProp?.GetValue(audioConfig, null) as System.Collections.IDictionary;
                var bankDict = _bankDictProp?.GetValue(audioConfig, null) as System.Collections.IDictionary;
                if (bgmDict == null || bankDict == null) return false;

                var stateDict = _stateDictProp?.GetValue(audioConfig, null) as System.Collections.IDictionary;
                var eventDict = _eventDictProp?.GetValue(audioConfig, null) as System.Collections.IDictionary;

                var list = new List<BgmBankInfo>();
                var byBank = new Dictionary<int, BgmBankInfo>();

                foreach (System.Collections.DictionaryEntry entry in bgmDict)
                {
                    object cfg = entry.Value;
                    if (cfg == null) continue;

                    int bankId = ReadInt(cfg, "BankId");
                    if (bankId == 0) continue;
                    if (byBank.ContainsKey(bankId)) continue; // 同一 bank 的状态变体只需一条

                    if (!bankDict.Contains(bankId)) continue;
                    object bank = bankDict[bankId];
                    if (bank == null) continue;

                    var info = new BgmBankInfo
                    {
                        BankId = bankId,
                        BgmId = ToIntSafe(entry.Key),
                        PlayEventConfigId = ReadInt(bank, "PlayEventId"),
                        StopEventConfigId = ReadInt(bank, "StopEventId"),
                        StateConfigId = ReadInt(cfg, "StateId")
                    };

                    if (info.StateConfigId != 0 && stateDict != null && stateDict.Contains(info.StateConfigId))
                    {
                        object state = stateDict[info.StateConfigId];
                        info.StateName = (ReadMember(state, "State") as string) ?? string.Empty;
                    }

                    if (eventDict != null && info.PlayEventConfigId != 0 && eventDict.Contains(info.PlayEventConfigId))
                    {
                        object ev = eventDict[info.PlayEventConfigId];
                        if (ev != null)
                        {
                            info.EventName = (ReadMember(ev, "EventName") as string) ?? string.Empty;
                            info.WwiseEventId = ReadUInt(ev, "EventID");
                        }
                    }

                    byBank[bankId] = info;
                    list.Add(info);
                }

                if (list.Count == 0) return false;

                var byPlayEvent = new Dictionary<int, BgmBankInfo>();
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].PlayEventConfigId != 0 && !byPlayEvent.ContainsKey(list[i].PlayEventConfigId))
                        byPlayEvent[list[i].PlayEventConfigId] = list[i];
                }

                lock (_bgmTableLock)
                {
                    _bgmByPlayEvent = byPlayEvent;
                    _bgmTable = list;
                }

                banks = list;
                return true;
            }
            catch { return false; }
        }

        private static int ToIntSafe(object value)
        {
            if (value == null) return 0;
            try
            {
                // 配表里的整数类型混用(int/uint/sfixed32/fixed32)，高位值直接 Convert 会溢出。
                if (value is int i) return i;
                if (value is uint u) return unchecked((int)u);
                if (value is long l) return unchecked((int)l);
                if (value is ulong ul) return unchecked((int)ul);
                return Convert.ToInt32(value);
            }
            catch { return 0; }
        }

        /// <summary>该 `Audio.Event` 配表 ID 是否为某个 BGM 银行的播放事件。</summary>
        internal static bool IsBgmPlayEvent(int eventConfigId)
        {
            return eventConfigId != 0 && TryGetBgmInfoByEventConfigId(eventConfigId, out _);
        }

        /// <summary>该 `Audio.Event` 配表 ID 是否为某个 BGM 银行的停止事件。</summary>
        internal static bool IsBgmStopEvent(int eventConfigId)
        {
            if (eventConfigId == 0) return false;
            if (!TryGetBgmTable(out var banks)) return false;
            for (int i = 0; i < banks.Count; i++)
            {
                if (banks[i].StopEventConfigId == eventConfigId) return true;
            }
            return false;
        }

        /// <summary>按 `Audio.Event` 配表 ID（播放事件）查找 BGM 记录。</summary>
        internal static bool TryGetBgmInfoByEventConfigId(int eventConfigId, out BgmBankInfo info)
        {
            info = null;
            if (eventConfigId == 0) return false;
            if (!TryGetBgmTable(out _)) return false;

            lock (_bgmTableLock)
            {
                if (_bgmByPlayEvent == null) return false;
                return _bgmByPlayEvent.TryGetValue(eventConfigId, out info);
            }
        }

        /// <summary>按 `Audio.Bank` 配表 ID 查找 BGM 记录。</summary>
        internal static bool TryGetBgmInfoByBank(int bankId, out BgmBankInfo info)
        {
            info = null;
            if (bankId == 0) return false;
            if (!TryGetBgmTable(out var banks)) return false;

            for (int i = 0; i < banks.Count; i++)
            {
                if (banks[i].BankId == bankId)
                {
                    info = banks[i];
                    return true;
                }
            }
            return false;
        }

        /// <summary>某个 BGM 银行的曲目当前是否正在播放（游戏自己的 BGM 状态机判定）。</summary>
        internal static bool IsBgmBankPlaying(int bankId)
        {
            EnsureInit();
            if (_isBgmPlayingMethod == null) return false;

            object audioManager = GetAudioManagerInstance();
            if (audioManager == null) return false;

            try { return (bool)_isBgmPlayingMethod.Invoke(audioManager, new object[] { bankId }); }
            catch { return false; }
        }

        /// <summary>
        /// 找出当前正在播放的 BGM。
        /// <paramref name="previousEventConfigId"/> 是上一次识别到的曲目：若同时有多个 bank 处于播放态，
        /// 优先返回与它不同的那个，从而识别出「切到新曲子」而不是重复处理旧曲子。
        /// </summary>
        internal static bool TryGetPlayingBgmBank(int previousEventConfigId, out BgmBankInfo info)
        {
            info = null;
            if (!TryGetBgmTable(out var banks)) return false;
            if (!IsAudioManagerReady) return false;

            BgmBankInfo first = null;
            for (int i = 0; i < banks.Count; i++)
            {
                if (!IsBgmBankPlaying(banks[i].BankId)) continue;

                if (banks[i].PlayEventConfigId != previousEventConfigId)
                {
                    info = banks[i];
                    return true;
                }
                if (first == null) first = banks[i];
            }

            info = first;
            return info != null;
        }
    }
}
