using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Bigfile.Behaviors;

/// <summary>
/// Paints the occurrences of a query inside a TextBlock by rebuilding its
/// inlines. Attached to the line template, so it only ever runs for realized
/// containers — a few dozen visible lines, whatever the document size.
///
/// The occurrence the reader is standing on is painted differently from the
/// rest, which is what makes stepping through several matches in one line
/// visible at all.
/// </summary>
public static class Highlight
{
    /// <summary>Runs built for one line, past which the rest is left plain.</summary>
    private const int MaxMatchesPerLine = 200;

    private static readonly Brush MatchBrush = CreateMatchBrush();

    private static readonly Brush CurrentBrush = CreateCurrentBrush();

    /// <summary>The line to display.</summary>
    public static readonly DependencyProperty TextProperty =
        DependencyProperty.RegisterAttached(
            "Text", typeof(string), typeof(Highlight),
            new PropertyMetadata(null, OnChanged));

    /// <summary>The text to pick out, or empty for no highlighting.</summary>
    public static readonly DependencyProperty QueryProperty =
        DependencyProperty.RegisterAttached(
            "Query", typeof(string), typeof(Highlight),
            new PropertyMetadata(null, OnChanged));

    public static string? GetText(DependencyObject element) =>
        (string?)element.GetValue(TextProperty);

    public static void SetText(DependencyObject element, string? value) =>
        element.SetValue(TextProperty, value);

    /// <summary>
    /// Offset of the occurrence the reader is on, or -1. Only honoured together
    /// with <see cref="IsCurrentProperty"/>, since every line is handed the same
    /// offset and only one of them is the current one.
    /// </summary>
    public static readonly DependencyProperty CurrentProperty =
        DependencyProperty.RegisterAttached(
            "Current", typeof(int), typeof(Highlight),
            new PropertyMetadata(-1, OnChanged));

    /// <summary>True on the line that holds the current match.</summary>
    public static readonly DependencyProperty IsCurrentProperty =
        DependencyProperty.RegisterAttached(
            "IsCurrent", typeof(bool), typeof(Highlight),
            new PropertyMetadata(false, OnChanged));

    public static string? GetQuery(DependencyObject element) =>
        (string?)element.GetValue(QueryProperty);

    public static void SetQuery(DependencyObject element, string? value) =>
        element.SetValue(QueryProperty, value);

    public static int GetCurrent(DependencyObject element) =>
        (int)element.GetValue(CurrentProperty);

    public static void SetCurrent(DependencyObject element, int value) =>
        element.SetValue(CurrentProperty, value);

    public static bool GetIsCurrent(DependencyObject element) =>
        (bool)element.GetValue(IsCurrentProperty);

    public static void SetIsCurrent(DependencyObject element, bool value) =>
        element.SetValue(IsCurrentProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock block)
        {
            Apply(block);
        }
    }

    private static void Apply(TextBlock block)
    {
        var text = GetText(block) ?? string.Empty;
        var query = GetQuery(block);

        if (string.IsNullOrEmpty(query))
        {
            block.Text = text;
            return;
        }

        // Assigning Text clears the inlines, so the plain path stays cheap and
        // the marked-up path is built from scratch every time.
        block.Inlines.Clear();

        var current = GetIsCurrent(block) ? GetCurrent(block) : -1;
        var start = 0;
        var matches = 0;

        while (matches < MaxMatchesPerLine)
        {
            var hit = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);

            if (hit < 0)
            {
                break;
            }

            if (hit > start)
            {
                block.Inlines.Add(new Run(text[start..hit]));
            }

            block.Inlines.Add(new Run(text.Substring(hit, query.Length))
            {
                Background = hit == current ? CurrentBrush : MatchBrush
            });

            start = hit + query.Length;
            matches++;
        }

        if (matches == 0)
        {
            block.Text = text;
            return;
        }

        if (start < text.Length)
        {
            block.Inlines.Add(new Run(text[start..]));
        }
    }

    /// <summary>
    /// A translucent amber reads as a highlight over both the light and the
    /// dark theme, and over the selection brush of the current match.
    /// </summary>
    private static Brush CreateMatchBrush()
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xB9, 0x00));
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The current occurrence gets the opaque version of the same amber, so it
    /// stands out among the other matches of its line without a second colour.
    /// </summary>
    private static Brush CreateCurrentBrush()
    {
        var brush = new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00));
        brush.Freeze();
        return brush;
    }
}
