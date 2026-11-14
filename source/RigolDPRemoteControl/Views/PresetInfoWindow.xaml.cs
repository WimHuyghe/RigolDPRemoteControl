using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using RigolDPRemoteControl.Settings;
using RigolDPRemoteControl.ViewModels;

namespace RigolDPRemoteControl.Views;

/// <summary>
/// "Preset info": a notes page (like a OneNote page, but without any Office dependency) for the setup that belongs
/// to a preset – formatted text, pictures and freehand drawing – plus a summary of the configured settings.
/// The page is stored in <see cref="AppSettings.PresetInfo"/> and saved inside the preset file.
/// </summary>
public partial class PresetInfoWindow : Window
{
    private static readonly double[] Sizes = { 10, 12, 14, 16, 18, 20, 24, 28, 36 };
    private readonly MainViewModel _vm;
    private readonly Func<Task> _savePreset;
    private readonly DispatcherTimer _storeTimer;
    private bool _loading;
    private bool _dirty;

    public PresetInfoWindow(MainViewModel vm, Func<Task> savePreset)
    {
        InitializeComponent();
        _vm = vm;
        _savePreset = savePreset;
        foreach (var s in Sizes) SizeCombo.Items.Add(s.ToString());
        _loading = true; SizeCombo.SelectedIndex = 2; _loading = false; // 14 pt

        Ink.DefaultDrawingAttributes = new DrawingAttributes { Color = Colors.Black, Width = 2.5, Height = 2.5, FitToCurve = true };
        Ink.StrokeCollected += (_, _) => MarkDirty();
        Ink.StrokeErased += (_, _) => MarkDirty();
        CommandManager.AddPreviewExecutedHandler(Page, Page_PreviewExecuted); // paste pictures

        _storeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _storeTimer.Tick += (_, _) => { _storeTimer.Stop(); Store(); };

        _vm.StorePresetInfo = Store;
        _vm.PresetInfoReplaced += OnPresetInfoReplaced;
        _vm.PropertyChanged += Vm_PropertyChanged;

        LoadPage();
        UpdateTitle();
        Loaded += async (_, _) => { Page.Focus(); await RefreshSummaryAsync(); };
        Closing += (_, _) => Store();
        Closed += (_, _) =>
        {
            _vm.StorePresetInfo = null;
            _vm.PresetInfoReplaced -= OnPresetInfoReplaced;
            _vm.PropertyChanged -= Vm_PropertyChanged;
        };
    }

