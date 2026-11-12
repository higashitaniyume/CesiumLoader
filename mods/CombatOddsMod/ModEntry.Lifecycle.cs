using System;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Gameplay;

namespace CombatOddsMod
{
    public static partial class ModEntry
    {
        private static bool _featuresStarted;
        private static bool _uiRegistered;
        private static volatile bool _disposed;
        private static volatile bool _configApplyPending;

        private static void OnConfigurationChanged(ModConfig config)
        {
            // File watchers can notify off-thread; the existing main tick owns
            // configuration application, Unity access and feature disposal.
            if (!_disposed) _configApplyPending = true;
        }

        private static void StopFeatures()
        {
            if (_featuresStarted)
            {
                GameEvents.BattleUpdate -= OnBattleUpdate;
                GameEvents.BattlePhaseChanged -= OnBattlePhaseChanged;
                GameEvents.CardUsed -= OnCombatCardUsed;
                GameEvents.EffectCardUsed -= OnCombatEffectCardUsed;
                GameEvents.QuickCardUsed -= OnCombatQuickCardUsed;
                GameEvents.HeroAttrUpdated -= OnHeroAttrUpdated;
                GameEvents.SkillUsed -= OnSkillUsed;
            }
            _featuresStarted = false;
            // A disposed game window must not prevent the remaining owners
            // from releasing their timers, handlers and cached player data.
            try { UnbindAtk(); } catch { }
            try { _overlay?.Hide(); } catch { }
            try { _hudBonus?.Dispose(); } catch { }
            try { _skillCooldown?.Dispose(); } catch { }
            try { _monsterCooldown?.Dispose(); } catch { }
            try { _handCount?.Dispose(); } catch { }
            try { _handOdds?.Dispose(); } catch { }
            try { _megasDamage?.Dispose(); } catch { }
            try { _playerAttrOverlay?.Dispose(); } catch { }
            _overlay = null;
            _hudBonus = null;
            _skillCooldown = null;
            _monsterCooldown = null;
            _handCount = null;
            _handOdds = null;
            _megasDamage = null;
            _playerAttrOverlay = null;
            _hud = null;
            _lastBattle = null;
            _lastLogged = null;
            _lastAtkBonusLog = null;
            _recentDamageEvents.Clear();
            _lastDamagePruneTime = 0;
            _lastAshDiagnostic.Clear();
        }

        private static void Cleanup()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ctx?.Config != null) _ctx.Config.Changed -= OnConfigurationChanged;
            _configApplyPending = false;
            StopFeatures();
            // UiService keeps the existing fallback registrations owned by
            // this context and removes them during its own owner cleanup.
            _uiRegistered = false;
            _ctx = null;
        }
    }
}
