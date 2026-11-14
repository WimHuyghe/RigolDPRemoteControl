using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace RigolDPRemoteControl.Infrastructure;

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class EmptyToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value == null || (value is string s && s.Length == 0) ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>
/// Attached behaviour for setpoint text boxes:
/// typing marks the box as "editing" (polling will not overwrite it), Enter applies, Esc or leaving the box reverts.
/// </summary>
public static class SetpointBox
{
    public static readonly DependencyProperty ApplyCommandProperty = DependencyProperty.RegisterAttached(
        "ApplyCommand", typeof(ICommand), typeof(SetpointBox), new PropertyMetadata(null, OnAttach));
    public static readonly DependencyProperty RevertCommandProperty = DependencyProperty.RegisterAttached(
        "RevertCommand", typeof(ICommand), typeof(SetpointBox), new PropertyMetadata(null));
    public static readonly DependencyProperty IsEditingProperty = DependencyProperty.RegisterAttached(
        "IsEditing", typeof(bool), typeof(SetpointBox), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static ICommand? GetApplyCommand(DependencyObject o) => (ICommand?)o.GetValue(ApplyCommandProperty);
    public static void SetApplyCommand(DependencyObject o, ICommand? v) => o.SetValue(ApplyCommandProperty, v);
    public static ICommand? GetRevertCommand(DependencyObject o) => (ICommand?)o.GetValue(RevertCommandProperty);
    public static void SetRevertCommand(DependencyObject o, ICommand? v) => o.SetValue(RevertCommandProperty, v);
    public static bool GetIsEditing(DependencyObject o) => (bool)o.GetValue(IsEditingProperty);
    public static void SetIsEditing(DependencyObject o, bool v) => o.SetValue(IsEditingProperty, v);

    private static void OnAttach(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb || e.OldValue != null) return;
        tb.GotKeyboardFocus += (_, _) => { SetIsEditing(tb, true); tb.SelectAll(); };
        tb.PreviewMouseLeftButtonDown += (_, args) =>
        {
            if (!tb.IsKeyboardFocusWithin) { tb.Focus(); args.Handled = true; }
        };
        tb.PreviewKeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                var cmd = GetApplyCommand(tb);
                if (cmd?.CanExecute(null) == true) cmd.Execute(null);
                SetIsEditing(tb, false);
                tb.SelectAll();
                args.Handled = true;
            }
            else if (args.Key == Key.Escape)
            {
                GetRevertCommand(tb)?.Execute(null);
                SetIsEditing(tb, false);
                Keyboard.ClearFocus();
                args.Handled = true;
            }
            else SetIsEditing(tb, true);
        };
        tb.LostKeyboardFocus += (_, _) =>
        {
            if (GetIsEditing(tb)) GetRevertCommand(tb)?.Execute(null);
            SetIsEditing(tb, false);
        };
    }
}
