using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Bigfile.Behaviors;
using Bigfile.ViewModels;
using Wpf.Ui.Controls;

namespace Bigfile.Views;

/// <summary>
/// Window code-behind. Kept minimal in MVVM — only focus, scrolling and
/// keyboard navigation live here, all of which are pure view concerns.
/// </summary>
public partial class MainWindow : FluentWindow
{
    /// <summary>Height and width of one character of the reader font, once measured.</summary>
    private Size _glyph;

    /// <summary>Edge of the rendered window icon, in device-independent pixels.</summary>
    private const int IconSize = 64;

    /// <summary>
    /// The Fluent icon font, named explicitly. An icon built outside the visual
    /// tree does not pick the font up from the control library's styles, and
    /// without it the glyph renders as an empty box.
    /// </summary>
    private static readonly FontFamily SymbolFont = new(
        new Uri("pack://application:,,,/Wpf.Ui;component/"),
        "./Resources/Fonts/#FluentSystemIcons-Regular");

    public MainWindow()
    {
        InitializeComponent();

        // The title bar takes the symbol directly, but Window.Icon — what the
        // taskbar and Alt+Tab show — wants an image, so the same glyph is drawn
        // into one. Rendering it beats shipping an .ico: one source of truth for
        // the icon, and nothing to keep in sync.
        Icon = RenderIcon(SymbolRegular.CodeText20);

        // The ViewModel cannot scroll or move focus through bound state, so it
        // asks for both through events, and it reads the viewport back through a
        // callback because only the view knows what is on screen.
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.MatchFound += ScrollToMatch;
            viewModel.SearchFocusRequested += FocusSearchBox;
            viewModel.VisibleRows = VisibleRows;

            // Closing the window has to close the document too: a downloaded
            // source lives in a temporary file that is only removed on dispose.
            Closed += (_, _) => viewModel.CloseCommand.Execute(null);
        }
    }

    /// <summary>
    /// Draws one Fluent symbol into a bitmap the window can use as its icon.
    ///
    /// The glyph sits on a filled tile rather than on nothing: a white glyph
    /// alone would disappear against a light taskbar.
    /// </summary>
    private static ImageSource RenderIcon(SymbolRegular symbol)
    {
        const int padding = 6;
        const int corner = 12;

        var glyph = new SymbolIcon
        {
            Symbol = symbol,
            FontFamily = SymbolFont,
            FontSize = IconSize - 2 * padding,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // A standalone element is never laid out by anyone, so it is measured and
        // arranged by hand before it can be rendered.
        var box = new Size(IconSize - 2 * padding, IconSize - 2 * padding);
        glyph.Measure(box);
        glyph.Arrange(new Rect(new Point(padding, padding), box));

        var tile = new DrawingVisual();

        using (var drawing = tile.RenderOpen())
        {
            drawing.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x3A)),
                null,
                new Rect(0, 0, IconSize, IconSize),
                corner,
                corner);
        }

        var bitmap = new RenderTargetBitmap(
            IconSize, IconSize, 96, 96, PixelFormats.Pbgra32);

        bitmap.Render(tile);
        bitmap.Render(glyph);
        bitmap.Freeze();

        return bitmap;
    }

    /// <summary>
    /// Brings a found match into view, a third down the viewport and — since the
    /// assignment asks for the word, not just the line — far enough across for
    /// the match itself to be on screen. Queued behind layout because switching
    /// the filter on or off replaces the item source, and the scroll extent is
    /// only right once the new one has been measured.
    /// </summary>
    private void ScrollToMatch(int line, int column) => ScrollToMatch(line, column, retry: true);

    private void ScrollToMatch(int line, int column, bool retry)
    {
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                if (SmoothScroll.FindViewer(LinesList) is not { } viewer)
                {
                    return;
                }

                var height = LineHeight(viewer);

                if (height <= 0)
                {
                    // Nothing has been measured yet. One more turn of the
                    // dispatcher and the panel will have laid its lines out.
                    if (retry)
                    {
                        ScrollToMatch(line, column, retry: false);
                    }

                    return;
                }

                SmoothScroll.To(viewer, line * height - viewer.ViewportHeight / 3);
                ScrollColumnIntoView(viewer, column);
            }),
            DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Nudges the viewport sideways only when the match is not already visible,
    /// so stepping through matches on screen does not shuffle the text about.
    /// </summary>
    private void ScrollColumnIntoView(ScrollViewer viewer, int column)
    {
        if (column <= 0)
        {
            viewer.ScrollToHorizontalOffset(0);
            return;
        }

        // The reader font is monospaced, so a column is just an index times a
        // character — no text measuring per line needed.
        var width = Glyph().Width;

        if (width <= 0)
        {
            return;
        }

        var x = column * width;
        var visible = x >= viewer.HorizontalOffset
                      && x + width <= viewer.HorizontalOffset + viewer.ViewportWidth;

        if (!visible)
        {
            viewer.ScrollToHorizontalOffset(Math.Max(0, x - viewer.ViewportWidth / 3));
        }
    }

    /// <summary>
    /// Rows the viewport shows, as item indexes. Derived from the scroll offset
    /// rather than from the realized containers, which recycling makes an
    /// unreliable window on their own.
    /// </summary>
    private (int First, int Last)? VisibleRows()
    {
        if (SmoothScroll.FindViewer(LinesList) is not { } viewer)
        {
            return null;
        }

        var count = LinesList.Items.Count;
        var height = LineHeight(viewer);

        if (count == 0 || height <= 0)
        {
            return null;
        }

        var first = Math.Clamp((int)(viewer.VerticalOffset / height), 0, count - 1);
        var last = Math.Clamp(
            (int)((viewer.VerticalOffset + viewer.ViewportHeight) / height), first, count - 1);

        return (first, last);
    }

    /// <summary>
    /// Focus moves once the bar the command just revealed has been laid out,
    /// hence the queued dispatch rather than a direct call.
    /// </summary>
    private void FocusSearchBox()
    {
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                SearchTextBox.Focus();
                SearchTextBox.SelectAll();
            }),
            DispatcherPriority.Input);
    }

    /// <summary>Gives the reader focus so it receives the navigation keys.</summary>
    private void LinesList_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            LinesList.Focus();
        }
    }

    /// <summary>
    /// Scrolls the viewport rather than walking the selection, so navigating a
    /// million lines never realizes more than the visible ones.
    /// </summary>
    private void LinesList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SmoothScroll.FindViewer(LinesList) is not { } viewer)
        {
            return;
        }

        var target = e.Key switch
        {
            Key.Home => 0,
            Key.End => viewer.ScrollableHeight,
            Key.PageUp => viewer.VerticalOffset - viewer.ViewportHeight,
            Key.PageDown => viewer.VerticalOffset + viewer.ViewportHeight,
            Key.Up => viewer.VerticalOffset - LineHeight(viewer),
            Key.Down => viewer.VerticalOffset + LineHeight(viewer),
            _ => double.NaN
        };

        if (double.IsNaN(target))
        {
            return;
        }

        SmoothScroll.To(viewer, target);
        e.Handled = true;
    }

    /// <summary>
    /// Glides on a wheel notch instead of letting the ScrollViewer jump, so the
    /// mouse behaves like the navigation keys. The notch is sized by the system
    /// wheel setting, which is what every other application scrolls by.
    /// </summary>
    private void LinesList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0 || SmoothScroll.FindViewer(LinesList) is not { } viewer)
        {
            return;
        }

        var scrollLines = SystemParameters.WheelScrollLines;

        // The setting is -1 when the system asks for a page per notch.
        var step = scrollLines < 0
            ? viewer.ViewportHeight
            : scrollLines * LineHeight(viewer);

        if (step <= 0)
        {
            return;
        }

        var notches = (double)e.Delta / Mouse.MouseWheelDeltaForOneLine;

        SmoothScroll.By(viewer, -notches * step);
        e.Handled = true;
    }

    /// <summary>
    /// A virtualized extent is an estimate, but spread over the whole document
    /// it gives the line height without measuring a container. Before the first
    /// layout there is no extent to divide, so the font is measured instead —
    /// otherwise the first scroll of a document would land at the top.
    /// </summary>
    private double LineHeight(ScrollViewer viewer) =>
        LinesList.Items.Count > 0 && viewer.ExtentHeight > 0
            ? viewer.ExtentHeight / LinesList.Items.Count
            : Glyph().Height;

    /// <summary>
    /// Size of one character in the reader font, measured once. Valid for every
    /// character because the font is monospaced.
    /// </summary>
    private Size Glyph()
    {
        if (_glyph.Height > 0)
        {
            return _glyph;
        }

        var typeface = new Typeface(
            LinesList.FontFamily, LinesList.FontStyle, LinesList.FontWeight, LinesList.FontStretch);

        var text = new FormattedText(
            "0",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            typeface,
            LinesList.FontSize,
            Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

        _glyph = new Size(text.Width, text.Height);
        return _glyph;
    }

    /// <summary>Puts the caret in the URL box as soon as the prompt appears.</summary>
    private void UrlTextBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            UrlTextBox.Focus();
        }
    }

    /// <summary>Same for the line count box of the generator prompt.</summary>
    private void RandomLineBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            RandomLineBox.Focus();
        }
    }
}
