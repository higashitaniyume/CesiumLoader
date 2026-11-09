namespace UnityEngine
{
    public enum FindObjectsInactive { Exclude, Include }
    public enum FindObjectsSortMode { None, InstanceID }
    public class Object
    {
        public static Object[] FindObjectsByType(Type type, FindObjectsInactive inactive, FindObjectsSortMode sort) => Array.Empty<Object>();
    }
    public enum KeyCode { None, Delete, Equals, Minus, LeftAlt, RightAlt, KeypadPlus, KeypadMinus }
    public static class Time
    {
        public static float Step = 0.02f;
        public static int Writes;
        public static bool FailAccess;
        public static float fixedDeltaTime
        {
            get { if (FailAccess) throw new MissingMethodException("fixture ECall"); return Step; }
            set { if (FailAccess) throw new MissingMethodException("fixture ECall"); Step = value; Writes++; }
        }
        public static float maximumDeltaTime = 1f / 3f;
        public static float Scale = 1f;
        public static int ScaleWrites;
        public static float timeScale
        {
            get { if (FailAccess) throw new MissingMethodException("fixture ECall"); return Scale; }
            set { if (FailAccess) throw new MissingMethodException("fixture ECall"); Scale = value; ScaleWrites++; }
        }
    }
}
namespace CesiumLoader.SDK.Runtime
{
    public static class RuntimeAssemblyService { public static Type FindType(string name) => null; }
}
namespace CesiumLoader.SDK.Configuration
{
    public class ModConfig
    {
        public readonly Dictionary<string, object> Values = new();
        public string Path => "test-config.json";
        public bool Has(string key) => Values.ContainsKey(key);
        public double GetDouble(string key, double fallback) => Has(key) ? Convert.ToDouble(Values[key]) : fallback;
        public float GetFloat(string key, float fallback) => Has(key) ? Convert.ToSingle(Values[key]) : fallback;
        public bool GetBool(string key, bool fallback) => Has(key) ? Convert.ToBoolean(Values[key]) : fallback;
        public string GetString(string key, string fallback) => Has(key) ? (string)Values[key] : fallback;
        public void Set(string key, object value) => Values[key] = value;
        public bool SetDefault(string key, object value) { if (Has(key)) return false; Set(key, value); return true; }
        public bool Describe(string key, ConfigDoc doc) => false;
        public void SaveIfDirty() { }
    }
    public class ConfigDoc { public static ConfigDoc Of(params string[] values) => new(); }
}
namespace CesiumLoader.SDK.Engine
{
    public static class UnityTime { public static float RealtimeSinceStartup { get; set; } }
}
namespace CesiumLoader.SDK.Inputs
{
    public static class InputService
    {
        public static string BackendName => "fixture";
        public static bool IsAvailable => true;
        public static readonly HashSet<UnityEngine.KeyCode> Held = new();
        public static readonly HashSet<UnityEngine.KeyCode> Pressed = new();
        public static bool IsKeyPressed(UnityEngine.KeyCode key) => Pressed.Contains(key);
        public static bool IsKeyHeld(UnityEngine.KeyCode key) => Held.Contains(key);
    }
}
namespace CesiumLoader.SDK.Logging
{
    public static class SdkLog { public static void Warn(string category, string message) { } }
    public class TestLog
    {
        public readonly List<string> Warnings = new();
        public void Info(string message) { }
        public void Warn(string message) => Warnings.Add(message);
        public void ReportCrash(string name, Exception error) => throw new InvalidOperationException(name, error);
    }
}
namespace CesiumLoader.SDK.Mods
{
    public abstract class ModBase
    {
        public CesiumLoader.SDK.Configuration.ModConfig Config { get; } = new();
        public CesiumLoader.SDK.Logging.TestLog Log { get; } = new();
        public object Context => null;
        public virtual string Version => "fixture";
        public static void Run(ModBase mod, int delay) { }
        public virtual void OnInitialize() { }
        public virtual void OnUpdate() { }
        public virtual void OnUnload() { }
        public virtual void OnConfigChanged() { }
    }
}
namespace CesiumLoader.SDK.Speed
{
    public static class SpeedHack
    {
        public const double DefaultSpeedStep = 0.5, MinSpeed = 1, MaxSpeed = 3, HardMaxSpeed = 100;
        public static bool IsAvailable { get; set; } = true;
        public static double Actual = 4;
        public static double Speed { get { Reads++; return Actual; } set { Actual = value; } }
        public static double Requested { get; private set; }
        public static bool AcceptRequest { get; set; } = true;
        public static readonly List<double> Requests = new();
        public static int Reads;
        public static string Directory => System.IO.Path.GetTempPath();
        public static string UnavailableReason => "fixture unavailable";
        public static string Describe() => "fixture engine";
        public static double ClampSpeed(double speed, double min, double max) => Math.Clamp(speed, min, max);
        public static double StepSpeed(double speed, int dir, double step, double min, double max) => ClampSpeed(speed + dir * step, min, max);
        public static bool SetSpeed(double speed) { Requested = speed; Requests.Add(speed); return AcceptRequest; }
    }
}
namespace CesiumLoader.SDK.UserInterface
{
    public enum UiNotificationLevel { Success, Info, Warning }
    public static class UiService
    {
        public static bool ShouldBlockGameInput { get; set; }
        public static void Notify(string text, UiNotificationLevel level, float seconds, object context) { }
    }
}
