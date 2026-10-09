using System;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace StandaloneSpectator;

/// <summary>An immutable wire frame, including a defensive copy of its payload.</summary>
public sealed record Frame
{
    public const int HeaderLength = 35;
    public const int MaxPayloadLength = 8 * 1024 * 1024;
    public long SessionId { get; }
    public short Command { get; }
    public byte Version1 { get; }
    public byte Version2 { get; }
    public byte Version3 { get; }
    public long Upsn { get; }
    public long Downsn { get; }
    public short Error { get; }
    public ImmutableArray<byte> Payload { get; }

    public Frame(long sessionId, short command, long upsn, long downsn, short error,
        ReadOnlySpan<byte> payload, byte version1 = 1, byte version2 = 0, byte version3 = 0)
    {
        if (payload.Length > MaxPayloadLength) throw new ArgumentOutOfRangeException(nameof(payload));
        SessionId = sessionId; Command = command; Upsn = upsn; Downsn = downsn; Error = error;
        Version1 = version1; Version2 = version2; Version3 = version3;
        Payload = ImmutableArray.Create(payload.ToArray());
    }

    public byte[] Encode()
    {
        var bytes = new byte[HeaderLength + Payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes, Payload.Length);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(4), SessionId);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(12), Command);
        bytes[14] = Version1; bytes[15] = Version2; bytes[16] = Version3;
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(17), Upsn);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(25), Downsn);
        BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(33), Error);
        Payload.AsSpan().CopyTo(bytes.AsSpan(HeaderLength));
        return bytes;
    }

    public static async Task<Frame> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        var header = new byte[HeaderLength];
        await stream.ReadExactlyAsync(header.AsMemory(0, 4), cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 0 || length > MaxPayloadLength)
            throw new InvalidDataException($"Invalid frame payload length: {length}.");
        await stream.ReadExactlyAsync(header.AsMemory(4), cancellationToken).ConfigureAwait(false);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return new Frame(BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(4)),
            BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(12)),
            BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(17)),
            BinaryPrimitives.ReadInt64BigEndian(header.AsSpan(25)),
            BinaryPrimitives.ReadInt16BigEndian(header.AsSpan(33)), payload, header[14], header[15], header[16]);
    }
}

// Local protocol evidence (paths relative to C:\src\Study\astralparty):
// decomp/_full/Core.Net/RPCMsgManager.cs:1134-1148 writes this 35-byte header,
// versions 1/0/0, DOWNSN=0, ERR=0; 1219-1225 correlates incoming UPSN.
// NetManager.cs:790-800 adds sent UPSN to ackQueue; 334-340 removes incoming UPSN.
// USocket.cs:378-408 only decodes frames; these paths send no separate ACK.
// TODO: determine whether unsolicited DOWNSN requires an application-level ACK
// using server documentation or a future explicitly authorized capture. Do not
// invent an ACK command, mirror DOWNSN, or assume a successful live session here.
// WatchJoinRoomC2SRPC.cs:9 sends 5191; RPCMsgManager.cs:2028-2030 routes 5192.
// Other command pairs must be checked by the caller; response mapping is configurable.
