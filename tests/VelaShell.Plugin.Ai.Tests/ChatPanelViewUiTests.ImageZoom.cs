using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.VisualTree;
using VelaShell.Plugin.Ai.Ui;

namespace VelaShell.Plugin.Ai.Tests;

public sealed partial class ChatPanelViewUiTests
{
    [TestMethod]
    public void ImagePreviewSizing_UsesPhysicalOneToOneAndFitsBothAxes()
    {
        Assert.AreEqual(1d, ImagePreviewView.FitScale(new PixelSize(800, 600), new Size(1000, 800), 1));
        Assert.AreEqual(1d, ImagePreviewView.FitScale(new PixelSize(1600, 1200), new Size(1000, 800), 2));
        Assert.AreEqual(0.25, ImagePreviewView.FitScale(new PixelSize(4000, 2000), new Size(1000, 800), 1));
        Assert.AreEqual(0.2, ImagePreviewView.FitScale(new PixelSize(1000, 4000), new Size(1000, 800), 1));
        Size small = ImagePreviewView.InitialWindowSize(new PixelSize(800, 600), new Size(1800, 1000), 1);
        Assert.AreEqual(new Size(848, 728), small);
        Size large = ImagePreviewView.InitialWindowSize(new PixelSize(4000, 2000), new Size(1200, 800), 1);
        Assert.IsTrue(large.Width <= 1200 && large.Height <= 800);
        Assert.AreEqual(2d, (large.Width - 48) / (large.Height - 128), 0.001);
    }

    [TestMethod]
    public void ImagePreview_FitsCompleteImageShowsZoomAndSupportsActualSize()
    {
        OnUi(async () =>
        {
            using var bitmap = new WriteableBitmap(new PixelSize(1600, 1000), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            var loc = new Loc("en");
            var view = new ImagePreviewView(bitmap, loc, 1);
            var window = new Window { Width = 640, Height = 480, Content = view };
            window.Show();
            try
            {
                await PumpAsync(5);
                Image image = view.GetVisualDescendants().OfType<Image>().Single();
                TextBlock percentage = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ImageZoomPercentage");
                ScrollViewer viewport = view.GetVisualDescendants().OfType<ScrollViewer>().Single(v => v.Name == "ImageViewport");
                Assert.IsTrue(image.Width <= viewport.Bounds.Width);
                Assert.IsTrue(image.Height <= viewport.Bounds.Height);
                Assert.AreEqual(1.6, image.Width / image.Height, 0.001);
                Assert.AreNotEqual("100%", percentage.Text);
                RaiseClick(view.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, "100%")));
                await PumpAsync(3);
                Assert.AreEqual("100%", percentage.Text);
                Assert.AreEqual(1600d / window.RenderScaling, image.Width);
                Assert.AreEqual(1000d / window.RenderScaling, image.Height);
                RaiseClick(view.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, loc["ImageFitWindow"])));
                await PumpAsync(3);
                Assert.IsTrue(image.Width <= viewport.Bounds.Width);
                window.Width = 480;
                window.Height = 360;
                await PumpAsync(5);
                Assert.IsTrue(image.Width <= viewport.Bounds.Width);
                Assert.IsTrue(image.Height <= viewport.Bounds.Height);
            }
            finally { view.ReleaseImage(); window.Close(); }
        });
    }
}
