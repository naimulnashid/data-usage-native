using DataUsage.App.Controls;
using DataUsage.App.Theme;
using DataUsage.Core;
using DataUsage.Core.Data;
using DataUsage.Core.Naming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace DataUsage.App.Views;

/// <summary>
/// Renaming an app, choosing its colour and giving it a logo, from wherever its
/// name is shown.
/// </summary>
/// <remarks>
/// <para>A rename is stored in the database (it is part of the history, so it
/// is backed up and survives a reset), renames the FAMILY, and keeps the
/// original name's colour and logo. The same rules as the web dashboard's
/// route: a name another app shows is refused (they would share a colour and a
/// logo), as are Other and Unattributed; an empty name clears the rename.</para>
/// <para>A colour is stored beside the renames (<see cref="ColorOverrides"/>)
/// and wins over the brand and palette colours everywhere the app is drawn.
/// Picked on the spectrum or typed as a hex code; "Default colour" drops it.</para>
/// <para>A logo is a file in the data folder's logos\, named after the app. Set
/// logo copies one in; dropping an image on the name does the same.</para>
/// </remarks>
public static class AppActions
{
    /// <summary>The pencil beside a name, faint until the pointer is near.</summary>
    public static Button Pencil(PageContext ctx, string key, string name, string baseName, double size = 14, bool alwaysVisible = false)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = size, Foreground = Palette.TextFaintBrush },
            Padding = new Thickness(5),
            MinWidth = 0,
            MinHeight = 0,
            Background = Palette.TransparentBrush,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = alwaysVisible ? 1 : 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) },
        };
        button.Resources["ButtonBackgroundPointerOver"] = Palette.SurfaceHoverBrush;
        button.Resources["ButtonBackgroundPressed"] = Palette.SurfaceHoverBrush;
        ToolTipService.SetToolTip(button, Ui.TipContent($"Rename {name}, or set its colour or logo"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Rename {name}");
        // Built on click, not with the row: a colour picker per table row is
        // a lot of controls nobody opened, and a fresh one reads the current
        // name and colour.
        button.Click += (_, _) => Editor(ctx, key, name, baseName).ShowAt(button);
        // Reachable by keyboard even while invisible.
        button.GotFocus += (_, _) => button.Opacity = 1;
        return button;
    }

    private static Flyout Editor(PageContext ctx, string key, string name, string baseName)
    {
        var stack = new StackPanel { Width = 300, Spacing = 10 };
        stack.Children.Add(Ui.Text("Display name", 14, 600));
        var box = new TextBox { Text = name, MaxLength = Renames.MaxLength, FontFamily = Fonts.Sans, FontSize = 15 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Display name");
        stack.Children.Add(box);
        var error = Ui.Text("", 13, 500, Palette.WarnBrush, wrap: true);
        error.Visibility = Visibility.Collapsed;
        stack.Children.Add(error);
        if (name != baseName) stack.Children.Add(Ui.Text($"Originally \"{baseName}\". Clear the box to restore it.", 13, 400, Palette.TextFaintBrush, wrap: true));

        // ---- Colour: the stored hex (override included), on Windows' own
        // picker, whose hex box is where a brand's exact code is typed.
        var original = AppColors.Of(ctx.State.Colors, name).ToLowerInvariant();
        var custom = ctx.State.ColorOverrides.ContainsKey(key);
        var colors = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 0), BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0) };
        colors.Children.Add(Ui.Text("Colour", 14, 600));
        var picker = new ColorPicker
        {
            Color = Palette.HexOrOther(original),
            IsAlphaEnabled = false,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true,
            IsMoreButtonVisible = false,
            IsColorSliderVisible = true,
            // The swatch beside the name already previews it, and the flyout is
            // 300 wide: the default picker is half as wide again and pushes Save
            // below a short window.
            IsColorPreviewVisible = false,
            ColorSpectrumShape = ColorSpectrumShape.Box,
            Width = 300,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(picker, $"Colour for {name}");
        colors.Children.Add(picker);
        var reset = Ui.Button("Default colour", fontSize: 14, padding: new Thickness(14, 7, 14, 7));
        reset.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        Ui.SetTip(reset, "Back to the brand or palette colour");
        colors.Children.Add(reset);
        stack.Children.Add(colors);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var save = Ui.Button("Save", primary: true, fontSize: 14, padding: new Thickness(16, 7, 16, 7));
        buttons.Children.Add(save);
        stack.Children.Add(buttons);

        var logos = new StackPanel { Spacing = 6, Margin = new Thickness(0, 6, 0, 0), BorderBrush = Palette.BorderBrush, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 12, 0, 0) };
        logos.Children.Add(Ui.Text("Logo", 14, 600));
        var logoRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var set = Ui.Button("Set logo…", fontSize: 14, padding: new Thickness(14, 7, 14, 7));
        var remove = Ui.Button("Remove logo", fontSize: 14, padding: new Thickness(14, 7, 14, 7));
        logoRow.Children.Add(set);
        logoRow.Children.Add(remove);
        logos.Children.Add(logoRow);
        logos.Children.Add(Ui.Text("Or drop an image onto the app's name. SVG, PNG, JPG, WebP, GIF, BMP or ICO.", 12.5, 400, Palette.TextFaintBrush, wrap: true));
        stack.Children.Add(logos);

        var flyout = new Flyout { Content = stack, Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft };
        flyout.Opening += (_, _) =>
        {
            remove.IsEnabled = ctx.State.Logos.HasOwnLogo(name);
            error.Visibility = Visibility.Collapsed;
        };
        flyout.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };

        void Fail(string message)
        {
            error.Text = message;
            error.Visibility = Visibility.Visible;
        }

        // Saves what changed: the name, then the colour.
        async void Save()
        {
            if (box.Text.Trim() != name)
            {
                var text = box.Text;
                var (err, _) = await Task.Run(() => Rename(ctx, key, text, baseName));
                if (err != RenameError.None) { Fail(Renames.Message(err)); return; }
            }
            var hex = ToHex(picker.Color);
            if (hex != original)
            {
                var (err, _) = await Task.Run(() => Recolor(ctx, key, hex));
                if (err != ColorError.None) { Fail(ColorOverrides.Message(err)); return; }
            }
            flyout.Hide();
            await ctx.State.ReloadAsync();
        }
        reset.Click += async (_, _) =>
        {
            var (err, _) = await Task.Run(() => Recolor(ctx, key, ""));
            if (err != ColorError.None) { Fail(ColorOverrides.Message(err)); return; }
            flyout.Hide();
            await ctx.State.ReloadAsync();
        };
        save.Click += (_, _) => Save();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            e.Handled = true;
            Save();
        };
        set.Click += async (_, _) =>
        {
            flyout.Hide();
            await PickLogo(ctx, name);
        };
        remove.Click += (_, _) =>
        {
            flyout.Hide();
            try { ctx.State.Logos.RemoveLogo(name); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = Shell.ShowMessage(ctx.Window, "Could not remove the logo", ex.Message); }
        };
        return flyout;
    }

    private static (RenameError, string?) Rename(PageContext ctx, string key, string requested, string baseName)
    {
        var current = ctx.State.Queries!.AppNamesByKey();
        using var db = OpenForWrite(ctx);
        return Renames.Save(db, key, requested, current, baseName);
    }

    /// <summary>Stores a colour, or clears it with an empty string.</summary>
    private static (ColorError, string?) Recolor(PageContext ctx, string key, string requested)
    {
        var known = ctx.State.Queries!.AppNamesByKey().Keys.ToHashSet();
        using var db = OpenForWrite(ctx);
        return ColorOverrides.Save(db, key, requested, known);
    }

    private static Microsoft.Data.Sqlite.SqliteConnection OpenForWrite(PageContext ctx)
    {
        var dir = ctx.State.DataDir ?? throw new InvalidOperationException("no data folder");
        return UsageDb.Open(AppPaths.DatabasePath(dir), AppPaths.ReadLocation().AllowSystemDrive || Environment.GetEnvironmentVariable(AppPaths.DataDirVariable) is { Length: > 0 });
    }

    private static string ToHex(Windows.UI.Color c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    public static async Task PickLogo(PageContext ctx, string name)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        foreach (var ext in AppIcons.Renderable) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(ctx.Window));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        SetLogo(ctx, name, file.Path);
    }

    private static void SetLogo(PageContext ctx, string name, string path)
    {
        try { ctx.State.Logos.SetLogo(name, path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _ = Shell.ShowMessage(ctx.Window, "Could not set the logo", ex.Message); }
    }

    /// <summary>Lets an image be dropped onto an element to become the app's logo.</summary>
    public static void AcceptLogoDrop(PageContext ctx, UIElement target, string name)
    {
        target.AllowDrop = true;
        target.DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = $"Set as the logo for {name}";
        };
        target.Drop += async (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.OfType<StorageFile>().FirstOrDefault(f => AppIcons.Renderable.Contains(Path.GetExtension(f.Path))) is { } file)
                SetLogo(ctx, name, file.Path);
        };
    }

    /// <summary>
    /// The "..." beside the pencil: hide the app from the By App page, or
    /// merge it into another. See Naming/AppPrefs.cs for what each means -
    /// hiding is that page's view only, merging is read everywhere.
    /// </summary>
    public static Button Menu(PageContext ctx, string key, string name, bool hidden, IReadOnlyList<(string Key, string Name)> candidates, bool alwaysVisible = false)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = "\uE712", FontSize = 14, Foreground = Palette.TextFaintBrush },
            Padding = new Thickness(5),
            MinWidth = 0,
            MinHeight = 0,
            Background = Palette.TransparentBrush,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = alwaysVisible ? 1 : 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(150) },
        };
        button.Resources["ButtonBackgroundPointerOver"] = Palette.SurfaceHoverBrush;
        button.Resources["ButtonBackgroundPressed"] = Palette.SurfaceHoverBrush;
        ToolTipService.SetToolTip(button, Ui.TipContent("Hide or merge"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"More for {name}");
        button.GotFocus += (_, _) => button.Opacity = 1;

        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var hide = new MenuFlyoutItem
        {
            Text = hidden ? "Show on this page" : "Hide from this page",
            Icon = new FontIcon { Glyph = hidden ? "\uE890" : "\uED1A" },
        };
        hide.Click += async (_, _) =>
        {
            await Task.Run(() =>
            {
                using var db = OpenForWrite(ctx);
                AppPrefs.SetHidden(db, key, !hidden);
            });
            await ctx.State.ReloadAsync();
        };
        menu.Items.Add(hide);
        var others = candidates.Where(c => c.Key != key).OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        var merge = new MenuFlyoutItem { Text = "Merge into…", Icon = new FontIcon { Glyph = "\uE71B" }, IsEnabled = others.Count > 0 };
        merge.Click += (_, _) => MergeFlyout(ctx, key, name, others).ShowAt(button);
        menu.Items.Add(merge);
        button.Flyout = menu;
        return button;
    }

    private static Flyout MergeFlyout(PageContext ctx, string key, string name, IReadOnlyList<(string Key, string Name)> others)
    {
        var stack = new StackPanel { Width = 300, Spacing = 10 };
        stack.Children.Add(Ui.Text($"Count {name} as part of", 14, 600, wrap: true));
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Choose an app", FontFamily = Fonts.Sans };
        foreach (var o in others) box.Items.Add(o.Name);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, $"App to merge {name} into");
        stack.Children.Add(box);
        stack.Children.Add(Ui.Text("Its traffic is then shown as that app's on every page. Separate it again from that app's Merged apps.", 12.5, 400, Palette.TextFaintBrush, wrap: true));
        var error = Ui.Text("", 13, 500, Palette.WarnBrush, wrap: true);
        error.Visibility = Visibility.Collapsed;
        stack.Children.Add(error);
        var go = Ui.Button("Merge", primary: true, fontSize: 14, padding: new Thickness(16, 7, 16, 7));
        go.HorizontalAlignment = HorizontalAlignment.Right;
        go.IsEnabled = false;
        box.SelectionChanged += (_, _) => go.IsEnabled = box.SelectedIndex >= 0;
        stack.Children.Add(go);
        var flyout = new Flyout { Content = stack, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        go.Click += async (_, _) =>
        {
            if (box.SelectedIndex < 0) return;
            var into = others[box.SelectedIndex].Key;
            var err = await Task.Run(() => Merge(ctx, key, into));
            if (err != MergeError.None)
            {
                error.Text = AppPrefs.Message(err);
                error.Visibility = Visibility.Visible;
                return;
            }
            flyout.Hide();
            await ctx.State.ReloadAsync();
        };
        return flyout;
    }

    /// <summary>Merges, or separates again with <paramref name="into"/> null.</summary>
    private static MergeError Merge(PageContext ctx, string key, string? into)
    {
        var known = ctx.State.Queries!.AppKeys();
        using var db = OpenForWrite(ctx);
        return AppPrefs.SetMerge(db, key, into, known);
    }

    /// <summary>Separate: undo a merge, from the target's Merged apps card.</summary>
    public static Button Separate(PageContext ctx, string key, string name)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = "\uE8C6", FontSize = 14, Foreground = Palette.TextMutedBrush },
            Padding = new Thickness(7),
            MinWidth = 0,
            MinHeight = 0,
            CornerRadius = new CornerRadius(15),
            Background = Palette.TransparentBrush,
            BorderBrush = Palette.BorderBrightBrush,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.Resources["ButtonBackgroundPointerOver"] = Palette.SurfaceHoverBrush;
        button.Resources["ButtonBorderBrushPointerOver"] = Palette.AccentBrush;
        ToolTipService.SetToolTip(button, Ui.TipContent($"Separate: show {name} as an app of its own again"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Separate {name}");
        button.Click += async (_, _) =>
        {
            var err = await Task.Run(() => Merge(ctx, key, null));
            if (err != MergeError.None) { _ = Shell.ShowMessage(ctx.Window, "Could not separate", AppPrefs.Message(err)); return; }
            await ctx.State.ReloadAsync();
        };
        return button;
    }

    /// <summary>
    /// A name cell: the mark, the name (a link when the app earns a page), the
    /// kind badge for non-path apps, and the pencil and the menu on hover.
    /// </summary>
    public static StackPanel NameCell(PageContext ctx, string key, string name, string baseName, string kind, bool detailed, double size = 15.5,
        bool hidden = false, IReadOnlyList<(string Key, string Name)>? candidates = null)
    {
        var state = ctx.State;
        var cell = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9.6, Background = Palette.TransparentBrush };
        cell.Children.Add(AppIconView.Create(name, state.Colors, state.Icons, 18));
        if (detailed)
        {
            var link = Ui.Link(name, () => ctx.Navigate(new Route(PageKind.App, key)), size, Palette.TextBrush);
            Ui.SetTip(link, $"{name} - open detail");
            cell.Children.Add(link);
        }
        else
        {
            var text = Ui.Text(name, size);
            Ui.SetTip(text, $"{name} - too little activity for a detail page");
            cell.Children.Add(text);
        }
        if (kind != "path") cell.Children.Add(Ui.Badge(kind == "appx" ? "store" : kind, size: 11.2, padding: new Thickness(7, 1.5, 7, 2)));
        if (hidden) cell.Children.Add(Ui.Badge("hidden", size: 11.2, padding: new Thickness(7, 1.5, 7, 2)));
        var pencil = Pencil(ctx, key, name, baseName);
        cell.Children.Add(pencil);
        var menu = candidates is null ? null : Menu(ctx, key, name, hidden, candidates);
        if (menu is not null) cell.Children.Add(menu);
        cell.PointerEntered += (_, _) =>
        {
            pencil.Opacity = 1;
            if (menu is not null) menu.Opacity = 1;
        };
        cell.PointerExited += (_, _) =>
        {
            if (pencil.FocusState == FocusState.Unfocused) pencil.Opacity = 0;
            if (menu is not null && menu.FocusState == FocusState.Unfocused) menu.Opacity = 0;
        };
        AcceptLogoDrop(ctx, cell, name);
        return cell;
    }
}
