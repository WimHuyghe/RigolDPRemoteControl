using System.Windows;
using RigolDPRemoteControl.Infrastructure;
using RigolDPRemoteControl.Settings;

namespace RigolDPRemoteControl.Views;

public partial class AppSettingsWindow : Window
{
    private readonly AppSettings _s;

    public AppSettingsWindow(AppSettings s)
    {
        InitializeComponent();
        _s = s;
        PollBox.Text = s.PollIntervalMs.ToString();
        StatusBox.Text = s.StatusIntervalMs.ToString();
        TimeoutBox.Text = s.CommandTimeoutMs.ToString();
        PortBox.Text = s.DefaultPort.ToString();
        AutoTrend.IsChecked = s.AutoStartTrend;
        SingleDelays.IsChecked = s.ApplyDelaysToSingleChannel;
        GoLocal.IsChecked = s.ReturnToLocalOnExit;
        ShowEnergy.IsChecked = s.ShowEnergy;
        SettingsPath.Text = AppPaths.SettingsFile;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PollBox.Text, out var poll) || poll < 0 || poll > 60000 ||
            !int.TryParse(StatusBox.Text, out var status) || status < 100 || status > 60000 ||
            !int.TryParse(TimeoutBox.Text, out var timeout) || timeout < 200 || timeout > 30000 ||
            !int.TryParse(PortBox.Text, out var port) || port < 1 || port > 65535)
        {
            ThemedMessageBox.Show(this, "Please enter valid whole numbers:\n• pause 0–60000 ms\n• status interval 100–60000 ms\n• timeout 200–30000 ms\n• port 1–65535",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _s.PollIntervalMs = poll;
        _s.StatusIntervalMs = status;
        _s.CommandTimeoutMs = timeout;
        _s.DefaultPort = port;
        _s.AutoStartTrend = AutoTrend.IsChecked == true;
        _s.ApplyDelaysToSingleChannel = SingleDelays.IsChecked == true;
        _s.ReturnToLocalOnExit = GoLocal.IsChecked == true;
        _s.ShowEnergy = ShowEnergy.IsChecked == true;
        DialogResult = true;
    }
}
