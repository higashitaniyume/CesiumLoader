using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CesiumLoader.SDK.Configuration;

namespace ToysBadgeMod
{
    internal enum BadgeClientKind
    {
        None = 0,
        Toys = 1,
        ModLab = 2
    }

    internal sealed class RoomMember
    {
        public string MemberId;
        public string Nick;
        public string NormalizedNick;
        public string ModVersion;
        public bool Online;
        public bool Self;
        public long JoinedAt;

        public RoomMember()
        {
            MemberId = "";
            Nick = "";
            NormalizedNick = "";
            ModVersion = "";
        }
    }

    internal sealed class JoinResponse
    {
        public int Protocol;
        public string RoomId;
        public int OnlineCount;
        public int Capacity;
        public long CreatedAt;
        public List<RoomMember> Members;
        public string Token;
        public long ExpiresAt;
        public long Cursor;
        public long Epoch;
        public int PollWaitSeconds;
        public long ServerTime;
        public string ErrorCode;
        public string ErrorMessage;

        public JoinResponse()
        {
            RoomId = "";
            Members = new List<RoomMember>();
            Token = "";
            ErrorCode = "";
            ErrorMessage = "";
        }
    }

    internal static class PresenceProtocol
    {
        public const int MinWatchCodeLength = 4;
        public const int MaxWatchCodeLength = 32;
        public const int MinClientIdLength = 8;
        public const int MaxClientIdLength = 64;
        public const int MaxNickLength = 24;
        public const string ModLabVersionPrefix = "APE-";
        public const string ToysTooltipText = "该玩家正在使用AstralParty.Toys";
        public const string ModLabTooltipText = "该玩家正在使用吉星ModLab";

        public static BadgeClientKind ResolveClientKind(string modVersion)
        {
            string v = (modVersion ?? "").Trim();
            if (v.StartsWith(ModLabVersionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return BadgeClientKind.ModLab;
            }
            return BadgeClientKind.Toys;
        }

        public static string GetTooltipText(BadgeClientKind kind)
        {
            if (kind == BadgeClientKind.ModLab)
            {
                return ModLabTooltipText;
            }
            return ToysTooltipText;
        }

        private static readonly Regex UbbTagRegex = new Regex(
            @"\[/?(?:color|size|b|i|u|s|sup|sub|url|img|align|font|outline|shadow)(?:=[^\]]*)?\]",
            RegexOptions.IgnoreCase);

        private static readonly Regex TmpTagRegex = new Regex(
            @"</?(?:color|size|b|i|u|s|sup|sub|align|font|sprite|gradient|mark|alpha|cspace|mspace|indent|line-height|line-indent|margin|noparse|page|pos|space|voffset|width|style|link)(?:=[^>]*)?>",
            RegexOptions.IgnoreCase);

        public static string MapRegion(string rawZone)
        {
            if (string.IsNullOrEmpty(rawZone)) return "cn";
            string s = rawZone.Trim().ToLowerInvariant();
            if (s.Length == 0 || s == "cn" || s == "aotcn") return "cn";
            if (s == "int") return "int";
            if (s == "jp") return "jp";
            if (s == "kr") return "kr";
            if (s == "us") return "us";
            return "other";
        }

        public static bool IsValidWatchCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return false;
            if (code.Length < MinWatchCodeLength || code.Length > MaxWatchCodeLength) return false;
            for (int i = 0; i < code.Length; i++)
            {
                char ch = code[i];
                bool ok = (ch >= 'A' && ch <= 'Z')
                    || (ch >= 'a' && ch <= 'z')
                    || (ch >= '0' && ch <= '9')
                    || ch == '_'
                    || ch == '-';
                if (!ok) return false;
            }
            return true;
        }

        public static bool IsValidClientId(string cid)
        {
            if (string.IsNullOrEmpty(cid)) return false;
            if (cid.Length < MinClientIdLength || cid.Length > MaxClientIdLength) return false;
            for (int i = 0; i < cid.Length; i++)
            {
                char ch = cid[i];
                if (ch < 0x21 || ch > 0x7E) return false;
            }
            return true;
        }

        public static string StripRichTextTags(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            string current = text;
            for (int pass = 0; pass < 4; pass++)
            {
                string next = UbbTagRegex.Replace(current, "");
                next = TmpTagRegex.Replace(next, "");
                if (string.Equals(next, current, StringComparison.Ordinal)) break;
                current = next;
            }
            return current;
        }

