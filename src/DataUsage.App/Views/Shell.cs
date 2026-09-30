using System.Diagnostics;
using DataUsage.App.Theme;
using DataUsage.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DataUsage.App.Views;

/// <summary>Small bridges to Windows (Explorer, a message box) and the shell's own screens.</summary>
public static class Shell
{
    public static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Explorer not starting is not worth a crash.
        }
    }

    public static async Task ShowMessage(Window window, string title, string message)
    {
        if (window.Content?.XamlRoot is null) return;
        var dialog = new ContentDialog
        {
            Title = title,
            Content = Ui.Paragraph(message, 14.5, Palette.TextMutedBrush),
            CloseButtonText = "OK",
            XamlRoot = window.Content.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
        };
        await dialog.ShowAsync();
    }

    /// <summary>An empty database: nothing has been collected yet.</summary>
    public static UIElement NoData(PageContext ctx)
    {
        var sync = Ui.Button("Sync now", primary: true);
        sync.Click += async (_, _) => await ((MainWindow)ctx.Window).SyncNowAsync();
        return Parts.Empty("No data yet",
            "Nothing has been collected into this data folder yet. The scheduled task collects every hour; Sync now runs a collection straight away. If it fails, the tasks may not be registered - run tools\\Install.ps1 once.",
            sync);
    }

    /// <summary>
    /// First run, or after a reset: where the history lives. The default is on
    /// D:, because a database on the system drive is destroyed by the reset
    /// this app exists to survive; choosing C: anyway takes a deliberate tick.
    /// </summary>
    public static UIElement Setup(PageContext ctx, Action chosen)
    {
        var stack = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Center, Padding = new Thickness(16, 56, 16, 56), Spacing = 14 };
        stack.Children.Add(Ui.Text("Where should the history live?", 35, 600, spacing: -0.02, wrap: true));
        stack.Children.Add(Ui.Paragraph(
            "Windows keeps only 30 to 60 days of per-app network usage, and a Windows reset erases even that. This app keeps it permanently - in a folder that must not be on the drive Windows is installed on. Point it at the same folder after a reset and the whole history is back.",
            15.5, Palette.TextMutedBrush));

        var drive = Path.GetPathRoot(AppPaths.DefaultDataDir)!;
        var box = new TextBox { Text = Directory.Exists(drive) ? AppPaths.DefaultDataDir : "", FontFamily = Fonts.Sans, FontSize = 15, PlaceholderText = AppPaths.DefaultDataDir };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Data folder");
        var browse = Ui.Button("Browse…");
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(box);
        Grid.SetColumn(browse, 1);
        row.Children.Add(browse);
        stack.Children.Add(row);

        var systemDrive = new CheckBox { Content = Ui.Text("Keep it on the system drive anyway. I understand a Windows reset will erase it.", 14.5, 400, Palette.WarnBrush, wrap: true), Visibility = Visibility.Collapsed };
        stack.Children.Add(systemDrive);
        var error = Ui.Text("", 14, 500, Palette.WarnBrush, wrap: true);
        stack.Children.Add(error);
        var go = Ui.Button("Use this folder", primary: true);
        stack.Children.Add(go);
        stack.Children.Add(Ui.Paragraph("An existing history in the folder is used as it is. Google Drive or OneDrive can back the folder up; restore from its `data-usage.db`, which is always a complete copy.", 13.5, Palette.TextFaintBrush));

        void Check()
        {
            var text = box.Text.Trim();
            var onSystem = text.Length > 0 && Path.IsPathFullyQualified(text) && AppPaths.IsOnSystemDrive(text);
            systemDrive.Visibility = onSystem ? Visibility.Visible : Visibility.Collapsed;
            go.IsEnabled = text.Length > 0 && (!onSystem || systemDrive.IsChecked == true);
        }
        box.TextChanged += (_, _) => Check();
        systemDrive.Click += (_, _) => Check();
        Check();

        browse.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(ctx.Window));
            if (await picker.PickSingleFolderAsync() is { } folder) box.Text = folder.Path;
        };
        go.Click += (_, _) =>
        {
            try
            {
                var dir = Path.GetFullPath(box.Text.Trim());
                Directory.CreateDirectory(dir);
                AppPaths.WriteLocation(dir, AppPaths.IsOnSystemDrive(dir));
                chosen();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                error.Text = ex.Message;
            }
        };
        return stack;
    }

    /// <summary>What this PC is called, and which app the overview splits out.</summary>
    public static async Task EditDevice(PageContext ctx)
    {
        var state = ctx.State;
        if (ctx.Window.Content?.XamlRoot is null || state.DataDir is null) return;
        var name = new TextBox { Text = state.Device.DeviceLabel ?? "", PlaceholderText = Environment.MachineName, Header = "Device name", FontFamily = Fonts.Sans };
        var split = new ComboBox { Header = "Split out on the overview", HorizontalAlignment = HorizontalAlignment.Stretch, FontFamily = Fonts.Sans };
        split.Items.Add("None");
        var names = await Task.Run(() => state.Queries?.ByApp(Core.Query.Scope.All).Apps.Take(25).Select(a => a.BaseName).ToList() ?? []);
        foreach (var n in names) split.Items.Add(n);
        if (state.Device.Split is { } s && !names.Contains(s)) split.Items.Add(s);
        split.SelectedItem = state.Device.Split ?? "None";
        var content = new StackPanel { Spacing = 14, Width = 380 };
        content.Children.Add(name);
        content.Children.Add(split);
        content.Children.Add(Ui.Paragraph("For a machine where one app dwarfs the rest, splitting it out shows everything else at a readable scale.", 13.5, Palette.TextFaintBrush));
        var dialog = new ContentDialog
        {
            Title = "This PC",
            Content = content,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = ctx.Window.Content.XamlRoot,
            RequestedTheme = ElementTheme.Dark,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        var device = DeviceSettings.Load(state.DataDir);
        device.DeviceLabel = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim();
        device.SplitApp = split.SelectedItem is string chosen && chosen != "None" ? chosen : null;
        state.SaveDevice(device);
    }
}
