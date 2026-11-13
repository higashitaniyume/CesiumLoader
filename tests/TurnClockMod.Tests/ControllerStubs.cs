using System;
using System.Collections.Generic;

namespace CesiumLoader.SDK.Configuration
{
    public sealed class ModConfig
    {
        private readonly Dictionary<string, object> _values = new Dictionary<string, object>();
        public readonly Dictionary<string, ConfigDoc> Descriptions = new Dictionary<string, ConfigDoc>();
        public string Path => "fixture-config.json";
        public bool Has(string name) => _values.ContainsKey(name);
        public void Set(string name, object value) { _values[name] = value; }
        public bool SetDefault(string name, object value) { if (Has(name)) return false; Set(name, value); return true; }
        public double GetDouble(string name, double fallback) => Has(name) ? Convert.ToDouble(_values[name]) : fallback;
        public bool GetBool(string name, bool fallback) => Has(name) ? Convert.ToBoolean(_values[name]) : fallback;
        public string GetString(string name, string fallback) => Has(name) ? (string)_values[name] : fallback;
        public bool Describe(string name, ConfigDoc doc) { Descriptions[name] = doc; return false; }
        public void SaveIfDirty() { }
    }
    public sealed class ConfigDoc
    {
        public string[] Descriptions;
        public static ConfigDoc Of(params string[] descriptions) => new ConfigDoc { Descriptions = descriptions };
    }
}
namespace CesiumLoader.SDK.Mods
{
    public sealed class FixtureLog
    {
        public readonly List<string> Messages = new List<string>();
        public readonly List<string> Warnings = new List<string>();
        public void Info(string message) { Messages.Add(message); }
        public void Warn(string message) { Warnings.Add(message); }
        public void ReportCrash(string location, Exception error) { throw new InvalidOperationException(location, error); }
    }
    public abstract class ModBase
    {
        public readonly Configuration.ModConfig Config = new Configuration.ModConfig();
        public readonly FixtureLog Log = new FixtureLog();
        public virtual string Name => "fixture";
        public virtual string Version => "fixture";
        public virtual string Author => "fixture";
        public virtual string Description => "fixture";
        public virtual void OnInitialize() { }
        public virtual void OnConfigChanged() { }
        public virtual void OnLateUpdate() { }
        public virtual void OnUnload() { }
        public static void Run(ModBase mod) { }
    }
}
namespace CesiumLoader.SDK.Speed
{
    public static class SpeedHack
    {
        public const double MinSpeed = 1, HardMaxSpeed = 100;
        public static bool IsAvailable => true;
        public static string Directory => System.IO.Path.GetTempPath();
        public static bool TryGetSpeed(out double speed) { speed = 1; return true; }
    }
}
namespace UnityEngine
{
    public static class Time
    {
        public static float Delta = .08f, UnscaledDelta = .02f, Scale = 4;
        public static int DeltaReads, UnscaledReads, ScaleReads;
        public static bool FailDeltas, FailScale;
        public static float deltaTime { get { DeltaReads++; if (FailDeltas) throw new MissingMethodException(); return Delta; } }
        public static float unscaledDeltaTime { get { UnscaledReads++; if (FailDeltas) throw new MissingMethodException(); return UnscaledDelta; } }
        public static float timeScale { get { ScaleReads++; if (FailScale) throw new MissingMethodException(); return Scale; } }
        public static void Reset()
        {
            Delta = .08f; UnscaledDelta = .02f; Scale = 4;
            DeltaReads = UnscaledReads = ScaleReads = 0;
            FailDeltas = FailScale = false;
        }
    }
}
namespace Tools
{
    public static class SimpleSingletonProvider<T> where T : class
    {
        public static T inst;
        public static bool hasInstance => inst != null;
    }
}
namespace GameLogic
{
    public static class OperationTimer
    {
        private static readonly Dictionary<long, object> timerDict = new Dictionary<long, object>();
        private static object ShowPlayerTimer;
        public static void Set(long key, object timer) { timerDict[key] = timer; }
        public static void Clear() { timerDict.Clear(); }
        public static void Show(object timer) { ShowPlayerTimer = timer; }
        public static void Reset() { timerDict.Clear(); ShowPlayerTimer = null; }
    }
    public sealed class GameLogicManager { public RoomLogic room; public BattleLogic battle; }
    public sealed class RoomLogic { public RoomController roomController; }
    public sealed class RoomController { public RoomStateType roomStateType; }
    public enum RoomStateType { READY, RUNNING }
    public sealed class BattleLogic { public bool clientFinishReady; }
}
namespace UI
{
    public sealed class UIManager
    {
        private readonly Dictionary<int, object> _cachedPanelDict = new Dictionary<int, object>();
        private object _cachedStoryWindow;
        public void SetPanel(int key, object panel) { _cachedPanelDict[key] = panel; }
        public void SetStory(object story) { _cachedStoryWindow = story; }
        public void Clear() { _cachedPanelDict.Clear(); _cachedStoryWindow = null; }
    }
}
