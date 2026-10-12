using System.Globalization;
using System.Text.Json;
using CharacterProbeMod;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
string text = "路径\\角色\"\n\r\t\0\u001f";
var row = new JsonRow().S("text", text).N("ticks", 638000000000000000).F("nan", float.NaN).B("flag", true);
using (var doc = JsonDocument.Parse(row.ToString()))
{
    Check(doc.RootElement.GetProperty("text").GetString() == text, "JSON escaping roundtrip failed");
    Check(doc.RootElement.GetProperty("ticks").GetInt64() == 638000000000000000, "Tick precision lost");
    Check(doc.RootElement.GetProperty("nan").ValueKind == JsonValueKind.Null, "Nonfinite JSON invalid");
}
CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
using (var doc = JsonDocument.Parse(new JsonRow().F("f", 1.25f).ToString()))
    Check(doc.RootElement.GetProperty("f").GetSingle() == 1.25f, "Decimal comma produced invalid JSON");
string dir = Path.Combine(Path.GetTempPath(), "character-probe-smoke-" + Guid.NewGuid());
Directory.CreateDirectory(dir);
string path = Path.Combine(dir, "observations.jsonl");
var log = new ProbeLog(path);
log.Add(new JsonRow().S("kind", "first"));
log.Flush();
log.Add(new JsonRow().S("kind", "second"));
log.Flush();
var lines = File.ReadAllLines(path);
Check(lines.Length == 2, "Flush lost or repeated records");
foreach (var line in lines) using (JsonDocument.Parse(line)) { }
string huge = new string('x', ProbeLog.MaxPendingCharacters);
log.Add(new JsonRow().S("payload", huge));
Check(log.Dropped == 1 && log.Records == 2, "Oversized single-record cap failed");
string burstPath = Path.Combine(dir, "burst.jsonl");
var burstLog = new ProbeLog(burstPath);
string burstPayload = new string('x', 150 * 1024);
burstLog.Add(new JsonRow().N("index", 1).S("payload", burstPayload));
burstLog.Add(new JsonRow().N("index", 2).S("payload", burstPayload));
Check(burstLog.Characters > 0, "Full buffer was not drained before periodic flush");
burstLog.Flush();
var burstLines = File.ReadAllLines(burstPath);
Check(burstLog.Dropped == 0 && burstLog.Records == 2 && burstLines.Length == 2, "Burst traffic dropped records");
for (int i = 0; i < burstLines.Length; i++)
{
    using var burstDoc = JsonDocument.Parse(burstLines[i]);
    Check(burstDoc.RootElement.GetProperty("index").GetInt32() == i + 1, "Burst flush changed ordering");
    Check(burstDoc.RootElement.GetProperty("payload").GetString() == burstPayload, "Burst payload was truncated");
}
var sourceDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../mods/CharacterProbeMod"));
foreach (string source in Directory.GetFiles(sourceDir, "*.cs"))
{
    string code = File.ReadAllText(source);
    foreach (string banned in new[] { "FileStream", "StreamWriter", "StreamReader", "new FileInfo", "new DirectoryInfo", "DllImport", "File.ReadAllLines", "Assembly.Location", "AppDomain.CurrentDomain.BaseDirectory", "new MissingMemberException", "new MissingMethodException" })
        Check(!code.Contains(banned), "Unsupported game BCL: " + banned + " in " + source);
}
var fixture = new AccessorDerived();
Check((int)ProbeReflection.Read(fixture, "normal") == 11, "Property read failed");
Check((int)ProbeReflection.Read(fixture, "nativeLike") == 22, "Getter-only fallback failed");
Check((int)ProbeReflection.Read(fixture, "inherited") == 33, "Inherited getter fallback failed");
Check((int)ProbeReflection.Read(fixture, "field") == 44, "Field fallback failed");
Check((int)ProbeReflection.Read(fixture, "nativeLike") == 22, "Cached read failed");
try { ProbeReflection.Read(fixture, "absent"); throw new Exception("Missing lookup unexpectedly succeeded"); }
catch (Exception ex) { Check(ex.Message.Contains("AccessorDerived.absent"), "Missing member diagnosis lost the actual name"); }
Console.WriteLine("PASS: JSON, incremental/bounded logs, BCL lint, properties, getter-only metadata, inherited getters, fields, cached lookups, missing member diagnosis.");
Console.WriteLine("Smoke artifacts: " + dir);

public class AccessorBase
{
    public int get_inherited() => 33;
    public int field = 44;
}
public class AccessorDerived : AccessorBase
{
    public int normal => 11;
    public int get_nativeLike() => 22;
}
