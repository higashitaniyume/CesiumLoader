using StandaloneSpectator;

var directory = args.Length == 1 ? args[0] : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../../extracted_dlls"));
using var protocol = new ProtocolMessages(directory);
var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    checks++;
}
Dictionary<string, object?> Fields(params (string Name, object? Value)[] values) => values.ToDictionary(v => v.Name, v => v.Value);
object Roundtrip(string type, Dictionary<string, object?>? fields = null)
{
    var message = protocol.Create(type, fields);
    var bytes = protocol.MessageBytes(message);
    var parsed = protocol.Parse(type, bytes);
    Check(bytes.SequenceEqual(protocol.MessageBytes(parsed)), type + " wire roundtrip");
    return parsed;
}
void Reject(Action action, string description)
{
    try { action(); }
    catch (InvalidOperationException) { checks++; return; }
    throw new InvalidOperationException("FAIL: " + description);
}

// Deliberately synthetic strings, never read credentials or contact any endpoint.
var china = Fields(("GameId", "fake-game"), ("ChannelId", "fake-channel"), ("AppId", "fake-app"),
    ("Sid", "offline-smoke-not-a-session"), ("Extra", "fake-extra"), ("DeviceId", "fake-device"));
var connect = Roundtrip("party.protocol.ConnectC2S", Fields(("Auth", "China"),
    ("ClientVer", "offline-smoke"), ("PublicKey", "fake-public-key"), ("China", china)));
Check(ProtocolMessages.Get(connect, "Auth")!.ToString() == "China", "symbolic enum conversion");
Check(ProtocolMessages.Get(connect, "AuthInfoCase")!.ToString() == "China", "China auth oneof");
Check((string)ProtocolMessages.Get(ProtocolMessages.Get(connect, "China")!, "Sid")! == "offline-smoke-not-a-session", "fake nested credentials");
protocol.Set(connect, "Auth", "4");
Check(ProtocolMessages.Get(connect, "Auth")!.ToString() == "China", "numeric enum string");
protocol.Set(connect, "Auth", 4);
Check(ProtocolMessages.Get(connect, "Auth")!.ToString() == "China", "numeric enum value");
Reject(() => protocol.Set(connect, "Auth", "Dev"), "Dev symbolic auth rejected");
Reject(() => protocol.Set(connect, "Auth", "0"), "Dev numeric auth rejected");
Reject(() => protocol.MessageBytes(protocol.Create("party.protocol.ConnectC2S")), "default Dev auth rejected");
Reject(() => protocol.Parse("party.protocol.ConnectC2S", Array.Empty<byte>()), "parsed default Dev rejected");

var join = Roundtrip("party.protocol.WatchJoinRoomC2S", Fields(("WatchCode", "OFFLINE-FAKE")));
Check((string)ProtocolMessages.Get(join, "WatchCode")! == "OFFLINE-FAKE", "watch code roundtrip");
var refresh = Roundtrip("party.protocol.WatchRefreshRoomStateC2S", Fields(("RoomId", "1234567890123"), ("RoomServerId", 7)));
Check((long)ProtocolMessages.Get(refresh, "RoomId")! == 1234567890123L, "sfixed64 room ID");
Check((int)ProtocolMessages.Get(refresh, "RoomServerId")! == 7, "sfixed32 room server ID");
Roundtrip("party.protocol.WatchExitRoomC2S");
var heartbeat = Roundtrip("party.protocol.HeartbeatC2S", Fields(("Client", 1234567890123L)));
Check((long)ProtocolMessages.Get(heartbeat, "Client")! == 1234567890123L, "heartbeat timestamp");

var player = protocol.Create("party.model.Player", Fields(("Id", 1234567890123L), ("Nick", "Offline Player"), ("Slot", 2)));
var room = protocol.Create("party.model.Room", Fields(("Id", 9876543210123L), ("Name", "Offline Room"),
    ("State", "Running"), ("Round", 3), ("Players", new[] { player }), ("RoomTerms", new object[] { "12", 34 })));
var snapshot = Roundtrip("party.protocol.WatchRefreshRoomStateS2C", Fields(("RoomId", 9876543210123L), ("Room", room)));
var parsedRoom = ProtocolMessages.Get(snapshot, "Room")!;
Check((long)ProtocolMessages.Get(parsedRoom, "Id")! == 9876543210123L, "nested room ID");
Check(ProtocolMessages.Get(parsedRoom, "State")!.ToString() == "Running", "room state enum");
Check((int)ProtocolMessages.Get(parsedRoom, "Round")! == 3, "room round");
Check((string)ProtocolMessages.Get(ProtocolMessages.Enumerable(parsedRoom, "Players").Single(), "Nick")! == "Offline Player", "repeated nested player");
Check(ProtocolMessages.Enumerable(parsedRoom, "RoomTerms").Select(Convert.ToInt32).SequenceEqual(new[] { 12, 34 }), "repeated scalar conversion");
Roundtrip("party.protocol.ReplaySnapshotS2C", Fields(("PlayerId", 1234567890123L), ("Room", room)));
Roundtrip("party.protocol.RunningGameS2C", Fields(("Room", room)));
var watchReply = Roundtrip("party.protocol.WatchJoinRoomS2C", Fields(("RoomId", 9876543210123L), ("Players", new[] { player })));
Check(ProtocolMessages.Enumerable(watchReply, "Players").Count() == 1, "watch join player list");

// ByteString construction and serialization must also use the extracted protobuf runtime.
var action = Roundtrip("party.model.Action", Fields(("Id", 1002), ("Data", new byte[] { 1, 2, 3 })));
var data = ProtocolMessages.Get(action, "Data")!;
Check(((byte[])data.GetType().GetMethod("ToByteArray", Type.EmptyTypes)!.Invoke(data, null)!).SequenceEqual(new byte[] { 1, 2, 3 }), "ByteString roundtrip");
Check(ProtocolMessages.Commands["login"] == 5001 && ProtocolMessages.Commands["heartbeat"] == 5003 &&
    ProtocolMessages.Commands["watchjoin"] == 5191 && ProtocolMessages.Commands["watchrefresh"] == 5193 &&
    ProtocolMessages.Commands["watchexit"] == 5195 && ProtocolMessages.Commands["ack"] == null, "verified command mappings");
Check(!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name?.StartsWith("Unity", StringComparison.Ordinal) == true), "Unity assemblies never loaded");
Console.WriteLine($"PASS: {checks} checks; real extracted schema, China fake auth, watch requests/replies, room snapshots, ByteString; no Unity or remote calls.");
