using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;

namespace CameraInspector;

internal partial class WifiScanWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IReadOnlyDictionary<string, string> _messages;
    private bool _closed;
    private bool _connecting;

    internal CameraReading? Reading { get; private set; }

    internal WifiScanWindow(LanguageOption language, IReadOnlyDictionary<string, string> messages, IPAddress? lastAddress)
    {
        _messages = messages;
        InitializeComponent();
        Language = XmlLanguage.GetLanguage(language.Culture.IetfLanguageTag);
        FlowDirection = language.Culture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        foreach (string key in new[] { "ScanWifi", "WifiSetup", "WifiFound", "WifiAddress", "WifiFind", "WifiConnect", "Close" })
            Resources[key] = messages[key];
        AddressBox.Text = lastAddress?.ToString() ?? "";
    }

    internal IPAddress? Address => WifiCamera.ParseAddress(AddressBox.Text);

    private async void OnLoaded(object sender, RoutedEventArgs e) => await DiscoverAsync();

    private async void OnFind(object sender, RoutedEventArgs e) => await DiscoverAsync();

    private async Task DiscoverAsync()
    {
        FindButton.IsEnabled = ConnectButton.IsEnabled = false;
        DiscoveredPicker.ItemsSource = null;
        DiscoveredPanel.Visibility = Visibility.Collapsed;
        SetStatus("WifiSearching");
        try
        {
            var addresses = await WifiCamera.DiscoverAsync(_lifetime.Token);
            if (_closed)
                return;
            DiscoveredPicker.ItemsSource = addresses;
            DiscoveredPanel.Visibility = addresses.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (addresses.Count > 0 && string.IsNullOrWhiteSpace(AddressBox.Text))
                DiscoveredPicker.SelectedIndex = 0;
            SetStatus(addresses.Count == 0 ? "WifiNone" : null);
        }
        catch (OperationCanceledException) when (_closed) { }
        catch (SocketException error)
        {
            Debug.WriteLine(error.Message);
            if (!_closed)
                SetStatus("WifiNone");
        }
        finally
        {
            if (!_closed)
                FindButton.IsEnabled = ConnectButton.IsEnabled = true;
        }
    }

    private void OnDiscoveredChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DiscoveredPicker.SelectedItem is IPAddress address)
            AddressBox.Text = address.ToString();
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if (_connecting)
            return;
        if (Address is not { } address)
        {
            SetStatus("WifiInvalidAddress", "Yellow");
            AddressBox.Focus();
            return;
        }

        _connecting = true;
        SetControlsEnabled(false);
        SetStatus("WifiPairing");
        try
        {
            Reading = await WifiCamera.ReadAsync(address, _lifetime.Token);
            if (!_closed)
                DialogResult = true;
        }
        catch (Exception error) when (error is SocketException or IOException or OperationCanceledException)
        {
            Debug.WriteLine(error.Message);
            if (!_closed)
                SetStatus("WifiFailed", "Yellow");
        }
        finally
        {
            _connecting = false;
            if (!_closed)
                SetControlsEnabled(true);
        }
    }

    private void SetControlsEnabled(bool enabled) =>
        FindButton.IsEnabled = ConnectButton.IsEnabled = AddressBox.IsEnabled = DiscoveredPicker.IsEnabled = enabled;

    private void SetStatus(string? key, string brush = "Secondary")
    {
        StatusText.Text = key is null ? "" : _messages[key];
        StatusText.Visibility = key is null ? Visibility.Collapsed : Visibility.Visible;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, brush);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.OnClosed(e);
    }
}
