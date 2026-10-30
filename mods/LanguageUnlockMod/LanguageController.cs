using System;
using System.Collections.Generic;
using System.IO;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Logging;
using CesiumLoader.SDK.Mods;
using Core;
using Cysharp.Threading.Tasks;
using Tools;

namespace LanguageUnlockMod
{
    /// <summary>
    /// 国服多语言支持。
    ///
    /// 国服客户端只带简中：设置里那 4 个语言选项在配置表里是齐的，但切换函数被剪成了空实现，
    /// 英 / 日 / 繁的正文与界面文案也没有随包下发。模组做三件事：
    ///
    ///  1. 接管设置界面的语言下拉框（替换游戏那句空实现）；
    ///  2. 切换时把国际服的同版本翻译写进 STR 配置表，并用自带 XML 设置 FairyGUI 文案；
    ///  3. 摘掉目标语言的 LanguageDict 条目，避免 UIManager 去加载国服不存在的文案包而卡死登录。
    ///
    /// 国际服原生支持 4 种语言，模组不介入。
    /// </summary>
    public sealed class LanguageController : ModBase
    {
        private ModConfig _config;
        private LanguageData _data;
        private SettingsUi _ui;

        private bool _active;
        private bool _enabled = true;
        private bool _applying;
        private LanguageType _selected = LanguageType.SimplifiedChinese;
        private LanguageType _pending = LanguageType.None;
        private bool _pendingReload;
        private readonly HashSet<LanguageType> _merged = new HashSet<LanguageType>();

        public override string Name { get { return "多语言支持"; } }
        public override string Version { get { return "1.0.0"; } }
        public override string Author { get { return "CesiumLoader"; } }

        public override string Description
        {
            get { return "在国服客户端启用设置里已有的英文 / 日文 / 繁中选项。"; }
        }

        /// <summary>当前选中的语言（设置界面据此显示）。</summary>
        internal LanguageType Selected { get { return _selected; } }

        public override void OnInitialize()
        {
            try
            {
                _config = Config;
                EnsureConfigDefaults();

                _enabled = _config == null || _config.GetBool("enabled", true);
                if (!IsChineseClient())
                {
                    Log.Info("非国服客户端: 游戏原生支持语言切换, 模组不介入");
                    return;
                }

                _data = new LanguageData(ResolveDirectory());
                LanguageRuntime.EnsureLanguageEntries(message => Log.Warn(message));

                _active = true;
                _ui = new SettingsUi(this, Log);

                LanguageType saved;
                if (!LanguageCodes.TryParseConfig(_config == null ? null : _config.GetString(LanguageCodes.ConfigKey, LanguageCodes.CodeSimplified), out saved))
                {
                    Log.Warn("配置里的 language 无效, 使用简中");
                    saved = LanguageType.SimplifiedChinese;
                }
                if (!_enabled) saved = LanguageType.SimplifiedChinese;

                _selected = saved;
                Log.Info("目标语言: " + LanguageCodes.DisplayName(_selected) + ", 数据目录: " + _data.Root);

                if (_selected == LanguageType.SimplifiedChinese) return;

                if (!_data.Has(_selected))
                {
                    Log.Error("缺少 " + LanguageCodes.DisplayName(_selected) + " 的数据包, 保持简中");
                    _selected = LanguageType.SimplifiedChinese;
                    return;
                }

                SwitchTo(_selected, reload: true).Forget();
            }
            catch (Exception e)
            {
                Log.Error("初始化失败: " + e);
            }
        }

        public override void OnUpdate()
        {
            if (_ui != null) _ui.Tick();

            // 配置表还没加载完时把切换挂起, 就绪后再继续（登录界面初始化前调用的情况）。
            if (_pending != LanguageType.None && StaticConfigure.Settings != null && StaticConfigure.Font != null)
            {
                LanguageType target = _pending;
                bool reload = _pendingReload;
                _pending = LanguageType.None;
                SwitchTo(target, reload).Forget();
            }
        }

        public override void OnConfigChanged()
        {
            try
            {
                if (_config == null) return;

                bool enabled = _config.GetBool("enabled", true);
                LanguageType target;
                if (!LanguageCodes.TryParseConfig(_config.GetString(LanguageCodes.ConfigKey, LanguageCodes.CodeSimplified), out target))
                {
                    return;
                }
                if (!enabled) target = LanguageType.SimplifiedChinese;

                _enabled = enabled;
                if (target == _selected) return;

                SwitchTo(target, reload: true).Forget();
            }
            catch (Exception e)
            {
                Log.Warn("应用配置失败: " + e.Message);
            }
        }

        public override void OnUnload()
        {
            if (_ui != null)
            {
                _ui.Release();
                _ui = null;
            }
            _active = false;
        }

        // =====================================================================
        // 切换
        // =====================================================================

        /// <summary>设置界面确认后调用。简中直接切回，其余语言走数据包。</summary>
        internal void RequestSwitch(LanguageType target)
        {
            if (!_active) return;
            if (!LanguageCodes.IsSupported(target))
            {
                Log.Warn("不支持的语言: " + target);
                return;
            }
            SwitchTo(target, reload: true).Forget();
        }

