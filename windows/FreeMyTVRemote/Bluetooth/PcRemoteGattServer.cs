using FreeMyTVRemote.Input;
using FreeMyTVRemote.Protocol;
using FreeMyTVRemote.Security;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

namespace FreeMyTVRemote.Bluetooth;

internal sealed class PcRemoteGattServer : IDisposable
{
    private static readonly TimeSpan AdvertisementStartTimeout = TimeSpan.FromSeconds(10); // Radio startup should be quick; a bound prevents a false-ready UI from hanging.
    private readonly PairingManager _pairing;
    private readonly WindowsInputDispatcher _input;
    private readonly SemaphoreSlim _commandGate = new(1, 1); // Serializes sequence acceptance and its corresponding OS input dispatch.
    private GattServiceProvider? _provider;
    private GattLocalCharacteristic? _pairingCharacteristic;
    private GattLocalCharacteristic? _commandCharacteristic;
    private GattLocalCharacteristic? _statusCharacteristic;
    private bool _statusSubscribed;
    private volatile bool _disposed;

    internal event EventHandler<string>? StatusChanged;
    internal bool IsAdvertising => !_disposed && (_provider?.AdvertisementStatus is GattServiceProviderAdvertisementStatus.Started or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData);
    internal bool HasAuthorizedClient => _statusSubscribed;

    internal PcRemoteGattServer(PairingManager pairing, WindowsInputDispatcher input)
    {
        _pairing = pairing;
        _input = input;
    }

    internal async Task StartAsync()
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync();
            ThrowIfDisposed();
            if (adapter is null)
                throw new NotSupportedException("Windows couldn't find a Bluetooth adapter.");
            if (!adapter.IsPeripheralRoleSupported)
                throw new NotSupportedException("This Bluetooth adapter can't advertise as a BLE peripheral.");

            var serviceResult = await GattServiceProvider.CreateAsync(RemoteProtocol.ServiceUuid);
            if (serviceResult.ServiceProvider is not null)
                _provider = serviceResult.ServiceProvider;
            ThrowIfDisposed();
            if (serviceResult.Error != BluetoothError.Success || _provider is null)
                throw new InvalidOperationException($"Windows couldn't create the Bluetooth service ({serviceResult.Error}).");

            _provider.AdvertisementStatusChanged += Provider_AdvertisementStatusChanged;

