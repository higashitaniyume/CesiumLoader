using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

string? bridgePath = null;
int port = 18742;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--bridge" && i + 1 < args.Length) bridgePath = args[++i];
    else if (args[i] == "--port" && i + 1 < args.Length && int.TryParse(args[++i], out int parsed) && parsed is >= 1 and <= 65535) port = parsed;
    else throw new ArgumentException("Usage: SpectatorApi --bridge <path> [--port <1..65535>]");
}
if (string.IsNullOrWhiteSpace(bridgePath)) throw new ArgumentException("--bridge <path> is required.");
using var bridge = new Bridge(Path.GetFullPath(bridgePath));
string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
var builder = WebApplication.CreateBuilder(Array.Empty<string>());
builder.Logging.ClearProviders(); // Do not log Authorization, request bodies, or watch codes.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Loopback, port);
    options.Limits.MaxRequestBodySize = Bridge.RequestLimit;
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
});
var app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Path != "/health")
    {
        string auth = context.Request.Headers.Authorization.ToString();
        byte[] supplied = Encoding.UTF8.GetBytes(auth);
        byte[] expected = Encoding.UTF8.GetBytes("Bearer " + token);
        if (!CryptographicOperations.FixedTimeEquals(supplied, expected))
        {
            context.Response.Headers.WWWAuthenticate = "Bearer";
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "unauthorized" });
            return;
        }
    }
    try { await next(context); }
    catch (PayloadTooLargeException) { await Error(context, 413, "payload_too_large"); }
    catch (BadHttpRequestException e) { await Error(context, e.StatusCode, "invalid_request"); }
    catch (JsonException) { await Error(context, 400, "invalid_json"); }
    catch (IOException) { await Error(context, 503, "bridge_unavailable"); }
    catch (UnauthorizedAccessException) { await Error(context, 503, "bridge_unavailable"); }
});
app.MapGet("/health", () => Results.Json(new { status = "ok" }));
app.MapGet("/state", async () =>
{
    var state = await bridge.State();
    return state is null ? Results.Json(new { error = "state_unavailable" }, statusCode: 503) : Results.Json(state.Value);
});
app.MapPost("/watch", (Func<HttpContext, Task<IResult>>)(c => Submit(c, "join", "watchCode")));
app.MapPost("/focus", (Func<HttpContext, Task<IResult>>)(c => Submit(c, "focus", "playerId")));
app.MapPost("/leave", (Func<HttpContext, Task<IResult>>)(c => Submit(c, "leave", null)));
app.MapGet("/commands/{id}", async (string id) =>
{
    if (!Bridge.ValidId(id)) return Results.Json(new { error = "invalid_id" }, statusCode: 400);
    var result = await bridge.Result(id);
    if (result is not null) return Results.Json(result.Value, statusCode: Bridge.IsTerminal(result.Value) ? 200 : 202);
    return bridge.IsPending(id)
        ? Results.Json(new { id, status = "pending" }, statusCode: 202)
        : Results.Json(new { error = "command_not_found" }, statusCode: 404);
});
Console.WriteLine($"SpectatorApi listening on http://127.0.0.1:{port}");
Console.WriteLine($"Bearer token: {token}");
await app.RunAsync();

async Task<IResult> Submit(HttpContext context, string action, string? field)
{
    if (context.Request.ContentLength > Bridge.RequestLimit) throw new PayloadTooLargeException();
    byte[] bytes = await Bridge.ReadBounded(context.Request.Body, Bridge.RequestLimit, context.RequestAborted);
    string? value = null;
    if (bytes.Length != 0)
    {
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return Results.Json(new { error = "object_required" }, statusCode: 400);
        var properties = doc.RootElement.EnumerateObject().ToArray();
        if (field is null ? properties.Length != 0 : properties.Length != 1 || properties[0].Name != field)
            return Results.Json(new { error = "unexpected_fields" }, statusCode: 400);
        if (field is not null)
        {
            if (properties[0].Value.ValueKind != JsonValueKind.String) return Results.Json(new { error = "string_required" }, statusCode: 400);
            value = properties[0].Value.GetString();
        }
    }
    if (field is not null && (string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl) || value != value.Trim()))
        return Results.Json(new { error = "invalid_" + field }, statusCode: 400);
    string? id = await bridge.Enqueue(action, action == "join" ? value : null, action == "focus" ? value : null);
    return id is null
        ? Results.Json(new { error = "command_pending" }, statusCode: 409)
        : Results.Json(new { id, status = "pending" }, statusCode: 202);
}
static async Task Error(HttpContext context, int status, string error)
{
    if (context.Response.HasStarted) return;
    context.Response.StatusCode = status;
    await context.Response.WriteAsJsonAsync(new { error });
}