        private async UniTaskVoid SwitchTo(LanguageType target, bool reload)
        {
            if (_applying)
            {
                Log.Warn("正在切换语言, 忽略重复请求");
                return;
            }

            if (target != LanguageType.SimplifiedChinese
                && (StaticConfigure.Settings == null || StaticConfigure.Font == null))
            {
                Log.Info("游戏配置表尚未就绪, 稍后重试: " + LanguageCodes.DisplayName(target));
                _pending = target;
                _pendingReload = reload;
                return;
            }

            _applying = true;
            try
            {
                if (target == LanguageType.SimplifiedChinese)
                {
                    GameSettings.languageType = LanguageType.SimplifiedChinese;
                    _selected = LanguageType.SimplifiedChinese;
                    SaveConfig();
                    Log.Info("已切回简中");
                    if (reload) ReLogin();
                    return;
                }

                if (_data == null || !_data.Has(target))
                {
                    Log.Error("缺少 " + LanguageCodes.DisplayName(target) + " 的语言数据, 保持当前语言");
                    return;
                }

                string error;
                int remapped = await FontProbe.EnsureAsync(target, Log);
                if (remapped > 0) Log.Warn("有 " + remapped + " 个字体回退到简中字体");

                int bannerImages = LanguageRuntime.AlignBannerImages();
                if (bannerImages > 0) Log.Info("已把 " + bannerImages + " 张横幅图片指回国服素材");

                LanguagePack pack = _data.GetPack(target, out error);
                if (pack == null)
                {
                    Log.Error(error);
                    return;
                }

                if (_merged.Add(target))
                {
                    int written = StrTableMerge.ApplyAll(pack, target);
                    Log.Info("已写入 " + written + " 条" + LanguageCodes.DisplayName(target) + "正文");
                }

                LanguageRuntime.HideLanguageDictionaryEntry(target);

                if (!LanguageRuntime.ApplyStrings(_data, target, out error))
                {
                    Log.Error(error);
                    return;
                }

                GameSettings.languageType = target;
                _selected = target;
                SaveConfig();
                Log.Info("已切换语言: " + LanguageCodes.DisplayName(target));

                if (reload) ReLogin();
            }
            catch (Exception e)
            {
                Log.Error("切换语言失败: " + e);
            }
            finally
            {
                _applying = false;
            }
        }

        private static void ReLogin()
        {
            try
            {
                SimpleSingletonProvider<GameManager>.inst.ReLogin().Forget();
            }
            catch (Exception e)
            {
                SdkLog.Error("LanguageUnlockMod", "重新登录失败: " + e.Message);
            }
        }

        // =====================================================================
        // 环境
        // =====================================================================

        private static bool IsChineseClient()
        {
            try
            {
                if (GameSettings.COUNTRY == 2) return true;
            }
            catch { }

            try
            {
                string version = GameSettings.ShowVersion;
                if (!string.IsNullOrEmpty(version) && version.IndexOf("[CN]", StringComparison.Ordinal) >= 0) return true;
            }
            catch { }

            return false;
        }

        private string ResolveDirectory()
        {
            try
            {
                ModContext context = CurrentContext();
                if (context != null && !string.IsNullOrEmpty(context.Directory)) return context.Directory;
            }
            catch { }

            try
            {
                string mods = Environment.GetEnvironmentVariable("CESIUM_MODS_DIR");
                if (!string.IsNullOrEmpty(mods)) return Path.Combine(mods, "LanguageUnlockMod");
            }
            catch { }

            return ".";
        }

        private ModContext CurrentContext()
        {
            try { return Context; }
            catch { return null; }
        }

        // =====================================================================
        // 配置
        // =====================================================================

        private void EnsureConfigDefaults()
        {
            if (_config == null) return;

            _config.SetDefault("enabled", true, ConfigDoc.Of(
                "总开关：关闭后保持简中，并停止接管设置里的语言选项。",
                "總開關：關閉後保持簡中，並停止接管設定裡的語言選項。",
                "Master switch: when off, stay in Simplified Chinese and stop taking over the language selector.",
                "全体スイッチ：オフにすると簡体字中国語のままになり、言語選択の引き継ぎを停止します。"));

            _config.SetDefault(LanguageCodes.ConfigKey, LanguageCodes.CodeSimplified, ConfigDoc.Of(
                "当前语言：zh / en / ja / tc。也可以在游戏「设置→屏幕→语言」里切换。",
                "目前語言：zh / en / ja / tc。也可以在遊戲「設定→螢幕→語言」裡切換。",
                "Current language: zh / en / ja / tc. You can also switch it in the in-game settings.",
                "現在の言語：zh / en / ja / tc。ゲーム内の設定からも切り替えられます。"));

            _config.SaveIfDirty();
        }

        private void SaveConfig()
        {
            try
            {
                if (_config == null) return;
                _config.Set(LanguageCodes.ConfigKey, LanguageCodes.ConfigValue(_selected));
                _config.SaveIfDirty();
            }
            catch (Exception e)
            {
                Log.Warn("保存配置失败: " + e.Message);
            }
        }
    }
}




