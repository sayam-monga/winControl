using System.Buffers.Binary;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace FreeMyTVRemote.Security;

internal sealed record PairingCredential(byte[] Token, string PeerDeviceId);

internal sealed class DpapiPairingStore
{
    // The UI is per Windows user, so CurrentUser DPAPI keeps the token usable only to that account.
    private const uint CryptProtectUiForbidden = 0x1; // Background DPAPI operations must not show credential UI during startup.
    private const byte RecordVersion = 1; // A version byte allows the encrypted on-disk record to evolve safely.
    private const int TokenLength = 32; // Pairing tokens are 256-bit HMAC keys.
    private const int MaximumPeerIdLength = 4096; // Windows device interface IDs are far shorter; this bounds corrupted records.
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string PairingPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FreeMyTV",
        "windows-pairing.bin");

    internal PairingCredential? Load()
    {
        if (!File.Exists(PairingPath))
            return null;

        var protectedBytes = File.ReadAllBytes(PairingPath);
        var plaintext = Transform(protectedBytes, protect: false);
        try
        {
            const int headerLength = 1 + TokenLength + sizeof(ushort);
            if (plaintext.Length < headerLength || plaintext[0] != RecordVersion)
                throw new CryptographicException("The saved pairing record has an unsupported format.");

            var peerIdLength = BinaryPrimitives.ReadUInt16LittleEndian(plaintext.AsSpan(1 + TokenLength, sizeof(ushort)));
            if (peerIdLength is 0 or > MaximumPeerIdLength || plaintext.Length != headerLength + peerIdLength)
                throw new CryptographicException("The saved pairing record is incomplete.");

            string peerDeviceId;
            try
            {
                peerDeviceId = StrictUtf8.GetString(plaintext.AsSpan(headerLength, peerIdLength));
            }
            catch (DecoderFallbackException exception)
            {
                throw new CryptographicException("The saved Bluetooth device ID is invalid.", exception);
            }

            return new PairingCredential(plaintext.AsSpan(1, TokenLength).ToArray(), peerDeviceId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    internal void Save(ReadOnlySpan<byte> token, string peerDeviceId)
    {
        if (token.Length != TokenLength)
            throw new ArgumentException("A 32-byte pairing token is required.", nameof(token));

        var peerIdBytes = StrictUtf8.GetBytes(peerDeviceId);
        if (peerIdBytes.Length is 0 or > MaximumPeerIdLength)
            throw new ArgumentException("A bounded Bluetooth device ID is required.", nameof(peerDeviceId));

        var plaintext = new byte[1 + TokenLength + sizeof(ushort) + peerIdBytes.Length];
        plaintext[0] = RecordVersion;
        token.CopyTo(plaintext.AsSpan(1, TokenLength));
        BinaryPrimitives.WriteUInt16LittleEndian(plaintext.AsSpan(1 + TokenLength, sizeof(ushort)), (ushort)peerIdBytes.Length);
        peerIdBytes.CopyTo(plaintext.AsSpan(1 + TokenLength + sizeof(ushort)));
        CryptographicOperations.ZeroMemory(peerIdBytes);

        var protectedBytes = Transform(plaintext, protect: true);
        var directory = Path.GetDirectoryName(PairingPath)!;
        var temporaryPath = Path.Combine(directory, $"{Path.GetRandomFileName()}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(temporaryPath, protectedBytes);
            File.Move(temporaryPath, PairingPath, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch (IOException)
            {
                // Cleanup must not hide the original DPAPI or file-write error.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup must not hide the original DPAPI or file-write error.
            }
        }
    }

    internal void Delete()
    {
        if (File.Exists(PairingPath))
            File.Delete(PairingPath);
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var pinned = GCHandle.Alloc(input, GCHandleType.Pinned);
        var inputBlob = new DataBlob { Length = input.Length, Data = pinned.AddrOfPinnedObject() };
        DataBlob outputBlob = default;
        try
        {
            var succeeded = protect
                ? CryptProtectData(ref inputBlob, "Free My TV pairing data", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outputBlob);
            if (!succeeded)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not protect or read the pairing data.");

            var result = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            pinned.Free();
            if (outputBlob.Data != IntPtr.Zero)
                _ = LocalFree(outputBlob.Data);
            CryptographicOperations.ZeroMemory(input);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        internal int Length;
        internal IntPtr Data;
    }

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob input,
        string description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob output);

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob input,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob output);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
