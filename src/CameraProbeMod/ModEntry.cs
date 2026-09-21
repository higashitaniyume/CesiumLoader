using System;
using System.Text;
using CesiumLoader.SDK;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CameraProbeMod
{
    /// <summary>
    /// 入口。原生加载器以 <c>CameraProbeMod.ModEntry.Main()</c> 调用(0 参数)。
    /// 注意: 它运行在加载器的 boot 线程上, <b>不是</b> Unity 主线程 ——
    /// 所以这里只做"启动", 真正的 Unity 访问交给 SDK 在主线程执行。
    /// </summary>
    public static class ModEntry
    {
        public static void Main()
        {
            // 新式生命周期: SDK 负责主线程切换 / 场景事件 / 卸载清理
            ModBase.Run(new CameraProbe());
        }
    }

    /// <summary>
    /// 相机探针: 打印相机细节, 用于确认自由相机方案是否可行。
    /// 默认按键 F9 重新探测。
    /// </summary>
    public sealed class CameraProbe : ModBase
    {
        private KeyCode _probeKey = KeyCode.F9;
        private ModConfig _config;

        /// <summary>显示名。</summary>
        public override string Name { get { return "相机探针"; } }

        /// <summary>版本。</summary>
        public override string Version { get { return "2.1.3"; } }

        /// <summary>初始化(主线程, 启动延迟之后)。</summary>
        public override void OnInitialize()
        {
            _config = Config;
            if (_config != null)
            {
                string keyName = _config.GetString("probeKey", "F9");
                KeyCode parsed;
                if (Enum.TryParse(keyName, true, out parsed)) _probeKey = parsed;
                Log.Info("探测按键: " + _probeKey + " (可在 config.json 里改 probeKey)");
            }

            Log.Info("相机探针已就绪, 按 " + _probeKey + " 输出相机信息");
            Probe("初始化");
        }

        /// <summary>场景加载完成后自动重新探测。</summary>
        public override void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            Probe("场景加载 " + scene.name);
        }

        /// <summary>每帧: 只处理按键。</summary>
        public override void OnUpdate()
        {
            try
            {
                if (InputService.IsKeyPressed(_probeKey)) Probe("手动");
            }
            catch (Exception e)
            {
                Log.ReportCrash("OnUpdate", e);
            }
        }

        /// <summary>卸载。</summary>
        public override void OnUnload()
        {
            Log.Info("相机探针已卸载");
        }

        /// <summary>探测并输出相机信息。</summary>
        public static void Probe(string reason)
        {
            try
            {
                var sb = new StringBuilder(2048);
                sb.AppendLine("========== 相机探针 (" + reason + ") ==========");

                // ---- Cinemachine 接管情况 ----
                bool cmAvailable = CinemachineService.IsAvailable;
                bool cmInitialized = CinemachineService.IsInitialized;
                object brain = cmAvailable ? CinemachineService.FindBrain() : null;

                sb.AppendLine("Cinemachine 可用   : " + (cmAvailable ? "是" : "否") +
                              "  (已初始化: " + (cmInitialized ? "是" : "否") + ")");
                if (brain != null)
                {
                    sb.AppendLine("Cinemachine Brain : " + UnityObject.GetHierarchyPath(brain as UnityEngine.Object) +
                                  " enabled=" + CinemachineService.IsBrainEnabled(brain) +
                                  " blending=" + CinemachineService.IsBlending(brain));
                    sb.AppendLine("当前接管相机      : " +
                                  (CinemachineService.GetActiveVirtualCameraName(brain) ?? "(无)"));
                }

                // ---- 全部相机 ----
                var cameras = CameraService.GetAllCameras(true);
                sb.AppendLine("相机总数          : " + cameras.Length);

                Camera main = CameraService.GetMainCamera();
                sb.AppendLine("主相机            : " + (UnityObject.IsAlive(main) ? Describe(main) : "(未找到)"));
                if (UnityObject.IsAlive(main))
                {
                    sb.AppendLine("  层级路径        : " + UnityObject.GetHierarchyPath(main));
                    sb.AppendLine("  状态            : " + CameraService.CaptureState(main).Describe());
                }

                for (int i = 0; i < cameras.Length; i++)
                {
                    var cam = cameras[i];
                    if (cam == null) continue;
                    bool isMain = UnityObject.IsAlive(main) && cam.GetInstanceID() == main.GetInstanceID();
                    sb.AppendLine((isMain ? "  [主] " : "  [" + i + "] ") + Describe(cam));
                }

                // ---- 可行性判断 ----
                sb.AppendLine("--- 自由相机可行性 ---");
                sb.AppendLine("  直接改 Camera.main.transform : " +
                              (brain != null ? "不可靠(Brain 每帧覆盖)" : "可行(无 Brain)"));
                sb.AppendLine("  经 Cinemachine VirtualCamera : " +
                              (cmAvailable ? "推荐(优先方案)" : "不可用(无 Cinemachine)"));
                sb.AppendLine("  回退(禁用 Brain 后直改相机)  : " +
                              (brain != null ? "可行(会暂时中断游戏相机控制)" : "可行"));
                sb.AppendLine("  输入后端                     : " + InputService.BackendName);
                sb.AppendLine("===============================================");

                SdkLog.Info("CameraProbe", sb.ToString());
            }
            catch (Exception e)
            {
                SdkLog.ReportCrash("CameraProbe", "Probe", e);
            }
        }

        private static string Describe(Camera cam)
        {
            try
            {
                return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "{0} tag={1} enabled={2} activeInHierarchy={3} depth={4:F2} fov={5:F1} near={6:F3} far={7:F1} ortho={8} size={9:F2} pos={10}",
                    cam.name, cam.tag, cam.enabled, cam.gameObject.activeInHierarchy,
                    cam.depth, cam.fieldOfView, cam.nearClipPlane, cam.farClipPlane,
                    cam.orthographic, cam.orthographicSize, cam.transform.position);
            }
            catch (Exception e)
            {
                return "(读取失败: " + e.Message + ")";
            }
        }
    }
}
