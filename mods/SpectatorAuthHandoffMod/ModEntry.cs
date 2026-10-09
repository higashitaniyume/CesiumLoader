using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using CesiumLoader.SDK.Configuration;
using CesiumLoader.SDK.Mods;
using party.protocol;

[assembly: CesiumLoader.SDK.Manifests.ModManifest("Spectator authentication handoff", "0.1.1", "local prototype",
    Permissions = CesiumLoader.SDK.Manifests.ModPermission.ReadGameState | CesiumLoader.SDK.Manifests.ModPermission.FileSystem,
    SdkVersion = "2.3.2")]

namespace SpectatorAuthHandoffMod
{
    public static class ModEntry
    {
        public static void Main() { ModBase.Run(new AuthHandoff(), 30000); }
    }

    // All filesystem and game access happens in ModBase's main-thread callbacks.
    public sealed class AuthHandoff : ModBase
    {
        private string _root;
        private DateTime _next;
        private DateTime _expires;
        private bool _hasResponse;
        private readonly HashSet<string> _consumed = new HashSet<string>();
        public override string Version { get { return "0.1.1"; } }

        public override void OnInitialize()
        {
            var mods = Environment.GetEnvironmentVariable("CESIUM_MODS_DIR");
            if (string.IsNullOrEmpty(mods)) throw new InvalidOperationException("CESIUM_MODS_DIR missing");
            _root = Path.Combine(Path.GetDirectoryName(mods), "auth-handoff");
            Directory.CreateDirectory(_root);
            // No timestamp APIs: remnants from a stopped game are always stale.
            File.Delete(Path.Combine(_root, "response.json"));
            File.Delete(Path.Combine(_root, "response.json.tmp"));
        }

        public override void OnUpdate() { Poll(DateTime.UtcNow); }

        internal void Poll(DateTime now)
        {
            if (_root == null || now < _next) return;
            _next = now.AddMilliseconds(500);
            try
            {
                if (_hasResponse && now >= _expires)
                {
                    File.Delete(Path.Combine(_root, "response.json"));
                    File.Delete(Path.Combine(_root, "response.json.tmp"));
                    _hasResponse = false;
                }
                var request = Path.Combine(_root, "request.json");
                if (!File.Exists(request)) return;
                var json = File.ReadAllText(request);
                // If deletion fails, never inspect or serialize the game token.
                File.Delete(request);
                string nonce;
                if (!TryNonce(json, out nonce) || !_consumed.Add(nonce) || _hasResponse) return;
                var token = GameLogic.LoginServiceHelper.connectToken;
                if (token == null || token.Auth != AuthType.China || token.China == null) return;
                var host = Core.GameSettings.IP;
                var port = Core.GameSettings.Port;
                var version = Core.GameSettings.APP_VERSION;
                if (string.IsNullOrEmpty(host) || port < 1 || port > 65535 || string.IsNullOrEmpty(version)) return;
                var captured = DateTime.UtcNow;
                var response = new Dictionary<string, object>
                {
                    { "nonce", nonce }, { "capturedUtc", captured.ToString("o") },
                    { "host", host }, { "port", port }, { "clientVersion", version },
                    { "connectRequestBase64", Convert.ToBase64String(Google.Protobuf.MessageExtensions.ToByteArray(token)) },
                    { "protocolVersion1", Core.GameSettings.VER1 },
                    { "protocolVersion2", Core.GameSettings.VER2 },
                    { "protocolVersion3", Core.GameSettings.VER3 }
                };
                var temp = Path.Combine(_root, "response.json.tmp");
                var target = Path.Combine(_root, "response.json");
                // Track before writing, so even a failed move leaves a timed cleanup.
                _expires = now.AddSeconds(30);
                _hasResponse = true;
                File.WriteAllText(temp, CesiumJson.Serialize(response));
                File.Delete(target);
                File.Move(temp, target);
            }
            catch
            {
                // Exception messages and stack traces may contain sensitive data.
                // Do not log or emit them. Retry expired cleanup on the next poll.
                try { File.Delete(Path.Combine(_root, "response.json.tmp")); } catch { }
            }
        }

        public override void OnUnload()
        {
            if (_root == null) return;
            try { File.Delete(Path.Combine(_root, "response.json")); } catch { }
            try { File.Delete(Path.Combine(_root, "response.json.tmp")); } catch { }
            _root = null;
        }

        internal static bool TryNonce(string json, out string nonce)
        {
            nonce = null;
            if (json == null || json.Length > 512) return false;
            object parsed;
            if (!CesiumJson.TryDeserialize(json, out parsed)) return false;
            var obj = parsed as Dictionary<string, object>;
            object value;
            if (obj == null || obj.Count != 1 || !obj.TryGetValue("nonce", out value)) return false;
            var text = value as string;
            if (text == null || text.Length != 32) return false;
            foreach (var c in text) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            // CesiumJson accepts trailing text and duplicate keys. Enforce this
            // protocol's literal single-key shape, permitting JSON whitespace only.
            var compact = new StringBuilder();
            bool quoted = false;
            foreach (var c in json)
            {
                if (c == '"') quoted = !quoted;
                if (!quoted && (c == ' ' || c == '\t' || c == '\r' || c == '\n')) continue;
                compact.Append(c);
            }
            if (compact.ToString() != "{\"nonce\":\"" + text + "\"}") return false;
            nonce = text;
            return true;
        }
    }
}
