using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using FreeMyTVRemote.Bluetooth;
using FreeMyTVRemote.Input;
using FreeMyTVRemote.Security;

namespace FreeMyTVRemote;

public partial class MainWindow : Window
{
    private readonly PairingManager _pairing = new();
    private readonly DispatcherTimer _refreshTimer;
    private PcRemoteGattServer? _server;
    private PcRemoteGattClient? _client;
    private bool _advertising;
    private bool _usingFallback;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
        _pairing.StateChanged += Pairing_StateChanged;

        // One-second updates keep the pairing countdown readable without polling Bluetooth or disk.
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => RefreshPairingUi();
        _refreshTimer.Start();

        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
        RefreshPairingUi();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        SetStatus("Checking Bluetooth adapter…");
        _server = new PcRemoteGattServer(_pairing, new WindowsInputDispatcher());
        _server.StatusChanged += Server_StatusChanged;
        try
        {
            await _server.StartAsync();
            _advertising = true;
            SetStatus(_pairing.IsPaired ? "Bluetooth is ready. Your iPhone is paired." : "Advertising over Bluetooth. Ready for your iPhone to connect.");
        }
        catch (NotSupportedException exception)
        {
            await StartFallbackClientAsync(exception.Message);
        }
        catch (Exception exception)
        {
            await StartFallbackClientAsync($"Windows couldn't start its Bluetooth receiver: {exception.Message}");
        }

