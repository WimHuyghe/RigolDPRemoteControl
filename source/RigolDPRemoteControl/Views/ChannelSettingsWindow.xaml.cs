using System.Windows;
using RigolDPRemoteControl.Devices;
using RigolDPRemoteControl.Infrastructure;
using RigolDPRemoteControl.ViewModels;

namespace RigolDPRemoteControl.Views;

public partial class ChannelSettingsWindow : Window
{
    private readonly ChannelViewModel _ch;
    private ProtectionSettings? _loaded;

    public ChannelSettingsWindow(ChannelViewModel ch)
    {
        InitializeComponent();
        _ch = ch;
        Title = $"{ch.ScpiName} settings – {ch.Device.Address}";
        HeaderText.Text = $"{ch.ScpiName}  –  {ch.Device.Model}";
        SubText.Text = $"{ch.Device.Address}   {ch.RatingText}";
        ColorBar.Fill = ch.ColorBrush;
        NameBox.Text = ch.Name;
        OnDelayBox.Text = (ch.OnDelayMs / 1000.0).ToString("0.###", Num.Inv);
        OffDelayBox.Text = (ch.OffDelayMs / 1000.0).ToString("0.###", Num.Inv);
        if (ch.TrendSeries != null)
        {
            TrendV.IsChecked = ch.TrendSeries.ShowVoltage;
            TrendI.IsChecked = ch.TrendSeries.ShowCurrent;
            TrendP.IsChecked = ch.TrendSeries.ShowPower;
        }
        ProtDelayOn.IsChecked = ch.ProtectionDelayEnabled;
        ProtDelayBox.Text = (ch.ProtectionDelayMs / 1000.0).ToString("0.###", Num.Inv);
        ProtDelayPanel.IsEnabled = ch.ProtectionDelayEnabled;
        ProtectionGrid.IsEnabled = false;
        ProtectionInfo.Text = ch.Device.IsConnected ? "Reading protection settings…" : "Not connected – protection settings cannot be read.";
        Loaded += async (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
            if (!ch.Device.IsConnected) return;
            _loaded = await ch.Device.QueryAsync(d => d.ReadProtectionAsync(ch.Index), "read protection");
            if (_loaded != null)
            {
                OvpBox.Text = Num.F(_loaded.OvpValue, 3);
                OcpBox.Text = Num.F(_loaded.OcpValue, 3);
                // with delayed protection the instrument is often deliberately off: show the wanted state
                OvpOn.IsChecked = ch.ProtectionDelayEnabled ? ch.Settings.DelayedOvpOn : _loaded.OvpEnabled;
                OcpOn.IsChecked = ch.ProtectionDelayEnabled ? ch.Settings.DelayedOcpOn : _loaded.OcpEnabled;
                ProtectionGrid.IsEnabled = true;
                UpdateProtectionInfo();
            }
            else ProtectionInfo.Text = "Protection settings could not be read.";
        };
    }

    private void ProtDelayOn_Changed(object sender, RoutedEventArgs e)
    {
        if (ProtDelayPanel == null) return; // during InitializeComponent
        ProtDelayPanel.IsEnabled = ProtDelayOn.IsChecked == true;
        UpdateProtectionInfo();
    }

    private void UpdateProtectionInfo()
    {
        if (_loaded == null) return;
        ProtectionInfo.Text = ProtDelayOn.IsChecked == true
            ? "'On' = enabled once the output has been on for the delay below; while the output is off OVP/OCP stay off."
            : "When a limit is exceeded the instrument switches the output off; the card then shows a red 'tripped' banner.";
    }

    private async void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!Num.TryParseUser(OnDelayBox.Text, out var onS) || onS < 0 || onS > 3600 ||
            !Num.TryParseUser(OffDelayBox.Text, out var offS) || offS < 0 || offS > 3600)
        {
            ThemedMessageBox.Show(this, "Delays must be numbers between 0 and 3600 seconds.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        bool protDelay = ProtDelayOn.IsChecked == true;
        double protDelayS = 0;
        if (protDelay && (!Num.TryParseUser(ProtDelayBox.Text, out protDelayS) || protDelayS < 0 || protDelayS > 3600))
        {
            ThemedMessageBox.Show(this, "The OVP/OCP enable delay must be a number between 0 and 3600 seconds.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        ProtectionSettings? newProt = null;
        if (_loaded != null && ProtectionGrid.IsEnabled)
        {
            if (!Num.TryParseUser(OvpBox.Text, out var ovp) || !Num.TryParseUser(OcpBox.Text, out var ocp))
            {
                ThemedMessageBox.Show(this, "OVP/OCP values must be numbers.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            newProt = new ProtectionSettings(ovp, OvpOn.IsChecked == true, ocp, OcpOn.IsChecked == true);
        }

        _ch.Name = NameBox.Text;
        _ch.OnDelayMs = (int)Math.Round(onS * 1000);
        _ch.OffDelayMs = (int)Math.Round(offS * 1000);
        if (_ch.TrendSeries != null)
        {
            _ch.TrendSeries.ShowVoltage = TrendV.IsChecked == true;
            _ch.TrendSeries.ShowCurrent = TrendI.IsChecked == true;
            _ch.TrendSeries.ShowPower = TrendP.IsChecked == true;
        }

        // delayed protection: remember the wanted OVP/OCP states; the instrument only gets them when the output
        // is on and the delay has passed – otherwise OVP/OCP are written as off
        bool wantOvp = newProt?.OvpEnabled ?? _ch.Settings.DelayedOvpOn;
        bool wantOcp = newProt?.OcpEnabled ?? _ch.Settings.DelayedOcpOn;
        bool wasDelayed = _ch.ProtectionDelayEnabled;
        _ch.SetProtectionDelay(protDelay, protDelay ? (int)Math.Round(protDelayS * 1000) : _ch.ProtectionDelayMs, wantOvp, wantOcp);

        if (newProt != null)
        {
            if (protDelay)
            {
                bool active = _ch.IsOutputOn && !_ch.IsProtectionDelayRunning;
                newProt = newProt with { OvpEnabled = active && wantOvp, OcpEnabled = active && wantOcp };
            }
            if (newProt != _loaded || protDelay != wasDelayed)
            {
                IsEnabled = false;
                await _ch.Device.RunAsync(d => d.SetProtectionAsync(_ch.Index, newProt), "set protection");
            }
        }
        DialogResult = true;
    }
}
