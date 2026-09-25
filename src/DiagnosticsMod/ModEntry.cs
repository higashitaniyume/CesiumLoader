using System;
using CesiumLoader.SDK;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DiagnosticsMod
{
    /// <summary>入口。原生加载器调用 <c>DiagnosticsMod.ModEntry.Main()</c>。</summary>
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new DiagnosticsModule());
        }
    }

    /// <summary>
    /// 诊断模块: 把运行时/相机/场景信息转储到 <c>logs\cesium-loader.log</c>。
    ///
    /// 按键(可在 config.json 修改):
    ///   F10  全量转储(运行时 + 相机 + 场景 + UI + 计数器)
    ///   F11  只转储相机信息
    ///   F12  只转储场景信息
    ///
    /// 为什么要有它: 出问题时最需要的信息是"主线程状态 / 相机是谁在接管 /
    /// 场景是否切了 / 各服务订阅数", 这些恰好都只能从 SDK 内部拿到。
    /// </summary>
    public sealed class DiagnosticsModule : ModBase
    {
        private ModConfig _config;
        private KeyCode _dumpAllKey = KeyCode.F10;
        private KeyCode _dumpCameraKey = KeyCode.F11;
        private KeyCode _dumpSceneKey = KeyCode.F12;
        private bool _dumpOnSceneChange = true;
        private int _dumpCount;

        /// <summary>显示名。</summary>
        public override string Name { get { return "诊断工具"; } }

        /// <summary>版本。</summary>
        public override string Version { get { return "2.2.1"; } }

        /// <summary>初始化。</summary>
        public override void OnInitialize()
        {
            _config = Config;
            if (_config != null)
            {
                _dumpAllKey = ParseKey(_config.GetString("dumpAllKey", "F10"), _dumpAllKey);
                _dumpCameraKey = ParseKey(_config.GetString("dumpCameraKey", "F11"), _dumpCameraKey);
                _dumpSceneKey = ParseKey(_config.GetString("dumpSceneKey", "F12"), _dumpSceneKey);
                _dumpOnSceneChange = _config.GetBool("dumpSceneOnChange", true);

                if (!_config.Has("dumpAllKey"))
                {
                    _config.Set("dumpAllKey", _dumpAllKey.ToString());
                    _config.Set("dumpCameraKey", _dumpCameraKey.ToString());
                    _config.Set("dumpSceneKey", _dumpSceneKey.ToString());
                    _config.Set("dumpOnSceneChange", _dumpOnSceneChange);
                    _config.Save();
                }
            }

            Log.Info("诊断工具已就绪: " + _dumpAllKey + "=全量, " +
                     _dumpCameraKey + "=相机, " + _dumpSceneKey + "=场景");
            Log.Info("输出文件: " + SdkDiagnostics.GetDumpFilePath());
            Log.Info("当前摘要: " + SdkDiagnostics.Summary());

            // 启动时先存一份基线, 便于和出问题时的现场对比
            SafeDump(() => SdkDiagnostics.Dump("启动基线"), "启动基线");
        }

        /// <summary>每帧: 只处理按键。</summary>
        public override void OnUpdate()
        {
            try
            {
                if (InputService.IsKeyPressed(_dumpAllKey)) SafeDump(() => SdkDiagnostics.Dump("手动全量"), "全量");
                else if (InputService.IsKeyPressed(_dumpCameraKey)) SafeDump(() => SdkDiagnostics.DumpCameraInfo(), "相机");
                else if (InputService.IsKeyPressed(_dumpSceneKey)) SafeDump(() => SdkDiagnostics.DumpSceneInfo(), "场景");
            }
            catch (Exception e)
            {
                Log.ReportCrash("OnUpdate", e);
            }
        }

        /// <summary>场景切换时自动存一份(便于回看切场景前后的相机接管情况)。</summary>
        public override void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!_dumpOnSceneChange) return;

            // 延后一帧: 场景刚加载完时相机可能还没建立
            CoroutineService.StartCoroutine(DumpAfterDelay(scene.name, 1f));
        }

        private System.Collections.IEnumerator DumpAfterDelay(string sceneName, float seconds)
        {
            yield return new RoutineWaitSeconds(seconds);
            SafeDump(() => SdkDiagnostics.Dump("场景加载后: " + sceneName), "场景 " + sceneName);
        }

        /// <summary>卸载。</summary>
        public override void OnUnload()
        {
            Log.Info("诊断工具已卸载 (本次共转储 " + _dumpCount + " 次)");
        }

        private void SafeDump(Func<string> dump, string label)
        {
            try
            {
                string path = dump();
                _dumpCount++;
                UiService.Notify("诊断已写入: " + label, UiNotificationLevel.Success, 2f, Context);
                if (!string.IsNullOrEmpty(path)) Log.Info("诊断文件: " + path);
            }
            catch (Exception e)
            {
                Log.ReportCrash("Dump/" + label, e);
            }
        }

        private static KeyCode ParseKey(string name, KeyCode fallback)
        {
            if (string.IsNullOrEmpty(name)) return fallback;
            try
            {
                KeyCode parsed;
                return Enum.TryParse(name, true, out parsed) ? parsed : fallback;
            }
            catch { return fallback; }
        }
    }
}
