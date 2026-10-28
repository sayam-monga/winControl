using System.Runtime.InteropServices;
using FreeMyTVRemote.Protocol;

namespace FreeMyTVRemote.Input;

internal sealed class WindowsInputDispatcher
{
    // Values mirror the documented Win32 INPUT, KEYBDINPUT, and MOUSEINPUT flags used by SendInput.
    private const uint InputKeyboard = 1; // Win32 INPUT uses type 1 for keyboard events.
    private const uint InputMouse = 0; // Win32 INPUT uses type 0 for mouse events.
    private const uint KeyboardKeyUp = 0x0002; // Win32 marks key release with KEYEVENTF_KEYUP.
    private const uint KeyboardUnicode = 0x0004; // Unicode text uses KEYEVENTF_UNICODE instead of virtual-key codes.
    private const uint KeyboardExtended = 0x0001; // Arrow, Windows, and context-menu keys require KEYEVENTF_EXTENDEDKEY.
    private const uint MouseMove = 0x0001; // Relative pointer input uses MOUSEEVENTF_MOVE without absolute coordinates.
    private const uint MouseLeftDown = 0x0002; // Left clicks use the Win32 left-button down flag.
    private const uint MouseLeftUp = 0x0004; // Left clicks release with the matching Win32 up flag.
    private const uint MouseRightDown = 0x0008; // Right clicks use the Win32 right-button down flag.
    private const uint MouseRightUp = 0x0010; // Right clicks release with the matching Win32 up flag.

    internal bool Dispatch(RemoteCommand command)
    {
        return command.Opcode switch
        {
            RemoteOpcode.Arrow => PressArrow((ArrowDirection)command.Payload[0]),
            RemoteOpcode.PointerMove => MovePointer(command.Payload),
            RemoteOpcode.LeftClick => Click(MouseLeftDown, MouseLeftUp),
            RemoteOpcode.RightClick => Click(MouseRightDown, MouseRightUp),
            RemoteOpcode.KeyPress => PressKey((RemoteKey)command.Payload[0]),
            RemoteOpcode.Text => TypeText(command.Text!),
            RemoteOpcode.MediaKey => PressMediaKey((RemoteMediaKey)command.Payload[0]),
            _ => false
        };
    }

    private static bool PressArrow(ArrowDirection direction)
    {
        var virtualKey = direction switch
        {
            ArrowDirection.Up => (ushort)0x26,
            ArrowDirection.Down => (ushort)0x28,
            ArrowDirection.Left => (ushort)0x25,
            ArrowDirection.Right => (ushort)0x27,
            _ => (ushort)0
        };
        return virtualKey != 0 && PressVirtualKey(virtualKey, extended: true);
    }

    private static bool PressKey(RemoteKey key)
    {
        var virtualKey = key switch
        {
            RemoteKey.Enter => (ushort)0x0D,
            RemoteKey.Escape => (ushort)0x1B,
            RemoteKey.Backspace => (ushort)0x08,
            RemoteKey.Home => (ushort)0x5B,
            RemoteKey.ContextMenu => (ushort)0x5D,
            _ => (ushort)0
        };
        return virtualKey != 0 && PressVirtualKey(virtualKey, extended: key is RemoteKey.Home or RemoteKey.ContextMenu);
    }

    private static bool PressMediaKey(RemoteMediaKey key)
    {
        var virtualKey = key switch
        {
            RemoteMediaKey.PlayPause => (ushort)0xB3,
            RemoteMediaKey.VolumeUp => (ushort)0xAF,
            RemoteMediaKey.VolumeDown => (ushort)0xAE,
            RemoteMediaKey.Mute => (ushort)0xAD,
            _ => (ushort)0
        };
        return virtualKey != 0 && PressVirtualKey(virtualKey, extended: true);
    }

    private static bool PressVirtualKey(ushort virtualKey, bool extended)
    {
        var extraFlags = extended ? KeyboardExtended : 0;
        var inputs = new[]
        {
            KeyboardInput(virtualKey, 0, extraFlags),
            KeyboardInput(virtualKey, 0, extraFlags | KeyboardKeyUp)
        };
        return Send(inputs);
    }

    private static bool TypeText(string text)
    {
        if (text.Length == 0)
            return false;

        var inputs = new NativeInput[text.Length * 2];
        for (var index = 0; index < text.Length; index++)
        {
            var codeUnit = text[index];
            inputs[index * 2] = KeyboardInput(0, codeUnit, KeyboardUnicode);
            inputs[index * 2 + 1] = KeyboardInput(0, codeUnit, KeyboardUnicode | KeyboardKeyUp);
        }
        return Send(inputs);
    }

    private static bool MovePointer(byte[] payload)
    {
        var dx = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(0, sizeof(short)));
        var dy = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(payload.AsSpan(sizeof(short), sizeof(short)));
        return Send([MouseInput(dx, dy, MouseMove)]);
    }

    private static bool Click(uint downFlag, uint upFlag) =>
        Send([MouseInput(0, 0, downFlag), MouseInput(0, 0, upFlag)]);

    private static NativeInput KeyboardInput(ushort virtualKey, ushort scanCode, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new InputData
        {
            Keyboard = new KeyboardData
            {
                VirtualKey = virtualKey,
                ScanCode = scanCode,
                Flags = flags
            }
        }
    };

    private static NativeInput MouseInput(int dx, int dy, uint flags) => new()
    {
        Type = InputMouse,
        Data = new InputData
        {
            Mouse = new MouseData
            {
                Dx = dx,
                Dy = dy,
                Flags = flags
            }
        }
    };

    private static bool Send(NativeInput[] inputs)
    {
        var inserted = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>());
        if (inserted == inputs.Length)
            return true;

        // Keyboard and button commands are emitted as down/up pairs; release the last down if Windows accepted only half a pair.
        if (inserted > 0 && (inserted & 1) == 1)
        {
            var unmatchedDown = inputs[inserted - 1];
            if (unmatchedDown.Type == InputKeyboard)
            {
                unmatchedDown.Data.Keyboard.Flags |= KeyboardKeyUp;
                _ = SendInput(1, [unmatchedDown], Marshal.SizeOf<NativeInput>());
            }
            else if (unmatchedDown.Type == InputMouse)
            {
                var releaseFlag = unmatchedDown.Data.Mouse.Flags switch
                {
                    MouseLeftDown => MouseLeftUp,
                    MouseRightDown => MouseRightUp,
                    _ => 0u
                };
                if (releaseFlag != 0)
                {
                    var release = unmatchedDown;
                    release.Data.Mouse.Flags = releaseFlag;
                    _ = SendInput(1, [release], Marshal.SizeOf<NativeInput>());
                }
            }
        }

        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        internal uint Type;
        internal InputData Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] internal MouseData Mouse;
        [FieldOffset(0)] internal KeyboardData Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseData
    {
        internal int Dx;
        internal int Dy;
        internal uint MouseDataValue;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardData
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal UIntPtr ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);
}
