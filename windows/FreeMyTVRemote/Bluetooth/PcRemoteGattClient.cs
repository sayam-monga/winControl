using FreeMyTVRemote.Input;
using FreeMyTVRemote.Protocol;
using FreeMyTVRemote.Security;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace FreeMyTVRemote.Bluetooth;

/// <summary>Finds the iPhone peripheral and receives authenticated remote commands from it.</summary>
internal sealed class PcRemoteGattClient : IDisposable
{
    private readonly PairingManager _pairing;
    private readonly WindowsInputDispatcher _input;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private BluetoothLEAdvertisementWatcher? _watcher;
    private BluetoothLEDevice? _device;
    private GattDeviceService? _service;
    private GattCharacteristic? _commandCharacteristic;
    private GattCharacteristic? _pairingCharacteristic;
    private GattCharacteristic? _statusCharacteristic;
    private string _peerDeviceId = string.Empty;
    private int _connectionInProgress;
    private bool _sessionStarted;
    private volatile bool _disposed;

    internal event EventHandler<string>? StatusChanged;
    internal event EventHandler? StateChanged;
    internal bool IsScanning => !_disposed && _watcher?.Status == BluetoothLEAdvertisementWatcherStatus.Started;
    internal bool IsConnected { get; private set; }
    internal bool IsWaitingForPairing { get; private set; }
    internal bool IsPairedPeerConnected => IsConnected && _pairing.IsPairedPeer(_peerDeviceId);
    internal bool HasSavedPairingForPeer => _pairing.IsPairedPeer(_peerDeviceId);

    internal PcRemoteGattClient(PairingManager pairing, WindowsInputDispatcher input)
    {
        _pairing = pairing;
        _input = input;
    }

