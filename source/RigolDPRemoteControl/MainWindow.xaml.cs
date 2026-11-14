using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RigolDPRemoteControl.Infrastructure;
using RigolDPRemoteControl.Settings;
using RigolDPRemoteControl.ViewModels;
using RigolDPRemoteControl.Views;

namespace RigolDPRemoteControl;

public partial class MainWindow : Window, IFloatingHost
{
    private readonly MainViewModel _vm;
    private bool _shutdownDone;

    public MainWindow(AppSettings settings)
    {
        InitializeComponent();
        _vm = new MainViewModel(settings);
        DataContext = _vm;
        RestoreGeometry(settings.Window);
        LiveTrend.Attach(_vm.TrendData, settings.Trend, _vm, () => _vm.RequestSave());
        _vm.StoreTrendSettings = LiveTrend.StoreSettings;
        _vm.TrendSettingsReplaced += () => LiveTrend.ReloadSettings();
        Loaded += (_, _) =>
        {
            _vm.Start();
            RestoreFloatingWindows();
            _vm.Devices.CollectionChanged += Devices_CollectionChanged;
        };
        InputBindings.Add(new KeyBinding(new RelayCommand(OpenHelp), Key.F1, ModifierKeys.None));
    }

    /// <summary>Owner for dialogs: the (floating) window the click came from, so the dialog is not hidden behind an always-on-top window.</summary>
    private Window OwnerFor(object? sender)
        => sender is DependencyObject d && Window.GetWindow(d) is { } w ? w : this;

    // ------------------------------------------------------------------ window geometry / shutdown

