using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace VelaShell.Plugin.Ai.Ui;

/// <summary>Fits the complete image initially; zoom is relative to physical source pixels.</summary>
internal sealed class ImagePreviewView : Grid
{
    private readonly Image _image;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _percentage;
    private readonly PixelSize _pixels;
    private double _screenScaling;
    private double _zoom = 1;
    private bool _fit = true;

    internal ImagePreviewView(Bitmap bitmap, Loc loc, double screenScaling, Func<Control, Task>? copyImage = null)
    {
        _pixels = bitmap.PixelSize;
        _screenScaling = screenScaling > 0 ? screenScaling : 1;
        RowDefinitions = [with("*,Auto")];
        _image = new Image
        {
            Name = "FullImage",
            Source = bitmap,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _scroll = new ScrollViewer
        {
            Name = "ImageViewport",
            Content = _image,
            Padding = new Thickness(8),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        _percentage = new TextBlock { Name = "ImageZoomPercentage", VerticalAlignment = VerticalAlignment.Center };
        _percentage[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("VelaTextPrimary");
        _percentage[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("VelaFontSize11");
        var toolbar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            ItemSpacing = 8,
            LineSpacing = 4,
            Margin = new Thickness(8, 4),
            Children =
            {
                Action("−", loc["ImageZoomOut"], () => SetZoom(_zoom / 1.25)),
                _percentage,
                Action("+", loc["ImageZoomIn"], () => SetZoom(_zoom * 1.25)),
                Action(loc["ImageActualSize"], loc["ImageActualSize"], () => SetZoom(1)),
                Action(loc["ImageFitWindow"], loc["ImageFitWindow"], Fit)
            }
        };
        if (copyImage is not null)
        {
            var copy = new Button { Name = "ImagePreviewCopyAction", Content = loc["CopyGeneratedImage"] };
            copy[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
            ToolTip.SetTip(copy, loc["CopyGeneratedImage"]);
            AutomationProperties.SetName(copy, loc["CopyGeneratedImage"]);
            copy.Click += async (_, _) => await copyImage(copy);
            toolbar.Children.Add(copy);
        }
        SetRow(toolbar, 1);
        Children.Add(_scroll);
        Children.Add(toolbar);
        _scroll.SizeChanged += (_, _) => { if (_fit) Fit(); };
        AttachedToVisualTree += (_, _) =>
        {
            _screenScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? _screenScaling;
            if (_fit) Fit(); else ApplyZoom();
        };
        _scroll.PointerWheelChanged += (_, e) =>
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                SetZoom(_zoom * (e.Delta.Y > 0 ? 1.25 : 0.8));
                e.Handled = true;
            }
        };
        _image.DoubleTapped += (_, e) => { if (_fit) SetZoom(1); else Fit(); e.Handled = true; };
        ApplyZoom();
    }

    internal static double FitScale(PixelSize pixels, Size viewport, double screenScaling)
        => Math.Min(1, Math.Min(Math.Max(1, viewport.Width) * screenScaling / pixels.Width,
            Math.Max(1, viewport.Height) * screenScaling / pixels.Height));

    internal static Size InitialWindowSize(PixelSize pixels, Size available, double screenScaling)
    {
        // PanelOptions sizes include the host's shadow margin and 28px title bar.
        double scale = FitScale(pixels, new Size(Math.Max(1, available.Width - 48), Math.Max(1, available.Height - 128)), screenScaling);
        return new Size(Math.Min(available.Width, Math.Max(320, pixels.Width * scale / screenScaling + 48)),
            Math.Min(available.Height, Math.Max(240, pixels.Height * scale / screenScaling + 128)));
    }

    internal void ReleaseImage() => _image.Source = null;

    private void Fit()
    {
        _fit = true;
        if (_scroll.Bounds.Width > 0 && _scroll.Bounds.Height > 0)
            _zoom = FitScale(_pixels, new Size(_scroll.Bounds.Width - 16, _scroll.Bounds.Height - 16), _screenScaling);
        ApplyZoom();
        _scroll.Offset = default;
    }

    private void SetZoom(double zoom)
    {
        _fit = false;
        _zoom = Math.Clamp(zoom, 0.01, 8);
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        _image.Width = _pixels.Width * _zoom / _screenScaling;
        _image.Height = _pixels.Height * _zoom / _screenScaling;
        _percentage.Text = (_zoom * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%";
    }

    private static Button Action(string text, string description, Action action)
    {
        var button = new Button { Content = text };
        button[!ThemeProperty] = new DynamicResourceExtension("VelaOutlineButtonTheme");
        ToolTip.SetTip(button, description);
        AutomationProperties.SetName(button, description);
        button.Click += (_, _) => action();
        return button;
    }
}