        public static string SanitizeNick(string nick)
        {
            if (string.IsNullOrEmpty(nick)) return "";
            string stripped = StripControlChars(StripRichTextTags(nick)).Trim();
            if (string.IsNullOrEmpty(stripped))
            {
                stripped = StripControlChars(nick).Trim();
            }
            if (stripped.Length > MaxNickLength)
            {
                stripped = stripped.Substring(0, MaxNickLength).TrimEnd();
            }
            return stripped;
        }

        public static string DeriveScopedClientId(string baseClientId, string profile, long playerId)
        {
            string b = (baseClientId ?? "").Trim();
            string suffix = "";
            if (playerId > 0)
            {
                suffix = "_p" + playerId.ToString(CultureInfo.InvariantCulture);
            }
            else if (!string.IsNullOrEmpty(profile))
            {
                StringBuilder safe = new StringBuilder(16);
                for (int i = 0; i < profile.Length && safe.Length < 16; i++)
                {
                    char ch = profile[i];
                    bool ok = (ch >= 'A' && ch <= 'Z')
                        || (ch >= 'a' && ch <= 'z')
                        || (ch >= '0' && ch <= '9')
                        || ch == '_'
                        || ch == '-';
                    if (ok) safe.Append(ch);
                }
                if (safe.Length > 0)
                {
                    suffix = "_pr_" + safe.ToString();
                }
            }

            if (string.IsNullOrEmpty(suffix))
            {
                return b;
            }

            int maxPrefix = MaxClientIdLength - suffix.Length;
            if (maxPrefix < 0) maxPrefix = 0;
            string prefix = b.Length > maxPrefix ? b.Substring(0, maxPrefix) : b;
            string combined = prefix + suffix;
            return IsValidClientId(combined) ? combined : b;
        }

        private static string StripControlChars(string input)
        {
            if (string.IsNullOrEmpty(input)) return "";
            StringBuilder sb = new StringBuilder(input.Length);
            for (int i = 0; i < input.Length; i++)
            {
                char ch = input[i];
                if (ch < 0x20 || ch == 0x7F) continue;
                sb.Append(ch);
            }
            return sb.ToString();
        }

        public static string BuildJoinUrl(string baseUrl, string watchCode)
        {
            string b = TrimTrailingSlash(baseUrl);
            return b + "/api/v1/rooms/" + Uri.EscapeDataString(watchCode ?? "") + "/join";
        }

        public static string BuildLeaveUrl(string baseUrl, string watchCode)
        {
            string b = TrimTrailingSlash(baseUrl);
            return b + "/api/v1/rooms/" + Uri.EscapeDataString(watchCode ?? "") + "/leave";
        }

        public static string BuildJoinBody(string region, string nick, string clientId, string modVersion, int heroId)
        {
            Dictionary<string, object> body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["region"] = string.IsNullOrEmpty(region) ? "cn" : region;
            body["nick"] = nick ?? "";
            body["clientId"] = clientId ?? "";
            body["modVersion"] = string.IsNullOrEmpty(modVersion) ? "1.0.0" : modVersion;
            body["heroId"] = heroId;
            return CesiumJson.Serialize(body);
        }

        public static string BuildLeaveBody(string region, string token)
        {
            Dictionary<string, object> body = new Dictionary<string, object>(StringComparer.Ordinal);
            body["region"] = string.IsNullOrEmpty(region) ? "cn" : region;
            body["token"] = token ?? "";
            return CesiumJson.Serialize(body);
        }

