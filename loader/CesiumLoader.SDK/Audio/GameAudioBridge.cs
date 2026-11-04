using System;
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
        private static MethodInfo _getMasterVol;
        private static MethodInfo _getBgmVol;
        private static MethodInfo _getSfxVol;
        private static MethodInfo _getVoiceVol;
        private static MethodInfo _setRtpcValue;
        private static object _audioManagerInst;

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
                    Type singletonType = RuntimeAssemblyService.FindType("Tools.SimpleSingletonProvider`1")?.MakeGenericType(audioMgrType);
                    _audioManagerInst = singletonType?.GetProperty("inst", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
                }
            }
            catch { }
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
                if (_audioManagerInst != null && _setRtpcValue != null)
                {
                    _setRtpcValue.Invoke(_audioManagerInst, new object[] { rtpcName, value });
                }
            }
            catch { }
        }
    }
}
