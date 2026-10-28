using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace FreeMyTVRemote.Protocol;

internal enum RemoteOpcode : byte
{
    Arrow = 0x01,
    PointerMove = 0x02,
    LeftClick = 0x03,
    RightClick = 0x04,
    KeyPress = 0x10,
    Text = 0x11,
    MediaKey = 0x20
}

internal enum RemoteKey : byte
{
    Enter = 0x01,
    Escape = 0x02,
    Backspace = 0x03,
    Home = 0x04,
    ContextMenu = 0x05
}

internal enum RemoteMediaKey : byte
{
    PlayPause = 0x01,
    VolumeUp = 0x02,
    VolumeDown = 0x03,
    Mute = 0x04
}

internal enum ArrowDirection : byte
{
    Up = 0x00,
    Down = 0x01,
    Left = 0x02,
    Right = 0x03
}

internal sealed record RemoteCommand(RemoteOpcode Opcode, byte[] Payload, string? Text = null);

internal static class RemoteProtocol
{
    // These stable app-specific UUIDs let both platforms discover the same private service and its two characteristics.
    internal static readonly Guid ServiceUuid = Guid.Parse("6D4F0001-4A2B-4B91-A6D7-31B7C2E10510");
    internal static readonly Guid CommandCharacteristicUuid = Guid.Parse("6D4F0002-4A2B-4B91-A6D7-31B7C2E10510");
    internal static readonly Guid PairingCharacteristicUuid = Guid.Parse("6D4F0003-4A2B-4B91-A6D7-31B7C2E10510");
    internal static readonly Guid StatusCharacteristicUuid = Guid.Parse("6D4F0004-4A2B-4B91-A6D7-31B7C2E10510");

    internal const byte Version = 1; // Version 1 is the first published wire format; later formats must negotiate explicitly.
    internal const int MaximumFrameLength = 20; // 20 bytes fits the default 23-byte ATT MTU after its 3-byte write header.
    internal const int HeaderLength = 6; // Version, opcode, and a four-byte sequence number identify each command.
    internal const int AuthenticationTagLength = 8; // A 64-bit HMAC tag leaves six payload bytes within the 20-byte frame limit.
    internal const int MaximumPayloadLength = MaximumFrameLength - HeaderLength - AuthenticationTagLength;
    internal const int MaximumPointerDelta = 2048; // Bound each movement to prevent malformed frames from causing extreme pointer jumps.

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryDecode(
        ReadOnlySpan<byte> frame,
        ReadOnlySpan<byte> token,
        uint previousSequence,
        bool hasPreviousSequence,
        out RemoteCommand? command,
        out uint sequence)
    {
        command = null;
        sequence = 0;
        if (token.Length != 32 || frame.Length < HeaderLength + AuthenticationTagLength || frame.Length > MaximumFrameLength)
            return false;
        if (frame[0] != Version)
            return false;

        var opcode = (RemoteOpcode)frame[1];
        var payloadLength = frame.Length - HeaderLength - AuthenticationTagLength;
        var payload = frame.Slice(HeaderLength, payloadLength);
        var suppliedTag = frame[^AuthenticationTagLength..];
        var expectedTag = HMACSHA256.HashData(token, frame[..^AuthenticationTagLength]);
        if (!CryptographicOperations.FixedTimeEquals(suppliedTag, expectedTag.AsSpan(0, AuthenticationTagLength)))
            return false;

        sequence = BinaryPrimitives.ReadUInt32LittleEndian(frame.Slice(2, sizeof(uint)));
        if (hasPreviousSequence && sequence <= previousSequence)
            return false;

        string? text = null;
        switch (opcode)
        {
            case RemoteOpcode.Arrow:
                if (payload.Length != 1 || payload[0] > (byte)ArrowDirection.Right)
                    return false;
                break;
            case RemoteOpcode.PointerMove:
                if (payload.Length != sizeof(short) * 2)
                    return false;
                var dx = BinaryPrimitives.ReadInt16LittleEndian(payload[..sizeof(short)]);
                var dy = BinaryPrimitives.ReadInt16LittleEndian(payload[sizeof(short)..]);
                if (Math.Abs((int)dx) > MaximumPointerDelta || Math.Abs((int)dy) > MaximumPointerDelta)
                    return false;
                break;
            case RemoteOpcode.LeftClick:
            case RemoteOpcode.RightClick:
                if (payload.Length != 0)
                    return false;
                break;
            case RemoteOpcode.KeyPress:
                if (payload.Length != 1 || !Enum.IsDefined((RemoteKey)payload[0]))
                    return false;
                break;
            case RemoteOpcode.Text:
                if (payload.Length is < 1 or > MaximumPayloadLength)
                    return false;
                try
                {
                    text = StrictUtf8.GetString(payload);
                }
                catch (DecoderFallbackException)
                {
                    return false;
                }
                break;
            case RemoteOpcode.MediaKey:
                if (payload.Length != 1 || !Enum.IsDefined((RemoteMediaKey)payload[0]))
                    return false;
                break;
            default:
                return false;
        }

        command = new RemoteCommand(opcode, payload.ToArray(), text);
        return true;
    }
}
