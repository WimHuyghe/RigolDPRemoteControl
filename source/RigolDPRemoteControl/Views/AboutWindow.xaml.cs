using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using RigolDPRemoteControl.Infrastructure;

namespace RigolDPRemoteControl.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppInfo.VersionText}";
        CopyrightText.Text = $"© 2026 {AppInfo.Author}  –  MIT license";

        if (AppInfo.RepositoryUrl.Length > 0)
        {
            RepoText.Text = AppInfo.RepositoryUrl.Replace("https://", "");
            RepoLink.Tag = AppInfo.RepositoryUrl;
            LicenseLink.Tag = AppInfo.RepositoryUrl + "/blob/main/LICENSE";
        }
        else
        {
            RepoLine.Visibility = Visibility.Collapsed;
            LicenseLink.Tag = Path.Combine(AppContext.BaseDirectory, "LICENSE.txt");
        }
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Hyperlink)?.Tag is not string target) return;
        if (!target.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !File.Exists(target)) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }
}
