using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;

namespace StandaloneSpectator;

/// <summary>Uses the extracted game's generated protobuf schema without starting Unity.</summary>
public sealed class ProtocolMessages : IDisposable
{
    // Verified in Core.Net.*C2SRPC.RPCCallStatic calls in AstralParty.Runtime.
    public const ushort Login = 5001;
    public const ushort WatchJoin = 5191;
    public const ushort WatchRefresh = 5193;
    public const ushort WatchExit = 5195;
    public const ushort Heartbeat = 5003;

    // NetManager.USocketEvent_OnMessage removes incoming UPSN from ackQueue.
    // No separate ACK command is established by the extracted client code.
    public static IReadOnlyDictionary<string, ushort?> Commands { get; } =
        new ReadOnlyDictionary<string, ushort?>(new Dictionary<string, ushort?>(StringComparer.OrdinalIgnoreCase)
        {
            ["login"] = Login, ["watchjoin"] = WatchJoin, ["watchrefresh"] = WatchRefresh,
            ["watchexit"] = WatchExit, ["heartbeat"] = Heartbeat, ["ack"] = null
        });

    private readonly SchemaContext context;
    private readonly Assembly schema;
    private readonly Type messageInterface;
    private readonly Type byteString;
    private readonly MethodInfo toByteArray;
    private bool disposed;

