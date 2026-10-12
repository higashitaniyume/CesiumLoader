using System;
using System.Reflection;
using CesiumLoader.SDK.Manifests;
using GameLogic;
using UI;

namespace AutoThanksMod
{
    // Called only on the main thread, after a real participant's battle enters settlement.
    // Reuse the game's handler so button locking, animations and success chat stay in sync.
    internal sealed class SettlementLikes
    {
        private readonly LikePolicy _policy = new LikePolicy();
        private readonly FieldInfo _settlementField = typeof(BattleSettlementPanel).GetField(
            "_BattleSettlement", BindingFlags.Instance | BindingFlags.NonPublic);
        private UIBattleSettlementPanel _ui;
        private UIBattleSettlement_Com_Item[] _items;

        internal bool Tick(BattleSettlementPanel panel, BattleLogic battle, long accountId, long nowTicks, int intervalMilliseconds,
            out long targetId)
        {
            targetId = 0;
            var ui = panel?.UIObject;
            if (ui == null || !ui.onStage || (ui.step.selectedIndex != 1 && ui.step.selectedIndex != 2)) return false;
            if (_settlementField == null)
                throw new MissingFieldException("BattleSettlementPanel", "_BattleSettlement");
            var settlement = _settlementField.GetValue(panel) as BattleSettlement;
            if (settlement == null) return false;
            ObserveWinnerLike(ui, settlement, battle, accountId, nowTicks);
            var rows = ui.com_ShowTime;
            if (rows == null) return false;
            if (!ReferenceEquals(_ui, ui))
            {
                _items = new[] { rows.com_Player1, rows.com_Player2, rows.com_Player3, rows.com_Player4 };
                _ui = ui;
            }
            // Step 1 is the PVP winner page. Only step 2 binds and displays the four player rows.
            if (ui.step.selectedIndex != 2 || !ui.com_ShowTime.visible) return false;

            // Inspect every row first: a manual like on a later row must also postpone the next automatic like.
            for (int i = 0; i < _items.Length; i++)
            {
                long id = GetRowTarget(_items[i], settlement.GetPlayerSettlement(i), accountId);
                if (id == 0) continue;
                var button = _items[i].btn_Praise;
                if (!_policy.HasAttempted(id) && (button.isFinish.selectedIndex == 1 || button.Cut_in.playing))
                    _policy.MarkAttempt(id, nowTicks);
                // The game may refresh the rows and reset the controller; preserve this session's attempted state.
                if (_policy.HasAttempted(id) && !button.Cut_in.playing)
                {
                    button.isFinish.selectedIndex = 1;
                    button.onClick.Retain();
                }
            }

            for (int i = 0; i < _items.Length; i++)
            {
                long id = GetRowTarget(_items[i], settlement.GetPlayerSettlement(i), accountId);
                if (id == 0 || !_policy.CanAttempt(id, nowTicks, intervalMilliseconds) ||
                    _items[i].btn_Praise.onClick.isEmpty) continue;
                if (!Permissions.Require(ModPermission.GameActions, "AutoThanksMod.LikePlayer")) return false;
                // Reserve before dispatch: errors and lost responses never cause repeated automatic requests.
                _policy.MarkAttempt(id, nowTicks);
                _items[i].btn_Praise.onClick.Call();
                targetId = id;
                return true; // At most one automatic like per update; never catch up after a stalled frame.
            }
            return false;
        }

        private void ObserveWinnerLike(UIBattleSettlementPanel ui, BattleSettlement settlement, BattleLogic battle,
            long accountId, long nowTicks)
        {
            // Match ShowWinner's exact condition, including bots in the winner count, to avoid stale hidden buttons.
            if (!settlement.IsPVP || ui.com_Winner?.btn_Praise == null || battle == null) return;
            var winners = battle.GetPlayerDataByTeamId(settlement.WinTeamId);
            if (winners == null || winners.Count != 1) return;
            var winner = winners[0];
            long id = GetPlayerId(settlement.GetPlayerSettlementById(winner.player.Id), accountId);
            var button = ui.com_Winner.btn_Praise;
            if (id != 0 && !_policy.HasAttempted(id) && (button.grayed || button.Cut_in?.playing == true))
                _policy.MarkAttempt(id, nowTicks);
        }

        private static long GetRowTarget(UIBattleSettlement_Com_Item item, PlayerSettlement settlement, long accountId)
        {
            if (item == null || !item.visible || item.btn_Praise == null || !item.btn_Praise.visible ||
                item.btn_Praise.isFinish == null || item.btn_Praise.Cut_in == null) return 0;
            // Same index mapping as BattleSettlementPanel.RefreshAchieveInfo; do not infer IDs from clamped slots.
            return GetPlayerId(settlement, accountId);
        }

        private static long GetPlayerId(PlayerSettlement settlement, long accountId)
        {
            var data = settlement?.PlayerData;
            var player = data?.player;
            if (player == null || player.Id == 0 || player.Id == accountId || player.IsBot ||
                data.characterType != CharacterType.Hero) return 0;
            return player.Id;
        }

        internal void Reset()
        {
            _policy.Reset();
            _ui = null;
            _items = null;
        }
    }
}
