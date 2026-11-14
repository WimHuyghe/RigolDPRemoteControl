using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace RigolDPRemoteControl.Views;

/// <summary>The main window, which docks / undocks panels (power supplies, channel groups).</summary>
public interface IFloatingHost
{
    /// <summary>The floating window is being moved: show or hide the dock zone.</summary>
    void FloatingMoving(FloatingPanelWindow w);
    /// <summary>The move has ended (mouse released): dock when it was released on the dock zone.</summary>
    void FloatingMoveEnded(FloatingPanelWindow w);
    /// <summary>The user closed the floating window: put the panel back into the main window.</summary>
    void FloatingClosedByUser(FloatingPanelWindow w);
    void FloatingPositionChanged(FloatingPanelWindow w);
}

/// <summary>
/// Undocked panel (a power supply or the channel groups): an always-on-top window that shows the same panel
/// (same template, same view model) as the main window. Dragged by the panel header or the title bar;
/// releasing it on the supplies pane of the main window docks it again.
/// </summary>
public sealed class FloatingPanelWindow : Window
{
    /// <summary>Set while the application closes: the windows then close without docking the panels.</summary>
    public static bool AppClosing { get; set; }

    private readonly IFloatingHost _host;
    private bool _docking;
    private bool _inMoveLoop;

    public object Item { get; }

    public FloatingPanelWindow(IFloatingHost host, object item, DataTemplate template, string title, double fixedWidth = double.NaN)
    {
        _host = host;
        Item = item;
        Title = title;
        Topmost = true;
        // no window frame (no title bar, icon, minimize / close buttons): only the panel itself is visible.
        // Moving is done by dragging the panel header; the transparent window shows the rounded card corners.
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = double.IsNaN(fixedWidth) ? SizeToContent.WidthAndHeight : SizeToContent.Height;
        if (!double.IsNaN(fixedWidth)) Width = fixedWidth;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SetResourceReference(ForegroundProperty, "TextPrimary");
        if (Application.Current?.MainWindow?.Icon is { } icon) Icon = icon;
        UseLayoutRounding = true;
        // accessibility / UI automation: the window is named after its title (not after the content object)
        SetBinding(System.Windows.Automation.AutomationProperties.NameProperty, new System.Windows.Data.Binding(nameof(Title)) { Source = this });

        Content = new ContentControl
        {
            Content = item,
            ContentTemplate = template,
            Focusable = false,
        };

        LocationChanged += (_, _) =>
        {
            _host.FloatingPositionChanged(this);
            if (_inMoveLoop) _host.FloatingMoving(this);
        };
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    /// <summary>Move the window with the mouse (left button must be down); afterwards the drop position decides about docking.</summary>
    public void BeginMove()
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        try { DragMove(); } catch (InvalidOperationException) { }
        // DragMove returns when the button is released; WM_EXITSIZEMOVE has normally handled the drop already
    }

    // the system move loop (title bar or DragMove): WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_ENTERSIZEMOVE = 0x0231, WM_EXITSIZEMOVE = 0x0232;
        if (msg == WM_ENTERSIZEMOVE) _inMoveLoop = true;
        else if (msg == WM_EXITSIZEMOVE && _inMoveLoop)
        {
            _inMoveLoop = false;
            // let the move loop finish before (possibly) closing this window
            Dispatcher.BeginInvoke(() => _host.FloatingMoveEnded(this));
        }
        return IntPtr.Zero;
    }

    /// <summary>Close because the panel goes back into the main window.</summary>
    public void CloseForDocking()
    {
        _docking = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (!_docking && !AppClosing) _host.FloatingClosedByUser(this);
    }

    // ------------------------------------------------------------------ helpers for the host

    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    private struct POINT { public int X, Y; }

    /// <summary>Mouse position in screen pixels (also valid during the system move loop).</summary>
    public static Point CursorScreenPixels()
    {
        GetCursorPos(out var p);
        return new Point(p.X, p.Y);
    }

    /// <summary>The panel header inside this window (element tagged "DragHeader").</summary>
    public FrameworkElement? FindHeader() => FindTagged(this);

    private static FrameworkElement? FindTagged(DependencyObject d)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var c = VisualTreeHelper.GetChild(d, i);
            if (c is FrameworkElement { Tag: "DragHeader" } fe) return fe;
            if (FindTagged(c) is { } r) return r;
        }
        return null;
    }
}