        public static JoinResponse ParseJoinResponse(string json)
        {
            JoinResponse res = new JoinResponse();
            if (string.IsNullOrEmpty(json)) return res;

            object rootObj;
            if (!CesiumJson.TryDeserialize(json, out rootObj)) return res;
            IDictionary<string, object> root = rootObj as IDictionary<string, object>;
            if (root == null) return res;

            res.Protocol = ReadInt(root, "protocol", 0);
            res.Cursor = ReadLong(root, "cursor", 0);
            res.Epoch = ReadLong(root, "epoch", 0);
            res.ServerTime = ReadLong(root, "serverTime", 0);

            IDictionary<string, object> errObj = ReadDict(root, "error");
            if (errObj != null)
            {
                res.ErrorCode = ReadString(errObj, "code", "");
                res.ErrorMessage = ReadString(errObj, "message", "");
            }

            IDictionary<string, object> sessionObj = ReadDict(root, "session");
            if (sessionObj != null)
            {
                res.Token = ReadString(sessionObj, "token", "");
                res.ExpiresAt = ReadLong(sessionObj, "expiresAt", 0);
            }

            IDictionary<string, object> cfgObj = ReadDict(root, "config");
            if (cfgObj != null)
            {
                res.PollWaitSeconds = ReadInt(cfgObj, "pollWaitSeconds", 0);
            }

            IDictionary<string, object> roomObj = ReadDict(root, "room");
            if (roomObj != null)
            {
                res.RoomId = ReadString(roomObj, "roomId", "");
                res.OnlineCount = ReadInt(roomObj, "onlineCount", 0);
                res.Capacity = ReadInt(roomObj, "capacity", 0);
                res.CreatedAt = ReadLong(roomObj, "createdAt", 0);

                object membersRaw;
                if (roomObj.TryGetValue("members", out membersRaw))
                {
                    IList membersList = membersRaw as IList;
                    if (membersList != null)
                    {
                        for (int i = 0; i < membersList.Count; i++)
                        {
                            IDictionary<string, object> item = membersList[i] as IDictionary<string, object>;
                            if (item == null) continue;
                            string rawNick = ReadString(item, "nick", "");
                            RoomMember m = new RoomMember();
                            m.MemberId = ReadString(item, "memberId", "");
                            m.Nick = rawNick;
                            m.NormalizedNick = SanitizeNick(rawNick);
                            m.ModVersion = ReadStringFirst(item, new string[] { "modVersion", "modversion", "mod_version" }, "");
                            m.Online = ReadBool(item, "online", false);
                            m.Self = ReadBool(item, "self", false);
                            m.JoinedAt = ReadLong(item, "joinedAt", 0);
                            res.Members.Add(m);
                        }
                    }
                }
            }

            return res;
        }

        private static string TrimTrailingSlash(string baseUrl)
        {
            string s = (baseUrl ?? "").Trim();
            while (s.EndsWith("/"))
            {
                s = s.Substring(0, s.Length - 1);
            }
            return s;
        }

        private static IDictionary<string, object> ReadDict(IDictionary<string, object> map, string key)
        {
            object val;
            if (map != null && map.TryGetValue(key, out val))
            {
                return val as IDictionary<string, object>;
            }
            return null;
        }

        private static string ReadString(IDictionary<string, object> map, string key, string fallback)
        {
            object val;
            if (map == null || !map.TryGetValue(key, out val) || val == null) return fallback;
            string s = val as string;
            if (s != null) return s;
            return Convert.ToString(val, CultureInfo.InvariantCulture) ?? fallback;
        }

        private static string ReadStringFirst(IDictionary<string, object> map, string[] keys, string fallback)
        {
            if (map == null || keys == null) return fallback;
            for (int i = 0; i < keys.Length; i++)
            {
                string val = ReadString(map, keys[i], "");
                if (!string.IsNullOrEmpty(val)) return val;
            }
            return fallback;
        }

        private static bool ReadBool(IDictionary<string, object> map, string key, bool fallback)
        {
            object val;
            if (map == null || !map.TryGetValue(key, out val) || val == null) return fallback;
            if (val is bool) return (bool)val;
            if (val is string)
            {
                bool b;
                if (bool.TryParse((string)val, out b)) return b;
            }
            try { return Convert.ToDouble(val, CultureInfo.InvariantCulture) != 0d; }
            catch { return fallback; }
        }

        private static int ReadInt(IDictionary<string, object> map, string key, int fallback)
        {
            long v = ReadLong(map, key, fallback);
            if (v < int.MinValue) return int.MinValue;
            if (v > int.MaxValue) return int.MaxValue;
            return (int)v;
        }

        private static long ReadLong(IDictionary<string, object> map, string key, long fallback)
        {
            object val;
            if (map == null || !map.TryGetValue(key, out val) || val == null) return fallback;
            try { return Convert.ToInt64(val, CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }
    }
}
