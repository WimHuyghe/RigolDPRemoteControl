using System.Text;
using System.Windows;
using System.Windows.Controls;
using RigolDPRemoteControl.Devices;
using RigolDPRemoteControl.ViewModels;

namespace RigolDPRemoteControl.Views;

public partial class DeviceInfoWindow : Window
{
    private readonly StringBuilder _text = new();

    public DeviceInfoWindow(DeviceViewModel d)
    {
        InitializeComponent();
        var id = d.Identity;
        var spec = id != null ? ModelCatalog.Resolve(id.Model) : d.Settings.Model.Length > 0 ? ModelCatalog.Resolve(d.Settings.Model) : null;
        HeaderText.Text = $"{d.Model}";
        SubText.Text = d.IsConnected ? $"Connected at {d.Address}" : $"Not connected – showing last known information ({d.Address})";

        Add("Manufacturer", id?.Manufacturer ?? "–");
        Add("Model", d.Model + (spec != null ? $"   ({spec.Series} series)" : ""));
        Add("Description", spec?.Description ?? "–");
        Add("Serial number", d.Serial.Length > 0 ? d.Serial : "–");
        Add("MAC address", string.IsNullOrEmpty(id?.MacAddress) ? "–" : id!.MacAddress);
        Add("Firmware version", id?.FirmwareVersion ?? "–");
        Add("SCPI version", string.IsNullOrEmpty(id?.ScpiVersion) ? "–" : id!.ScpiVersion);
        Add(d.IsSerial ? "Connection (RS232)" : "IP address / port",
            (d.IsSerial ? $"{d.Host.ToUpperInvariant()}, {d.Port} baud, 8N1" : $"{d.Host} : {d.Port}") + (d.IsSimulated ? "  (simulated)" : ""));
        if (spec != null) Add("Interfaces", spec.Interfaces);
        if (spec != null)
        {
            Add("Outputs", string.Join("\n", d.Channels.Select(c =>
                $"{c.ScpiName}: {(c.RatingText.Length > 0 ? c.RatingText : "?")}" + (string.IsNullOrEmpty(c.Name) ? "" : $"   ({c.Name})"))));
            if (spec.RangeInfo != null) Add("Ranges / modes", spec.RangeInfo);
            if (spec.TotalPowerW > 0) Add("Total power", $"{spec.TotalPowerW:0} W");
        }
        Add("*IDN? response", id?.RawIdn ?? "–");
    }

    private void Add(string label, string value)
    {
        int row = InfoGrid.RowDefinitions.Count;
        InfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var l = new TextBlock { Text = label, Margin = new Thickness(0, 3, 18, 3), Style = (Style)FindResource("Secondary"), VerticalAlignment = VerticalAlignment.Top };
        var v = new TextBlock
        {
            Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3), FontWeight = FontWeights.SemiBold,
        };
        v.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        Grid.SetRow(l, row); Grid.SetRow(v, row); Grid.SetColumn(v, 1);
        InfoGrid.Children.Add(l); InfoGrid.Children.Add(v);
        _text.AppendLine($"{label}: {value.Replace("\n", "; ")}");
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_text.ToString()); } catch { }
    }
}
