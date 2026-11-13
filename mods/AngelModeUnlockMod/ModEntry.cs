using System;
using System.Reflection;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.Runtime;
using CesiumLoader.SDK.UserInterface;
using CesiumLoader.SDK.Engine;
using UnityEngine.SceneManagement;

namespace AngelModeUnlockMod
{
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new AngelModeUnlockController());
        }
    }

    /// <summary>
    /// 解除审核渠道对天使模式(和谐/SFW)开关的封锁。
    ///
    /// 原理：游戏按渠道拉取 RemoteConfig（AOT 类 <c>Tools.RemoteConfig</c>），
    /// TapTap 等渠道的配置把 <c>IsAuditMode</c>/<c>IsAngelMode</c> 置 true 且缺省值也是 true
    /// （fail-closed）。热更代码在打开设置窗口时按 <c>IsAuditMode</c> 决定是否显示
    /// 天使模式开关（UI.SettingWindow.LiveMode_Refresh → isAuditMode 控制器）。
    ///
    /// 本模组在热更初始化后把这两个静态标记清为 false，并在运行期间保持
    /// （游戏重新拉取 RemoteConfig 或重登时可能再写回 true，看门狗会再次清掉）。
    /// 清掉后设置页的行为与 Steam 国服一致：开关可见、可自由切换，
    /// 切换走游戏原生链路（GameSettings.angelMode → UpdateAdultModeState →
    /// SyncHarmonyStatus → ClientHarmonyC2S 5369 → UIManager.SwitchAdultMode）。
    ///
    /// 另外提供 angelMode= sfw/unsfw 强制固定项（默认 keep 不干预）。
    /// </summary>
    public sealed class AngelModeUnlockController : ModBase
    {
        /// <summary>看门狗检查间隔(秒)。RemoteConfig 只在启动/重拉时写回，低频即可。</summary>
        private const float WatchdogIntervalSeconds = 2f;

        private bool _enabled = true;
        private bool _unlockAudit = true;
        private bool _unlockAngel = true;
        private bool _restoreSwitch = true;
        private string _switchTitle = string.Empty;
        private string _angelMode = "keep";

        private CesiumLoader.SDK.Logging.ModLogger _logger;
        private SettingsLiveModeUi _settingsUi;
        private bool _lastAuditMode;

        // 反射缓存(首次解析后复用; 解析不到保持 null 并在日志里说明)
        private Type _rcType;
        // RemoteConfig 成员可能是静态属性也可能是静态字段, 解析时属性→字段两级兼容
        private MemberInfo _rcAuditMode;
        private MemberInfo _rcAngelMode;
        private MemberInfo _rcJsonData;
        private FieldInfo _gsAngelMode;
        private MethodInfo _gsUpdateAdultMode;
        private PropertyInfo _uiManagerInst;
        private PropertyInfo _uiManagerHasInstance;
        private MethodInfo _uiSwitchAdultMode;

        private float _nextWatchdog;
        private bool _auditUnlockedReported;
        private bool _angelUnlockedReported;
        private bool _remoteConfigLogged;
        private bool _resolveFailureLogged;
        private int _angelForcedCount;

        public override string Name { get { return "天使模式解锁"; } }
        public override string Version { get { return "1.0.1"; } }
        public override string Author { get { return "CesiumLoader"; } }
        public override string Description
        {
            get { return "解除审核渠道对天使模式开关的封锁，设置页可像 Steam 国服一样自由切换。"; }
        }

        public override void OnInitialize()
        {
            LoadConfig(isInitial: true);
            _logger = Log;
            _settingsUi = new SettingsLiveModeUi(_logger);
            Log.Info("天使模式解锁已就绪: enabled=" + _enabled
                + ", unlockAuditMode=" + _unlockAudit
                + ", unlockAngelMode=" + _unlockAngel
                + ", restoreSwitch=" + _restoreSwitch
                + ", angelMode=" + _angelMode);
        }

        public override void OnConfigChanged()
        {
            LoadConfig(isInitial: false);
            Log.Info("天使模式解锁配置已热重载: unlockAuditMode=" + _unlockAudit
                + ", unlockAngelMode=" + _unlockAngel
                + ", restoreSwitch=" + _restoreSwitch
                + ", angelMode=" + _angelMode);
        }

        private void LoadConfig(bool isInitial)
        {
            if (Config == null) return;
            _enabled = Config.GetBool("enabled", _enabled);
            _unlockAudit = Config.GetBool("unlockAuditMode", _unlockAudit);
            _unlockAngel = Config.GetBool("unlockAngelMode", _unlockAngel);
            _restoreSwitch = Config.GetBool("restoreSwitch", _restoreSwitch);
            _switchTitle = Config.GetString("switchTitle", _switchTitle) ?? string.Empty;
            string angel = Config.GetString("angelMode", _angelMode);
            _angelMode = NormalizeAngelMode(angel, _angelMode);

            if (isInitial)
            {
                AngelModeUnlockConfigDocs.Apply(Config);
                Config.SetDefault("enabled", _enabled);
                Config.SetDefault("unlockAuditMode", _unlockAudit);
                Config.SetDefault("unlockAngelMode", _unlockAngel);
                Config.SetDefault("restoreSwitch", _restoreSwitch);
                Config.SetDefault("switchTitle", _switchTitle);
                Config.SetDefault("angelMode", _angelMode);
                Config.SaveIfDirty();
            }
        }

        private static string NormalizeAngelMode(string value, string fallback)
        {
            if (string.IsNullOrEmpty(value)) return fallback;
            string v = value.Trim().ToLowerInvariant();
            if (v == "keep" || v == "sfw" || v == "unsfw") return v;
            return fallback;
        }

        public override void OnUpdate()
        {
            if (!_enabled) return;

            // 设置页开关行每帧复核: 删减版 UI 包会在切页签/重开窗口时用控制器和 GGroup
            // 重新把它藏起来, 低频轮询会留下"打开设置又没了"。这里只读缓存字段, 开销是一次字段读。
            if (_settingsUi != null)
            {
                _settingsUi.Tick(_lastAuditMode, _restoreSwitch, _switchTitle);
            }

            if (UnityTime.RealtimeSinceStartup < _nextWatchdog) return;
            _nextWatchdog = UnityTime.RealtimeSinceStartup + WatchdogIntervalSeconds;

            try
            {
                Tick();
            }
            catch (Exception e)
            {
                Log.ReportCrash("Watchdog", e);
            }
        }

        public override void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // 场景切换时立即跑一轮：登录后进主城、重连等时机都要重新对齐
            _nextWatchdog = 0f;
        }

        public override void OnUnload()
        {
            Log.Info("天使模式解锁已卸载（本次强制 angelMode " + _angelForcedCount + " 次）；重启游戏恢复渠道原始行为。");
        }

        private void Tick()
        {
            Type remoteConfigType = ResolveRemoteConfig();
            if (remoteConfigType == null)
            {
                // 解析失败每次都会重试(RemoteConfig 所属程序集可能比 mod 更晚就绪); 失败原因只报一次
                if (!_resolveFailureLogged)
                {
                    _resolveFailureLogged = true;
                    var names = RuntimeAssemblyService.GetAssemblyNames();
                    Log.Warn("未找到 Tools.RemoteConfig, 本模组暂不介入。已加载程序集 "
                        + names.Count + " 个: " + string.Join(", ", names.ToArray()));
                }
                return;
            }

            try { LogRemoteConfigOnce(remoteConfigType); }
            catch (Exception e) { Log.Debug("RemoteConfig 初始状态日志失败: " + e.Message); }

            if (_rcAuditMode != null)
            {
                try
                {
                    bool current = ReadFlag(_rcAuditMode, remoteConfigType);
                    // 设置页把控制器压到非审核页(1)的判断依据, 即使不清标记也要读一次真实值
                    _lastAuditMode = current;
                    if (_unlockAudit && current)
                    {
                        WriteFlag(_rcAuditMode, remoteConfigType, false);
                        _lastAuditMode = false;
                        if (!_auditUnlockedReported)
                        {
                            _auditUnlockedReported = true;
                            Log.Info("RemoteConfig.IsAuditMode " + current + " → false：审核模式已解除，设置页将按非审核渠道显示。");
                            UiService.Notify("天使模式开关已解锁", UiNotificationLevel.Success, 3f, Context);
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Warn("清 IsAuditMode 失败: " + e.Message);
                }
            }

            if (_unlockAngel && _rcAngelMode != null)
            {
                try
                {
                    bool current = ReadFlag(_rcAngelMode, remoteConfigType);
                    if (current)
                    {
                        WriteFlag(_rcAngelMode, remoteConfigType, false);
                        if (!_angelUnlockedReported)
                        {
                            _angelUnlockedReported = true;
                            Log.Info("RemoteConfig.IsAngelMode " + current + " → false：天使模式限制标记已清除。");
                        }
                    }
                }
                catch (Exception e)
                {
                    Log.Warn("清 IsAngelMode 失败: " + e.Message);
                }
            }

            if (_angelMode != "keep")
            {
                ForceAngelMode(_angelMode == "sfw");
            }
        }

        // ------------------------------------------------------------------
        // 反射解析
        // ------------------------------------------------------------------

        // RemoteConfig 的真实全名以 global-metadata.dat 为准: 类型标识串只有 "RemoteConfig"
        // (无 get_/set_ 前缀的 IsAuditMode/IsAngelMode 是静态字段, 无 <...>k__BackingField),
        // 配置加载器的字面量也按简单类名 '|' 拼接 —— 类型大概率在全局命名空间。
        // 保守起见按候选列表尝试, Tools.RemoteConfig 仅作兜底。
        private static readonly string[] RemoteConfigCandidates = { "RemoteConfig", "Tools.RemoteConfig" };

        private Type ResolveRemoteConfig()
        {
            if (_rcAuditMode == null || _rcAngelMode == null)
            {
                Type type = null;
                foreach (var candidate in RemoteConfigCandidates)
                {
                    type = FindTypeRobust(candidate);
                    if (type != null)
                    {
                        if (candidate != RemoteConfigCandidates[0])
                            Log.Info("RemoteConfig 解析命中候选 " + candidate);
                        break;
                    }
                }
                if (type == null) return null;
                _rcType = type;
                _rcAuditMode = FindStaticMember(type, "IsAuditMode");
                _rcAngelMode = FindStaticMember(type, "IsAngelMode");
                _rcJsonData = FindStaticMember(type, "JsonData");
                if (_rcAuditMode == null || _rcAngelMode == null)
                {
                    Log.Warn(type.FullName + " 上没有 IsAuditMode/IsAngelMode 成员，本模组不介入。");
                    return null;
                }
                Log.Info("已解析 " + type.FullName + "（程序集 " + SafeAssemblyName(type.Assembly) + "）");
            }
            return _rcType;
        }

        // 游戏的 AOT 镜像裁掉了 Assembly.GetTypes()，SDK 的全程序集类型索引因此恒为空，
        // FindType 全局查找永远 miss（SpeedHackMod 也记了同样的坑）。改用 Bootstrap 验证过的
        // 按名查找多级 fallback：Assembly.GetType(单参) → Type.GetType("全名, 程序集名")。
        private Type FindTypeRobust(string fullName)
        {
            Type type = null;
            try { type = RuntimeAssemblyService.FindType(fullName); }
            catch { /* 索引构建失败按 miss 处理 */ }
            if (type != null) return type;

            foreach (var asm in RuntimeAssemblyService.GetAssemblies())
            {
                type = TryGetTypeOnAssembly(asm, fullName);
                if (type != null) return type;
            }
            return null;
        }

        /// <summary>
        /// 单个程序集上的按名查找，隔离成方法：一旦 <c>Assembly.GetType</c> 在 AOT 镜像里
        /// 被裁剪，JIT 失败的异常在调用点抛出，只有调用方的 try/catch 接得住。
        /// </summary>
        private static Type TryGetTypeOnAssembly(Assembly asm, string fullName)
        {
            if (asm == null) return null;
            try
            {
                Type type = asm.GetType(fullName); // 单参重载, Bootstrap 已验证游戏内可用
                if (type != null) return type;
            }
            catch { }
            try
            {
                return Type.GetType(fullName + ", " + asm.GetName().Name);
            }
            catch { return null; }
        }

        private static string SafeAssemblyName(Assembly assembly)
        {
            try { return assembly.GetName().Name; }
            catch { return "?"; }
        }

        private void LogRemoteConfigOnce(Type remoteConfigType)
        {
            if (_remoteConfigLogged || _rcJsonData == null) return;
            _remoteConfigLogged = true;
            try
            {
                object json;
                if (_rcJsonData is PropertyInfo jsonProperty) json = jsonProperty.GetValue(null, null);
                else json = ((FieldInfo)_rcJsonData).GetValue(null);
                bool audit = ReadFlag(_rcAuditMode, remoteConfigType);
                bool angel = ReadFlag(_rcAngelMode, remoteConfigType);
                // 服务器 JSON 的两个标记已经反映在静态属性上; JsonData 只报有无,
                // 不调用 JSONNode.ToString —— 它若被 AOT 裁剪, 调用点 JIT 会失败。
                Log.Info("RemoteConfig 初始状态: IsAuditMode=" + audit + ", IsAngelMode=" + angel
                    + ", JsonData=" + (json == null ? "null（服务器配置未取到，缺省值也是 true）" : "已获取"));
            }
            catch (Exception e)
            {
                Log.Debug("读取 RemoteConfig 初始状态失败: " + e.Message);
            }
        }

        private static MemberInfo FindStaticMember(Type type, string name)
        {
            PropertyInfo property = RuntimeAssemblyService.FindProperty(type, name);
            if (property != null) return property;
            return RuntimeAssemblyService.FindField(type, name);
        }

        private static bool ReadFlag(MemberInfo member, Type owner)
        {
            object value;
            if (member is PropertyInfo property) value = property.GetValue(null, null);
            else value = ((FieldInfo)member).GetValue(null);
            if (value is bool) return (bool)value;
            throw new Exception(owner.Name + "." + member.Name + " 不是 bool（实际 " + (value == null ? "null" : value.GetType().Name) + "）");
        }

        private static void WriteFlag(MemberInfo member, Type owner, bool value)
        {
            if (member is PropertyInfo property)
            {
                if (property.CanWrite)
                {
                    property.SetValue(null, value, null);
                    return;
                }
                // 属性只读时退回自动属性后备字段（IL2CPP 下名字形如 <X>k__BackingField）
                FieldInfo backing = owner.GetField("<" + property.Name + ">k__BackingField",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (backing == null)
                    throw new Exception(owner.Name + "." + property.Name + " 不可写且无后备字段");
                backing.SetValue(null, value);
                return;
            }
            FieldInfo field = member as FieldInfo;
            if (field == null)
                throw new Exception(owner.Name + "." + member.Name + " 不是可写的静态属性或字段");
            field.SetValue(null, value);
        }

        // ------------------------------------------------------------------
        // 强制天使模式
        // ------------------------------------------------------------------

        private void ForceAngelMode(bool state)
        {
            try
            {
                if (_gsAngelMode == null)
                {
                    Type settings = FindTypeRobust("Core.GameSettings");
                    if (settings == null) return;
                    _gsAngelMode = RuntimeAssemblyService.FindField(settings, "angelMode");
                    _gsUpdateAdultMode = RuntimeAssemblyService.FindMethod(settings, "UpdateAdultModeState", 1, includeNonPublic: true);
                    if (_gsAngelMode == null)
                    {
                        Log.Warn("Core.GameSettings.angelMode 不可用，强制固定项不生效。");
                        _angelMode = "keep";
                        return;
                    }
                }

                object current = _gsAngelMode.GetValue(null);
                bool currentBool = current is bool && (bool)current;
                if (currentBool == state) return;

                _gsAngelMode.SetValue(null, state);
                Log.Info("GameSettings.angelMode " + currentBool + " → " + state + "（angelMode=" + _angelMode + "，第 "
                    + (_angelForcedCount + 1) + " 次强制）");
                _angelForcedCount++;

                if (_gsUpdateAdultMode != null)
                {
                    // 游戏原生链路: SyncHarmonyStatus(登录后才真正发 RPC) + ES3 存档
                    _gsUpdateAdultMode.Invoke(null, new object[] { state });
                }

                MethodInfo switchAdultMode = ResolveSwitchAdultMode();
                if (switchAdultMode != null)
                {
                    switchAdultMode.Invoke(GetUiManager(), new object[] { state });
                }
            }
            catch (Exception e)
            {
                Log.Warn("强制 angelMode 失败: " + e.Message);
            }
        }

        private MethodInfo ResolveSwitchAdultMode()
        {
            if (_uiSwitchAdultMode != null) return _uiSwitchAdultMode;
            Type managerType = FindTypeRobust("UI.UIManager");
            if (managerType == null) return null;
            _uiSwitchAdultMode = RuntimeAssemblyService.FindMethod(managerType, "SwitchAdultMode", 1);
            return _uiSwitchAdultMode;
        }

        private object GetUiManager()
        {
            try
            {
                if (_uiManagerInst == null)
                {
                    var provider = FindTypeRobust("Tools.SimpleSingletonProvider`1");
                    if (provider == null) return null;
                    var managerType = FindTypeRobust("UI.UIManager");
                    if (managerType == null) return null;
                    var closed = provider.MakeGenericType(managerType);
                    _uiManagerHasInstance = RuntimeAssemblyService.FindProperty(closed, "hasInstance");
                    _uiManagerInst = RuntimeAssemblyService.FindProperty(closed, "inst");
                }

                // hasInstance=false 时不要碰 inst: SimpleSingletonProvider 会直接 new 一个 UIManager
                object has = _uiManagerHasInstance != null ? _uiManagerHasInstance.GetValue(null, null) : null;
                if (!(has is bool) || !(bool)has) return null;
                return _uiManagerInst.GetValue(null, null);
            }
            catch (Exception e)
            {
                Log.Debug("获取 UIManager 失败: " + e.Message);
                return null;
            }
        }
    }
}
