using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

// Only the real lifecycle partial is linked here. These SDK/UI owners exercise
// callback ownership and failure isolation without loading Unity or game RPCs.
namespace CesiumLoader.SDK.Configuration
{
    internal sealed class ModConfig
    {
        public event Action<ModConfig> Changed;
        public int ListenerCount => Changed?.GetInvocationList().Length ?? 0;
        public void Notify() => Changed?.Invoke(this);
    }
}

namespace CesiumLoader.SDK.Gameplay
{
    internal static class GameEvents
    {
        public static event Action BattleUpdate, BattlePhaseChanged, CardUsed,
            EffectCardUsed, QuickCardUsed, HeroAttrUpdated, SkillUsed;
        public static int ListenerCount => Count(BattleUpdate) + Count(BattlePhaseChanged) + Count(CardUsed) +
            Count(EffectCardUsed) + Count(QuickCardUsed) + Count(HeroAttrUpdated) + Count(SkillUsed);
        private static int Count(Action action) => action?.GetInvocationList().Length ?? 0;
    }
}

namespace CombatOddsMod
{
    internal sealed class LifecycleOwner : IDisposable
    {
        public int DisposeCount, HideCount, ResetCount;
        public bool ThrowOnDispose;
        public void Hide() => HideCount++;
        public void Reset() => ResetCount++;
        public void Dispose()
        {
            DisposeCount++;
            if (ThrowOnDispose) throw new InvalidOperationException("Disposed native window");
        }
    }

    internal sealed class LifecycleContext
    {
        public readonly CesiumLoader.SDK.Configuration.ModConfig Config = new CesiumLoader.SDK.Configuration.ModConfig();
    }

    public static partial class ModEntry
    {
        private static CombatOddsConfig _cfg;
        private static LifecycleContext _ctx;
        private static LifecycleOwner _overlay, _hudBonus, _skillCooldown, _monsterCooldown,
            _handCount, _megasDamage, _playerAttrOverlay, _handOdds;
        private static object _lastBattle;
        private static string _hud, _lastLogged, _lastAtkBonusLog;
        private static readonly Dictionary<string, float> _recentDamageEvents = new Dictionary<string, float>();
        private static readonly Dictionary<long, string> _lastAshDiagnostic = new Dictionary<long, string>();
        private static float _lastDamagePruneTime;
        private static bool _atkBound;

        internal static LifecycleOwner[] BeginLifecycleTest()
        {
            Cleanup();
            _disposed = false;
            _configApplyPending = false;
            _featuresStarted = true;
            _uiRegistered = true;
            _cfg = new CombatOddsConfig { Enabled = true };
            _ctx = new LifecycleContext();
            _ctx.Config.Changed += OnConfigurationChanged;
            CesiumLoader.SDK.Gameplay.GameEvents.BattleUpdate += OnBattleUpdate;
            CesiumLoader.SDK.Gameplay.GameEvents.BattlePhaseChanged += OnBattlePhaseChanged;
            CesiumLoader.SDK.Gameplay.GameEvents.CardUsed += OnCombatCardUsed;
            CesiumLoader.SDK.Gameplay.GameEvents.EffectCardUsed += OnCombatEffectCardUsed;
            CesiumLoader.SDK.Gameplay.GameEvents.QuickCardUsed += OnCombatQuickCardUsed;
            CesiumLoader.SDK.Gameplay.GameEvents.HeroAttrUpdated += OnHeroAttrUpdated;
            CesiumLoader.SDK.Gameplay.GameEvents.SkillUsed += OnSkillUsed;
            _overlay = new LifecycleOwner(); _hudBonus = new LifecycleOwner();
            _skillCooldown = new LifecycleOwner(); _monsterCooldown = new LifecycleOwner();
            _handCount = new LifecycleOwner(); _megasDamage = new LifecycleOwner();
            _playerAttrOverlay = new LifecycleOwner();
            _handOdds = new LifecycleOwner();
            _atkBound = true;
            _lastBattle = new object();
            _hud = _lastLogged = _lastAtkBonusLog = "test";
            _recentDamageEvents["battle"] = 1f;
            _lastAshDiagnostic[1] = "test";
            _lastDamagePruneTime = 1f;
            return new[] { _overlay, _hudBonus, _skillCooldown, _monsterCooldown,
                _handCount, _megasDamage, _playerAttrOverlay, _handOdds };
        }