sealed class PayloadTooLargeException : Exception;

sealed class Bridge : IDisposable
{
    public const int RequestLimit = 4096;
    const int StateLimit = 1024 * 1024;
    const int ResultLimit = 64 * 1024;
    readonly string root;
    readonly string pendingPath;
    readonly FileStream processLock;
    readonly SemaphoreSlim gate = new(1, 1);
    string? pending;
    public Bridge(string root)
    {
        this.root = root;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "commands"));
        Directory.CreateDirectory(Path.Combine(root, "results"));
        Directory.CreateDirectory(Path.Combine(root, ".api"));
        processLock = new FileStream(Path.Combine(root, ".api", "server.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        pendingPath = Path.Combine(root, ".api", "pending.json");
        try
        {
            if (File.Exists(pendingPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(pendingPath));
                pending = doc.RootElement.GetProperty("id").GetString();
                if (pending is null || !ValidId(pending)) throw new InvalidDataException("Invalid pending ledger; inspect bridge before recovery.");
            }
        }
        catch { processLock.Dispose(); throw; }
    }
    public static bool ValidId(string id) => Regex.IsMatch(id, "\\A[0-9a-f]{32}\\z", RegexOptions.CultureInvariant);
    public bool IsPending(string id) => pending == id;
    public async Task<JsonElement?> State()
    {
        JsonElement? state = await ReadJson(Path.Combine(root, "state.json"), StateLimit);
        if (state is null) return null;
        var s = state.Value;
        if (!s.TryGetProperty("sampledUtc", out var sampled) || sampled.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParseExact(sampled.GetString(), new[] { "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz" }, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) ||
            timestamp.Offset != TimeSpan.Zero ||
            !s.TryGetProperty("sessionId", out var session) || session.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(session.GetString())) return null;
        var age = DateTimeOffset.UtcNow - timestamp;
        if (age > TimeSpan.FromSeconds(5) || age < TimeSpan.FromSeconds(-1)) return null;
        // Defense in depth: only the official PVE spectating bridge may expose card data.
        if (!s.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String || mode.GetString() != "pve" ||
            !s.TryGetProperty("officialSpectating", out var official) || official.ValueKind != JsonValueKind.True) return null;
        return s;
    }
    public async Task<JsonElement?> Result(string id)
    {
        var result = await ReadJson(Path.Combine(root, "results", id + ".json"), ResultLimit);
        if (result is null || !result.Value.TryGetProperty("id", out var actual) || actual.ValueKind != JsonValueKind.String || actual.GetString() != id ||
            !result.Value.TryGetProperty("sessionId", out var session) || session.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(session.GetString()) ||
            !result.Value.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String ||
            status.GetString() is not ("pending" or "done" or "failed" or "timeout")) return null;
        return result;
    }
    public static bool IsTerminal(JsonElement result) => result.GetProperty("status").GetString() is "done" or "failed" or "timeout";
    public async Task<string?> Enqueue(string action, string? watchCode, string? playerId)
    {
        await gate.WaitAsync();
        try
        {
            if (pending is not null)
            {
                var result = await Result(pending);
                if (result is null || !IsTerminal(result.Value)) return null;
                File.Delete(pendingPath);
                pending = null;
            }
            // Also reject unknown leftovers, including commands from an earlier server process.
            if (Directory.EnumerateFiles(Path.Combine(root, "commands"), "*.json").Any()) return null;
            string id = Guid.NewGuid().ToString("N");
            // Journal first: a crash must never silently allow another in-flight command.
            await AtomicWrite(pendingPath, JsonSerializer.SerializeToUtf8Bytes(new { id }));
            pending = id;
            await AtomicWrite(Path.Combine(root, "commands", id + ".json"), JsonSerializer.SerializeToUtf8Bytes(new { id, action, watchCode, playerId }));
            return id;
        }
        finally { gate.Release(); }
    }
    static async Task AtomicWrite(string destination, byte[] bytes)
    {
        string temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes);
            File.Move(temp, destination, false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    static async Task<JsonElement?> ReadJson(string path, int limit)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(await ReadBounded(stream, limit), new JsonDocumentOptions { MaxDepth = 32 });
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (Exception e) when (e is IOException or JsonException or PayloadTooLargeException or UnauthorizedAccessException) { return null; }
    }
    public static async Task<byte[]> ReadBounded(Stream stream, int limit, CancellationToken cancellation = default)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        while (true)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, limit + 1 - (int)output.Length)), cancellation);
            if (count == 0) return output.ToArray();
            output.Write(buffer, 0, count);
            if (output.Length > limit) throw new PayloadTooLargeException();
        }
    }
    public void Dispose() { processLock.Dispose(); gate.Dispose(); }
}
