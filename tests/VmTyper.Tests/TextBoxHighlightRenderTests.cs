using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VmTyper.Helpers;
using VmTyper.Models;
using Xunit;
using TextBox = System.Windows.Controls.TextBox;

namespace VmTyper.Tests;

[Trait("Category", "Unit")]
public sealed class TextBoxHighlightRenderTests
{
    [Fact]
    public void Highlight_IsDrawnOverTheWord_EvenWhenTheWindowIsNotActive()
    {
        (byte r, byte g, byte b) inside = default, outside = default;
        Exception? error = null;

        var thread = new Thread(() =>
        {
            try
            {
                var box = new TextBox
                {
                    Text = "alpha beta gamma",
                    IsReadOnly = true,
                    FontSize = 16,
                    Background = Brushes.Black,
                    Foreground = Brushes.White
                };
                var window = new Window
                {
                    Content = box,
                    Width = 400,
                    Height = 120,
                    ShowActivated = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -10000,
                    Top = -10000
                };
                window.Show();
                box.UpdateLayout();

                TextBoxHighlight.SetRange(box, new TextHighlight(6, 4)); // "beta"
                box.UpdateLayout();
                window.UpdateLayout();
                Dispatcher_Flush();

                var word = box.GetRectFromCharacterIndex(7);
                var other = box.GetRectFromCharacterIndex(13);
                inside = Pixel(window, word.Left + 1, word.Top + 1);
                outside = Pixel(window, other.Left + 1, other.Top + 1);
                window.Close();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(error);
        Assert.True(inside.r > 150 && inside.b < 100, $"Sem realce âmbar sobre a palavra: {inside}");
        Assert.True(outside.r < 100, $"Realce fora da palavra: {outside}");
    }

    private static void Dispatcher_Flush() =>
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);

    private static (byte, byte, byte) Pixel(Window window, double x, double y)
    {
        var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(window);
        var px = new byte[4];
        bmp.CopyPixels(new Int32Rect((int)x, (int)y, 1, 1), px, 4, 0);
        return (px[2], px[1], px[0]);
    }
}