    private void Vm_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPresetName)) UpdateTitle();
    }

    private void UpdateTitle()
        => Title = "Preset info  –  " + (_vm.CurrentPresetName.Length > 0 ? _vm.CurrentPresetName : "current configuration (not saved as preset yet)");

    private async void OnPresetInfoReplaced()
    {
        LoadPage();
        UpdateTitle();
        await RefreshSummaryAsync();
    }

    // ------------------------------------------------------------------ load / store

    private void LoadPage()
    {
        _loading = true;
        try
        {
            var info = _vm.Settings.PresetInfo;
            PageGrid.MinHeight = Math.Max(1400, info.PageHeight);
            Page.Document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new FontFamily("Segoe UI"), FontSize = 14 };
            if (info.NotesXamlPackage.Length > 0)
            {
                try
                {
                    using var ms = new MemoryStream(Convert.FromBase64String(info.NotesXamlPackage));
                    new TextRange(Page.Document.ContentStart, Page.Document.ContentEnd).Load(ms, DataFormats.XamlPackage);
                }
                catch (Exception ex) { _vm.ReportError("Preset info text could not be read: " + ex.Message); }
            }
            Ink.Strokes = new StrokeCollection();
            if (info.InkStrokes.Length > 0)
            {
                try
                {
                    using var ms = new MemoryStream(Convert.FromBase64String(info.InkStrokes));
                    Ink.Strokes = new StrokeCollection(ms);
                }
                catch (Exception ex) { _vm.ReportError("Preset info drawing could not be read: " + ex.Message); }
            }
            Ink.Strokes.StrokesChanged += (_, _) => MarkDirty();
            _dirty = false;
        }
        finally { _loading = false; }
    }

    /// <summary>Copies the page into the application settings (and from there into a preset when it is saved).</summary>
    private void Store()
    {
        if (!_dirty) return;
        try
        {
            var info = _vm.Settings.PresetInfo;
            using (var ms = new MemoryStream())
            {
                new TextRange(Page.Document.ContentStart, Page.Document.ContentEnd).Save(ms, DataFormats.XamlPackage);
                info.NotesXamlPackage = Convert.ToBase64String(ms.ToArray());
            }
            using (var ms = new MemoryStream())
            {
                Ink.Strokes.Save(ms, true);
                info.InkStrokes = Ink.Strokes.Count > 0 ? Convert.ToBase64String(ms.ToArray()) : "";
            }
            info.PageHeight = PageGrid.MinHeight;
            info.Modified = DateTime.Now;
            _dirty = false;
            _vm.RequestSave();
        }
        catch (Exception ex) { _vm.ReportError("Preset info could not be stored: " + ex.Message); }
    }

    private void MarkDirty()
    {
        if (_loading) return;
        _dirty = true;
        _storeTimer.Stop();
        _storeTimer.Start();
    }

    private void Page_Changed(object sender, TextChangedEventArgs e) => MarkDirty();

    // ------------------------------------------------------------------ modes

    private void Mode_Click(object sender, RoutedEventArgs e)
    {
        TypeMode.IsChecked = sender == TypeMode;
        DrawMode.IsChecked = sender == DrawMode;
        EraseMode.IsChecked = sender == EraseMode;
        bool typing = TypeMode.IsChecked == true;
        Ink.IsHitTestVisible = !typing;
        Ink.EditingMode = EraseMode.IsChecked == true ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.Ink;
        TextTools.Visibility = typing ? Visibility.Visible : Visibility.Collapsed;
        PenTools.Visibility = typing ? Visibility.Collapsed : Visibility.Visible;
        if (typing) Page.Focus();
    }

    private void PenColor_Click(object sender, RoutedEventArgs e)
    {
        var c = ColorOf(sender);
        Ink.DefaultDrawingAttributes.Color = c;
        Ink.DefaultDrawingAttributes.IsHighlighter = false;
        PenWidth_Click(ThickPen.IsChecked == true ? ThickPen : ThinPen, e);
        Mode_Click(DrawMode, e);
    }

    private void Marker_Click(object sender, RoutedEventArgs e)
    {
        Ink.DefaultDrawingAttributes.Color = Color.FromRgb(0xFF, 0xEB, 0x3B);
        Ink.DefaultDrawingAttributes.IsHighlighter = true;
        Ink.DefaultDrawingAttributes.Width = Ink.DefaultDrawingAttributes.Height = 14;
        Mode_Click(DrawMode, e);
    }

    private void PenWidth_Click(object sender, RoutedEventArgs e)
    {
        bool thick = sender == ThickPen;
        ThickPen.IsChecked = thick;
        ThinPen.IsChecked = !thick;
        if (!Ink.DefaultDrawingAttributes.IsHighlighter)
            Ink.DefaultDrawingAttributes.Width = Ink.DefaultDrawingAttributes.Height = thick ? 5 : 2.5;
    }

    private void ClearInk_Click(object sender, RoutedEventArgs e)
    {
        if (Ink.Strokes.Count == 0) return;
        if (ThemedMessageBox.Show(this, "Remove the complete drawing from this page? (The text stays.)", "Clear drawing",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            Ink.Strokes.Clear();
    }

    private void ExtendPage_Click(object sender, RoutedEventArgs e)
    {
        PageGrid.MinHeight += 600;
        MarkDirty();
        PageScroller.ScrollToVerticalOffset(PageScroller.ScrollableHeight + 600);
    }

    // ------------------------------------------------------------------ text formatting

    private static Color ColorOf(object sender)
        => (Color)ColorConverter.ConvertFromString((string)((FrameworkElement)sender).Tag);

    private void TextColor_Click(object sender, RoutedEventArgs e)
    {
        Page.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(ColorOf(sender)));
        Page.Focus();
    }

    private void Highlight_Click(object sender, RoutedEventArgs e)
    {
        var current = Page.Selection.GetPropertyValue(TextElement.BackgroundProperty) as SolidColorBrush;
        bool on = current == null || current.Color.A == 0;
        Page.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, on ? new SolidColorBrush(Color.FromRgb(0xFF, 0xF1, 0x76)) : null);
        Page.Focus();
    }

    private void SizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SizeCombo.SelectedIndex < 0 || _loading || Page == null) return;
        Page.Selection.ApplyPropertyValue(TextElement.FontSizeProperty, Sizes[SizeCombo.SelectedIndex]);
        Page.Focus();
    }

    private void ApplyParagraphStyle(double size, FontWeight weight)
    {
        var p = Page.CaretPosition.Paragraph;
        if (p == null) return;
        var range = new TextRange(p.ContentStart, p.ContentEnd);
        range.ApplyPropertyValue(TextElement.FontSizeProperty, size);
        range.ApplyPropertyValue(TextElement.FontWeightProperty, weight);
        Page.Focus();
    }

    private void Heading1_Click(object sender, RoutedEventArgs e) => ApplyParagraphStyle(24, FontWeights.SemiBold);
    private void Heading2_Click(object sender, RoutedEventArgs e) => ApplyParagraphStyle(18, FontWeights.SemiBold);
    private void Normal_Click(object sender, RoutedEventArgs e) => ApplyParagraphStyle(14, FontWeights.Normal);

    private void InsertDate_Click(object sender, RoutedEventArgs e)
    {
        Page.CaretPosition.InsertTextInRun(DateTime.Now.ToString("dd-MM-yyyy HH:mm") + " ");
        Page.Focus();
    }

    // ------------------------------------------------------------------ pictures

    private void Page_PreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Command != ApplicationCommands.Paste) return;
        try
        {
            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } img)
            {
                InsertPicture(img);
                e.Handled = true;
            }
        }
        catch { /* clipboard busy: let the normal paste handle it */ }
    }

    private void InsertImage_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Insert picture",
            Filter = "Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|All files|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(dlg.FileName);
            bmp.EndInit();
            InsertPicture(bmp);
        }
        catch (Exception ex)
        {
            ThemedMessageBox.Show(this, "The picture could not be loaded: " + ex.Message, "Insert picture", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Inserts a picture at the caret. The picture is re-encoded as PNG so it is stored inside the page.</summary>
    private void InsertPicture(BitmapSource source)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        var frame = BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        frame.Freeze();

        double width = Math.Min(frame.PixelWidth, Math.Max(200, Page.ActualWidth - 90));
        var image = new Image { Source = frame, Width = width, Stretch = Stretch.Uniform };
        var pos = Page.CaretPosition.GetInsertionPosition(LogicalDirection.Forward);
        _ = new InlineUIContainer(image, pos);
        Page.Focus();
        MarkDirty();
    }

    // ------------------------------------------------------------------ summary

    private async Task RefreshSummaryAsync()
    {
        try
        {
            SummaryBox.Text = "Reading the configuration…";
            var p = await _vm.CapturePresetAsync();
            SummaryBox.Text = PresetSummary.Build(p, _vm.CurrentPresetName);
        }
        catch (Exception ex) { SummaryBox.Text = "Summary could not be created: " + ex.Message; }
    }

    private async void RefreshSummary_Click(object sender, RoutedEventArgs e) => await RefreshSummaryAsync();

    private void SummaryToPage_Click(object sender, RoutedEventArgs e)
    {
        var para = new Paragraph(new Run(SummaryBox.Text.TrimEnd()))
        {
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF4, 0xF6)),
            Padding = new Thickness(8),
        };
        Page.Document.Blocks.Add(para);
        Page.Document.Blocks.Add(new Paragraph());
        Page.ScrollToEnd();
        PageScroller.ScrollToEnd();
        MarkDirty();
    }

    // ------------------------------------------------------------------ footer

    private async void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        Store();
        await _savePreset();
        await RefreshSummaryAsync();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Marks the page as changed so it is stored (used after programmatic changes).</summary>
    public void ForceStore() { _dirty = true; Store(); }
}
