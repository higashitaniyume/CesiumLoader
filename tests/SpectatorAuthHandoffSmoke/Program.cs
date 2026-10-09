using System;
using System.Collections.Generic;
using System.IO;
using CesiumLoader.SDK.Configuration;
using SpectatorAuthHandoffMod;
using party.protocol;

static class Program
{
    static void Check(bool ok, string name) { if (!ok) throw new Exception("Smoke failed: " + name); }
    static void Main()
    {
        const string nonce = "0123456789abcdef0123456789abcdef";
        string parsed;
        Check(AuthHandoff.TryNonce(" { \"nonce\" : \"" + nonce + "\" } \n", out parsed) && parsed == nonce, "valid marker");
        foreach (var bad in new[] { "null", "{}", "{\"nonce\":1}", "{\"nonce\":\"short\"}", "{\"nonce\":\"" + nonce.ToUpperInvariant() + "\"}", "{\"nonce\":\"" + nonce + "\"} trailing", "{\"nonce\":\"" + nonce + "\",\"nonce\":\"" + nonce + "\"}", "{\"nonce\":\"" + nonce + "\",}", "{\"nonce\":\"" + nonce + "\",\"extra\":true}" })
            Check(!AuthHandoff.TryNonce(bad, out parsed), "malformed marker");
        var scratch = Path.Combine(Path.GetTempPath(), "SpectatorAuthHandoffSmoke-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(scratch, "auth-handoff");
        var previous = Environment.GetEnvironmentVariable("CESIUM_MODS_DIR");
        Environment.SetEnvironmentVariable("CESIUM_MODS_DIR", Path.Combine(scratch, "mods"));
        Directory.CreateDirectory(root);
        var response = Path.Combine(root, "response.json");
        var request = Path.Combine(root, "request.json");
        var temp = Path.Combine(root, "response.json.tmp");
        var mod = new AuthHandoff();
        try
        {
            ModEntry.Main();
            File.WriteAllText(response, "synthetic stale"); File.WriteAllText(temp, "synthetic stale");
            mod.OnInitialize();
            Check(!File.Exists(response) && !File.Exists(temp), "startup cleanup");
            var now = DateTime.UtcNow;
            GameLogic.LoginServiceHelper.connectToken = new ConnectC2S { Auth = AuthType.China, China = new ChinaInfo() };
            mod.Poll(now);
            Check(Google.Protobuf.MessageExtensions.Calls == 0 && !File.Exists(response), "never automatic");
            File.WriteAllText(request, "{\"nonce\":\"" + nonce + "\"}");
            mod.Poll(now.AddMilliseconds(100));
            Check(File.Exists(request), "half second throttle");
            mod.Poll(now.AddSeconds(1));
            Check(!File.Exists(request) && File.Exists(response) && !File.Exists(temp), "one response marker consumed");
            var obj = CesiumJson.Deserialize(File.ReadAllText(response)) as Dictionary<string, object>;
            Check(obj != null && obj.Count == 9 && Convert.ToInt32(obj["protocolVersion1"])==3 && Convert.ToInt32(obj["protocolVersion2"])==2 && Convert.ToInt32(obj["protocolVersion3"])==1 && (string)obj["nonce"] == nonce && (string)obj["host"] == "localhost" && (string)obj["clientVersion"] == "synthetic" && obj.ContainsKey("capturedUtc") && obj.ContainsKey("port") && obj.ContainsKey("connectRequestBase64"), "schema");
            File.WriteAllText(request, "{\"nonce\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\"}");
            mod.Poll(now.AddSeconds(2));
            Check(!File.Exists(request) && Google.Protobuf.MessageExtensions.Calls == 1, "single live response");
            mod.Poll(now.AddSeconds(30)); Check(File.Exists(response), "response before lifetime");
            mod.Poll(now.AddSeconds(31)); Check(!File.Exists(response), "response expires");
            File.WriteAllText(request, "{\"nonce\":\"" + nonce + "\"}"); mod.Poll(now.AddSeconds(32));
            Check(!File.Exists(response) && Google.Protobuf.MessageExtensions.Calls == 1, "nonce replay rejected");
            int i = 3;
            foreach (var token in new[] { null, new ConnectC2S { Auth = AuthType.Dev, China = new ChinaInfo() }, new ConnectC2S { Auth = AuthType.Other, China = new ChinaInfo() }, new ConnectC2S { Auth = AuthType.China } })
            {
                GameLogic.LoginServiceHelper.connectToken = token;
                File.WriteAllText(request, "{\"nonce\":\"" + i.ToString().PadLeft(32, '0') + "\"}");
                mod.Poll(now.AddSeconds(32 + i++));
                Check(!File.Exists(request) && !File.Exists(response) && Google.Protobuf.MessageExtensions.Calls == 1, "null or invalid Auth rejected");
            }
            Console.WriteLine("PASS: strict nonce, startup cleanup/delay, explicit marker, throttle, schema, one-shot, single response, lifetime, null/non-China rejection.");
        }
        finally
        {
            mod.OnUnload(); Environment.SetEnvironmentVariable("CESIUM_MODS_DIR", previous);
            // Only this generated smoke scratch directory is removed.
            Directory.Delete(scratch, true);
        }
    }
}
