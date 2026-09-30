using DataUsage.App.Controls;
using DataUsage.App.Theme;
using DataUsage.Core.Query;
using DataUsage.Core.View;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DataUsage.App.Views;

/// <summary>Pieces more than one page draws.</summary>
public static class Parts
{
    /// <summary>The page's h1 and the line under it.</summary>
    public static StackPanel PageHead(string title, string? sub, UIElement? before = null, UIElement? after = null, UIElement? titleExtra = null)
    {
        var head = new StackPanel { Margin = new Thickness(0, 0, 0, 35) };
        if (before is not null) head.Children.Add(before);
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        if (titleExtra is not null) titleRow.Children.Add(titleExtra);
        var h1 = Ui.Text(title, 35, 600, spacing: -0.02);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHeadingLevel(h1, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1);
        titleRow.Children.Add(h1);
        head.Children.Add(titleRow);
        if (sub is not null)
        {
            var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 11, Margin = new Thickness(0, 6, 0, 0) };
            line.Children.Add(Ui.Text(sub, 15, 400, Palette.TextMutedBrush, wrap: true));
            if (after is not null) line.Children.Add(after);
            head.Children.Add(line);
        }
        return head;
    }

    /// <summary>The small uppercase label over a figure.</summary>
    public static TextBlock StatLabel(string text)
    {
        var label = Ui.Caps(text, 13, 0.09);
        label.IsTextSelectionEnabled = false;
        return label;
    }

    /// <summary>
    /// A byte figure with its unit set smaller - "685 GB" - counting up to
    /// its value. <paramref name="size"/> is the web's --fs-stat.
    /// </summary>
    public static StackPanel BytesValue(long bytes, Brush? brush = null, double size = 44)
    {
        var (_, unit) = Format.SplitBytes(bytes);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = size * 0.14 };
        var value = Ui.Text("", size, 650, brush ?? Palette.TextBrush, -0.035, numeric: true);
        // Count up in the final unit, so "685" never passes through "685 MB".
        var divisor = Math.Pow(1024, Array.IndexOf(new[] { "B", "KB", "MB", "GB", "TB", "PB" }, unit));
        var decimals = Format.SplitBytes(bytes).Value.Contains('.') ? Format.SplitBytes(bytes).Value.Split('.')[1].Length : 0;
        CountUp.Apply(value, bytes / divisor, v => v.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture));
        row.Children.Add(value);
        var unitText = Ui.Text(unit, size * 0.42, 600, Palette.TextMutedBrush);
        unitText.VerticalAlignment = VerticalAlignment.Bottom;
        unitText.Margin = new Thickness(0, 0, 0, size * 0.14);
        row.Children.Add(unitText);
        return row;
    }

    /// <summary>
    /// One score card. All carry sent and received: the split IS the shape of
    /// a machine that seeds or uploads.
    /// </summary>
    public static Border StatCard(string label, Totals totals, bool accent = false, string? range = null, int delay = 0)
    {
        var stack = new StackPanel();
        stack.Children.Add(StatLabel(label));
        var value = BytesValue(totals.Total, accent ? Palette.AccentBrightBrush : null);
        value.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(value);
        stack.Children.Add(Io(totals.Sent, totals.Received));
        if (range is not null)
        {
            var r = Ui.Text(range, 13, 400, Palette.TextFaintBrush);
            r.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(r);
        }
        var card = Ui.Card(stack, new Thickness(25.6), hover: true);
        Ui.Rise(card, delay);
        return card;
    }

    /// <summary>"↑ 120 GB  ↓ 565 GB".</summary>
    public static StackPanel Io(long sent, long received)
    {
        var io = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Margin = new Thickness(0, 7, 0, 0) };
        var up = Ui.Text($"↑ {Format.Bytes(sent)}", 15, 400, Palette.TextMutedBrush, numeric: true);
        Ui.SetTip(up, "Uploaded");
        var down = Ui.Text($"↓ {Format.Bytes(received)}", 15, 400, Palette.TextMutedBrush, numeric: true);
        Ui.SetTip(down, "Downloaded");
        io.Children.Add(up);
        io.Children.Add(down);
        return io;
    }

    /// <summary>A figure card: label, a value, a line under it (Sync Status, app detail).</summary>
    public static Border FigureCard(string label, UIElement value, string? sub, int delay = 0)
    {
        var stack = new StackPanel();
        stack.Children.Add(StatLabel(label));
        if (value is FrameworkElement fe) fe.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(value);
        if (sub is not null)
        {
            var s = Ui.Text(sub, 15, 400, Palette.TextMutedBrush, wrap: true);
            s.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(s);
        }
        var card = Ui.Card(stack, new Thickness(25.6), hover: true);
        Ui.Rise(card, delay);
        return card;
    }

    /// <summary>
    /// The right-aligned callout in a card head: "HEAVIEST DAY  Jul 6" over the
    /// figure in the accent.
    /// </summary>
    public static StackPanel Callout(string label, string date, string value)
    {
        var box = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9, HorizontalAlignment = HorizontalAlignment.Right };
        var l = Ui.Caps(label, 12, 0.08);
        l.VerticalAlignment = VerticalAlignment.Bottom;
        head.Children.Add(l);
        head.Children.Add(Ui.Text(date, 15, 500, Palette.TextMutedBrush));
        box.Children.Add(head);
        var v = Ui.Text(value, 21.6, 650, Palette.AccentBrightBrush, -0.02, numeric: true);
        v.HorizontalAlignment = HorizontalAlignment.Right;
        box.Children.Add(v);
        return box;
    }

    public static FitGrid Grid(double min, params UIElement[] children)
    {
        var grid = FitGrid.AutoFit(min, 18.4);
        foreach (var c in children) grid.Children.Add(c);
        return grid;
    }

    /// <summary>A red-bordered alert, for a genuinely failing collector.</summary>
    public static Border Alert(string title, string body, string? detail = null)
    {
        var stack = new StackPanel();
        stack.Children.Add(Ui.Text(title, 20.8, 600, Palette.WarnBrush));
        var b = Ui.Text(body, 15.5, 400, Palette.TextBrush, wrap: true);
        b.Margin = new Thickness(0, 6, 0, 0);
        stack.Children.Add(b);
        if (detail is not null)
        {
            var d = Ui.Paragraph(detail, 14.5, Palette.TextMutedBrush);
            d.Margin = new Thickness(0, 9, 0, 0);
            stack.Children.Add(d);
        }
        var alert = new Border
        {
            Padding = new Thickness(21, 18, 21, 18),
            CornerRadius = new CornerRadius(Ui.Radius),
            BorderBrush = Palette.WarnBrush,
            BorderThickness = new Thickness(1),
            Background = Palette.WarnDimBrush,
            Margin = new Thickness(0, 0, 0, 18.4),
            Child = stack,
        };
        Ui.Rise(alert);
        return alert;
    }

    /// <summary>The centred "nothing here" message, with an optional action.</summary>
    public static StackPanel Empty(string title, string body, UIElement? action = null, string? code = null)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(16, 64, 16, 64), MaxWidth = 620 };
        var h = Ui.Text(title, 35, 600, spacing: -0.02);
        h.HorizontalAlignment = HorizontalAlignment.Center;
        stack.Children.Add(h);
        var p = Ui.Text(body, 16, 400, Palette.TextMutedBrush, wrap: true);
        p.TextAlignment = TextAlignment.Center;
        p.Margin = new Thickness(0, 13, 0, 22);
        stack.Children.Add(p);
        if (code is not null)
        {
            var pre = new Border
            {
                Background = Palette.SurfaceBrush,
                BorderBrush = Palette.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Ui.RadiusSmall),
                Padding = new Thickness(18, 14, 18, 14),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 22),
                Child = Ui.Mono(code, 14),
            };
            stack.Children.Add(pre);
        }
        if (action is FrameworkElement a)
        {
            a.HorizontalAlignment = HorizontalAlignment.Center;
            stack.Children.Add(a);
        }
        return stack;
    }

    /// <summary>A mono cell (dates in the run table, raw SRUM identities).</summary>
    public static TextBlock MonoCell(string text, Brush? brush = null, double size = 14.5) => Ui.Mono(text, size, brush ?? Palette.TextBrush);
}
