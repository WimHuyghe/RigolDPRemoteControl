using RigolDPRemoteControl.Views;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using RigolDPRemoteControl.Infrastructure;
using RigolDPRemoteControl.Settings;
using RigolDPRemoteControl.ViewModels;
using ScottPlot;
using ScottPlot.AxisPanels;
using ScottPlot.Plottables;
using SPColor = ScottPlot.Color;
using SPI = ScottPlot.Interactivity;
using SPR = ScottPlot.Interactivity.UserActionResponses;

namespace RigolDPRemoteControl.Trend;

/// <summary>
/// Trend of voltage, current and power. Two layouts:
/// <list type="bullet">
/// <item>combined – one plot with three y-axes (V solid / A dashed / W dotted)</item>
/// <item>separate – three stacked plots (V, A, W) that share the time axis</item>
/// </list>
/// Used for the live trend and for the history viewer.
/// Raw samples stay in <see cref="TrendSeries"/>; only a decimated copy of the visible range is handed to ScottPlot,
/// so long trends stay fast and the background sampling is never blocked by rendering.
/// </summary>
public partial class TrendView : UserControl
{
    private sealed class SeriesPlots
    {
        public required TrendSeries Series { get; init; }
        public readonly Scatter[] Lines = new Scatter[3];
        public readonly List<double>[] Xs = { new(), new(), new() };
        public readonly List<double>[] Ys = { new(), new(), new() };
    }

    private static readonly (string Text, double Seconds)[] Windows =
    {
        ("10 s", 10), ("30 s", 30), ("1 min", 60), ("2 min", 120), ("5 min", 300), ("10 min", 600),
        ("30 min", 1800), ("1 h", 3600), ("4 h", 14400), ("12 h", 43200), ("All", 0),
    };
    private static readonly int[] SmoothOptions = { 3, 5, 10, 20, 50, 100, 200, 500 };
    private static readonly string[] Units = { "V", "A", "W" };

    // fixed margins in separate mode so the three data areas line up exactly
    private const int PadLeft = 82, PadRight = 28, PadTop = 8, PadBottomInner = 8, PadBottomLast = 52;
    /// <summary>Space between the legend and the top pane (ScottPlot draws an empty top-axis band there).</summary>
    private const float LegendGap = 12;

    /// <summary>
    /// Stacks the panes vertically so that every pane gets the same data-area height
    /// (the top pane also holds the legend, the bottom pane the time labels).
    /// </summary>
    private sealed class StackedLayout : IMultiplotLayout
    {
        public required Func<int, int, float> VerticalPad { get; init; } // (index, count) -> top + bottom margin

        public PixelRect[] GetSubplotRectangles(SubplotCollection subplots, PixelRect figureRect)
        {
            int n = subplots.Count;
            var pads = Enumerable.Range(0, n).Select(i => VerticalPad(i, n)).ToArray();
            float dataHeight = Math.Max(20, (figureRect.Height - pads.Sum()) / n);
            var rects = new PixelRect[n];
            float y = figureRect.Top;
            for (int i = 0; i < n; i++)
            {
                float h = dataHeight + pads[i];
                rects[i] = new PixelRect(figureRect.Left, figureRect.Right, y + h, y);
                y += h;
            }
            return rects;
        }
    }

    private readonly DispatcherTimer _timer;
    private readonly Dictionary<TrendSeries, SeriesPlots> _plots = new();
    private readonly HashSet<TrendSeries> _hooked = new(); // event handlers attached once per series (plots are rebuilt on layout change)

    // ---- plot structure (rebuilt when switching between combined / separate) ----
    private readonly List<Plot> _panes = new();
    private DateTimeXAxis[] _xs = Array.Empty<DateTimeXAxis>();
    private DateTimeXAxis _x = null!;               // x axis of the first pane (all panes show the same range)
    private IYAxis _yV = null!, _yI = null!, _yP = null!;
    private Annotation _pausedNote = null!, _emptyNote = null!;
    private ScottPlot.Panels.LegendPanel? _legendPanel;
    private bool _separate;
    private float _legendPad;

    private TrendDataSet? _data;
    private TrendSettings _settings = new();
    private ILiveTrendController? _live;
    private Action? _settingsChanged;

    private bool _follow = true;
    private bool _dirty = true;
    private bool _initializing;
    private long _lastVersion = -1;
    private DateTime _lastCompute = DateTime.MinValue;
    private double _lastX0 = double.NaN, _lastX1 = double.NaN;
    private bool _fitAllPending;
    /// <summary>Per y-axis (V, A, W): autoscale temporarily suspended because the user zoomed/panned with the mouse.</summary>
    private readonly bool[] _ySuspended = new bool[3];
    private System.Windows.Point? _dragStart;
    private bool IsZoomed => _ySuspended.Any(s => s) || (_data?.IsLive == true && !_follow);

    /// <summary>The legend is shown above the trend: on the top pane.</summary>
    private Plot LegendPlot => _panes[0];

    private float LegendSpace => Math.Max(0, _legendPad);
    private float TopPad(int i) => PadTop + (i == 0 ? LegendSpace : 0);
    private static float BottomPad(int i, int n) => i == n - 1 ? PadBottomLast : PadBottomInner;
    private PixelPadding PanePadding(int i, int n) => new(PadLeft, PadRight, BottomPad(i, n), TopPad(i));
    private StackedLayout NewStackedLayout() => new() { VerticalPad = (i, n) => TopPad(i) + BottomPad(i, n) };

    public TrendView()
    {
        InitializeComponent();

        ConfigureMouseControls();
        // Any mouse zoom/pan (both directions) pauses following and the y-autoscale of all axes until 'Reset zoom' / 'Follow live'.
        PlotControl.PreviewMouseWheel += (_, _) => { SyncModifierKeys(); UserMovedView(); };
        PlotControl.PreviewMouseDown += (_, e) =>
        {
            SyncModifierKeys();
            var pos = e.GetPosition(PlotControl);
            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 1 && IsNearRuler(pos) && Keyboard.Modifiers == ModifierKeys.None)
            {
                // drag the ruler instead of panning the plot
                _draggingRuler = true;
                PlotControl.UserInputProcessor.Disable();
                PlotControl.CaptureMouse();
                e.Handled = true;
                return;
            }
            if (e.ChangedButton == MouseButton.Right && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
            {
                // CTRL+SHIFT+right-drag: zoom evenly (same factor for time and values)
                BeginEvenZoom(pos);
                e.Handled = true;
                return;
            }
            _dragStart = pos;
            // double-click: back to the live view (history: complete trend) – replaces ScottPlot's benchmark
            if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2) { ResetZoom(); e.Handled = true; }
        };
        PlotControl.PreviewMouseMove += (_, e) =>
        {
            SyncModifierKeys();
            var pos = e.GetPosition(PlotControl);
            if (_draggingRuler) { MoveRuler(TimeAt(pos)); e.Handled = true; return; }
            if (_evenZoom != null) { EvenZoomTo(pos); e.Handled = true; return; }
            PlotControl.Cursor = IsNearRuler(pos) ? Cursors.SizeWE : null;
            bool pressed = e.LeftButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed
                           || e.MiddleButton == MouseButtonState.Pressed;
            if (pressed && _dragStart is { } start && (e.GetPosition(PlotControl) - start).Length > 4)
            {
                _dragStart = null;
                UserMovedView();
            }
        };
        PlotControl.PreviewMouseUp += (_, e) =>
        {
            if (_draggingRuler || _evenZoom != null)
            {
                _draggingRuler = false;
                _evenZoom = null;
                PlotControl.ReleaseMouseCapture();
                PlotControl.UserInputProcessor.Enable();
                e.Handled = true;
                return;
            }
            // a click without dragging moves the ruler to the clicked time
            if (_rulerOn && e.ChangedButton == MouseButton.Left && Keyboard.Modifiers == ModifierKeys.None
                && _dragStart is { } start && (e.GetPosition(PlotControl) - start).Length <= 4)
                MoveRuler(TimeAt(e.GetPosition(PlotControl)));
            _dragStart = null;
        };
        PlotControl.SizeChanged += (_, _) => _dirty = true; // legend wrapping depends on the width