        private static void UnbindAtk() => _atkBound = false;
        private static void OnBattleUpdate() { }
        private static void OnBattlePhaseChanged() { }
        private static void OnCombatCardUsed() { }
        private static void OnCombatEffectCardUsed() { }
        private static void OnCombatQuickCardUsed() { }
        private static void OnHeroAttrUpdated() { }
        private static void OnSkillUsed() { }

        internal static CesiumLoader.SDK.Configuration.ModConfig LifecycleConfig => _ctx.Config;
        internal static bool ConfigApplyPending => _configApplyPending;
        internal static void EndLifecycleTest() => Cleanup();
        internal static bool LifecycleReferencesCleared => _ctx == null && _overlay == null && _hudBonus == null &&
            _skillCooldown == null && _monsterCooldown == null && _handCount == null && _megasDamage == null &&
            _playerAttrOverlay == null && _handOdds == null && _lastBattle == null && _hud == null &&
            _lastLogged == null && _lastAtkBonusLog == null &&
            !_atkBound && !_featuresStarted && !_uiRegistered &&
            _disposed && !_configApplyPending && _recentDamageEvents.Count == 0 &&
            _lastAshDiagnostic.Count == 0 && _lastDamagePruneTime == 0f;
    }
}

namespace CombatOddsMod.Tests
{
    [CollectionDefinition("Module lifecycle", DisableParallelization = true)]
    public sealed class ModuleLifecycleCollection { }

    [Collection("Module lifecycle")]
    public class ModEntryLifecycleTests
    {
        [Fact]
        public void CleanupReleasesOtherOwnersAfterOneWindowThrowsAndIsIdempotent()
        {
            var owners = ModEntry.BeginLifecycleTest();
            var config = ModEntry.LifecycleConfig;
            Assert.Equal(7, CesiumLoader.SDK.Gameplay.GameEvents.ListenerCount);
            owners[1].ThrowOnDispose = true;
            ModEntry.EndLifecycleTest();
            Assert.Equal(0, CesiumLoader.SDK.Gameplay.GameEvents.ListenerCount);
            Assert.Equal(0, config.ListenerCount);
            for (int i = 1; i <= 6; i++) Assert.Equal(1, owners[i].DisposeCount);
            Assert.Equal(1, owners[0].HideCount);
            Assert.Equal(1, owners[7].DisposeCount);
            Assert.True(ModEntry.LifecycleReferencesCleared);
            ModEntry.EndLifecycleTest();
            Assert.Equal(1, owners[1].DisposeCount);
        }

        [Fact]
        public async Task BackgroundConfigurationOnlyQueuesWork()
        {
            var owners = ModEntry.BeginLifecycleTest();
            var config = ModEntry.LifecycleConfig;
            await Task.Run(() => { for (int i = 0; i < 100; i++) config.Notify(); });
            Assert.True(ModEntry.ConfigApplyPending);
            Assert.Equal(7, CesiumLoader.SDK.Gameplay.GameEvents.ListenerCount);
            Assert.All(owners, owner => Assert.Equal(0, owner.DisposeCount));
            ModEntry.EndLifecycleTest();
            config.Notify();
            Assert.False(ModEntry.ConfigApplyPending);
        }

        [Fact]
        public void RepeatedOwnedLifetimesDoNotAccumulateSubscriptions()
        {
            for (int i = 0; i < 1000; i++)
            {
                ModEntry.BeginLifecycleTest();
                Assert.Equal(7, CesiumLoader.SDK.Gameplay.GameEvents.ListenerCount);
                ModEntry.EndLifecycleTest();
                Assert.Equal(0, CesiumLoader.SDK.Gameplay.GameEvents.ListenerCount);
                Assert.True(ModEntry.LifecycleReferencesCleared);
            }
        }
    }
}
