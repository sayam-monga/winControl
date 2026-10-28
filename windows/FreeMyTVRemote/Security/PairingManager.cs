using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using FreeMyTVRemote.Protocol;

namespace FreeMyTVRemote.Security;

internal enum PairingReadStatus : byte
{
    Idle = 0,
    InvalidCode = 1,
    Locked = 2,
    Paired = 3,
    CodeExpired = 4,
    StorageError = 5
}

internal sealed class PairingManager
{
    private const int TokenLength = 32; // A random 256-bit token is the HMAC key used after the one-time pairing code.
    private const int PairingCodeDigits = 6; // Six digits keep manual entry quick; server-side attempt limits bound guessing.
    private const int FailedAttemptsBeforeLockout = 3; // Three failures gives the code entry a clear, memorable lockout rule.
    private static readonly TimeSpan PairingCodeLifetime = TimeSpan.FromMinutes(5); // Enough time to switch apps and enter the code.
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(1); // Slows guessing without forcing the receiver to restart.
    private static readonly TimeSpan TokenReadRetryWindow = TimeSpan.FromMinutes(2); // Allows a phone to finish a multi-part GATT read after reconnecting.
    private static readonly byte[] PairingPrefix = [RemoteProtocol.Version, 0x01]; // Pairing writes are version 1 with the fixed pairing opcode.

    private readonly object _gate = new();
    private readonly DpapiPairingStore _store = new();
    private byte[]? _token;
    private string? _pairedPeerDeviceId;
    private string? _code;
    private DateTimeOffset _codeExpiresAt;
    private DateTimeOffset? _lockedUntil;
    private DateTimeOffset? _tokenReadUntil;
    private uint _lastSequence;
    private bool _hasLastSequence;
    private int _failedAttempts;
    private PairingReadStatus _lastStatus = PairingReadStatus.Idle;

    internal event EventHandler? StateChanged;
    internal string? StorageError { get; private set; }
    internal string? PairedPeerDeviceId
    {
        get { lock (_gate) return _pairedPeerDeviceId; }
    }

