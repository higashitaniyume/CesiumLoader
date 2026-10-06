using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AstralParty.Agent;
using AstralParty.AgentMod.Bridge;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Gameplay;
using CesiumLoader.SDK.Mods;
using CesiumLoader.SDK.Scenes;
using UnityEngine.SceneManagement;

namespace AstralParty.AgentMod
{
    /// <summary>入口。原生加载器调用 <c>AstralParty.AgentMod.ModEntry.Main()</c>。</summary>
    public static class ModEntry
    {
        public static void Main()
        {
            ModBase.Run(new AgentBridgeModule());
        }
    }

    /// <summary>
    /// AI Agent 桥接 mod。
    ///
    /// 职责(只做搬运, 不做决策 —— 决策在外部 AI 那边):
    ///   1. 观测: state.json(局面 + 当前待响应窗口 + 已等待时长) 每 250ms 一次;
    ///   2. 事件: events.jsonl(SDK 的强类型事件) 与 actions.jsonl(服务器原始动作流);
    ///   3. 执行: commands\*.json → GameActions → results\*.json(主线程, 与玩家手动操作同链路);
    ///   4. 心跳: bridge.json(外部据此判断"游戏内桥接是否活着");
    ///   5. 安全: control.json 的急停/演练/只读开关, 命令 TTL, 结果清理。
    ///
    /// 线程模型: 事件回调在网络线程只入队; 所有文件读写与游戏 API 访问都在主线程(OnUpdate)。
    /// </summary>
    public sealed class AgentBridgeModule : ModBase
    {
        private const string ModVersion = "1.0.0";

        private BridgeSettings _settings;
        private BridgeJournal _journal;
        private PendingTracker _tracker;
        private StateProbe _probe;
        private RawActionObserver _observer;
        private CommandRunner _runner;

        private string _root;
        private long _startedAtMs;
        private long _stateSeq;
        private long _ticks;
        private long _commandsExecuted;
        private long _commandsRejected;
        private long _stateWrites;
        private bool _wasInRoom;
        private bool _lastInBattle;

        private long _nextStateAt;
        private long _nextPollAt;
        private long _nextHeartbeatAt;
        private long _nextControlAt;

        private bool _eventsSubscribed;

        public override string Name { get { return "AI Agent 桥接"; } }
        public override string Version { get { return ModVersion; } }

        public override void OnInitialize()
        {
            _settings = BridgeSettings.FromConfig(Config);
            _root = string.IsNullOrEmpty(_settings.AgentDir) ? AgentBridgeLayout.ResolveRoot() : _settings.AgentDir;
            BridgeSettings.WriteDefaults(Config, _root);

            _startedAtMs = AgentBridgeLayout.NowMs();
            _tracker = new PendingTracker();
            _journal = new BridgeJournal(_settings.RecentActionLimit);
            _probe = new StateProbe(_tracker);
            _runner = new CommandRunner(_settings, _tracker, SelfIdReader);
            _observer = new RawActionObserver(_journal, _tracker, SelfIdReader, _settings.LogActions);

            try { AgentBridgeLayout.EnsureDirectories(_root); }
            catch (Exception e) { Log.ReportCrash("AgentBridge/EnsureDirectories", e); }

            _settings.ApplyControl(_root);
            _observer.Subscribe();
            SubscribeEvents();

            Log.Info("AI Agent 桥接已就绪: " + _root);
            Log.Info("开关: enableActions=" + _settings.EnableActions +
                     ", pauseActions=" + _settings.PauseActions +
                     ", dryRun=" + _settings.DryRun);
            Log.Info("外部用 MCP server(mcp\\AstralParty.Mcp)接管; state.json 每 " +
                     _settings.StateIntervalMs + "ms 刷新一次");

            WriteState(true);
            WriteHeartbeat();
        }

        public override void OnUpdate()
        {
            try
            {
                long now = AgentBridgeLayout.NowMs();
                _ticks++;

                if (now >= _nextControlAt)
                {
                    _nextControlAt = now + _settings.ControlPollMs;
                    _settings.ApplyControl(_root);
                }

                if (now >= _nextPollAt)
                {
                    _nextPollAt = now + _settings.PollIntervalMs;
                    PollCommands(now);
                }

                if (now >= _nextStateAt)
                {
                    _nextStateAt = now + _settings.StateIntervalMs;
                    WriteState(false);
                }

                if (now >= _nextHeartbeatAt)
                {
                    _nextHeartbeatAt = now + _settings.HeartbeatMs;
                    WriteHeartbeat();
                    Maintain(now);
                }

                // 队列里没东西时 Flush 只是一次加锁判断, 很便宜
                _journal.Flush(_root, _settings.MaxJournalBytes);
            }
            catch (Exception e)
            {
                Log.ReportCrash("AgentBridge/OnUpdate", e);
            }
        }

