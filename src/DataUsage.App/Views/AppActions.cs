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
/// Renaming an app and giving it a logo, from wherever its name is shown.
/// </summary>
/// <remarks>
/// <para>A rename is stored in the database (it is part of the history, so it
/// is backed up and survives a reset), renames the FAMILY, and keeps the
/// original name's colour and logo. The same rules as the web dashboard's
/// route: a name another app shows is refused (they would share a colour and a
/// logo), as are Other and Unattributed; an empty name clears the rename.</para>
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
        ToolTipService.SetToolTip(button, Ui.TipContent($"Rename {name}, or set its logo"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, $"Rename {name}");
        button.Flyout = Editor(ctx, key, name, baseName);
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

        async void Save()
        {
            var (err, _) = await Task.Run(() => Rename(ctx, key, box.Text, baseName));
            if (err != RenameError.None)
            {
                error.Text = Renames.Message(err);
                error.Visibility = Visibility.Visible;
                return;
            }
            flyout.Hide();
            await ctx.State.ReloadAsync();
        }
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
        var dir = ctx.State.DataDir ?? throw new InvalidOperationException("no data folder");
        var current = ctx.State.Queries!.AppNamesByKey().ToDictionary(kv => kv.Key, kv => kv.Value.Name);
        using var db = UsageDb.Open(AppPaths.DatabasePath(dir), AppPaths.ReadLocation().AllowSystemDrive || Environment.GetEnvironmentVariable(AppPaths.DataDirVariable) is { Length: > 0 });
        return Renames.Save(db, key, requested, current, baseName);
    }

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
    /// A name cell: the mark, the name (a link when the app earns a page), the
    /// kind badge for non-path apps, and the pencil on hover.
    /// </summary>
    public static StackPanel NameCell(PageContext ctx, string key, string name, string baseName, string kind, bool detailed, double size = 15.5)
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
        var pencil = Pencil(ctx, key, name, baseName);
        cell.Children.Add(pencil);
        cell.PointerEntered += (_, _) => pencil.Opacity = 1;
        cell.PointerExited += (_, _) => { if (pencil.FocusState == FocusState.Unfocused) pencil.Opacity = 0; };
        AcceptLogoDrop(ctx, cell, name);
        return cell;
    }
}