        foreach (var w in Windows) WindowCombo.Items.Add(w.Text);
        foreach (var s in SmoothOptions) SmoothCombo.Items.Add(s.ToString());

        BuildPlots();

        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            ThemeManager.ThemeChanged += ApplyTheme;
            ApplyTheme();
            _timer.Start();
            FitComboWidth(WindowCombo);
            FitComboWidth(SmoothCombo);
        };
        Unloaded += (_, _) =>
        {
            ThemeManager.ThemeChanged -= ApplyTheme;
            _timer.Stop();
        };
    }

    // ================================================================== public API

    /// <summary>Connect the view to a data set. <paramref name="live"/> is null for the history viewer.</summary>
    public void Attach(TrendDataSet data, TrendSettings settings, ILiveTrendController? live, Action? settingsChanged = null)
    {
        _data = data;
        _settings = settings;
        _live = live;
        _settingsChanged = settingsChanged;
        SeriesList.ItemsSource = data.Series;

        _initializing = true;
        // live-only controls; the history viewer shows the name of the opened file instead of the recording file
        var liveOnly = live != null ? Visibility.Visible : Visibility.Collapsed;
        FollowBtn.Visibility = RecordBtn.Visibility = ClearBtn.Visibility = SaveAsBtn.Visibility = liveOnly;
        if (live == null)
        {
            FileText.Text = data.SourceFile != null ? Path.GetFileName(data.SourceFile) : "Trend";
            FileText.ToolTip = data.SourceFile ?? "Trend";
        }
        LoadSettingsIntoUi();
        _initializing = false;

        _follow = live != null;
        FollowBtn.IsChecked = _follow;
        if (live != null)
        {
            live.PropertyChanged += Live_PropertyChanged;
            UpdateRecordButton();
        }
        else _fitAllPending = true;

        _dirty = true;
    }

    private void LoadSettingsIntoUi()
    {
        var settings = _settings;
        int wi = Array.FindIndex(Windows, w => Math.Abs(w.Seconds - settings.WindowSeconds) < 0.5);
        WindowCombo.SelectedIndex = wi >= 0 ? wi : 3;
        SmoothBtn.IsChecked = settings.Smoothing;
        int si = Array.IndexOf(SmoothOptions, settings.SmoothingSamples);
        SmoothCombo.SelectedIndex = si >= 0 ? si : 2;
        SplitBtn.IsChecked = settings.SeparatePlots;
        BuildPlots();
        LoadAxis(settings.Voltage, AutoV, MinV, MaxV, _yV);
        LoadAxis(settings.Current, AutoI, MinI, MaxI, _yI);
        LoadAxis(settings.Power, AutoP, MinP, MaxP, _yP);
    }

    /// <summary>The settings object was changed from outside (a preset was loaded): show them.</summary>
    public void ReloadSettings()
    {
        _initializing = true;
        try { LoadSettingsIntoUi(); }
        finally { _initializing = false; }
        Array.Clear(_ySuspended);
        if (_data?.IsLive == true) { _follow = true; FollowBtn.IsChecked = true; }
        _dirty = true;
    }

    /// <summary>Copies UI state into the settings object (live trend settings are persisted).</summary>
    public void StoreSettings()
    {
        _settings.Voltage.Auto = AutoV.IsChecked == true;
        _settings.Current.Auto = AutoI.IsChecked == true;
        _settings.Power.Auto = AutoP.IsChecked == true;
        _settings.SeparatePlots = _separate;
    }

    // ================================================================== plot structure

    /// <summary>(Re)creates the plot(s): one combined plot with 3 y-axes, or 3 stacked plots sharing the time axis.</summary>
    private void BuildPlots()
    {
        // keep the visible time range when switching layout
        double keepX0 = _x?.Min ?? double.NaN, keepX1 = _x?.Max ?? double.NaN;

        _separate = _settings.SeparatePlots;
        _plots.Clear();
        _panes.Clear();
        _legendPanel = null;
        _legendPad = 0;

        int count = _separate ? 3 : 1;
        for (int i = 0; i < count; i++) _panes.Add(new Plot());

        PlotControl.Reset(_panes[0]);
        var mp = PlotControl.Multiplot;
        while (mp.Subplots.Count > 1) mp.Subplots.RemoveAt(mp.Subplots.Count - 1);
        if (mp.Subplots.Count == 0) mp.Subplots.Add(_panes[0]);
        for (int i = 1; i < count; i++) mp.Subplots.Add(_panes[i]);
        mp.Layout = _separate ? NewStackedLayout() : new ScottPlot.MultiplotLayouts.Rows();
        if (_separate) mp.SharedAxes.ShareX(_panes);

        _xs = new DateTimeXAxis[count];
        for (int i = 0; i < count; i++)
        {
            var x = _panes[i].Axes.DateTimeTicksBottom();
            if (x.TickGenerator is ScottPlot.TickGenerators.DateTimeAutomatic dta)
                dta.LabelFormatter = dt => (_x!.Max - _x.Min) > 2.0 ? dt.ToString("dd-MM HH:mm") : dt.ToString("HH:mm:ss");
            _xs[i] = x;
            _panes[i].Legend.DisplayPlottableLegendItems = false;
            _panes[i].Legend.ShowItemsFromHiddenPlottables = false;
        }
        _x = _xs[0];

        if (_separate)
        {
            _yV = _panes[0].Axes.Left;
            _yI = _panes[1].Axes.Left;
            _yP = _panes[2].Axes.Left;
            _yV.Label.Text = "Voltage [V]";
            _yI.Label.Text = "Current [A]";
            _yP.Label.Text = "Power [W]";
            for (int i = 0; i < count; i++)
            {
                _panes[i].Layout.Fixed(PanePadding(i, count));
                // the time axis is shared: only the bottom pane shows time labels (grid lines stay in every pane)
                if (i < count - 1) _xs[i].TickLabelStyle.IsVisible = false;
            }
        }
        else
        {
            var p = _panes[0];
            _yV = p.Axes.Left;
            _yI = p.Axes.Right;
            _yP = p.Axes.AddRightAxis();
            _yV.Label.Text = "Voltage [V]  (solid)";
            _yI.Label.Text = "Current [A]  (dashed)";
            _yP.Label.Text = "Power [W]  (dotted)";
        }
        SetY(_yV, 0, 10);
        SetY(_yI, 0, 1);
        SetY(_yP, 0, 10);
        if (!double.IsNaN(keepX0) && keepX1 > keepX0) SetX(keepX0, keepX1);


        _pausedNote = _panes[^1].Add.Annotation("", Alignment.LowerCenter);
        _pausedNote.LabelFontSize = 12;
        _pausedNote.IsVisible = false;

        _emptyNote = _panes[0].Add.Annotation("No trend data yet", Alignment.MiddleCenter);
        _emptyNote.LabelFontSize = 16;

        CreateRulerLines();
        UpdateRuler();

        BuildMenu();
        CombinedHint.Visibility = _separate ? Visibility.Collapsed : Visibility.Visible; // line styles only apply to the combined plot
        SeparateHint.Visibility = _separate ? Visibility.Visible : Visibility.Collapsed;
        UpdateLegend();
        ApplyTheme();
        RestoreManualAxes();
        _lastX0 = _lastX1 = double.NaN;
        _dirty = true;
        PlotControl.Refresh();
    }

    private int PaneIndex(int quantity) => _separate ? quantity : 0;
    private IYAxis AxisOf(int quantity) => quantity switch { 0 => _yV, 1 => _yI, _ => _yP };

    private Plot PlotOfAxis(IAxis axis) => _panes.FirstOrDefault(p => p.Axes.GetAxes().Contains(axis)) ?? _panes[0];

    private void SetY(IYAxis axis, double lo, double hi) => PlotOfAxis(axis).Axes.SetLimitsY(lo, hi, axis);

    private void SetX(double x0, double x1)
    {
        for (int i = 0; i < _panes.Count; i++) _panes[i].Axes.SetLimitsX(x0, x1, _xs[i]);
    }

    // ================================================================== rendering

    private void Tick()
    {
        if (_data == null || !IsVisible) return;
        SyncSeries();

        // in separate mode the user may have panned/zoomed any of the panes: keep all time axes identical
        bool viewChanged = false;
        foreach (var x in _xs)
        {
            if (!Same(x.Min, _lastX0) || !Same(x.Max, _lastX1))
            {
                viewChanged = true;
                if (_xs.Length > 1) SetX(x.Min, x.Max);
                break;
            }
        }

        long version = 0;
        foreach (var s in _data.Series) version += s.Version;
        bool newData = version != _lastVersion;
        bool liveDue = newData && (DateTime.Now - _lastCompute).TotalMilliseconds >= 200;

        if (_dirty || viewChanged || liveDue || (newData && !_data.IsLive))
        {
            _lastVersion = version;
            Recompute();
        }
    }

    private static bool Same(double a, double b) => Math.Abs(a - b) <= Math.Max(1e-12, Math.Abs(a) * 1e-12);

    private void SyncSeries()
    {
        if (_data == null) return;
        // series removed from the data set (power supply removed / preset loaded): remove their lines
        if (_plots.Count > _data.Series.Count || _plots.Keys.Any(k => !_data.Series.Contains(k)))
        {
            foreach (var gone in _plots.Keys.Where(k => !_data.Series.Contains(k)).ToList())
            {
                for (int q = 0; q < 3; q++) _panes[PaneIndex(q)].Remove(_plots[gone].Lines[q]);
                _plots.Remove(gone);
            }
            _dirty = true;
        }
        bool added = false;
        foreach (var s in _data.Series)
        {
            if (_plots.ContainsKey(s)) continue;
            var sp = new SeriesPlots { Series = s };
            for (int q = 0; q < 3; q++)
            {
                int pane = PaneIndex(q);
                var line = _panes[pane].Add.ScatterLine(sp.Xs[q], sp.Ys[q]);
                line.Axes.XAxis = _xs[pane];
                line.Axes.YAxis = AxisOf(q);
                line.LineWidth = q == 0 || _separate ? 1.8f : 1.5f;
                // in the combined plot the line style tells the quantities apart; separate plots use solid lines
                line.LinePattern = _separate || q == 0 ? LinePattern.Solid : q == 1 ? LinePattern.Dashed : LinePattern.Dotted;
                line.MarkerSize = 0;
                line.IsVisible = false;
                sp.Lines[q] = line;
            }
            if (_hooked.Add(s))
            {
                s.VisibilityChanged += () => _dirty = true;
                s.PropertyChanged += (_, e) => { if (e.PropertyName is nameof(TrendSeries.Label) or nameof(TrendSeries.ColorHex)) _dirty = true; };
            }
            _plots[s] = sp;
            added = true;
        }
        if (added)
        {
            for (int i = 0; i < _rulerLines.Count; i++) _panes[i].MoveToTop(_rulerLines[i]);
            _panes[0].MoveToTop(_emptyNote);
            _panes[^1].MoveToTop(_pausedNote);
            _dirty = true;
        }
    }

    private (double first, double last) DataBounds()
    {
        double first = double.PositiveInfinity, last = double.NegativeInfinity;
        if (_data == null) return (first, last);
        foreach (var s in _data.Series)
        {
            lock (s.Sync)
            {
                if (s.T.Count == 0) continue;
                first = Math.Min(first, s.T[0]);
                last = Math.Max(last, s.T[^1]);
            }
        }
        return (first, last);
    }

    private double WindowDays => _settings.WindowSeconds / 86400.0;

    private void Recompute()
    {
        if (_data == null) return;
        _dirty = false;
        _lastCompute = DateTime.Now;
        var (first, last) = DataBounds();
        bool hasData = !double.IsInfinity(first);

        // ---- x range ----
        if (_fitAllPending && hasData)
        {
            _fitAllPending = false;
            double pad = Math.Max((last - first) * 0.01, 1.0 / 86400);
            SetX(first - pad, last + pad);
        }
        else if (_data.IsLive && _follow)
        {
            double x1, x0;
            if (hasData)
            {
                if (_settings.WindowSeconds <= 0) { x0 = first; x1 = last + Math.Max((last - first) * 0.02, 1.0 / 86400); }
                else { x1 = last + WindowDays * 0.02; x0 = x1 - WindowDays; }
            }
            else
            {
                x1 = DateTime.Now.ToOADate();
                x0 = x1 - (_settings.WindowSeconds > 0 ? WindowDays : 60 / 86400.0);
            }
            SetX(x0, x1);
        }
        double vx0 = _x.Min, vx1 = _x.Max;
        double w = Math.Max(vx1 - vx0, 1e-9);
        double ext0 = vx0 - w, ext1 = vx1 + w;
        int buckets = Math.Max(200, (int)(PlotControl.ActualWidth * 1.5));
        int smoothN = _settings.Smoothing ? _settings.SmoothingSamples : 0;
        string suffix = _settings.Smoothing ? $"  (smoothed {smoothN})" : "";

        var mins = new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity };
        var maxs = new[] { double.NegativeInfinity, double.NegativeInfinity, double.NegativeInfinity };
        var axisUsed = new bool[3];
        var legend = LegendPlot.Legend.ManualItems;
        legend.Clear();

        foreach (var sp in _plots.Values)
        {
            var s = sp.Series;
            var color = SafeColor(s.ColorHex);
            if (s.ShowVoltage || s.ShowCurrent || s.ShowPower)
            {
                // one legend entry per channel (colour = channel)
                var shown = string.Join("/", new[] { s.ShowVoltage ? "V" : null, s.ShowCurrent ? "A" : null, s.ShowPower ? "W" : null }
                    .Where(u => u != null));
                legend.Add(new LegendItem
                {
                    LabelText = $"{s.Label}  [{shown}]{suffix}",
                    LineColor = color,
                    LineWidth = 3,
                    MarkerSize = 0,
                });
            }
            for (int q = 0; q < 3; q++)
            {
                var line = sp.Lines[q];
                var quantity = (Quantity)q;
                if (!s.IsShown(quantity))
                {
                    line.IsVisible = false;
                    sp.Xs[q].Clear(); sp.Ys[q].Clear();
                    continue;
                }
                (double mn, double mx) r;
                lock (s.Sync)
                    r = Decimator.Build(s.T, s.Values(quantity), ext0, ext1, vx0, vx1, buckets, smoothN, sp.Xs[q], sp.Ys[q]);
                line.IsVisible = sp.Xs[q].Count > 0;
                line.Color = color;
                line.LegendText = $"{s.Label} [{Units[q]}]{suffix}";
                if (line.IsVisible) axisUsed[q] = true;
                if (r.mn < mins[q]) mins[q] = r.mn;
                if (r.mx > maxs[q]) maxs[q] = r.mx;
            }
        }

        // ---- y axes ----
        ApplyAutoY(_yV, AutoV.IsChecked == true && !_ySuspended[0], mins[0], maxs[0], 0.5);
        ApplyAutoY(_yI, AutoI.IsChecked == true && !_ySuspended[1], mins[1], maxs[1], 0.05);
        ApplyAutoY(_yP, AutoP.IsChecked == true && !_ySuspended[2], mins[2], maxs[2], 0.5);
        ShowAutoRanges();
        UpdateRuler();
        var timeLabel = "Time  " + DateTime.FromOADate(Math.Clamp(vx1, 0, 2958465)).ToString("dddd d MMMM yyyy");
        for (int i = 0; i < _xs.Length; i++) _xs[i].LabelText = i == _xs.Length - 1 ? timeLabel : "";
        if (!_separate)
        {
            _yP.IsVisible = axisUsed[2] || !hasData;
            _yI.IsVisible = axisUsed[1] || !hasData;
        }
        else UpdateSeparateLegendPadding();

        // ---- annotations ----
        SmoothBanner.Visibility = _settings.Smoothing ? Visibility.Visible : Visibility.Collapsed;
        SmoothBannerText.Text = $"SMOOTHING ON  –  moving average over {smoothN} samples (display only – recorded data is raw)";
        _pausedNote.IsVisible = IsZoomed;
        _pausedNote.Text = _data.IsLive && !_follow
            ? "Zoomed: live view + autoscale paused (recording continues). Double-click = reset"
            : "Zoomed: autoscale paused. Double-click = reset";
        _emptyNote.IsVisible = !hasData;
        _emptyNote.Text = _data.IsLive
            ? (_live?.IsRecording == true ? "Waiting for samples…" : "Trend recording is stopped – press Start")
            : "This trend file contains no samples";

        _lastX0 = _x.Min;
        _lastX1 = _x.Max;
        PlotControl.Refresh();
    }

    /// <summary>Separate mode uses fixed margins (aligned panes); the top pane needs extra room for the legend.</summary>
    private void UpdateSeparateLegendPadding()
    {
        float want = _settings.ShowLegend ? MeasureLegendHeight() : 0;
        if (float.IsNaN(want) || Math.Abs(want - _legendPad) < 2) return;
        _legendPad = want;
        LegendPlot.Layout.Fixed(PanePadding(0, _panes.Count));
        PlotControl.Multiplot.Layout = NewStackedLayout();
    }

    /// <summary>
    /// Exact height needed for the horizontally wrapped legend above the top pane. ScottPlot wraps the legend against
    /// the width of the data area; <see cref="Legend.GetLayout"/> gives the wrapped size for that width.
    /// (Its own panel measurement lags one render behind, which made the legend overlap the plot.)
    /// </summary>
    private float MeasureLegendHeight()
    {
        var lg = LegendPlot.Legend;
        if (lg.ManualItems.Count == 0) return 0;
        float figureWidth = LegendPlot.RenderManager.LastRender.FigureRect.Width;
        if (!(figureWidth > 0)) figureWidth = (float)(PlotControl.ActualWidth * PlotControl.DisplayScale); // before the first render
        float dataWidth = Math.Max(60, figureWidth - PadLeft - PadRight);
        using var paint = Paint.NewDisposablePaint();
        var layout = lg.GetLayout(new PixelSize(dataWidth, 100_000), paint);
        float pad = _legendPanel != null ? _legendPanel.Padding.Top + _legendPanel.Padding.Bottom : 0;
        return layout.LegendRect.Height + pad + LegendGap;
    }

    private void ApplyAutoY(IYAxis axis, bool auto, double mn, double mx, double flatPad)
    {
        if (!auto || double.IsInfinity(mn) || double.IsInfinity(mx)) return;
        double span = mx - mn;
        double pad = span < Math.Max(1e-6, Math.Abs(mx) * 1e-4)
            ? Math.Max(Math.Abs(mx) * 0.05, flatPad)   // flat line: centre it with a sensible range
            : span * 0.08;
        SetY(axis, mn - pad, mx + pad);
    }

    private static SPColor SafeColor(string hex)
    {
        try { return SPColor.FromHex(hex); } catch { return SPColor.FromHex("#808080"); }
    }

    private void ApplyTheme()
    {
        if (_panes.Count == 0) return;
        bool dark = ThemeManager.IsDark;
        var fig = SPColor.FromHex(dark ? "#222227" : "#FFFFFF");
        var data = SPColor.FromHex(dark ? "#1A1A1E" : "#FFFFFF");
        var fg = SPColor.FromHex(dark ? "#D6D6DC" : "#222222");
        var grid = SPColor.FromHex(dark ? "#33333B" : "#E4E4EA");
        foreach (var plot in _panes)
        {
            plot.FigureBackground.Color = fig;
            plot.DataBackground.Color = data;
            plot.Axes.Color(fg);
            plot.Grid.MajorLineColor = grid;
            plot.Legend.BackgroundColor = SPColor.FromHex(dark ? "#2A2A31" : "#FAFAFA");
            plot.Legend.FontColor = fg;
            plot.Legend.OutlineColor = SPColor.FromHex(dark ? "#44444E" : "#CCCCCC");
        }
        _pausedNote.LabelBackgroundColor = SPColor.FromHex(dark ? "#2A2A31" : "#F3F3F5");
        _pausedNote.LabelFontColor = fg;
        _pausedNote.LabelBorderColor = SPColor.FromHex(dark ? "#44444E" : "#CCCCCC");
        _emptyNote.LabelBackgroundColor = SPColor.FromHex("#00000000");
        _emptyNote.LabelBorderColor = SPColor.FromHex("#00000000");
        _emptyNote.LabelFontColor = SPColor.FromHex(dark ? "#77777F" : "#9A9AA2");
        foreach (var vl in _rulerLines)
        {
            vl.Color = fg;              // neutral colour, distinct from the channel colours
            vl.LabelStyle.ForeColor = fig;
            vl.LabelStyle.BackgroundColor = fg;
            vl.LabelStyle.FontSize = 12;
        }
        _dirty = true;
        PlotControl.Refresh();
    }

    private void UpdateLegend()
    {
        var plot = LegendPlot;
        if (_legendPanel != null)
        {
            plot.Axes.Remove(_legendPanel);
            _legendPanel = null;
        }
        if (_settings.ShowLegend)
        {
            _legendPanel = plot.ShowLegend(Edge.Top); // above the trend(s)
            plot.Legend.ShowItemsFromHiddenPlottables = false;
            plot.Legend.DisplayPlottableLegendItems = false;
            plot.Legend.Orientation = ScottPlot.Orientation.Horizontal;
            plot.Legend.TightHorizontalWrapping = true;
        }
        else plot.HideLegend();
        _legendPad = -1; // force margin update in separate mode
        _dirty = true;
    }

    private void UserMovedView()
    {
        if (_data?.IsLive == true && _follow)
        {
            _follow = false;
            FollowBtn.IsChecked = false;
        }
        // the mouse zooms/pans x and y: stop autoscale from undoing the user's y zoom
        for (int q = 0; q < 3; q++) _ySuspended[q] = true;
        _dirty = true;
    }

    /// <summary>
    /// Undo mouse zoom/pan: live trend → follow the newest samples again; history → show everything.
    /// Autoscale is re-applied to the axes that have Auto ticked, manual axes return to their min/max.
    /// </summary>
    private void ResetZoom()
    {
        Array.Clear(_ySuspended);
        if (_data?.IsLive == true)
        {
            _follow = true;
            FollowBtn.IsChecked = true;
        }
        else _fitAllPending = true;
        RestoreManualAxes();
        _dirty = true;
    }

    private void RestoreManualAxes()
    {
        foreach (var tag in new[] { "V", "I", "P" })
        {
            var (a, auto, _, _, axis) = AxisFor(tag);
            if (auto.IsChecked != true && a.Max > a.Min) SetY(axis, a.Min, a.Max);
        }
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e) => ResetZoom();

    private void Split_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        bool sep = SplitBtn.IsChecked == true;
        if (sep == _separate) return;
        _settings.SeparatePlots = sep;
        BuildPlots();
        _settingsChanged?.Invoke();
    }

    /// <summary>
    /// Sizes a drop-down to its longest entry, so every choice is fully readable (a ComboBox otherwise only fits the selected entry).
    /// The width of the box chrome (padding, arrow) is measured from the control itself, so it follows the theme.
    /// </summary>
    private static void FitComboWidth(ComboBox cb)
    {
        if (cb.Items.Count == 0) return;
        var tf = new System.Windows.Media.Typeface(cb.FontFamily, cb.FontStyle, cb.FontWeight, cb.FontStretch);
        double ppd = System.Windows.Media.VisualTreeHelper.GetDpi(cb).PixelsPerDip;
        double TextWidth(string s) => new System.Windows.Media.FormattedText(s, System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, tf, cb.FontSize, System.Windows.Media.Brushes.Black, ppd).WidthIncludingTrailingWhitespace;

        cb.Width = double.NaN; // natural size = chrome + selected entry
        cb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double chrome = cb.DesiredSize.Width - TextWidth(cb.SelectedItem?.ToString() ?? "");
        double longest = cb.Items.Cast<object>().Max(o => TextWidth(o?.ToString() ?? ""));
        cb.Width = Math.Ceiling(longest + chrome + 4);
    }

    // ================================================================== context menu (right mouse button)

    private void BuildMenu()
    {
        var menu = PlotControl.Menu;
        if (menu == null) return;
        menu.Clear();
        menu.Add("Save image…", _ => SaveImage());
        menu.Add("Copy image to clipboard", _ => CopyImage());
        menu.AddSeparator();
        menu.Add("Export CSV – all data…", _ => ExportCsv(false));
        menu.Add("Export CSV – visible time range…", _ => ExportCsv(true));
        menu.AddSeparator();
        menu.Add("Reset zoom (back to live view / full trend)   [double-click]", _ => ResetZoom());
        menu.Add("Fit all (complete trend, autoscaled)   [middle-click]", _ => FitAll());
        menu.Add("Autoscale all axes", _ => AutoscaleAll());
        menu.AddSeparator();
        // the texts show what the item will do (the menu is rebuilt when the layout or ruler changes)
        menu.Add(_separate ? "Combined plot (V, A, W in one plot)" : "Separate V / A / W plots", _ => SplitBtn.IsChecked = !_separate);
        menu.Add(_rulerOn ? "Hide ruler" : "Show ruler", _ => RulerBtn.IsChecked = !_rulerOn);
        menu.Add("Show / hide legend", _ => { _settings.ShowLegend = !_settings.ShowLegend; UpdateLegend(); _settingsChanged?.Invoke(); });
        menu.Add("Clear trend", _ => Clear_Click(this, new RoutedEventArgs()));
        menu.AddSeparator();
        menu.Add("Trend navigation (mouse / keys)…", _ => Views.TrendNavigationWindow.ShowFor(Window.GetWindow(this)));
    }

    private void SaveImage()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save trend image",
            Filter = _separate
                ? "PNG image|*.png|JPEG image|*.jpg|Bitmap|*.bmp"
                : "PNG image|*.png|JPEG image|*.jpg|SVG vector image|*.svg|Bitmap|*.bmp",
            InitialDirectory = AppPaths.TrendDir,
            FileName = $"Trend_{DateTime.Now:yyyyMMdd_HHmmss}.png",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            var (w, h) = ImageSize();
            var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            if (ext == ".svg") { _panes[0].SaveSvg(dlg.FileName, w, h); return; }
            using var img = PlotControl.Multiplot.Render(w, h); // all panes in one image
            switch (ext)
            {
                case ".jpg": case ".jpeg": img.SaveJpeg(dlg.FileName, 95); break;
                case ".bmp": img.SaveBmp(dlg.FileName); break;
                default: img.SavePng(dlg.FileName); break;
            }
        }
        catch (Exception ex) { ShowError("Saving image failed: " + ex.Message); }
    }

    private void CopyImage()
    {
        try
        {
            var (w, h) = ImageSize();
            using var img = PlotControl.Multiplot.Render(w, h);
            var bytes = img.GetImageBytes(ImageFormat.Png, 100);
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            Clipboard.SetImage(bmp);
        }
        catch (Exception ex) { ShowError("Copy failed: " + ex.Message); }
    }

    private (int w, int h) ImageSize()
    {
        var src = PresentationSource.FromVisual(this);
        double scale = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        return (Math.Max(400, (int)(PlotControl.ActualWidth * scale)), Math.Max(300, (int)(PlotControl.ActualHeight * scale)));
    }

    private void ExportCsv(bool visibleOnly)
    {
        if (_data == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = visibleOnly ? "Export visible time range to CSV" : "Export trend to CSV",
            Filter = "CSV file|*.csv",
            InitialDirectory = AppPaths.TrendDir,
            FileName = $"TrendExport_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        try
        {
            string? note = _settings.Smoothing ? "Note: values are raw samples (smoothing was only applied to the display)" : null;
            if (visibleOnly) TrendFile.Export(dlg.FileName, _data.Series, _x.Min, _x.Max, note);
            else TrendFile.Export(dlg.FileName, _data.Series, note: note);
        }
        catch (Exception ex) { ShowError("Export failed: " + ex.Message); }
    }

    private void AutoscaleAll()
    {
        AutoV.IsChecked = AutoI.IsChecked = AutoP.IsChecked = true;
        _settings.Voltage.Auto = _settings.Current.Auto = _settings.Power.Auto = true;
        foreach (var tag in new[] { "V", "I", "P" }) SetRangeEditable(tag, false);
        ResetZoom();
        _settingsChanged?.Invoke();
    }

    private void ShowError(string msg)
        => ThemedMessageBox.Show(Window.GetWindow(this), msg, "Trend", MessageBoxButton.OK, MessageBoxImage.Warning);

    // ================================================================== toolbar handlers

    private void Live_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ILiveTrendController.IsRecording) or nameof(ILiveTrendController.RecordingFileName))
            Dispatcher.BeginInvoke(UpdateRecordButton);
    }

    private void UpdateRecordButton()
    {
        if (_live == null) return;
        bool rec = _live.IsRecording;
        RecordBtn.IsChecked = rec;
        RecordText.Text = rec ? "Recording" : "Start";
        RecordIcon.Text = rec ? "" : ""; // Stop / Record glyphs
        RecordBtn.SetResourceReference(BackgroundProperty, rec ? "ErrorBrush" : "OnBrush");
        RecordBtn.ToolTip = rec
            ? "Recording – click to stop. The data stays visible."
            : "Start recording. This clears the trend and overwrites the default trend file (LiveTrend.csv).";
        FileText.Text = rec ? _live.RecordingFileName : "(not recording)";
        FileText.ToolTip = rec ? $"Trend file that is being recorded (in the trend folder):\n{_live.RecordingFileName}" : "Recording is stopped – press Start";
        _dirty = true;
    }

    private bool DefaultFileWouldBeLost()
        => _live != null && _data?.HasData == true &&
           string.Equals(_live.RecordingFileName, Path.GetFileName(AppPaths.DefaultTrendFile), StringComparison.OrdinalIgnoreCase);

    private void RecordBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_live == null) return;
        if (_live.IsRecording) _live.StopRecording();
        else
        {
            if (_data?.HasData == true &&
                ThemedMessageBox.Show(Window.GetWindow(this),
                    "Starting a new recording clears the current trend and overwrites the default trend file (LiveTrend.csv).\n\nUse 'Save as…' first to keep it. Continue?",
                    "Start trend", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                UpdateRecordButton();
                return;
            }
            _live.StartRecording();
            _follow = true;
            FollowBtn.IsChecked = true;
        }
        UpdateRecordButton();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_live == null || _data == null) return;
        if (DefaultFileWouldBeLost() || (_data.HasData && !_live.IsRecording))
        {
            if (ThemedMessageBox.Show(Window.GetWindow(this), "Clear the trend? Unsaved trend data in LiveTrend.csv is overwritten.",
                    "Clear trend", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        }
        _live.ClearTrend();
        _follow = true;
        FollowBtn.IsChecked = true;
        _dirty = true;
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_live == null) return;
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save trend as",
            Filter = "Trend file (CSV)|*.csv",
            InitialDirectory = AppPaths.TrendDir,
            FileName = $"Trend_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        _live.SaveTrendAs(dlg.FileName);
        UpdateRecordButton();
    }

    private async void History_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open trend",
            Filter = "Trend file (CSV)|*.csv|All files|*.*",
            InitialDirectory = AppPaths.TrendDir,
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        var file = dlg.FileName;
        try
        {
            Cursor = Cursors.Wait;
            var data = await Task.Run(() => TrendFile.Load(file));
            var settings = new TrendSettings
            {
                WindowSeconds = 0,
                Smoothing = _settings.Smoothing,
                SmoothingSamples = _settings.SmoothingSamples,
                ShowLegend = _settings.ShowLegend,
                SeparatePlots = _separate,
            };
            new TrendHistoryWindow(data, settings).Show();
        }
        catch (Exception ex) { ShowError($"Cannot open {Path.GetFileName(file)}: {ex.Message}"); }
        finally { Cursor = null; }
    }

    private void Follow_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        if (FollowBtn.IsChecked == true)
        {
            if (!_follow) ResetZoom(); // back to live view: autoscaling applies again
        }
        else _follow = false;
        _dirty = true;
    }

    private void WindowCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || WindowCombo.SelectedIndex < 0) return;
        _settings.WindowSeconds = Windows[WindowCombo.SelectedIndex].Seconds;
        if (_data?.IsLive == true) ResetZoom();
        else
        {
            var (first, last) = DataBounds();
            if (!double.IsInfinity(first))
            {
                if (_settings.WindowSeconds <= 0) _fitAllPending = true;
                else
                {
                    double x1 = Math.Min(_x.Max, last);
                    SetX(x1 - WindowDays, x1);
                }
            }
        }
        _settingsChanged?.Invoke();
        _dirty = true;
    }

    private void FitAll_Click(object sender, RoutedEventArgs e) => FitAll();

    /// <summary>Complete trend with autoscaled axes (right-click menu "Fit all" and middle-click).</summary>
    private void FitAll()
    {
        if (_data?.IsLive == true) { _follow = false; FollowBtn.IsChecked = false; }
        Array.Clear(_ySuspended); // fit all = whole time range with autoscaled y axes
        _fitAllPending = true;
        _dirty = true;
    }

    private void Smooth_Click(object sender, RoutedEventArgs e)
    {
        _settings.Smoothing = SmoothBtn.IsChecked == true;
        _settingsChanged?.Invoke();
        _dirty = true;
    }

    private void SmoothCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || SmoothCombo.SelectedIndex < 0) return;
        _settings.SmoothingSamples = SmoothOptions[SmoothCombo.SelectedIndex];
        _settingsChanged?.Invoke();
        _dirty = true;
    }

    // ================================================================== axis scaling

    /// <summary>Axis limit as text, with a number of decimals that suits the axis span (invariant culture).</summary>
    private static string FormatLimit(double v, double span)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) return "";
        int decimals = span > 0 && !double.IsInfinity(span) ? Math.Clamp(3 - (int)Math.Floor(Math.Log10(span)), 0, 6) : 3;
        var s = Math.Round(v, decimals).ToString("0." + new string('#', Math.Max(1, decimals)), Num.Inv);
        return s == "-0" ? "0" : s;
    }

    private StackPanel RangeOf(string? tag) => tag switch { "I" => RangeI, "P" => RangeP, _ => RangeV };

    /// <summary>The min/max fields can only be edited for manual scaling; with Auto they are clearly greyed out.</summary>
    private void SetRangeEditable(string? tag, bool editable)
    {
        var r = RangeOf(tag);
        r.IsEnabled = editable;
        r.Opacity = editable ? 1.0 : 0.35;
    }

    /// <summary>While Auto is ticked the min/max fields follow the actual axis range, so unticking Auto keeps that scale.</summary>
    private void ShowAutoRanges()
    {
        foreach (var tag in new[] { "V", "I", "P" })
        {
            var (_, auto, min, max, axis) = AxisFor(tag);
            if (auto.IsChecked != true) continue;
            double span = axis.Max - axis.Min;
            min.Text = FormatLimit(axis.Min, span);
            max.Text = FormatLimit(axis.Max, span);
        }
    }

    private void LoadAxis(AxisSettings a, CheckBox auto, TextBox min, TextBox max, IYAxis axis)
    {
        auto.IsChecked = a.Auto;
        min.Text = FormatLimit(a.Min, a.Max - a.Min);
        max.Text = FormatLimit(a.Max, a.Max - a.Min);
        SetRangeEditable(auto.Tag as string, !a.Auto);
        if (!a.Auto && a.Max > a.Min) SetY(axis, a.Min, a.Max);
    }

    private (AxisSettings a, CheckBox auto, TextBox min, TextBox max, IYAxis axis) AxisFor(object? tag) => (tag as string) switch
    {
        "I" => (_settings.Current, AutoI, MinI, MaxI, _yI),
        "P" => (_settings.Power, AutoP, MinP, MaxP, _yP),
        _ => (_settings.Voltage, AutoV, MinV, MaxV, _yV),
    };

    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string;
        var (a, auto, min, max, axis) = AxisFor(tag);
        a.Auto = auto.IsChecked == true;
        SetRangeEditable(tag, !a.Auto);
        if (a.Auto) _ySuspended[tag == "I" ? 1 : tag == "P" ? 2 : 0] = false; // ticking Auto re-applies autoscale now
        else
        {
            // manual scaling starts from exactly the scale that is shown now (the fields followed the autoscale)
            double span = axis.Max - axis.Min;
            min.Text = FormatLimit(axis.Min, span);
            max.Text = FormatLimit(axis.Max, span);
            if (Num.TryParseUser(min.Text, out var mn) && Num.TryParseUser(max.Text, out var mx) && mx > mn)
            {
                a.Min = mn;
                a.Max = mx;
                SetY(axis, mn, mx);
            }
        }
        _settingsChanged?.Invoke();
        _dirty = true;
    }

    private void AxisBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { ApplyManualAxis(sender); e.Handled = true; }
    }

    private void AxisBox_LostFocus(object sender, RoutedEventArgs e) => ApplyManualAxis(sender);

    private void ApplyManualAxis(object sender)
    {
        var (a, auto, min, max, axis) = AxisFor((sender as FrameworkElement)?.Tag);
        if (auto.IsChecked == true) return;
        if (Num.TryParseUser(min.Text, out var mn) && Num.TryParseUser(max.Text, out var mx) && mx > mn)
        {
            a.Min = mn;
            a.Max = mx;
            SetY(axis, mn, mx);
            _settingsChanged?.Invoke();
            _dirty = true;
        }
        else
        {
            min.Text = FormatLimit(a.Min, a.Max - a.Min);
            max.Text = FormatLimit(a.Max, a.Max - a.Min);
        }
    }

    // ================================================================== ruler

    private readonly List<VerticalLine> _rulerLines = new();
    private bool _rulerOn;
    private double _rulerX = double.NaN;
    private bool _draggingRuler;

    /// <summary>One vertical line per pane (all at the same time); only the bottom one carries the time label.</summary>
    private void CreateRulerLines()
    {
        _rulerLines.Clear();
        for (int i = 0; i < _panes.Count; i++)
        {
            var vl = _panes[i].Add.VerticalLine(0);
            vl.Axes.XAxis = _xs[i];
            vl.Axes.YAxis = _panes[i].Axes.Left;
            vl.LineWidth = 1.5f;
            vl.LinePattern = LinePattern.Dashed;
            vl.EnableAutoscale = false;
            vl.ExcludeFromLegend = true;
            vl.IsVisible = false;
            _rulerLines.Add(vl);
        }
    }

    private Pixel ToPixel(System.Windows.Point p)
        => new((float)(p.X * PlotControl.DisplayScale), (float)(p.Y * PlotControl.DisplayScale));

    private double TimeAt(System.Windows.Point p) => _panes[0].GetCoordinates(ToPixel(p), _xs[0], _panes[0].Axes.Left).X;

    private bool IsNearRuler(System.Windows.Point p)
    {
        if (!_rulerOn || double.IsNaN(_rulerX)) return false;
        float lineX = _panes[0].GetPixel(new Coordinates(_rulerX, 0), _xs[0], _panes[0].Axes.Left).X;
        return Math.Abs(lineX - ToPixel(p).X) <= 6 * PlotControl.DisplayScale;
    }

    private void MoveRuler(double x)
    {
        _rulerX = x;
        UpdateRuler();
        PlotControl.Refresh();
    }

    private void Ruler_Click(object sender, RoutedEventArgs e)
    {
        _rulerOn = RulerBtn.IsChecked == true;
        if (_rulerOn)
        {
            // (re)place the ruler in the middle of the view when it is not visible
            if (double.IsNaN(_rulerX) || _rulerX < _x.Min || _rulerX > _x.Max) _rulerX = (_x.Min + _x.Max) / 2;
            SeriesBtn.IsChecked = true; // the values are shown in the series panel
        }
        BuildMenu();
        UpdateRuler();
        PlotControl.Refresh();
    }

    /// <summary>Positions the ruler lines and shows the values of every plotted series at the ruler time.</summary>
    private void UpdateRuler()
    {
        bool show = _rulerOn && !double.IsNaN(_rulerX);
        for (int i = 0; i < _rulerLines.Count; i++)
        {
            var vl = _rulerLines[i];
            vl.IsVisible = show;
            if (!show) continue;
            vl.X = _rulerX;
            vl.Text = i == _rulerLines.Count - 1 ? DateTime.FromOADate(_rulerX).ToString("HH:mm:ss.f") : "";
        }
        RulerHeader.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (_data == null) return;
        int smoothN = _settings.Smoothing ? _settings.SmoothingSamples : 0;
        foreach (var s in _data.Series) s.RulerText = show ? ReadoutAt(s, _rulerX, smoothN) : "";
        if (show)
        {
            RulerTimeRun.Text = "Ruler  " + DateTime.FromOADate(_rulerX).ToString("dd-MM-yyyy  HH:mm:ss.fff");
            RulerNote.Text = smoothN > 0
                ? $"Values below each series: smoothed over {smoothN} samples, as plotted."
                : "Values below each series: sample nearest to the ruler.";
        }
    }

    private static string ReadoutAt(TrendSeries s, double x, int smoothN)
    {
        if (!s.ShowVoltage && !s.ShowCurrent && !s.ShowPower) return "";
        lock (s.Sync)
        {
            int n = s.T.Count;
            if (n == 0 || x < s.T[0] || x > s.T[^1]) return "no data at the ruler";
            int i = s.T.BinarySearch(x);
            if (i < 0)
            {
                i = ~i;
                if (i >= n) i = n - 1;
                else if (i > 0 && x - s.T[i - 1] < s.T[i] - x) i--;
            }
            int h = smoothN > 1 ? smoothN / 2 : 0;
            int a = Math.Max(0, i - h), b = Math.Min(n - 1, i + h);
            double Avg(List<double> v)
            {
                double sum = 0;
                for (int k = a; k <= b; k++) sum += v[k];
                return sum / (b - a + 1);
            }
            var parts = new List<string>(3);
            if (s.ShowVoltage) parts.Add(Num.F(Avg(s.V), 3) + " V");
            if (s.ShowCurrent) parts.Add(Num.F(Avg(s.I), 4) + " A");
            if (s.ShowPower) parts.Add(Num.F(Avg(s.P), 3) + " W");
            return string.Join("   ", parts);
        }
    }

    // ================================================================== mouse controls

    /// <summary>
    /// Mouse controls as in ScottPlot 4:
    /// left-drag pan (CTRL: horizontal only, SHIFT: vertical only), right-drag zoom (CTRL: horizontal, SHIFT: vertical,
    /// CTRL+SHIFT: evenly – handled in <see cref="BeginEvenZoom"/>), middle-drag or ALT+left-drag zoom to the selected region,
    /// wheel zoom at the cursor, right-click menu, middle-click fit all. Double-click returns to the live view (no benchmark).
    /// </summary>
    private void ConfigureMouseControls()
    {
        var uip = PlotControl.UserInputProcessor;
        var r = uip.UserActionResponses;
        r.Clear(); // replace ScottPlot's default responses (incl. the double-click benchmark)

        r.Add(new SPR.MouseDragZoomRectangle(SPI.StandardMouseButtons.Middle)
        {
            SecondaryMouseButton = SPI.StandardMouseButtons.Left,
            SecondaryKey = SPI.StandardKeys.Alt,
        });

        var pan = new SPR.MouseDragPan(SPI.StandardMouseButtons.Left);
        pan.KeysThatLockY.Clear(); pan.KeysThatLockY.Add(SPI.StandardKeys.Control); // CTRL: pan horizontally
        pan.KeysThatLockX.Clear(); pan.KeysThatLockX.Add(SPI.StandardKeys.Shift);   // SHIFT: pan vertically
        r.Add(pan);

        var zoom = new SPR.MouseDragZoom(SPI.StandardMouseButtons.Right);
        zoom.KeysThatLockY.Clear(); zoom.KeysThatLockY.Add(SPI.StandardKeys.Control); // CTRL: zoom horizontally
        zoom.KeysThatLockX.Clear(); zoom.KeysThatLockX.Add(SPI.StandardKeys.Shift);   // SHIFT: zoom vertically
        r.Add(zoom);

        r.Add(new SPR.MouseWheelZoom(SPI.StandardKeys.Shift, SPI.StandardKeys.Control));
        r.Add(new SPR.SingleClickContextMenu(SPI.StandardMouseButtons.Right));
        r.Add(new SPR.SingleClickResponse(SPI.StandardMouseButtons.Middle, (_, _) => Dispatcher.BeginInvoke(FitAll)));
    }

    /// <summary>
    /// ScottPlot only sees modifier keys when the plot has keyboard focus; copy the real state on every mouse event,
    /// so CTRL / SHIFT / ALT always work while the mouse is over the plot.
    /// </summary>
    private void SyncModifierKeys()
    {
        var ks = PlotControl.UserInputProcessor.KeyState;
        ks.Reset();
        var m = Keyboard.Modifiers;
        if (m.HasFlag(ModifierKeys.Control)) ks.Add(SPI.StandardKeys.Control);
        if (m.HasFlag(ModifierKeys.Shift)) ks.Add(SPI.StandardKeys.Shift);
        if (m.HasFlag(ModifierKeys.Alt)) ks.Add(SPI.StandardKeys.Alt);
    }

    private sealed class EvenZoomState
    {
        public System.Windows.Point Start;
        public double X0, X1, AnchorX;
        public (IYAxis Axis, double Min, double Max, double Anchor)[] Y = Array.Empty<(IYAxis, double, double, double)>();
    }

    private EvenZoomState? _evenZoom;

    /// <summary>CTRL+SHIFT+right-drag: zoom time and value axes with the same factor around the mouse position.</summary>
    private void BeginEvenZoom(System.Windows.Point pos)
    {
        var px = ToPixel(pos);
        var st = new EvenZoomState { Start = pos, X0 = _x.Min, X1 = _x.Max, AnchorX = TimeAt(pos) };
        var ys = new List<(IYAxis, double, double, double)>();
        for (int q = 0; q < 3; q++)
        {
            var axis = AxisOf(q);
            int pane = PaneIndex(q);
            var rect = _panes[pane].RenderManager.LastRender.DataRect;
            bool underMouse = px.X >= rect.Left && px.X <= rect.Right && px.Y >= rect.Top && px.Y <= rect.Bottom;
            double anchor = underMouse ? _panes[pane].GetCoordinates(px, _xs[pane], axis).Y : (axis.Min + axis.Max) / 2;
            ys.Add((axis, axis.Min, axis.Max, anchor));
        }
        st.Y = ys.ToArray();
        _evenZoom = st;
        UserMovedView();
        PlotControl.UserInputProcessor.Disable();
        PlotControl.CaptureMouse();
    }

    private void EvenZoomTo(System.Windows.Point pos)
    {
        var st = _evenZoom;
        if (st == null) return;
        // drag right or up = zoom in, left or down = zoom out (as right-drag zoom)
        double d = ((pos.X - st.Start.X) - (pos.Y - st.Start.Y)) / 2;
        double f = Math.Exp(-d / 150.0);
        SetX(st.AnchorX - (st.AnchorX - st.X0) * f, st.AnchorX + (st.X1 - st.AnchorX) * f);
        foreach (var (axis, min, max, a) in st.Y) SetY(axis, a - (a - min) * f, a + (max - a) * f);
        _dirty = true;
        PlotControl.Refresh();
    }
}