        RefreshPairingUi();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _isClosing = true;
        _refreshTimer.Stop();
        if (_server is not null)
        {
            _server.StatusChanged -= Server_StatusChanged;
            _server.Dispose();
        }
        if (_client is not null)
        {
            _client.StatusChanged -= Client_StatusChanged;
            _client.StateChanged -= Client_StateChanged;
            _client.Dispose();
        }
    }

    private async Task StartFallbackClientAsync(string serverUnavailableReason)
    {
        if (_usingFallback || _isClosing)
            return;

        _advertising = false;
        _usingFallback = true;
        if (_server is not null)
        {
            _server.StatusChanged -= Server_StatusChanged;
            _server.Dispose();
            _server = null;
        }
        _client = new PcRemoteGattClient(_pairing, new WindowsInputDispatcher());
        _client.StatusChanged += Client_StatusChanged;
        _client.StateChanged += Client_StateChanged;
        SetStatus($"{serverUnavailableReason} Windows is switching to iPhone compatibility mode.");
        try
        {
            await _client.StartAsync();
        }
        catch (Exception exception)
        {
            SetStatus($"Windows couldn't start the Bluetooth client: {exception.Message}");
        }
        RefreshPairingUi();
    }

    private void Client_StatusChanged(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            SetStatus(message);
            RefreshPairingUi();
        });
    }

    private void Client_StateChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(RefreshPairingUi);
    }

    private async void FallbackPair_Click(object sender, RoutedEventArgs e)
    {
        if (_client is null)
            return;
        var code = new string(FallbackPairingCodeText.Text.Where(character => character is >= '0' and <= '9').Take(6).ToArray());
        FallbackPairingCodeText.Text = code;
        await _client.SubmitPairingCodeAsync(code);
        FallbackPairingCodeText.Clear();
    }

    private void Server_StatusChanged(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_isClosing || _usingFallback)
                return;

            _advertising = _server?.IsAdvertising ?? false;
            SetStatus(message);
            RefreshPairingUi();
            if (!_advertising && _server is not null)
                _ = StartFallbackClientAsync(message);
        });
    }

    private void Pairing_StateChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RefreshPairingUi();
            if (_usingFallback)
                return;
            if (_pairing.IsPaired)
            {
                var status = !_advertising
                    ? "Pairing is saved, but Bluetooth advertising is stopped. Reopen the receiver to reconnect."
                    : _server?.HasAuthorizedClient == true
                        ? "Your paired iPhone is connected over Bluetooth."
                        : "The receiver is advertising. Your paired iPhone is not currently connected.";
                SetStatus(status);
            }
        });
    }

    private void ResetPairing_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "This forgets the saved Bluetooth pairing. You'll need to pair again with a new code.",
            "Forget Bluetooth pairing?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            _pairing.ForgetPairing();
            SetStatus(_advertising ? "A new pairing code is ready." : "Pairing cleared. Bluetooth is not advertising.");
        }
        catch (Exception)
        {
            SetStatus("Windows couldn't clear the saved pairing. Check your Windows user profile.");
        }
    }

    private void RefreshPairingUi()
    {
        if (_usingFallback)
        {
            RefreshFallbackUi();
            return;
        }

        FallbackPairingPanel.Visibility = Visibility.Collapsed;
        if (_pairing.StorageError is not null)
        {
            PairingHeading.Text = "Pairing storage needs attention";
            PairingInstructions.Text = _pairing.StorageError;
            CodePanel.Visibility = Visibility.Collapsed;
            ResetPairingButton.Content = "Reset saved pairing";
            ResetPairingButton.Visibility = Visibility.Visible;
            return;
        }

        if (!_advertising)
        {
            PairingHeading.Text = "Bluetooth setup";
            PairingInstructions.Text = "Windows could not start its Bluetooth receiver.";
            CodePanel.Visibility = Visibility.Collapsed;
            ResetPairingButton.Visibility = _pairing.IsPaired ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        if (_pairing.IsPaired)
        {
            PairingHeading.Text = "iPhone paired";
            PairingInstructions.Text = "Your pairing token is protected with Windows DPAPI. Forget the pairing below if you want to connect a different iPhone.";
            CodePanel.Visibility = Visibility.Collapsed;
            ResetPairingButton.Content = "Forget paired iPhone";
            ResetPairingButton.Visibility = Visibility.Visible;
            return;
        }

        ResetPairingButton.Visibility = Visibility.Collapsed;
        if (_pairing.IsLocked)
        {
            PairingHeading.Text = "Pairing temporarily locked";
            PairingInstructions.Text = "Too many incorrect codes were entered. Wait about a minute, then try again. The current code remains valid unless it expires.";
            CodePanel.Visibility = Visibility.Collapsed;
            return;
        }

        PairingHeading.Text = "Pairing code";
        PairingInstructions.Text = _pairing.LastStatus switch
        {
            PairingReadStatus.InvalidCode => "That code wasn't correct. Try the current code below. Three incorrect attempts temporarily lock pairing.",
            PairingReadStatus.CodeExpired => "The previous code expired. Enter the refreshed code below.",
            _ => "Open Free My TV on your iPhone, choose this Windows PC under Bluetooth, then enter the code shown here."
        };
        PairingCodeText.Text = _pairing.CurrentCode ?? "------";
        var seconds = _pairing.SecondsUntilCodeExpires;
        PairingCountdownText.Text = seconds > 0 ? $"Code refreshes in {Math.Ceiling(seconds / 60.0):0} min" : "Preparing a new code…";
        CodePanel.Visibility = Visibility.Visible;
    }

    private void RefreshFallbackUi()
    {
        CodePanel.Visibility = Visibility.Collapsed;
        var client = _client;
        if (_pairing.StorageError is not null)
        {
            PairingHeading.Text = "Pairing storage needs attention";
            PairingInstructions.Text = _pairing.StorageError;
            FallbackPairingPanel.Visibility = Visibility.Collapsed;
            ResetPairingButton.Content = "Reset saved pairing";
            ResetPairingButton.Visibility = Visibility.Visible;
            return;
        }

        if (client?.IsConnected == true && client.HasSavedPairingForPeer)
        {
            PairingHeading.Text = "iPhone connected";
            PairingInstructions.Text = "The iPhone remote is connected over Bluetooth. Windows accepts only authenticated, allowlisted remote commands.";
            FallbackPairingPanel.Visibility = Visibility.Collapsed;
            ResetPairingButton.Visibility = Visibility.Collapsed;
            return;
        }

        ResetPairingButton.Visibility = _pairing.IsPaired ? Visibility.Visible : Visibility.Collapsed;
        ResetPairingButton.Content = "Clear Windows pairing";
        if (client?.IsConnected == true && !_pairing.IsPaired)
        {
            PairingHeading.Text = "Pairing cleared on Windows";
            PairingInstructions.Text = "On the iPhone, tap Forget Windows pairing to clear its saved token. Windows will show a new code after the phone forgets it.";
            FallbackPairingPanel.Visibility = Visibility.Collapsed;
            ResetPairingButton.Visibility = Visibility.Collapsed;
            return;
        }
        if (client?.IsWaitingForPairing == true)
        {
            PairingHeading.Text = "Pair with iPhone";
            if (_pairing.IsPaired)
            {
                PairingInstructions.Text = client.HasSavedPairingForPeer
                    ? "Windows and the iPhone don't have matching saved pairing data. Forget the pairing on both devices, reconnect, and pair again."
                    : "Windows has a saved pairing for a different iPhone. Forget the saved pairing, then reconnect and pair this iPhone.";
                FallbackPairingPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                PairingInstructions.Text = "Keep the iPhone's Windows connection screen open. Enter the current six-digit code shown there.";
                FallbackPairingPanel.Visibility = Visibility.Visible;
            }
            return;
        }

        PairingHeading.Text = "Waiting for iPhone";
        PairingInstructions.Text = "On the iPhone, open Free My TV, choose Windows PC over Bluetooth, and keep the connection screen open.";
        FallbackPairingPanel.Visibility = Visibility.Collapsed;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        StatusDot.Fill = _advertising || _client?.IsConnected == true ? Brushes.ForestGreen : Brushes.SlateGray;
    }
}