        /// <summary>切场景时补一条事件(便于外部把局面变化和动作流对上)。</summary>
        public override void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            try
            {
                var extra = new Dictionary<string, object>();
                try { extra["Scene"] = scene.name; } catch { }
                _journal.AddEvent("SceneLoaded", 0, extra);
                WriteState(false);
            }
            catch { }
        }

        public override void OnUnload()
        {
            try
            {
                _observer?.Unsubscribe();
                _journal?.Flush(_root, _settings.MaxJournalBytes);
                WriteState(true);
                WriteHeartbeat();
                Log.Info("AI Agent 桥接已卸载 (执行 " + _commandsExecuted + " 条命令, 拒绝 " +
                         _commandsRejected + " 条)");
            }
            catch { }
        }

        // ============================== 事件订阅 ==============================

        /// <summary>
        /// 订阅 SDK 的强类型事件, 全部只做"入队"(回调可能来自网络线程)。
        /// 不反订阅: mod 在进程内只初始化一次, 订阅随进程结束; 若将来支持热重载需要补 Unsubscribe。
        /// </summary>
        private void SubscribeEvents()
        {
            if (_eventsSubscribed) return;
            try
            {
                GameEvents.CardUsed += OnCardUsed;
                GameEvents.NoCard += OnNoCard;
                GameEvents.EffectCardUsed += OnEffectCardUsed;
                GameEvents.SkillUsed += OnSkillUsed;
                GameEvents.QuickCardUsed += OnQuickCardUsed;
                GameEvents.DiceResult += OnDiceResult;
                GameEvents.Move += OnMove;
                GameEvents.BattleDice += OnBattleDice;
                GameEvents.RewardCardSelected += OnRewardCardSelected;
                GameEvents.ShopCandidates += OnShopCandidates;
                GameEvents.RelicCandidates += OnRelicCandidates;
                GameEvents.RelicSelected += OnRelicSelected;
                GameEvents.RelicsSynced += OnRelicsSynced;

                // 这两个事件的负载类型来自游戏程序集, 用 lambda 让编译器推断, 免得在这里写死类型名
                GameEvents.BattleUpdate += b => _journal.AddEvent("BattleUpdate", 0, null);
                GameEvents.HandChanged += (pid, cards) =>
                {
                    var extra = new Dictionary<string, object>();
                    try { extra["Count"] = cards == null ? 0 : cards.Count; } catch { }
                    _journal.AddEvent("HandChanged", pid, extra);
                };

                _eventsSubscribed = true;
            }
            catch (Exception e)
            {
                Log.ReportCrash("AgentBridge/SubscribeEvents", e);
            }
        }

        private void OnCardUsed(long pid, int cardId, int remain) { _journal.AddEvent("CardUsed", pid, Pack("CardId", cardId, "RemainHand", remain)); }
        private void OnNoCard(long pid) { _journal.AddEvent("NoCard", pid, null); }
        private void OnEffectCardUsed(long pid, int cardId, int remain) { _journal.AddEvent("EffectCardUsed", pid, Pack("CardId", cardId, "RemainHand", remain)); }
        private void OnSkillUsed(long pid, int skillId) { _journal.AddEvent("SkillUsed", pid, Pack("SkillId", skillId)); }
        private void OnQuickCardUsed(long pid, int cardId, int origin) { _journal.AddEvent("QuickCardUsed", pid, Pack("CardId", cardId, "OriginCardId", origin)); }
        private void OnDiceResult(long pid, int point, int max) { _journal.AddEvent("DiceResult", pid, Pack("Point", point, "MaxPoint", max)); }
        private void OnMove(long pid, int steps, bool arrived)
        {
            _journal.AddEvent("Move", pid, Pack("Steps", steps, "Arrived", arrived));
            // S2C 的移动回执 = 移动窗口结束(否则残留的 move 窗口会让 agent 以为还没走)
            _tracker.OnMoved(pid, AgentBridgeLayout.NowMs());
        }
        private void OnBattleDice(long pid, int point) { _journal.AddEvent("BattleDice", pid, Pack("Point", point)); }
        private void OnRewardCardSelected(long pid, int cardId)
        {
            _journal.AddEvent("RewardCardSelected", pid, Pack("CardId", cardId));
            _tracker.OnRewardSelected(pid, cardId, AgentBridgeLayout.NowMs());
        }

