using RigolDPRemoteControl.Views;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using RigolDPRemoteControl.Infrastructure;
using RigolDPRemoteControl.Settings;

namespace RigolDPRemoteControl;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { LogCrash(args.Exception); args.SetObserved(); };

        // Keep the regional date format, but always use '.' as decimal separator (same as the instrument and the CSV files)
        var culture = (CultureInfo)CultureInfo.CurrentCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ".";
        culture.NumberFormat.NumberGroupSeparator = ",";
        culture.NumberFormat.CurrencyDecimalSeparator = ".";
        culture.NumberFormat.PercentDecimalSeparator = ".";
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.CurrentCulture = culture;

        var settings = SettingsService.Load();
        ThemeManager.Apply(settings.DarkMode);

        var window = new MainWindow(settings);
        MainWindow = window;
        window.Show();

        // "RigolDPRemoteControl.exe mytrend.csv" opens the trend in the history viewer
        foreach (var arg in e.Args.Where(a => a.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) && File.Exists(a)))
            OpenHistory(arg);    }

    private static async void OpenHistory(string file)
    {
        try
        {
            var data = await Task.Run(() => Trend.TrendFile.Load(file));
            new Trend.TrendHistoryWindow(data, new TrendSettings { WindowSeconds = 0 }).Show();
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show($"Cannot open {file}:\n{ex.Message}", "Rigol DP Remote Control", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogCrash(e.Exception);
        var msg = $"Unexpected error:\n\n{e.Exception.Message}\n\nDetails were written to settings\\error.log";
        try { ThemedMessageBox.Show(msg, "Rigol DP Remote Control", MessageBoxButton.OK, MessageBoxImage.Warning); }
        catch { MessageBox.Show(msg, "Rigol DP Remote Control", MessageBoxButton.OK, MessageBoxImage.Warning); } // theme itself broken
        e.Handled = true;
    }

    private static void LogCrash(Exception ex)
    {
        try { File.AppendAllText(Path.Combine(AppPaths.SettingsDir, "error.log"), $"{DateTime.Now:O}\n{ex}\n\n"); } catch { }
    }
}
