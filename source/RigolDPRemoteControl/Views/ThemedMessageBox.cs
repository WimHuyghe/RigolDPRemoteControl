using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RigolDPRemoteControl.Views;

/// <summary>
/// Drop-in replacement for <see cref="MessageBox"/> that follows the application theme (dark / light) instead of
/// showing the grey Windows message box. Same arguments and return values as MessageBox.Show.
/// </summary>
public static class ThemedMessageBox
{
    public static MessageBoxResult Show(string text, string caption, MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
        => Show(null, text, caption, button, icon, defaultResult);

    public static MessageBoxResult Show(Window? owner, string text, string caption, MessageBoxButton button = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        var app = Application.Current;
        if (app == null) return MessageBox.Show(text, caption, button, icon, defaultResult);
        if (app.Dispatcher.CheckAccess() == false)
            return app.Dispatcher.Invoke(() => Show(owner, text, caption, button, icon, defaultResult));

        owner ??= app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? app.MainWindow;
        if (owner != null && !owner.IsVisible) owner = null;
        var dlg = new MessageWindow(text, caption, button, icon, defaultResult) { Owner = owner };
        if (owner == null) { dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen; dlg.ShowInTaskbar = true; }
        dlg.ShowDialog();
        return dlg.Result;
    }

    private sealed class MessageWindow : Window
    {
        public MessageBoxResult Result { get; private set; }
        private readonly string _text, _caption;

        public MessageWindow(string text, string caption, MessageBoxButton button, MessageBoxImage icon, MessageBoxResult defaultResult)
        {
            _text = text;
            _caption = caption;
            Title = caption;
            SizeToContent = SizeToContent.WidthAndHeight;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            MinWidth = 360;
            SetResourceReference(BackgroundProperty, "AppBackground");
            SetResourceReference(ForegroundProperty, "TextPrimary");
            UseLayoutRounding = true;

            // result when the window is closed with the X button or Esc (as MessageBox does)
            Result = button switch
            {
                MessageBoxButton.OK => MessageBoxResult.OK,
                MessageBoxButton.YesNo => MessageBoxResult.No,
                _ => MessageBoxResult.Cancel,
            };

            var root = new DockPanel();

            // ---- button bar ----
            var bar = new Border { Padding = new Thickness(18, 12, 18, 12), BorderThickness = new Thickness(0, 1, 0, 0) };
            bar.SetResourceReference(Border.BackgroundProperty, "PanelBackground");
            bar.SetResourceReference(Border.BorderBrushProperty, "CardBorder");
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            bar.Child = buttons;
            DockPanel.SetDock(bar, Dock.Bottom);
            root.Children.Add(bar);

            var kinds = button switch
            {
                MessageBoxButton.OKCancel => new[] { MessageBoxResult.OK, MessageBoxResult.Cancel },
                MessageBoxButton.YesNo => new[] { MessageBoxResult.Yes, MessageBoxResult.No },
                MessageBoxButton.YesNoCancel => new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel },
                _ => new[] { MessageBoxResult.OK },
            };
            var def = kinds.Contains(defaultResult) ? defaultResult : kinds[0];
            var accent = TryFindResource("AccentButtonStyle") as Style;
            foreach (var k in kinds)
            {
                var b = new Button
                {
                    Content = k.ToString(),
                    MinWidth = 90,
                    Margin = new Thickness(8, 0, 0, 0),
                    IsDefault = k == def,
                    IsCancel = k == MessageBoxResult.Cancel || (k == MessageBoxResult.No && !kinds.Contains(MessageBoxResult.Cancel))
                               || (kinds.Length == 1),
                };
                if (k == def && accent != null) b.Style = accent;
                var result = k;
                b.Click += (_, _) => { Result = result; DialogResult = true; };
                buttons.Children.Add(b);
                if (k == def) Loaded += (_, _) => b.Focus();
            }

            // ---- icon + text ----
            var body = new DockPanel { Margin = new Thickness(20, 18, 24, 18) };
            var (glyph, brushKey) = icon switch
            {
                MessageBoxImage.Warning => ("", "WarningBrush"),
                MessageBoxImage.Error => ("", "ErrorBrush"),
                MessageBoxImage.Question => ("", "AccentBrush"),
                MessageBoxImage.Information => ("", "AccentBrush"),
                _ => ((string?)null, ""),
            };
            if (glyph != null)
            {
                var ic = new TextBlock
                {
                    Text = glyph, FontSize = 30, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Top,
                    FontFamily = (FontFamily)FindResource("IconFont"),
                };
                ic.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
                DockPanel.SetDock(ic, Dock.Left);
                body.Children.Add(ic);
            }
            var tb = new TextBlock
            {
                Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 480, FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center, LineHeight = 20,
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            body.Children.Add(tb);
            root.Children.Add(body);

            Content = root;

            // Ctrl+C copies the message, like the Windows message box
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    try { Clipboard.SetText($"{_caption}\n\n{_text}"); } catch { }
                    e.Handled = true;
                }
            };
        }
    }
}