        private void OnShopCandidates(long pid, IReadOnlyList<int> ids)
        {
            // 只记事件: 窗口状态统一由 RawActionObserver 填(它拿得到 sn/价格/售罄信息)
            _journal.AddEvent("ShopCandidates", pid, Pack("Candidates", Join(ids)));
        }

        private void OnRelicCandidates(long pid, IReadOnlyList<int> ids)
        {
            _journal.AddEvent("RelicCandidates", pid, Pack("Candidates", Join(ids)));
        }

        private void OnRelicSelected(long pid, int relicId)
        {
            _journal.AddEvent("RelicSelected", pid, Pack("RelicId", relicId));
            _tracker.OnRelicSelected(pid, relicId, AgentBridgeLayout.NowMs());
        }

        private void OnRelicsSynced(long pid, IReadOnlyList<int> ids)
        {
            _journal.AddEvent("RelicsSynced", pid, Pack("Relics", Join(ids)));
        }

        private static Dictionary<string, object> Pack(string k1, object v1)
        {
            var d = new Dictionary<string, object>(2);
            d[k1] = v1;
            return d;
        }

        private static Dictionary<string, object> Pack(string k1, object v1, string k2, object v2)
        {
            var d = new Dictionary<string, object>(4);
            d[k1] = v1;
            d[k2] = v2;
            return d;
        }

