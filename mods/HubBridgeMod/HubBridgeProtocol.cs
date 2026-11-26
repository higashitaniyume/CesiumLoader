using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using CesiumLoader.SDK.Configuration;

namespace HubBridgeMod
{
    internal sealed class BridgeEndpointInfo
    {
        public int Port;
        public string Token;
        public int Pid;
        public long UpdatedAt;

        public BridgeEndpointInfo()
        {
            Token = "";
        }

        public bool IsValid()
        {
            return Port >= 1 && Port <= 65535 && !string.IsNullOrEmpty(Token) && Token.Length <= 256;
        }

        public string BaseUrl()
        {
            return "http://127.0.0.1:" + Port.ToString(CultureInfo.InvariantCulture);
        }

        public static bool TryParse(string json, out BridgeEndpointInfo info, out string error)
        {
            info = null;
            error = "";
            if (string.IsNullOrWhiteSpace(json) || json.Length > 16384)
            {
                error = "endpoint_invalid_size";
                return false;
            }

            object raw;
            if (!CesiumJson.TryDeserialize(json, out raw))
            {
                error = "endpoint_invalid_json";
                return false;
            }

            IDictionary<string, object> dict = raw as IDictionary<string, object>;
            if (dict == null)
            {
                error = "endpoint_not_object";
                return false;
            }

            BridgeEndpointInfo parsed = new BridgeEndpointInfo();
            parsed.Port = HubBridgeProtocol.ReadInt(dict, "port", 0);
            parsed.Token = HubBridgeProtocol.ReadString(dict, "token", "").Trim();
            parsed.Pid = HubBridgeProtocol.ReadInt(dict, "pid", 0);
            parsed.UpdatedAt = HubBridgeProtocol.ReadLong(dict, "updatedAt", 0);

            if (!parsed.IsValid())
            {
                error = "endpoint_invalid_fields";
                return false;
            }

            info = parsed;
            return true;
        }
    }

    internal sealed class BridgeCommand
    {
        public string CommandId;
        public string Action;
        public long GameRoomId;
        public string GamePwd;
        public int RoomServerId;

        public BridgeCommand()
        {
            CommandId = "";
            Action = "";
            GamePwd = "";
        }
    }

    internal sealed class BridgeRoomSnapshot
    {
        public long RoomId;
        public string Pwd;
        public int RoomServerId;
        public int MapId;
        public int MapType;
        public long MasterId;
        public bool IsMaster;
        public string State;
        public int PlayerCount;

        public BridgeRoomSnapshot()
        {
            Pwd = "";
            State = "";
        }

        public Dictionary<string, object> ToDictionary()
        {
            return new Dictionary<string, object>
            {
                { "gameRoomId", RoomId },
                { "gamePwd", Pwd ?? "" },
                { "roomServerId", RoomServerId },
                { "mapId", MapId },
                { "mapType", MapType },
                { "masterId", MasterId },
                { "isMaster", IsMaster },
                { "state", State ?? "" },
                { "playerCount", PlayerCount },
            };
        }
    }

    internal sealed class BridgeEvent
    {
        public string EventId;
        public string Type;
        public long AtMs;
        public Dictionary<string, object> Data;
        public int Attempts { get; set; }

        public BridgeEvent(string eventId, string type, long atMs, Dictionary<string, object> data)
        {
            EventId = eventId;
            Type = type;
            AtMs = atMs;
            Data = data ?? new Dictionary<string, object>();
        }

        public string ToJson(string sessionId)
        {
            Dictionary<string, object> body = new Dictionary<string, object>
            {
                { "eventId", EventId },
                { "sessionId", sessionId ?? "" },
                { "type", Type },
                { "atMs", AtMs },
                { "data", Data },
            };
            return CesiumJson.Serialize(body);
        }
    }

    internal static class HubBridgeProtocol
    {
        public const int ProtocolVersion = 1;

        public static string ResolveEndpointPath(string configuredPath, string modsDir)
        {
            string trimmed = (configuredPath ?? "").Trim();
            if (!string.IsNullOrEmpty(trimmed)) return trimmed;
            if (string.IsNullOrEmpty(modsDir)) return "";
            string loaderRoot = Path.GetDirectoryName(modsDir);
            if (string.IsNullOrEmpty(loaderRoot)) return "";
            return Path.Combine(Path.Combine(loaderRoot, "hub-bridge"), "endpoint.json");
        }