    private void RestoreGeometry(WindowSettings w)
    {
        if (!double.IsNaN(w.Left) && !double.IsNaN(w.Top))
        {
            var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            if (vs.Contains(new Point(w.Left + 50, w.Top + 20)))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = w.Left;
                Top = w.Top;
            }
        }
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Width = Math.Max(MinWidth, Math.Min(w.Width, SystemParameters.VirtualScreenWidth));
        Height = Math.Max(MinHeight, Math.Min(w.Height, SystemParameters.VirtualScreenHeight));
        if (w.LeftPaneWidth > 200) LeftColumn.Width = new GridLength(w.LeftPaneWidth);
        if (w.Maximized) WindowState = WindowState.Maximized;
    }

    private void StoreGeometry()
    {
        var w = _vm.Settings.Window;
        var b = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        w.Left = b.Left; w.Top = b.Top; w.Width = b.Width; w.Height = b.Height;
        w.Maximized = WindowState == WindowState.Maximized;
        w.LeftPaneWidth = LeftColumn.ActualWidth;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_shutdownDone) { base.OnClosing(e); return; }
        e.Cancel = true;
        IsEnabled = false;
        StoreGeometry();
        LiveTrend.StoreSettings();
        _vm.StorePresetInfo?.Invoke(); // an open preset-info page must be in the settings before they are saved
        try { await _vm.ShutdownAsync(); } catch { }
        _shutdownDone = true;
        FloatingPanelWindow.AppClosing = true; // floating panels stay floating at the next start
        foreach (Window w in Application.Current.Windows) if (w != this) w.Close();
        Close();
    }

    // ------------------------------------------------------------------ toolbar

    private void AddDevice_Click(object sender, RoutedEventArgs e) => _vm.AddDevice();

    private void HostBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { _vm.AddDevice(); e.Handled = true; }
    }

    private void AllOff_Click(object sender, RoutedEventArgs e) => _vm.AllOff();

    private void Help_Click(object sender, RoutedEventArgs e) => OpenHelp();

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void OpenHelp()
    {
        var file = AppPaths.HelpFile;
        if (!File.Exists(file)) { _vm.ReportError("Help file not found: " + file); return; }
        try { Process.Start(new ProcessStartInfo(file) { UseShellExecute = true }); }
        catch (Exception ex) { _vm.ReportError("Cannot open help: " + ex.Message); }
    }

    private void AppSettings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AppSettingsWindow(_vm.Settings) { Owner = this };
        if (dlg.ShowDialog() == true) _vm.SettingsChanged();
    }

    private void LogPopup_Closed(object sender, EventArgs e) => LogToggle.IsChecked = false;

    // ------------------------------------------------------------------ presets

    private PresetInfoWindow? _presetInfo;

    private void PresetInfo_Click(object sender, RoutedEventArgs e)
    {
        if (_presetInfo != null)
        {
            if (_presetInfo.WindowState == WindowState.Minimized) _presetInfo.WindowState = WindowState.Normal;
            _presetInfo.Activate();
            return;
        }
        _presetInfo = new PresetInfoWindow(_vm, SavePresetInteractiveAsync) { Owner = this };
        _presetInfo.Closed += (_, _) => _presetInfo = null;
        _presetInfo.Show();
    }

    private async void SavePreset_Click(object sender, RoutedEventArgs e) => await SavePresetInteractiveAsync();

    private async Task SavePresetInteractiveAsync()
    {
        if (_vm.Devices.Count == 0 && _vm.Groups.Count == 0)
        {
            _vm.ReportError("Nothing to save yet – add a power supply first.");
            return;
        }
        var offline = _vm.DevicesWithoutInstrumentData.Select(d => $"{d.Model} {d.Address}").ToList();
        if (offline.Count > 0 && ThemedMessageBox.Show(this,
                "These power supplies are not connected:\n\n  " + string.Join("\n  ", offline) +
                "\n\nTheir setpoints and OVP/OCP settings cannot be read and are not stored in the preset " +
                "(names, delays, groups and trend settings are). Save anyway?",
                "Save preset", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save preset",
            Filter = PresetService.DialogFilter,
            DefaultExt = PresetService.Extension,
            InitialDirectory = AppPaths.PresetDir,
            FileName = (_vm.CurrentPresetName.Length > 0 ? _vm.CurrentPresetName : "Preset_" + DateTime.Now.ToString("yyyyMMdd_HHmm"))
                       + PresetService.Extension,
        };
        if (dlg.ShowDialog(this) != true) return;
        Cursor = Cursors.Wait;
        try { await _vm.SavePresetAsync(dlg.FileName); }
        finally { Cursor = null; }
    }

    private async void LoadPreset_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Load preset",
            Filter = PresetService.DialogFilter,
            InitialDirectory = AppPaths.PresetDir,
        };
        if (dlg.ShowDialog(this) != true) return;

        PresetFile preset;
        try { preset = PresetService.Load(dlg.FileName); }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(this, $"Cannot read {Path.GetFileName(dlg.FileName)}:\n{ex.Message}", "Load preset",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        var name = Path.GetFileNameWithoutExtension(dlg.FileName);

        // outputs that are on (or about to switch) must be switched off first – the user has to agree
        int on = _vm.ChannelsOnCount;
        if (on > 0)
        {
            if (ThemedMessageBox.Show(this,
                    $"{on} channel(s) are switched ON (or have a delayed switch pending).\n\n" +
                    "Before the preset is loaded, ALL channels of ALL power supplies will be switched OFF immediately (without on/off delays).\n\n" +
                    "Switch everything off and load the preset?",
                    "Load preset – outputs will be switched off", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
        }

        IsEnabled = false;
        Cursor = Cursors.Wait;
        try
        {
            if (!await _vm.SwitchEverythingOffAsync())
            {
                ThemedMessageBox.Show(this, "Not all outputs could be switched off (see the log). The preset was NOT loaded.",
                    "Load preset", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            await _vm.LoadPresetAsync(preset, name);
        }
        catch (Exception ex)
        {
            _vm.ReportError("Loading preset failed: " + ex.Message);
        }
        finally
        {
            Cursor = null;
            IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ groups

    private void NewGroup_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.AllChannels.Count == 0)
        {
            _vm.ReportError("Add (and connect) a power supply first – groups are built from its channels.");
            return;
        }
        var dlg = new GroupEditorWindow(_vm, null) { Owner = OwnerFor(sender) };
        if (dlg.ShowDialog() == true) _vm.AddGroup(dlg.GroupName, dlg.SelectedKeys);
    }

    private void GroupEdit_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GroupViewModel g) return;
        var dlg = new GroupEditorWindow(_vm, g) { Owner = OwnerFor(sender) };
        if (dlg.ShowDialog() == true)
        {
            g.Name = dlg.GroupName;
            g.Settings.Members = dlg.SelectedKeys.ToList();
            _vm.RequestSave();
        }
    }

    private void GroupDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not GroupViewModel g) return;
        if (ThemedMessageBox.Show(OwnerFor(sender), $"Delete group '{g.Name}'?\n(The channels themselves are not affected.)", "Delete group",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            _vm.RemoveGroup(g);
    }

    // ------------------------------------------------------------------ devices

    private void DeviceInfo_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeviceViewModel d) return;
        new DeviceInfoWindow(d) { Owner = OwnerFor(sender) }.ShowDialog();
    }

    private void DeviceUp_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DeviceViewModel d) _vm.MoveDevice(d, -1);
    }

    private void DeviceDown_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DeviceViewModel d) _vm.MoveDevice(d, +1);
    }

    private async void DeviceRemove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DeviceViewModel d) return;
        if (ThemedMessageBox.Show(OwnerFor(sender),
                $"Remove {d.Model} at {d.Address} from the application?\n\nThe outputs are NOT switched off. Channel names, delays, group memberships and the live trend of this supply are removed.",
                "Remove power supply", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await _vm.RemoveDeviceAsync(d);
    }

    // ------------------------------------------------------------------ channels

    private void ChannelSettings_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ChannelViewModel ch) OpenChannelSettings(ch, OwnerFor(sender));
    }

    private void ChannelName_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && (sender as FrameworkElement)?.DataContext is ChannelViewModel ch)
        {
            OpenChannelSettings(ch, OwnerFor(sender));
            e.Handled = true;
        }
    }

    private static void OpenChannelSettings(ChannelViewModel ch, Window owner)
    {
        new ChannelSettingsWindow(ch) { Owner = owner }.ShowDialog();
    }

    // ------------------------------------------------------------------ floating panels (undock / dock by dragging the header)

    private readonly Dictionary<object, FloatingPanelWindow> _floating = new();
    private FrameworkElement? _dragHeader;
    private Point _dragStartPx;
    private bool _dragging;
    private const double DragThresholdPx = 8;

    /// <summary>True when the mouse went down on a button / input inside the header (those keep their own function).</summary>
    private static bool IsOnControl(object? source, DependencyObject header)
    {
        var d = source as DependencyObject;
        while (d != null && !ReferenceEquals(d, header))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase or ComboBox) return true;
            d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    private void PanelHeader_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var header = (FrameworkElement)sender;
        if (IsOnControl(e.OriginalSource, header) || e.ClickCount > 1) return;
        e.Handled = true;
        if (Window.GetWindow(header) is FloatingPanelWindow fw)
        {
            fw.BeginMove(); // floating: move the window; dropping it on the supply list docks it
            return;
        }
        _dragHeader = header;
        _dragging = false;
        _dragStartPx = FloatingPanelWindow.CursorScreenPixels();
        header.CaptureMouse();
    }

    private void PanelHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragHeader == null || !ReferenceEquals(sender, _dragHeader)) return;
        if (e.LeftButton != MouseButtonState.Pressed) { EndHeaderDrag(); return; }
        var cur = FloatingPanelWindow.CursorScreenPixels();
        if (!_dragging && (cur - _dragStartPx).Length < DragThresholdPx) return;
        _dragging = true;
        Mouse.OverrideCursor = Cursors.SizeAll;
        if (IsOverDockZone(cur)) return; // still inside the supply list

        // the mouse left the supply list: tear the panel off into its own window and keep moving it
        var item = _dragHeader.DataContext;
        var grab = e.GetPosition(_dragHeader);
        EndHeaderDrag();
        if (item != null) Undock(item, grab);
    }

    private void PanelHeader_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(sender, _dragHeader)) EndHeaderDrag();
    }

    private void PanelHeader_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(sender, _dragHeader)) { _dragHeader = null; _dragging = false; Mouse.OverrideCursor = null; }
    }

    private void EndHeaderDrag()
    {
        var h = _dragHeader;
        _dragHeader = null;
        _dragging = false;
        Mouse.OverrideCursor = null;
        h?.ReleaseMouseCapture();
    }

    /// <summary>Is the mouse (screen pixels) over the supply list of this window?</summary>
    private bool IsOverDockZone(Point screenPx)
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return false;
        var p = SupplyPane.PointFromScreen(screenPx);
        return p.X >= 0 && p.Y >= 0 && p.X <= SupplyPane.ActualWidth && p.Y <= SupplyPane.ActualHeight;
    }

    private void SetFloating(object item, bool floating)
    {
        if (item is DeviceViewModel d) d.IsFloating = floating;
        else if (item is MainViewModel m) m.IsGroupsFloating = floating;
    }

    private FloatingPanelWindow CreateFloating(object item)
    {
        FloatingPanelWindow w;
        if (item is DeviceViewModel d)
        {
            w = new FloatingPanelWindow(this, d, (DataTemplate)FindResource("DevicePanel"), d.Title);
            w.SetBinding(TitleProperty, new System.Windows.Data.Binding(nameof(DeviceViewModel.Title)) { Source = d });
        }
        else
            w = new FloatingPanelWindow(this, _vm, (DataTemplate)FindResource("GroupsPanel"), "Channel groups", fixedWidth: 640);
        _floating[item] = w;
        w.Closed += (_, _) => { if (_floating.TryGetValue(item, out var cur) && ReferenceEquals(cur, w)) _floating.Remove(item); };
        return w;
    }

    /// <summary>Tear a panel off while dragging: the new window appears under the mouse and follows it until the button is released.</summary>
    private void Undock(object item, Point grab)
    {
        if (_floating.TryGetValue(item, out var existing)) { existing.Activate(); return; }
        SetFloating(item, true);
        var w = CreateFloating(item);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var cur = FloatingPanelWindow.CursorScreenPixels();
        w.Left = cur.X / dpi.DpiScaleX - grab.X - 8; // the panel card has a margin of 8,4 inside the frameless window
        w.Top = cur.Y / dpi.DpiScaleY - grab.Y - 4;
        w.Show();
        w.UpdateLayout();
        // fine-tune: keep the header exactly at the grab point
        if (w.FindHeader() is { } h)
        {
            var hp = h.PointToScreen(new Point(0, 0));
            w.Left += (cur.X - hp.X) / dpi.DpiScaleX - grab.X;
            w.Top += (cur.Y - hp.Y) / dpi.DpiScaleY - grab.Y;
        }
        w.Activate();
        w.BeginMove();
    }

    private void Dock(object item)
    {
        DockHint.Visibility = Visibility.Collapsed;
        SetFloating(item, false);
        if (_floating.TryGetValue(item, out var w))
        {
            _floating.Remove(item);
            w.CloseForDocking();
        }
        Activate();
    }

    /// <summary>Open the floating windows that were open at the last exit (or come from a loaded preset).</summary>
    private void RestoreFloatingWindows()
    {
        foreach (var d in _vm.Devices.Where(d => d.IsFloating)) OpenFloatingAtSaved(d);
        if (_vm.IsGroupsFloating) OpenFloatingAtSaved(_vm);
    }

    private void OpenFloatingAtSaved(object item)
    {
        if (_floating.ContainsKey(item)) return;
        var w = CreateFloating(item);
        var (l, t) = item is DeviceViewModel d ? (d.Settings.FloatLeft, d.Settings.FloatTop) : (_vm.Settings.GroupsFloatLeft, _vm.Settings.GroupsFloatTop);
        var vs = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (double.IsNaN(l) || double.IsNaN(t) || !vs.Contains(new Point(l + 60, t + 20)))
        {
            l = Left + 80 + 30 * _floating.Count;
            t = Top + 90 + 30 * _floating.Count;
        }
        w.Left = l;
        w.Top = t;
        w.Show();
    }

    private void Devices_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // removed supplies (or a loaded preset): close their windows; new supplies that are floating (preset) get one
        foreach (var item in _floating.Keys.OfType<DeviceViewModel>().Where(d => !_vm.Devices.Contains(d)).ToList())
        {
            var w = _floating[item];
            _floating.Remove(item);
            w.CloseForDocking();
        }
        foreach (var d in _vm.Devices.Where(d => d.IsFloating)) OpenFloatingAtSaved(d);
    }

    private void FloatingShow_Click(object sender, RoutedEventArgs e)
    {
        var item = (sender as FrameworkElement)?.DataContext;
        if (item == null) return;
        if (!_floating.TryGetValue(item, out var w)) { OpenFloatingAtSaved(item); return; }
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
    }

    private void FloatingDock_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is { } item) Dock(item);
    }

    // ---- IFloatingHost ----

    public void FloatingMoving(FloatingPanelWindow w)
    {
        bool over = IsOverDockZone(FloatingPanelWindow.CursorScreenPixels());
        if (over) DockHintText.Text = w.Item is DeviceViewModel d ? $"Release to dock {d.Title}" : "Release to dock the channel groups";
        DockHint.Visibility = over ? Visibility.Visible : Visibility.Collapsed;
    }

    public void FloatingMoveEnded(FloatingPanelWindow w)
    {
        DockHint.Visibility = Visibility.Collapsed;
        if (_floating.ContainsValue(w) && IsOverDockZone(FloatingPanelWindow.CursorScreenPixels())) Dock(w.Item);
    }

    public void FloatingClosedByUser(FloatingPanelWindow w)
    {
        // closing the floating window puts the panel back into the main window
        if (_floating.TryGetValue(w.Item, out var cur) && ReferenceEquals(cur, w)) _floating.Remove(w.Item);
        SetFloating(w.Item, false);
    }

    public void FloatingPositionChanged(FloatingPanelWindow w)
    {
        if (w.WindowState != WindowState.Normal) return;
        if (w.Item is DeviceViewModel d) { d.Settings.FloatLeft = w.Left; d.Settings.FloatTop = w.Top; }
        else { _vm.Settings.GroupsFloatLeft = w.Left; _vm.Settings.GroupsFloatTop = w.Top; }
        _vm.RequestSave();
    }
}