        private static string Join(IReadOnlyList<int> ids)
        {
            if (ids == null) return string.Empty;
            var sb = new StringBuilder();
            for (int i = 0; i < ids.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ids[i]);
            }
            return sb.ToString();
        }

        private long SelfIdReader()
        {
            try { return _probe != null ? _probe.SelfId : 0; }
            catch { return 0; }
        }

        // ============================== 命令处理 ==============================

        private void PollCommands(long now)
        {
            string dir = AgentBridgeLayout.CommandsDir(_root);
            string[] files;
            try
            {
                if (!Directory.Exists(dir)) return;
                files = Directory.GetFiles(dir, "*.json");
            }
            catch { return; }
            if (files == null || files.Length == 0) return;

            Array.Sort(files, StringComparer.Ordinal); // 文件名带零填充 seq = 下发顺序

            int handled = 0;
            for (int i = 0; i < files.Length; i++)
            {
                if (handled >= _settings.MaxCommandsPerTick) break;

                string file = files[i];
                long seq;
                string id;
                if (!AgentBridgeLayout.TryParseCommandFileName(Path.GetFileName(file), out seq, out id))
                {
                    TryDelete(file);
                    continue;
                }

                string json = AgentBridgeLayout.ReadAllTextOrNull(file);
                if (json == null) continue; // 读不到(极少): 下一 tick 再说

                BridgeCommand cmd;
                string error;
                BridgeResult result;
                if (!BridgeCommandParser.TryParse(json, out cmd, out error))
                {
                    result = BridgeResult.Failure(id, null, AgentBridgeLayout.Code.BadArgs, error);
                }
                else if (cmd.IssuedAtMs > 0 && now - cmd.IssuedAtMs > _settings.CommandTtlMs)
                {
                    // 上次会话残留 / 已经没人等的命令: 明确拒绝, 绝不能"补发"到对局里
                    result = BridgeResult.Failure(id, cmd.Tool, AgentBridgeLayout.Code.Expired,
                        "命令已过期(" + (now - cmd.IssuedAtMs) + "ms > TTL " + _settings.CommandTtlMs + "ms), 未执行");
                }
                else
                {
                    result = _runner.Execute(cmd);
                }

                if (result.Ok) _commandsExecuted++; else _commandsRejected++;
                WriteResult(result);
                TryDelete(file);
                handled++;
            }
        }

        private void WriteResult(BridgeResult result)
        {
            try
            {
                string path = AgentBridgeLayout.ResultPath(_root, result.Id);
                AgentBridgeLayout.WriteAtomic(path, result.ToJson());
            }
            catch { }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // ============================== 状态与心跳 ==============================

        private void WriteState(bool force)
        {
            try
            {
                _stateSeq++;
                var st = _probe.Capture(ModVersion, _stateSeq, AgentBridgeLayout.NowMs());

                // 离开房间(单位列表清空) → 丢掉上个对局的待响应窗口, 免得残留到下一局
                if (_wasInRoom && st.Units.Count == 0) _tracker.Reset();
                _wasInRoom = st.Units.Count > 0;
                _lastInBattle = st.InBattle;

                st.Counters.CommandsExecuted = _commandsExecuted;
                st.Counters.CommandsRejected = _commandsRejected;
                st.Counters.EventsLogged = _journal.EventsLogged;
                st.Counters.ActionsLogged = _journal.ActionsLogged;
                st.Counters.Ticks = _ticks;
                st.Counters.StateWrites = _stateWrites + 1;

                var control = new AgentControl
                {
                    EnableActions = _settings.EnableActions,
                    PauseActions = _settings.PauseActions,
                    DryRun = _settings.DryRun,
                    ControlReadAtMs = _settings.ControlReadAtMs,
                    ControlSource = _settings.ControlFileApplied ? "control.json" : "config.json"
                };
                st.Control = control;

                st.RecentActions = _journal.RecentSnapshot();

                AgentBridgeLayout.WriteAtomic(AgentBridgeLayout.StatePath(_root), CesiumJson.SerializePretty(st));
                _stateWrites++;
            }
            catch (Exception e)
            {
                if (force) Log.ReportCrash("AgentBridge/WriteState", e);
            }
        }

        private void WriteHeartbeat()
        {
            try
            {
                var d = new Dictionary<string, object>(16);
                d[AgentBridgeLayout.BridgeField.Schema] = AgentBridgeLayout.SchemaVersion;
                d[AgentBridgeLayout.BridgeField.ModVersion] = ModVersion;
                d[AgentBridgeLayout.BridgeField.SdkVersion] = SdkVersionText();
                d[AgentBridgeLayout.BridgeField.ProcessId] = CurrentProcessId();
                d[AgentBridgeLayout.BridgeField.StartedAtMs] = _startedAtMs;
                d[AgentBridgeLayout.BridgeField.LastTickMs] = AgentBridgeLayout.NowMs();
                d[AgentBridgeLayout.BridgeField.TickCount] = _ticks;
                d[AgentBridgeLayout.BridgeField.StateSeq] = _stateSeq;
                d[AgentBridgeLayout.BridgeField.AgentDir] = _root;
                d[AgentBridgeLayout.BridgeField.Scene] = SafeSceneName();
                d[AgentBridgeLayout.BridgeField.InRoom] = _wasInRoom;
                d[AgentBridgeLayout.BridgeField.InBattle] = _lastInBattle;
                d[AgentBridgeLayout.BridgeField.CommandsExecuted] = _commandsExecuted;
                d[AgentBridgeLayout.BridgeField.CommandsRejected] = _commandsRejected;

                AgentBridgeLayout.WriteAtomic(AgentBridgeLayout.BridgePath(_root), CesiumJson.Serialize(d));
            }
            catch { }
        }

        private string SafeSceneName()
        {
            try { return SceneService.GetActiveSceneName(); } catch { return null; }
        }

        private static string SdkVersionText()
        {
            try { return typeof(GameActions).Assembly.GetName().Version.ToString(); }
            catch { return "unknown"; }
        }

        private static int CurrentProcessId()
        {
            try { return System.Diagnostics.Process.GetCurrentProcess().Id; } catch { return 0; }
        }

        /// <summary>周期性维护: 清理过期结果文件与残留 tmp。</summary>
        private void Maintain(long now)
        {
            try
            {
                string results = AgentBridgeLayout.ResultsDir(_root);
                if (Directory.Exists(results))
                {
                    foreach (var f in Directory.GetFiles(results, "*.json"))
                    {
                        try
                        {
                            long written = File.GetLastWriteTimeUtc(f).Ticks / TimeSpan.TicksPerMillisecond;
                            if (now - written > _settings.ResultRetentionMs) File.Delete(f);
                        }
                        catch { }
                    }
                }

                string commands = AgentBridgeLayout.CommandsDir(_root);
                if (Directory.Exists(commands))
                {
                    foreach (var f in Directory.GetFiles(commands, "*.tmp"))
                    {
                        try
                        {
                            long written = File.GetLastWriteTimeUtc(f).Ticks / TimeSpan.TicksPerMillisecond;
                            if (now - written > 60000) File.Delete(f);
                        }
                        catch { }
                    }
                }
            }
            catch { }
        }
    }
}
