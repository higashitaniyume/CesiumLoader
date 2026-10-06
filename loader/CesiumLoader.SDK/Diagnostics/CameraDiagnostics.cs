using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using CesiumLoader.SDK.Cameras;
using CesiumLoader.SDK.Engine;

namespace CesiumLoader.SDK.Diagnostics
{
    /// <summary>采集相机信息。</summary>
    public static class CameraDiagnostics
    {
        /// <summary>采集相机快照。</summary>
        public static CameraDump Collect()
        {
            var dump = new CameraDump { CapturedUtc = DateTime.UtcNow.ToString("o") };

            try
            {
                var cameras = CameraService.GetAllCameras(true);
                dump.CameraCount = cameras.Length;

                var main = CameraService.GetMainCamera();
                dump.HasMainCamera = UnityObject.IsAlive(main);
                if (dump.HasMainCamera)
                {
                    dump.MainCameraName = UnityObject.GetName(main);
                    dump.MainCameraPath = UnityObject.GetHierarchyPath(main);
                    dump.MainCameraState = CameraService.CaptureState(main);
                }

                for (int i = 0; i < cameras.Length && i < 40; i++)
                {
                    dump.Cameras.Add(CameraService.DescribeCamera(cameras[i]));
                }
                if (cameras.Length > 40) dump.Cameras.Add("... 还有 " + (cameras.Length - 40) + " 台相机");
            }
            catch (Exception e)
            {
                dump.Cameras.Add("采集相机信息失败: " + e.Message);
            }

            try
            {
                dump.CinemachineAvailable = CinemachineService.IsAvailable;
                dump.CinemachineInitialized = CinemachineService.IsInitialized;

                var brain = CinemachineService.FindBrain();
                if (brain != null)
                {
                    dump.BrainEnabled = CinemachineService.IsBrainEnabled(brain);
                    dump.BrainBlending = CinemachineService.IsBlending(brain);
                    dump.ActiveVirtualCamera = CinemachineService.GetActiveVirtualCameraName(brain);
                }

                dump.CinemachineDetail = CinemachineService.Describe();
            }
            catch (Exception e)
            {
                dump.CinemachineDetail = "采集 Cinemachine 信息失败: " + e.Message;
            }

            return dump;
        }
    }
}