    internal PairingManager()
    {
        try
        {
            var credential = _store.Load();
            if (credential is not null)
            {
                if (credential.Token.Length != TokenLength || string.IsNullOrWhiteSpace(credential.PeerDeviceId))
                {
                    CryptographicOperations.ZeroMemory(credential.Token);
                    StorageError = "Windows couldn't read the saved pairing. Forget the pairing to start again.";
                    _lastStatus = PairingReadStatus.StorageError;
                }
                else
                {
                    _token = credential.Token;
                    _pairedPeerDeviceId = credential.PeerDeviceId;
                    _lastStatus = PairingReadStatus.Paired;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or System.ComponentModel.Win32Exception)
        {
            StorageError = "Windows couldn't read the saved pairing. Forget the pairing to start again.";
            _lastStatus = PairingReadStatus.StorageError;
        }

        if (_token is null && StorageError is null)
            GenerateCode(DateTimeOffset.UtcNow);
    }

    internal bool IsPaired
    {
        get { lock (_gate) return _token is not null; }
    }

    internal string? CurrentCode
    {
        get
        {
            lock (_gate)
            {
                RefreshExpiredCode(DateTimeOffset.UtcNow);
                return _token is null && StorageError is null && !IsLockoutActive(DateTimeOffset.UtcNow) ? _code : null;
            }
        }
    }

    internal int SecondsUntilCodeExpires
    {
        get
        {
            lock (_gate)
            {
                RefreshExpiredCode(DateTimeOffset.UtcNow);
                var seconds = (_codeExpiresAt - DateTimeOffset.UtcNow).TotalSeconds;
                return _token is null && !IsLockoutActive(DateTimeOffset.UtcNow) && seconds > 0
                    ? (int)Math.Ceiling(seconds)
                    : 0;
            }
        }
    }

    internal bool IsLocked
    {
        get { lock (_gate) return IsLockoutActive(DateTimeOffset.UtcNow); }
    }

    internal PairingReadStatus LastStatus
    {
        get { lock (_gate) return _lastStatus; }
    }

    internal PairingReadStatus ProcessPairingWrite(ReadOnlySpan<byte> value, string peerDeviceId)
    {
        PairingReadStatus result;
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;

            if (StorageError is not null)
                result = _lastStatus = PairingReadStatus.StorageError;
            else if (_token is not null)
                result = _lastStatus = PairingReadStatus.Paired;
            else if (IsLockoutActive(now))
                result = _lastStatus = PairingReadStatus.Locked;
            else if (_code is null || _codeExpiresAt <= now)
            {
                _failedAttempts = 0;
                _lockedUntil = null;
                GenerateCode(now);
                result = _lastStatus = PairingReadStatus.CodeExpired;
            }
            else if (!string.IsNullOrWhiteSpace(peerDeviceId) && IsValidPairingFrame(value) && CodeMatches(value[PairingPrefix.Length..]))
            {
                var token = RandomNumberGenerator.GetBytes(TokenLength);
                var installed = false;
                try
                {
                    _store.Save(token, peerDeviceId);
                    _token = token;
                    installed = true;
                    _pairedPeerDeviceId = peerDeviceId;
                    _code = null;
                    _lockedUntil = null;
                    _failedAttempts = 0;
                    _tokenReadUntil = now + TokenReadRetryWindow;
                    _lastSequence = 0;
                    _hasLastSequence = false;
                    result = _lastStatus = PairingReadStatus.Paired;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or System.ComponentModel.Win32Exception or ArgumentException)
                {
                    StorageError = "Windows couldn't securely save the pairing. Check your Windows user profile and try again.";
                    result = _lastStatus = PairingReadStatus.StorageError;
                }
                finally
                {
                    if (!installed)
                        CryptographicOperations.ZeroMemory(token);
                }
            }
            else
            {
                if (_lockedUntil is { } expiredLockout && expiredLockout <= now)
                {
                    _lockedUntil = null;
                    _failedAttempts = 0;
                }
                _failedAttempts++;
                if (_failedAttempts >= FailedAttemptsBeforeLockout)
                {
                    _lockedUntil = now + LockoutDuration;
                    result = _lastStatus = PairingReadStatus.Locked;
                }
                else
                    result = _lastStatus = PairingReadStatus.InvalidCode;
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }

    internal byte[] GetPairingReadValue(string peerDeviceId)
    {
        lock (_gate)
        {
            if (_token is not null && string.Equals(_pairedPeerDeviceId, peerDeviceId, StringComparison.Ordinal) && _tokenReadUntil is { } tokenReadUntil && tokenReadUntil > DateTimeOffset.UtcNow)
                return _token.ToArray();

            if (_tokenReadUntil is { } expiredReadWindow && expiredReadWindow <= DateTimeOffset.UtcNow)
                _tokenReadUntil = null;

            return [RemoteProtocol.Version, (byte)_lastStatus];
        }
    }

    internal bool TryAuthenticateCommand(ReadOnlySpan<byte> frame, string peerDeviceId, out RemoteCommand? command)
    {
        command = null;
        uint sequence = 0;
        bool authenticated;
        lock (_gate)
        {
            authenticated = _token is not null && string.Equals(_pairedPeerDeviceId, peerDeviceId, StringComparison.Ordinal) && RemoteProtocol.TryDecode(
                frame,
                _token,
                _lastSequence,
                _hasLastSequence,
                out command,
                out sequence);

            if (authenticated)
            {
                _lastSequence = sequence;
                _hasLastSequence = true;
                _tokenReadUntil = null;
                return true;
            }
        }

        command = null;
        return false;
    }

    internal bool IsPairedPeer(string peerDeviceId)
    {
        lock (_gate)
            return _token is not null && string.Equals(_pairedPeerDeviceId, peerDeviceId, StringComparison.Ordinal);
    }

    internal bool SavePairingToken(ReadOnlySpan<byte> token, string peerDeviceId)
    {
        if (token.Length != TokenLength || string.IsNullOrWhiteSpace(peerDeviceId))
            return false;

        var saved = false;
        lock (_gate)
        {
            if (StorageError is not null || (_token is not null && !string.Equals(_pairedPeerDeviceId, peerDeviceId, StringComparison.Ordinal)))
                return false;

            try
            {
                _store.Save(token, peerDeviceId);
                if (_token is not null)
                    CryptographicOperations.ZeroMemory(_token);
                _token = token.ToArray();
                _pairedPeerDeviceId = peerDeviceId;
                _code = null;
                _lockedUntil = null;
                _failedAttempts = 0;
                _tokenReadUntil = null;
                _lastSequence = 0;
                _hasLastSequence = false;
                StorageError = null;
                _lastStatus = PairingReadStatus.Paired;
                saved = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or System.ComponentModel.Win32Exception or ArgumentException)
            {
                StorageError = "Windows couldn't securely save the pairing. Check your Windows user profile and try again.";
                _lastStatus = PairingReadStatus.StorageError;
            }
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
        return saved;
    }

    internal void BeginConnectionSession(string peerDeviceId)
    {
        lock (_gate)
        {
            if (_token is null || !string.Equals(_pairedPeerDeviceId, peerDeviceId, StringComparison.Ordinal))
                return;
            _lastSequence = 0;
            _hasLastSequence = false;
        }
    }

    internal void ForgetPairing()
    {
        lock (_gate)
        {
            _store.Delete();
            if (_token is not null)
                CryptographicOperations.ZeroMemory(_token);
            _token = null;
            _pairedPeerDeviceId = null;
            _tokenReadUntil = null;
            _lastSequence = 0;
            _hasLastSequence = false;
            _failedAttempts = 0;
            _lockedUntil = null;
            StorageError = null;
            _lastStatus = PairingReadStatus.Idle;
            GenerateCode(DateTimeOffset.UtcNow);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool IsValidPairingFrame(ReadOnlySpan<byte> value) =>
        value.Length == PairingPrefix.Length + PairingCodeDigits &&
        value[0] == PairingPrefix[0] &&
        value[1] == PairingPrefix[1] &&
        value[PairingPrefix.Length..].IndexOfAnyExceptInRange((byte)'0', (byte)'9') < 0;

    private bool CodeMatches(ReadOnlySpan<byte> candidate)
    {
        Span<byte> expected = stackalloc byte[PairingCodeDigits];
        Encoding.ASCII.GetBytes(_code!, expected);
        return CryptographicOperations.FixedTimeEquals(candidate, expected);
    }

    private void RefreshExpiredCode(DateTimeOffset now)
    {
        if (_token is not null || StorageError is not null || IsLockoutActive(now))
            return;

        if (_code is null || _codeExpiresAt <= now)
        {
            _failedAttempts = 0;
            _lockedUntil = null;
            GenerateCode(now);
        }
        else if (_lockedUntil is not null && _lockedUntil <= now)
        {
            _failedAttempts = 0;
            _lockedUntil = null;
        }
    }

    private void GenerateCode(DateTimeOffset now)
    {
        // A uniform random six-digit decimal code is easy to enter from the iPhone keyboard.
        _code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        _codeExpiresAt = now + PairingCodeLifetime;
    }

    private bool IsLockoutActive(DateTimeOffset now) => _lockedUntil is { } lockedUntil && lockedUntil > now;
}
