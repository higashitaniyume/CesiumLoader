using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CesiumLoader.SDK.Internals;
using CesiumLoader.SDK.Scenes;

namespace CesiumLoader.SDK.Diagnostics
{
    /// <summary>采集场景信息。</summary>
    public static class SceneDiagnostics
    {
        /// <summary>采集场景快照。</summary>
        public static SceneDump Collect()
        {
            var dump = new SceneDump { CapturedUtc = DateTime.UtcNow.ToString("o") };

            try
            {
                dump.SceneCount = SceneService.GetSceneCount();
                dump.Scenes = SceneService.GetLoadedSceneNames();

                var active = SceneService.GetActiveScene();
                dump.ActiveSceneName = UnityCall.SceneName(active);
                dump.ActiveSceneHandle = UnityCall.SceneHandle(active);

                var roots = SceneService.GetRootGameObjects(active);
                dump.RootObjectCount = roots.Length;
                for (int i = 0; i < roots.Length && i < 60; i++)
                {
                    var go = roots[i];
                    if (go == null) continue;
                    dump.RootObjects.Add(UnityCall.Name(go) + (UnityCall.ActiveInHierarchy(go) ? "" : " (未激活)"));
                }
                if (roots.Length > 60) dump.RootObjects.Add("... 还有 " + (roots.Length - 60) + " 个");
            }
            catch (Exception e)
            {
                dump.RootObjects.Add("采集场景信息失败: " + e.Message);
            }

            try
            {
                dump.SceneSubscribers = SceneService.SceneLoadedSubscriberCount +
                                        SceneService.SceneUnloadedSubscriberCount +
                                        SceneService.ActiveSceneChangedSubscriberCount;
                dump.ForwardedLoads = SceneService.ForwardedLoadCount;
                dump.ForwardedActiveChanges = SceneService.ForwardedActiveChangeCount;
            }
            catch { }

            return dump;
        }

        private static string SafeString(Func<string> get)
        {
            try { return get(); } catch { return null; }
        }
    }
}
