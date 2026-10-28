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
    private bool _advertising;

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
            _advertising = false;
            SetStatus(exception.Message);
        }
        catch (Exception exception)
        {
            _advertising = false;
            SetStatus($"Windows couldn't start the Bluetooth service: {exception.Message}");
        }

        RefreshPairingUi();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        if (_server is not null)
        {
            _server.StatusChanged -= Server_StatusChanged;
            _server.Dispose();
        }
    }

    private void Server_StatusChanged(object? sender, string message)
    {
        Dispatcher.BeginInvoke(() =>
        {
            _advertising = _server?.IsAdvertising ?? false;
            SetStatus(message);
            RefreshPairingUi();
        });
    }

    private void Pairing_StateChanged(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            RefreshPairingUi();
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
            "This forgets the saved iPhone pairing. You'll need to pair again with a new code.",
            "Forget paired iPhone?",
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
            PairingInstructions.Text = "This Windows adapter must support BLE peripheral mode. The iPhone fallback connection will be added in a later update.";
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

    private void SetStatus(string message)
    {
        StatusText.Text = message;
        StatusDot.Fill = _advertising ? Brushes.ForestGreen : Brushes.SlateGray;
    }
}