    internal async Task StartAsync()
    {
        ThrowIfDisposed();
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null)
            throw new NotSupportedException("Windows couldn't find a Bluetooth adapter for the iPhone fallback connection.");
        var filter = new BluetoothLEAdvertisementFilter();
        filter.Advertisement.ServiceUuids.Add(RemoteProtocol.ServiceUuid);
        _watcher = new BluetoothLEAdvertisementWatcher
        {
            AdvertisementFilter = filter,
            ScanningMode = BluetoothLEScanningMode.Active
        };
        _watcher.Received += Watcher_Received;
        _watcher.Stopped += Watcher_Stopped;
        _watcher.Start();
        StatusChanged?.Invoke(this, "Windows is scanning for the iPhone remote over Bluetooth. Open the Windows connection screen on your iPhone.");
    }

    internal async Task SubmitPairingCodeAsync(string code)
    {
        if (!IsConnected || _pairingCharacteristic is null ||
            code.Length != 6 || code.Any(character => character is < '0' or > '9'))
        {
            StatusChanged?.Invoke(this, "Enter the six-digit code shown in the iPhone app.");
            return;
        }

        var request = new byte[8];
        request[0] = RemoteProtocol.Version;
        request[1] = 0x01;
        System.Text.Encoding.ASCII.GetBytes(code, request.AsSpan(2));
        try
        {
            var write = await _pairingCharacteristic.WriteValueWithResultAsync(
                CryptographicBuffer.CreateFromByteArray(request), GattWriteOption.WriteWithResponse);
            if (write.Status != GattCommunicationStatus.Success)
            {
                StatusChanged?.Invoke(this, "The iPhone did not accept the pairing request. Check Bluetooth security and try again.");
                return;
            }

            await ReadPairingResultAsync();
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke(this, $"Windows couldn't submit the pairing code: {exception.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        StopWatcher();
        DetachDevice();
        _commandGate.Dispose();
    }

    private void Watcher_Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (_disposed || Interlocked.CompareExchange(ref _connectionInProgress, 1, 0) != 0)
            return;
        _watcher?.Stop();
        _ = ConnectAsync(args.BluetoothAddress);
    }

    private async Task ConnectAsync(ulong bluetoothAddress)
    {
        try
        {
            StatusChanged?.Invoke(this, "Found the iPhone remote. Connecting over Bluetooth…");
            var device = await BluetoothLEDevice.FromBluetoothAddressAsync(bluetoothAddress);
            if (_disposed)
            {
                device?.Dispose();
                return;
            }
            if (device is null)
                throw new InvalidOperationException("Windows couldn't open the iPhone Bluetooth device.");

            _device = device;
            _peerDeviceId = device.DeviceId;
            _device.ConnectionStatusChanged += Device_ConnectionStatusChanged;

            var serviceResult = await device.GetGattServicesForUuidAsync(RemoteProtocol.ServiceUuid, BluetoothCacheMode.Uncached);
            if (serviceResult.Status != GattCommunicationStatus.Success || serviceResult.Services.Count != 1)
                throw new InvalidOperationException("The iPhone didn't provide the Free My TV Bluetooth service.");
            _service = serviceResult.Services[0];

            var characteristicResult = await _service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (characteristicResult.Status != GattCommunicationStatus.Success)
                throw new InvalidOperationException("Windows couldn't load the iPhone Bluetooth controls.");
            _commandCharacteristic = characteristicResult.Characteristics.FirstOrDefault(characteristic => characteristic.Uuid == RemoteProtocol.CommandCharacteristicUuid);
            _pairingCharacteristic = characteristicResult.Characteristics.FirstOrDefault(characteristic => characteristic.Uuid == RemoteProtocol.PairingCharacteristicUuid);
            _statusCharacteristic = characteristicResult.Characteristics.FirstOrDefault(characteristic => characteristic.Uuid == RemoteProtocol.StatusCharacteristicUuid);
            if (_commandCharacteristic is null || _pairingCharacteristic is null || _statusCharacteristic is null)
                throw new InvalidOperationException("The iPhone is missing a required Bluetooth control.");

            _commandCharacteristic.ValueChanged += CommandCharacteristic_ValueChanged;
            _statusCharacteristic.ValueChanged += StatusCharacteristic_ValueChanged;
            await SubscribeAsync(_statusCharacteristic, "connection status");
            await SubscribeAsync(_commandCharacteristic, "remote commands");
            await UpdateConnectionStateAsync();
        }
        catch (Exception exception)
        {
            if (!_disposed)
            {
                IsConnected = false;
                IsWaitingForPairing = false;
                StatusChanged?.Invoke(this, $"Windows couldn't connect to the iPhone remote: {exception.Message}");
                StateChanged?.Invoke(this, EventArgs.Empty);
                DetachDevice();
                RestartWatcher();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _connectionInProgress, 0);
        }
    }

    private async Task SubscribeAsync(GattCharacteristic characteristic, string description)
    {
        var result = await characteristic.WriteClientCharacteristicConfigurationDescriptorAsync(
            GattClientCharacteristicConfigurationDescriptorValue.Notify);
        if (result != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Windows couldn't subscribe to iPhone {description}.");
    }

    private async Task UpdateConnectionStateAsync()
    {
        if (_disposed || _statusCharacteristic is null)
            return;

        var status = await ReadValueAsync(_statusCharacteristic);
        if (status.Length != 5 || status[0] != RemoteProtocol.Version)
            throw new InvalidOperationException("The iPhone returned an unsupported Bluetooth status.");

        if (status[3] != 0)
        {
            if (!_pairing.IsPairedPeer(_peerDeviceId))
            {
                await ReadPairingResultAsync();
                if (!_pairing.IsPairedPeer(_peerDeviceId))
                {
                    IsConnected = false;
                    IsWaitingForPairing = true;
                    StatusChanged?.Invoke(this, "The iPhone has a saved pairing that Windows cannot read. Forget its saved pairing, then pair again.");
                    StateChanged?.Invoke(this, EventArgs.Empty);
                    return;
                }
            }
            if (!_sessionStarted)
            {
                _pairing.BeginConnectionSession(_peerDeviceId);
                _sessionStarted = true;
            }
            IsWaitingForPairing = false;
            IsConnected = status[2] != 0;
            StatusChanged?.Invoke(this, IsConnected
                ? "Your paired iPhone is connected over Bluetooth."
                : "Connected to the iPhone. Waiting for its remote screen to enable command notifications.");
        }
        else
        {
            IsConnected = false;
            IsWaitingForPairing = true;
            StatusChanged?.Invoke(this, "Connected to the iPhone. Enter the six-digit code shown in its Windows connection screen.");
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReadPairingResultAsync()
    {
        if (_pairingCharacteristic is null)
            return;
        var value = await ReadValueAsync(_pairingCharacteristic);
        if (value.Length == 32)
        {
            if (!_pairing.SavePairingToken(value, _peerDeviceId))
                throw new InvalidOperationException(_pairing.StorageError ?? "Windows already has a pairing for a different iPhone. Forget that pairing first.");
            await UpdateConnectionStateAsync();
            return;
        }

        if (value.Length != 2 || value[0] != RemoteProtocol.Version)
            throw new InvalidOperationException("The iPhone returned an invalid pairing response.");
        var status = (PairingReadStatus)value[1];
        StatusChanged?.Invoke(this, status switch
        {
            PairingReadStatus.Idle => "Enter the six-digit code shown in the iPhone app.",
            PairingReadStatus.InvalidCode => "That code wasn't correct. Check the six digits shown on the iPhone and try again.",
            PairingReadStatus.Locked => "Pairing is temporarily locked after three incorrect codes. Wait one minute and retry.",
            PairingReadStatus.Paired => "The iPhone reports an existing pairing. Forget its saved Windows pairing before pairing again.",
            PairingReadStatus.CodeExpired => "The pairing code expired. Use the current code shown on the iPhone.",
            _ => "The iPhone couldn't save the pairing. Check Keychain storage and retry."
        });
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<byte[]> ReadValueAsync(GattCharacteristic characteristic)
    {
        var result = await characteristic.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (result.Status != GattCommunicationStatus.Success)
            throw new InvalidOperationException($"Windows couldn't read iPhone characteristic {characteristic.Uuid} ({result.Status}).");
        using var reader = DataReader.FromBuffer(result.Value);
        var value = new byte[checked((int)result.Value.Length)];
        reader.ReadBytes(value);
        return value;
    }

    private async void CommandCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        if (_disposed || !_pairing.IsPairedPeer(_peerDeviceId))
            return;
        await _commandGate.WaitAsync();
        try
        {
            using var reader = DataReader.FromBuffer(args.CharacteristicValue);
            var frame = new byte[checked((int)args.CharacteristicValue.Length)];
            reader.ReadBytes(frame);
            if (_pairing.TryAuthenticateCommand(frame, _peerDeviceId, out var command) && command is not null)
            {
                if (!_input.Dispatch(command))
                    StatusChanged?.Invoke(this, "Windows didn't accept that input. Apps running as administrator may block remote control.");
            }
        }
        catch
        {
            // Invalid, unauthenticated, or stale notifications are ignored without sending input.
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private async void StatusCharacteristic_ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try { await UpdateConnectionStateAsync(); }
        catch (Exception exception)
        {
            if (!_disposed)
                StatusChanged?.Invoke(this, $"The iPhone Bluetooth connection needs attention: {exception.Message}");
        }
    }

    private void Device_ConnectionStatusChanged(BluetoothLEDevice sender, object args)
    {
        if (_disposed || sender.ConnectionStatus != BluetoothConnectionStatus.Disconnected)
            return;
        IsConnected = false;
        IsWaitingForPairing = false;
        StatusChanged?.Invoke(this, "Bluetooth disconnected from the iPhone. Keep its Windows connection screen open and retry.");
        StateChanged?.Invoke(this, EventArgs.Empty);
        DetachDevice();
        RestartWatcher();
    }

    private void Watcher_Stopped(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if (!_disposed && args.Error != BluetoothError.Success)
            StatusChanged?.Invoke(this, $"Windows Bluetooth scanning stopped ({args.Error}). Reopen the receiver to retry.");
    }

    private void RestartWatcher()
    {
        if (_disposed || _watcher is null)
            return;
        try
        {
            if (_watcher.Status != BluetoothLEAdvertisementWatcherStatus.Started)
                _watcher.Start();
            StatusChanged?.Invoke(this, "Windows is scanning for the iPhone remote over Bluetooth.");
        }
        catch (Exception exception)
        {
            StatusChanged?.Invoke(this, $"Windows couldn't restart Bluetooth scanning: {exception.Message}");
        }
    }

    private void StopWatcher()
    {
        if (_watcher is null)
            return;
        _watcher.Received -= Watcher_Received;
        _watcher.Stopped -= Watcher_Stopped;
        try { _watcher.Stop(); } catch { /* The radio may already be unavailable. */ }
        _watcher = null;
    }

    private void DetachDevice()
    {
        if (_commandCharacteristic is not null)
            _commandCharacteristic.ValueChanged -= CommandCharacteristic_ValueChanged;
        if (_statusCharacteristic is not null)
            _statusCharacteristic.ValueChanged -= StatusCharacteristic_ValueChanged;
        if (_service is not null)
            _service.Dispose();
        if (_device is not null)
        {
            _device.ConnectionStatusChanged -= Device_ConnectionStatusChanged;
            _device.Dispose();
        }
        _service = null;
        _device = null;
        _commandCharacteristic = null;
        _pairingCharacteristic = null;
        _statusCharacteristic = null;
        _peerDeviceId = string.Empty;
        _sessionStarted = false;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PcRemoteGattClient));
    }
}