            var secureWrite = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Write,
                WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
                UserDescription = "Free My TV remote commands"
            };
            var commandResult = await _provider.Service.CreateCharacteristicAsync(RemoteProtocol.CommandCharacteristicUuid, secureWrite);
            ThrowIfDisposed();
            if (commandResult.Error != BluetoothError.Success || commandResult.Characteristic is null)
                throw new InvalidOperationException($"Windows couldn't create the command characteristic ({commandResult.Error}).");
            _commandCharacteristic = commandResult.Characteristic;
            _commandCharacteristic.WriteRequested += CommandCharacteristic_WriteRequested;

            var securePairing = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Write,
                ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
                WriteProtectionLevel = GattProtectionLevel.EncryptionRequired,
                UserDescription = "Free My TV pairing"
            };
            var pairingResult = await _provider.Service.CreateCharacteristicAsync(RemoteProtocol.PairingCharacteristicUuid, securePairing);
            ThrowIfDisposed();
            if (pairingResult.Error != BluetoothError.Success || pairingResult.Characteristic is null)
                throw new InvalidOperationException($"Windows couldn't create the pairing characteristic ({pairingResult.Error}).");
            _pairingCharacteristic = pairingResult.Characteristic;
            _pairingCharacteristic.WriteRequested += PairingCharacteristic_WriteRequested;
            _pairingCharacteristic.ReadRequested += PairingCharacteristic_ReadRequested;

            var statusParameters = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
                ReadProtectionLevel = GattProtectionLevel.EncryptionRequired,
                UserDescription = "Free My TV connection status"
            };
            var statusResult = await _provider.Service.CreateCharacteristicAsync(RemoteProtocol.StatusCharacteristicUuid, statusParameters);
            ThrowIfDisposed();
            if (statusResult.Error != BluetoothError.Success || statusResult.Characteristic is null)
                throw new InvalidOperationException($"Windows couldn't create the status characteristic ({statusResult.Error}).");
            _statusCharacteristic = statusResult.Characteristic;
            _statusCharacteristic.ReadRequested += StatusCharacteristic_ReadRequested;
            _statusCharacteristic.SubscribedClientsChanged += StatusCharacteristic_SubscribedClientsChanged;
            _pairing.StateChanged += Pairing_StateChanged;

            ThrowIfDisposed();
            _provider.StartAdvertising(new GattServiceProviderAdvertisingParameters
            {
                IsConnectable = true,
                IsDiscoverable = true
            });
            await WaitForAdvertisingAsync(_provider);
            ThrowIfDisposed();
            StatusChanged?.Invoke(this, "Advertising over Bluetooth. Ready for your iPhone to connect.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_provider is not null)
        {
            _provider.AdvertisementStatusChanged -= Provider_AdvertisementStatusChanged;
            try { _provider.StopAdvertising(); } catch { /* The provider may already have stopped after a radio or startup error. */ }
        }
        if (_commandCharacteristic is not null)
            _commandCharacteristic.WriteRequested -= CommandCharacteristic_WriteRequested;
        if (_pairingCharacteristic is not null)
        {
            _pairingCharacteristic.WriteRequested -= PairingCharacteristic_WriteRequested;
            _pairingCharacteristic.ReadRequested -= PairingCharacteristic_ReadRequested;
        }
        if (_statusCharacteristic is not null)
        {
            _statusCharacteristic.ReadRequested -= StatusCharacteristic_ReadRequested;
            _statusCharacteristic.SubscribedClientsChanged -= StatusCharacteristic_SubscribedClientsChanged;
        }
        _pairing.StateChanged -= Pairing_StateChanged;
    }

    private void Provider_AdvertisementStatusChanged(GattServiceProvider sender, GattServiceProviderAdvertisementStatusChangedEventArgs args)
    {
        if (args.Status is GattServiceProviderAdvertisementStatus.Aborted or GattServiceProviderAdvertisementStatus.Stopped)
            StatusChanged?.Invoke(this, "Windows BLE advertising stopped unexpectedly.");
        NotifyStatusChanged();
    }

    private static async Task WaitForAdvertisingAsync(GattServiceProvider provider)
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStatusChanged(GattServiceProvider sender, GattServiceProviderAdvertisementStatusChangedEventArgs args)
        {
            if (args.Status is GattServiceProviderAdvertisementStatus.Started or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData)
                started.TrySetResult(true);
            else if (args.Status == GattServiceProviderAdvertisementStatus.Aborted)
                started.TrySetResult(false);
        }

        provider.AdvertisementStatusChanged += OnStatusChanged;
        try
        {
            if (provider.AdvertisementStatus is GattServiceProviderAdvertisementStatus.Started or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData)
                return;
            if (provider.AdvertisementStatus == GattServiceProviderAdvertisementStatus.Aborted)
                throw new InvalidOperationException("Windows aborted BLE advertising.");

            var timeout = Task.Delay(AdvertisementStartTimeout);
            var completed = await Task.WhenAny(started.Task, timeout);
            if (completed != started.Task)
                throw new TimeoutException("Windows did not confirm BLE advertising before the startup deadline.");
            if (!await started.Task)
                throw new InvalidOperationException("Windows aborted BLE advertising.");
        }
        finally
        {
            provider.AdvertisementStatusChanged -= OnStatusChanged;
        }
    }

    private async void PairingCharacteristic_WriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        GattWriteRequest? request = null;
        var responded = false;
        try
        {
            request = await args.GetRequestAsync();
            if (_disposed)
            {
                if (request.Option == GattWriteOption.WriteWithResponse)
                    request.RespondWithProtocolError(0x80);
                responded = true;
                return;
            }
            if (request.Option != GattWriteOption.WriteWithResponse)
                return;
            if (request.Offset != 0)
            {
                request.RespondWithProtocolError(0x07); // ATT Invalid Offset.
                responded = true;
                return;
            }

            var value = await ReadBufferAsync(request.Value);
            if (_disposed)
            {
                request.RespondWithProtocolError(0x80);
                responded = true;
                return;
            }
            _ = _pairing.ProcessPairingWrite(value, GetPeerDeviceId(args.Session));
            request.Respond();
            responded = true;
        }
        catch
        {
            if (request is not null && !responded && request.Option == GattWriteOption.WriteWithResponse)
            {
                try { request.RespondWithProtocolError(0x80); } catch { /* The request may have expired while the callback was running. */ }
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void PairingCharacteristic_ReadRequested(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        GattReadRequest? request = null;
        var responded = false;
        try
        {
            request = await args.GetRequestAsync();
            if (_disposed)
            {
                request.RespondWithProtocolError(0x80);
                responded = true;
                return;
            }
            var value = _pairing.GetPairingReadValue(GetPeerDeviceId(args.Session));
            var offset = checked((int)request.Offset);
            if (offset > value.Length)
            {
                request.RespondWithProtocolError(0x07); // ATT Invalid Offset.
                responded = true;
                return;
            }

            var available = value.Length - offset;
            var count = (int)Math.Min(request.Length, (uint)available);
            var slice = value.AsSpan(offset, count).ToArray();
            request.RespondWithValue(CryptographicBuffer.CreateFromByteArray(slice));
            responded = true;
        }
        catch
        {
            if (request is not null && !responded)
            {
                try { request.RespondWithProtocolError(0x80); } catch { /* The request may have expired while the callback was running. */ }
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void CommandCharacteristic_WriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        GattWriteRequest? request = null;
        var responded = false;
        try
        {
            request = await args.GetRequestAsync();
            if (_disposed)
            {
                if (request.Option == GattWriteOption.WriteWithResponse)
                    request.RespondWithProtocolError(0x80);
                responded = true;
                return;
            }
            if (request.Option != GattWriteOption.WriteWithResponse)
                return;
            if (request.Offset != 0)
            {
                request.RespondWithProtocolError(0x07); // ATT Invalid Offset.
                responded = true;
                return;
            }

            var value = await ReadBufferAsync(request.Value);
            if (_disposed)
            {
                request.RespondWithProtocolError(0x80);
                responded = true;
                return;
            }
            await _commandGate.WaitAsync();
            try
            {
                if (_disposed)
                {
                    request.RespondWithProtocolError(0x80);
                    responded = true;
                    return;
                }

                if (_pairing.TryAuthenticateCommand(value, GetPeerDeviceId(args.Session), out var command) && command is not null)
                {
                    if (!_input.Dispatch(command))
                    {
                        StatusChanged?.Invoke(this, "Windows didn't accept that input. Apps running as administrator may block remote control.");
                        request.RespondWithProtocolError(0x80);
                    }
                    else
                        request.Respond();
                    responded = true;
                    return;
                }

                // ATT 0x05 means the request is not authorized; 0x80 is this service's invalid-frame error.
                request.RespondWithProtocolError(_pairing.IsPaired ? (byte)0x80 : (byte)0x05);
                responded = true;
            }
            finally
            {
                _commandGate.Release();
            }
        }
        catch
        {
            if (request is not null && !responded && request.Option == GattWriteOption.WriteWithResponse)
            {
                try { request.RespondWithProtocolError(0x80); } catch { /* The request may have expired while the callback was running. */ }
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private async void StatusCharacteristic_ReadRequested(GattLocalCharacteristic sender, GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        GattReadRequest? request = null;
        var responded = false;
        try
        {
            request = await args.GetRequestAsync();
            if (_disposed)
            {
                request.RespondWithProtocolError(0x80);
                responded = true;
                return;
            }
            var value = GetStatusValue();
            var offset = checked((int)request.Offset);
            if (offset > value.Length)
            {
                request.RespondWithProtocolError(0x07); // ATT Invalid Offset.
                responded = true;
                return;
            }

            var count = (int)Math.Min(request.Length, (uint)(value.Length - offset));
            request.RespondWithValue(CryptographicBuffer.CreateFromByteArray(value.AsSpan(offset, count).ToArray()));
            responded = true;
        }
        catch
        {
            if (request is not null && !responded)
            {
                try { request.RespondWithProtocolError(0x80); } catch { /* The request may have expired while the callback was running. */ }
            }
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void StatusCharacteristic_SubscribedClientsChanged(GattLocalCharacteristic sender, object args)
    {
        RefreshAuthorizedSubscriberState(sender);
    }

    private void RefreshAuthorizedSubscriberState(GattLocalCharacteristic? sender = null)
    {
        if (_disposed)
            return;
        sender ??= _statusCharacteristic;
        if (sender is null)
            return;

        string? pairedPeerDeviceId = null;
        foreach (var client in sender.SubscribedClients)
        {
            var candidate = GetPeerDeviceId(client.Session);
            if (_pairing.IsPairedPeer(candidate))
            {
                pairedPeerDeviceId = candidate;
                break;
            }
        }

        var hasClient = pairedPeerDeviceId is not null;
        if (hasClient && !_statusSubscribed)
            _pairing.BeginConnectionSession(pairedPeerDeviceId!);
        _statusSubscribed = hasClient;
        if (_statusSubscribed)
            StatusChanged?.Invoke(this, "Your paired iPhone is connected over Bluetooth.");
        else if (IsAdvertising)
            StatusChanged?.Invoke(this, "Advertising over Bluetooth. Ready for your iPhone to connect.");
        NotifyStatusChanged();
    }

    private void Pairing_StateChanged(object? sender, EventArgs args) => RefreshAuthorizedSubscriberState();

    private async void NotifyStatusChanged()
    {
        var characteristic = _statusCharacteristic;
        if (_disposed || characteristic is null || characteristic.SubscribedClients.Count == 0)
            return;

        try
        {
            await characteristic.NotifyValueAsync(CryptographicBuffer.CreateFromByteArray(GetStatusValue()));
        }
        catch
        {
            // Notifications are best-effort; the protected read characteristic remains the source of current state.
        }
    }

    private byte[] GetStatusValue() =>
    [
        RemoteProtocol.Version,
        IsAdvertising ? (byte)1 : (byte)0,
        _statusSubscribed ? (byte)1 : (byte)0,
        _pairing.IsPaired ? (byte)1 : (byte)0,
        (byte)_pairing.LastStatus
    ];

    private static string GetPeerDeviceId(GattSession? session)
    {
        try { return session?.DeviceId?.Id ?? string.Empty; }
        catch { return string.Empty; }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(PcRemoteGattServer));
    }

    private static Task<byte[]> ReadBufferAsync(IBuffer buffer)
    {
        using var reader = DataReader.FromBuffer(buffer);
        var bytes = new byte[checked((int)buffer.Length)];
        reader.ReadBytes(bytes);
        return Task.FromResult(bytes);
    }
}