        public static string MapRegion(string authTypeName, string rawZone)
        {
            if (!string.IsNullOrEmpty(authTypeName))
            {
                string auth = authTypeName.Trim().ToLowerInvariant();
                if (auth == "china") return "cn";
                if (auth == "abroad") return "int";
            }

            if (!string.IsNullOrEmpty(rawZone))
            {
                string zone = rawZone.Trim().ToLowerInvariant();
                if (zone == "cn" || zone == "aotcn" || zone == "china") return "cn";
                if (zone == "int" || zone == "jp" || zone == "abroad") return "int";
            }

            return "";
        }

        public static string BuildRoomSignature(BridgeRoomSnapshot room)
        {
            if (room == null || room.RoomId <= 0) return "";
            return string.Concat(
                room.RoomId.ToString(CultureInfo.InvariantCulture), "|",
                room.Pwd ?? "", "|",
                room.RoomServerId.ToString(CultureInfo.InvariantCulture), "|",
                room.MapId.ToString(CultureInfo.InvariantCulture), "|",
                room.MapType.ToString(CultureInfo.InvariantCulture), "|",
                room.MasterId.ToString(CultureInfo.InvariantCulture), "|",
                room.State ?? "", "|",
                room.PlayerCount.ToString(CultureInfo.InvariantCulture));
        }

        public static bool EvaluateJoinEligibility(
            bool isLoggedIn,
            long uid,
            string sceneName,
            bool isInRoom,
            long currentRoomId,
            bool inMatchTeam,
            long targetRoomId,
            out string errorCode,
            out string reason)
        {
            errorCode = "";
            reason = "";

            if (targetRoomId <= 0)
            {
                errorCode = "invalid_room_id";
                reason = "目标游戏房间号无效。";
                return false;
            }

            if (!isLoggedIn || uid <= 0)
            {
                errorCode = "not_logged_in";
                reason = "游戏尚未登录完成，请先进入主界面后再进房。";
                return false;
            }

            if (!string.Equals(sceneName, "Home", StringComparison.OrdinalIgnoreCase))
            {
                errorCode = "wrong_scene";
                reason = "当前不在游戏主界面，无法自动加入房间。";
                return false;
            }

            if (inMatchTeam)
            {
                errorCode = "in_match_team";
                reason = "当前处于组队匹配状态，请先退出匹配队列。";
                return false;
            }

            if (isInRoom)
            {
                if (currentRoomId == targetRoomId)
                {
                    errorCode = "already_in_target_room";
                    reason = "已经在目标游戏房间内。";
                    return true;
                }

                errorCode = "already_in_other_room";
                reason = "当前已在其它游戏房间中，请先退出原房间。";
                return false;
            }

            return true;
        }

        public static List<BridgeCommand> ParseCommandsResponse(string json)
        {
            List<BridgeCommand> list = new List<BridgeCommand>();
            if (string.IsNullOrWhiteSpace(json)) return list;

            object raw;
            if (!CesiumJson.TryDeserialize(json, out raw)) return list;
            IDictionary<string, object> root = raw as IDictionary<string, object>;
            if (root == null) return list;

            object commandsRaw;
            if (!root.TryGetValue("commands", out commandsRaw)) return list;
            IList arr = commandsRaw as IList;
            if (arr == null) return list;

            for (int i = 0; i < arr.Count; i++)
            {
                IDictionary<string, object> item = arr[i] as IDictionary<string, object>;
                if (item == null) continue;

                string cmdId = ReadString(item, "commandId", "").Trim();
                string action = ReadString(item, "action", "").Trim();
                if (string.IsNullOrEmpty(cmdId) || string.IsNullOrEmpty(action)) continue;

                BridgeCommand cmd = new BridgeCommand();
                cmd.CommandId = cmdId;
                cmd.Action = action;
                cmd.GameRoomId = ReadLong(item, "gameRoomId", 0);
                cmd.GamePwd = ReadString(item, "gamePwd", "");
                cmd.RoomServerId = ReadInt(item, "roomServerId", 0);
                list.Add(cmd);
            }

            return list;
        }

        public static string ReadString(IDictionary<string, object> map, string key, string fallback)
        {
            object val;
            if (map == null || !map.TryGetValue(key, out val) || val == null) return fallback;
            string s = val as string;
            if (s != null) return s;
            return Convert.ToString(val, CultureInfo.InvariantCulture) ?? fallback;
        }

        public static int ReadInt(IDictionary<string, object> map, string key, int fallback)
        {
            long v = ReadLong(map, key, fallback);
            if (v < int.MinValue) return int.MinValue;
            if (v > int.MaxValue) return int.MaxValue;
            return (int)v;
        }

        public static long ReadLong(IDictionary<string, object> map, string key, long fallback)
        {
            object val;
            if (map == null || !map.TryGetValue(key, out val) || val == null) return fallback;
            try { return Convert.ToInt64(val, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }
    }
}
