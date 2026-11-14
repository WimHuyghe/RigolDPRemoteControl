using System.Windows;
using RigolDPRemoteControl.Infrastructure;
using RigolDPRemoteControl.ViewModels;

namespace RigolDPRemoteControl.Views;

/// <summary>
/// Defines a group: membership, plus the name and on/off delays of every channel, all in one table.
/// Names and delays are applied to the channels when OK is pressed.
/// </summary>
public partial class GroupEditorWindow : Window
{
    public sealed class Item : ObservableObject
    {
        public required ChannelViewModel Channel { get; init; }
        private bool _sel;
        public bool IsSelected { get => _sel; set => SetField(ref _sel, value); }
        private string _name = "", _on = "0", _off = "0";
        public string Name { get => _name; set => SetField(ref _name, value); }
        public string OnDelay { get => _on; set => SetField(ref _on, value); }
        public string OffDelay { get => _off; set => SetField(ref _off, value); }
    }

    private readonly List<Item> _items;

    public GroupEditorWindow(MainViewModel main, GroupViewModel? group)
    {
        InitializeComponent();
        Title = group == null ? "New channel group" : $"Edit group – {group.Name}";
        NameBox.Text = group?.Name ?? $"Group {main.Groups.Count + 1}";
        var members = new HashSet<string>(group?.Settings.Members ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        _items = main.Devices.SelectMany(d => d.Channels)
            .Select(c => new Item
            {
                Channel = c,
                IsSelected = members.Contains(c.Key),
                Name = c.Name,
                OnDelay = (c.OnDelayMs / 1000.0).ToString("0.###", Num.Inv),
                OffDelay = (c.OffDelayMs / 1000.0).ToString("0.###", Num.Inv),
            })
            .ToList();
        ChannelList.ItemsSource = _items;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public string GroupName => NameBox.Text.Trim();
    public IEnumerable<string> SelectedKeys => _items.Where(i => i.IsSelected).Select(i => i.Channel.Key);

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (GroupName.Length == 0)
        {
            ThemedMessageBox.Show(this, "Please enter a group name.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (!SelectedKeys.Any())
        {
            ThemedMessageBox.Show(this, "Select at least one channel.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // validate all delays first, then apply names and delays
        var parsed = new List<(Item item, int onMs, int offMs)>();
        foreach (var it in _items)
        {
            if (!TryDelay(it.OnDelay, out var on) || !TryDelay(it.OffDelay, out var off))
            {
                ThemedMessageBox.Show(this, $"{it.Channel.ScpiName} {it.Channel.Device.Address}: delays must be numbers between 0 and 3600 seconds.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            parsed.Add((it, on, off));
        }
        foreach (var (it, on, off) in parsed)
        {
            if (it.Channel.Name != it.Name.Trim()) it.Channel.Name = it.Name;
            if (it.Channel.OnDelayMs != on) it.Channel.OnDelayMs = on;
            if (it.Channel.OffDelayMs != off) it.Channel.OffDelayMs = off;
        }
        DialogResult = true;
    }

    private static bool TryDelay(string text, out int ms)
    {
        ms = 0;
        if (string.IsNullOrWhiteSpace(text)) return true;
        if (!Num.TryParseUser(text, out var s) || s < 0 || s > 3600) return false;
        ms = (int)Math.Round(s * 1000);
        return true;
    }
}