    public ProtocolMessages(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var root = Path.GetFullPath(directory);
        var runtime = Path.Combine(root, "AstralParty.Runtime.dll");
        var protobuf = Path.Combine(root, "Google.Protobuf.Runtime.dll");
        if (!File.Exists(runtime)) throw new FileNotFoundException("Extracted protocol schema was not found.", runtime);
        if (!File.Exists(protobuf)) throw new FileNotFoundException("Extracted protobuf runtime was not found.", protobuf);
        context = new SchemaContext(root);
        try
        {
            var protoAssembly = context.LoadFromAssemblyPath(protobuf);
            schema = context.LoadFromAssemblyPath(runtime);
            messageInterface = protoAssembly.GetType("Google.Protobuf.IMessage", true)!;
            byteString = protoAssembly.GetType("Google.Protobuf.ByteString", true)!;
            var extensions = protoAssembly.GetType("Google.Protobuf.MessageExtensions", true)!;
            toByteArray = extensions.GetMethod("ToByteArray", BindingFlags.Public | BindingFlags.Static,
                null, new[] { messageInterface }, null)
                ?? throw new MissingMethodException(extensions.FullName, "ToByteArray(IMessage)");
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    public object Create(string fullName, IDictionary<string, object?>? properties = null)
    {
        ThrowIfDisposed();
        var type = MessageType(fullName);
        var message = Activator.CreateInstance(type)!;
        if (properties != null)
            foreach (var property in properties) Set(message, property.Key, property.Value);
        return message;
    }

    public byte[] MessageBytes(object message)
    {
        ThrowIfDisposed();
        RequireMessage(message);
        RejectDevLogin(message);
        return (byte[])toByteArray.Invoke(null, new[] { message })!;
    }

    public object Parse(string fullName, byte[] payload)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(payload);
        var type = MessageType(fullName);
        var parser = type.GetProperty("Parser", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
            ?? throw new MissingMemberException(type.FullName, "Parser");
        var parse = parser.GetType().GetMethod("ParseFrom", new[] { typeof(byte[]) })
            ?? throw new MissingMethodException(parser.GetType().FullName, "ParseFrom(byte[])");
        var message = parse.Invoke(parser, new object[] { payload })!;
        RejectDevLogin(message);
        return message;
    }

    public static object? Get(object message, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(message);
        return Property(message.GetType(), propertyName).GetValue(message);
    }

    public static IEnumerable<object> Enumerable(object message, string propertyName)
    {
        var value = Get(message, propertyName);
        if (value == null) return Array.Empty<object>();
        if (value is string || value is not IEnumerable sequence)
            throw new ArgumentException($"{propertyName} is not a collection.", nameof(propertyName));
        return sequence.Cast<object>();
    }

    /// <summary>Sets a scalar/nested field or replaces a repeated field's contents.</summary>
    public void Set(object message, string propertyName, object? value)
    {
        ThrowIfDisposed();
        RequireMessage(message);
        var property = Property(message.GetType(), propertyName);
        if (message.GetType().FullName == "party.protocol.ConnectC2S" && propertyName == "Dev" && value != null)
            throw new InvalidOperationException("Dev authentication is disabled.");
        if (property.SetMethod != null)
        {
            var converted = ConvertValue(property.PropertyType, value);
            if (message.GetType().FullName == "party.protocol.ConnectC2S" && propertyName == "Auth" &&
                converted?.ToString() == "Dev")
                throw new InvalidOperationException("Dev authentication is disabled.");
            property.SetValue(message, converted);
            return;
        }
        var collection = property.GetValue(message) ?? throw new InvalidOperationException("Collection field is null.");
        if (value is string || value is not IEnumerable items)
            throw new ArgumentException($"{propertyName} requires a collection.", nameof(value));
        var args = property.PropertyType.GetGenericArguments();
        if (args.Length != 1 || property.PropertyType.FullName?.StartsWith("Google.Protobuf.Collections.RepeatedField`1", StringComparison.Ordinal) != true)
            throw new ArgumentException($"{propertyName} is not a writable scalar or repeated field.", nameof(propertyName));
        var convertedItems = items.Cast<object?>().Select(item => ConvertValue(args[0], item)).ToArray();
        var clear = property.PropertyType.GetMethod("Clear", Type.EmptyTypes)!;
        var add = property.PropertyType.GetMethod("Add", new[] { args[0] })!;
        clear.Invoke(collection, null);
        foreach (var item in convertedItems) add.Invoke(collection, new[] { item });
    }

    private object? ConvertValue(Type target, object? value)
    {
        if (value == null)
        {
            if (target.IsValueType) throw new ArgumentException($"Null cannot be assigned to {target.Name}.");
            return null;
        }
        if (target.IsInstanceOfType(value)) return value;
        if (target.IsEnum)
            return value is string text ? Enum.Parse(target, text, ignoreCase: true) :
                Enum.ToObject(target, Convert.ChangeType(value, Enum.GetUnderlyingType(target), CultureInfo.InvariantCulture)!);
        if (target == byteString && value is byte[] bytes)
            return byteString.GetMethod("CopyFrom", new[] { typeof(byte[]) })!.Invoke(null, new object[] { bytes });
        if (messageInterface.IsAssignableFrom(target) && value is IDictionary<string, object?> fields)
            return Create(target.FullName!, fields);
        return Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    private Type MessageType(string fullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        var type = schema.GetType(fullName, throwOnError: true)!;
        if (!messageInterface.IsAssignableFrom(type)) throw new ArgumentException($"{fullName} is not a protobuf message.", nameof(fullName));
        return type;
    }

    private void RequireMessage(object message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!messageInterface.IsInstanceOfType(message))
            throw new ArgumentException("Expected a protobuf message from this schema context.", nameof(message));
    }

    private static PropertyInfo Property(Type type, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new MissingMemberException(type.FullName, name);
    }

    private static void RejectDevLogin(object message)
    {
        if (message.GetType().FullName == "party.protocol.ConnectC2S" &&
            (Get(message, "Auth")?.ToString() == "Dev" || Get(message, "Dev") != null))
            throw new InvalidOperationException("Dev authentication is disabled; choose an explicit supported Auth value.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, this);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        context.Unload();
    }

    private sealed class SchemaContext : AssemblyLoadContext
    {
        private readonly string directory;
        public SchemaContext(string directory) : base(isCollectible: true)
        {
            this.directory = directory;
            Resolving += ResolveExtracted;
        }

        private Assembly? ResolveExtracted(AssemblyLoadContext loadContext, AssemblyName name)
        {
            var simpleName = name.Name;
            // Extracted framework/Unity DLLs are IL2CPP metadata shells, not executable dependencies.
            if (simpleName == null || simpleName is "mscorlib" or "netstandard" or "System" ||
                simpleName.StartsWith("System.", StringComparison.Ordinal) ||
                simpleName.StartsWith("Unity", StringComparison.Ordinal)) return null;
            var candidate = Path.Combine(directory, simpleName + ".dll");
            return File.Exists(candidate) ? loadContext.LoadFromAssemblyPath(candidate) : null;
        }
    }
}
