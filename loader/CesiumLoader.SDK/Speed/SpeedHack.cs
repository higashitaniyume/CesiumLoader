using System;
using System.Globalization;
using CesiumLoader.SDK.Internals;
using CesiumLoader.SDK.Manifests;

namespace CesiumLoader.SDK.Speed
{
    /// <summary>
    /// Native clock control over process-local UDP (CESIUM_SPEED_PORT).
    /// Requests are asynchronous; no file transport or P/Invoke fallback is used.
    /// Reads pump a bounded nonblocking receive queue. Unknown/expired replies are
    /// not proof of 1x: use TryGetSpeed for acknowledgement-sensitive operations.
    /// Restoring 1x keeps hooks installed and the virtual clock continuous.
    /// </summary>
    public static class SpeedHack
    {
        public const double MinSpeed = 1.0;
        public const double MaxSpeed = 3.0;
        public const double HardMaxSpeed = 100.0;
        public const double DefaultSpeedStep = 0.5;
        private static readonly object Gate = new object();
        private static readonly SpeedUdpChannel Channel = new SpeedUdpChannel(background: true);
        internal static int PortOverride { get; set; }
        internal static void ResetCache() { lock (Gate) Channel.Reset(); }

        /// <summary>Legacy path metadata only; never used for communication.</summary>
        [Obsolete("Speed control uses UDP via CESIUM_SPEED_PORT, not a directory.")]
        public static string Directory
        {
            get { try { return Environment.GetEnvironmentVariable("CESIUM_SPEED_DIR"); } catch { return null; } }
        }

        public static void Refresh() { ResetCache(); }
        public static bool IsAvailable { get { lock (Gate) return Channel.Read(ResolvePort()) != null; } }
        public static string UnavailableReason
        {
            get { lock (Gate) { return Channel.Read(ResolvePort()) == null ? Channel.Error : null; } }
        }
        public static string Diagnostics()
        {
            lock (Gate) return "UDP CESIUM_SPEED_PORT=" + ResolvePort() + " " + (Channel.Error ?? "ready");
        }
        public static string Describe()
        {
            lock (Gate)
            {
                var state = Channel.Read(ResolvePort());
                return state == null ? "变速引擎不可用(" + Channel.Error + ")" :
                    "变速引擎可用 当前 " + state.Speed.ToString("0.###", CultureInfo.InvariantCulture) +
                    "x 基础 " + state.Base.ToString("0.###", CultureInfo.InvariantCulture) + "x UDP 127.0.0.1:" + ResolvePort();
            }
        }

        /// <summary>Fresh acknowledged speed; false means unknown, never implicit 1x.</summary>
        public static bool TryGetSpeed(out double speed)
        {
            lock (Gate)
            {
                var state = Channel.Read(ResolvePort());
                speed = state == null ? 1 : state.Speed;
                return state != null;
            }
        }
        /// <summary>Compatibility API: unknown state returns 1x. Prefer TryGetSpeed.</summary>
        public static double Speed { get { double value; TryGetSpeed(out value); return value; } }
        public static double BaseSpeed { get { lock (Gate) return Channel.Read(ResolvePort())?.Base ?? 1; } }

        /// <summary>
        /// Sends an asynchronous speed request in [1,100]. True means sent, not
        /// applied. The previous acknowledgement is invalidated until a correlated
        /// reply arrives. Lost requests are retried with the same id, not reapplied.
        /// </summary>
        public static bool SetSpeed(double speed)
        {
            if (double.IsNaN(speed) || double.IsInfinity(speed) || speed < MinSpeed || speed > HardMaxSpeed) return false;
            if (!Permissions.Require(ModPermission.SpeedHack, "SpeedHack.SetSpeed")) return false;
            lock (Gate) return Channel.Set(ResolvePort(), Math.Round(speed, 3, MidpointRounding.AwayFromZero));
        }
        public static bool Reset() { return SetSpeed(1); }

        private static int ResolvePort()
        {
            if (PortOverride != 0) return PortOverride > 0 && PortOverride <= 65535 ? PortOverride : 0;
            try
            {
                int port;
                return int.TryParse(Environment.GetEnvironmentVariable("CESIUM_SPEED_PORT"), out port) && port > 0 && port <= 65535 ? port : 0;
            }
            catch { return 0; }
        }

        public static double ClampSpeed(double speed, double min = MinSpeed, double max = MaxSpeed)
        {
            if (double.IsNaN(min) || double.IsInfinity(min) || min <= 0) min = MinSpeed;
            if (double.IsNaN(max) || double.IsInfinity(max) || max <= 0) max = MaxSpeed;
            if (min > max) { double tmp = min; min = max; max = tmp; }
            min = Math.Max(MinSpeed, min); max = Math.Max(MinSpeed, max);
            if (double.IsNaN(speed) || double.IsInfinity(speed)) return MinSpeed;
            if (speed < min) return min;
            if (speed > max) return max;
            return Math.Round(speed, 3, MidpointRounding.AwayFromZero);
        }
        public static double StepSpeed(double current, double delta, double step = DefaultSpeedStep, double min = MinSpeed, double max = MaxSpeed)
        {
            if (double.IsNaN(current) || double.IsInfinity(current)) current = MinSpeed;
            if (double.IsNaN(delta) || double.IsInfinity(delta) || delta == 0) return ClampSpeed(current, min, max);
            if (double.IsNaN(step) || double.IsInfinity(step) || step <= 0) step = DefaultSpeedStep;
            return ClampSpeed(current + delta * step, min, max);
        }
    }
}
