using System.Text;
using XrayDesktop.Config;
using XrayDesktop.Diagnostics;
using XrayDesktop.Storage;

namespace XrayDesktop.UI;

internal sealed partial class MainForm
{
    private readonly DesktopPreferenceStore desktopStore = new(Path.Combine(AppContext.BaseDirectory, "data"));
    private DesktopPreferences desktopPreferences = DesktopPreferences.Defaults;
    private bool desktopUnreadable;
    private readonly CheckBox minimizeToTray = new() { Text = "Minimize to notification area (Close still exits)", AutoSize = true };
    private readonly CheckBox rememberWindow = new() { Text = "Remember window location and size", AutoSize = true, Checked = true };
    private readonly CheckBox autoScroll = new() { Text = "Auto-scroll logs", AutoSize = true, Checked = true };
    private readonly Label desktopNotice = new() { AutoSize = true, Text = "Preferences are stored locally. No automatic core start or disk log recording." };
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Text = Bootstrap.ProductName };
    private readonly ContextMenuStrip trayMenu = new();
    private readonly ToolStripMenuItem trayShow = new("Show window"), trayStart = new("Start Xray"), trayStop = new("Stop Xray"),
        trayEnable = new("Enable system proxy"), trayRestore = new("Restore system proxy"), trayExit = new("Exit (restore and stop)");
    private FormWindowState lastVisibleState = FormWindowState.Normal;
    private bool initialized;
    private bool desktopDisposed;

    private static void AddScrollablePage(TabPage tab, Control[] controls)
    {
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8) };
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, RowCount = controls.Length };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < controls.Length; i++)
        {
            table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(controls[i], 0, i);
        }
        viewport.Controls.Add(table); tab.Controls.Add(viewport);
        void FitLabels()
        {
            var width = Math.Max(120, viewport.ClientSize.Width - viewport.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 12);
            foreach (var label in controls.SelectMany(Flatten).OfType<Label>()) label.MaximumSize = new Size(width, 0);
        }
        static IEnumerable<Control> Flatten(Control control)
        {
            yield return control;
            foreach (Control child in control.Controls)
                foreach (var descendant in Flatten(child)) yield return descendant;
        }
        viewport.SizeChanged += (_, _) => FitLabels();
        FitLabels();
    }

    private void ConfigureTray()
    {
        trayMenu.Items.AddRange([trayShow, new ToolStripSeparator(), trayStart, trayStop, trayEnable, trayRestore, new ToolStripSeparator(), trayExit]);
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (_, _) => ShowWindow();
        trayShow.Click += (_, _) => ShowWindow();
        trayStart.Click += async (_, _) => await RunTrayAsync(StartAsync);
        trayStop.Click += async (_, _) => await RunTrayAsync(async _ => { await StopCoreAsync(); });
        trayEnable.Click += async (_, _) => await RunTrayAsync(EnableProxyAsync);
        trayRestore.Click += async (_, _) => await RunTrayAsync(_ => RestoreProxyAsync());
        trayExit.Click += (_, _) => { if (!busy && !closing) { ShowWindow(); Close(); } };
    }
    private static bool CommandEnabled(FlowLayoutPanel bar, string name) => bar.Controls.OfType<Button>().Any(button => button.Name == name && button.Enabled);
    private async Task RunTrayAsync(Func<CancellationToken, Task> command)
    {
        if (busy || closing) return;
        ShowWindow(); // All confirmation/error dialogs retain a visible owner.
        await RunAsync(command);
    }
    private void ShowWindow()
    {
        if (WindowState == FormWindowState.Minimized) WindowState = lastVisibleState;
        if (!Visible) Show();
        Activate();
    }
    private WindowPlacement CapturePlacement()
    {
        var state = WindowState == FormWindowState.Minimized ? lastVisibleState : WindowState;
        var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
        return new(bounds.X, bounds.Y, bounds.Width, bounds.Height, state == FormWindowState.Maximized);
    }
    private void RestorePlacement(WindowPlacement placement)
    {
        var primary = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromControl(this).WorkingArea;
        var bounds = placement.Fit(Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray(), primary, MinimumSize);
        MinimumSize = new Size(Math.Min(MinimumSize.Width, bounds.Width), Math.Min(MinimumSize.Height, bounds.Height));
        StartPosition = FormStartPosition.Manual; Bounds = bounds;
        if (placement.Maximized) WindowState = FormWindowState.Maximized;
    }
    private void ApplyDesktopPreferences()
    {
        minimizeToTray.Checked = desktopPreferences.MinimizeToTray;
        rememberWindow.Checked = desktopPreferences.RememberWindow;
        autoScroll.Checked = desktopPreferences.AutoScrollLogs;
    }
    private async Task SaveDesktopAsync(CancellationToken token)
    {
        if (desktopUnreadable && MessageBox.Show(this, "Replace unreadable desktop preferences?", "Preferences", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var value = new DesktopPreferences(1, minimizeToTray.Checked, rememberWindow.Checked, autoScroll.Checked, rememberWindow.Checked ? CapturePlacement() : null);
        await Task.Run(() => desktopStore.Save(value), token);
        desktopPreferences = value; desktopUnreadable = false;
        desktopNotice.Text = "Desktop preferences saved. Minimize behavior is now active; Close still restores and exits.";
    }
    private async Task ResetDesktopAsync(CancellationToken token)
    {
        if (MessageBox.Show(this, "Reset desktop and diagnostic preferences to defaults? Profile files and proxy recovery are untouched. This also replaces unreadable preference files.", "Reset preferences", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await Task.Run(() => desktopStore.Save(DesktopPreferences.Defaults), token);
        desktopPreferences = DesktopPreferences.Defaults; desktopUnreadable = false; ApplyDesktopPreferences();
        await Task.Run(() => probePreferences.Save(ProbePreferences.Defaults), token);
        probePreferencesUnreadable = false; diagnostics.ApplyPreferences(ProbePreferences.Defaults);
        enableOnStart.Checked = false;
        desktopNotice.Text = "Desktop and diagnostic preferences reset.";
    }
    private async Task ClearProfilesAsync(CancellationToken token)
    {
        if (session.HasChild) throw new InvalidOperationException("Stop the owned core before clearing profile references.");
        if (MessageBox.Show(this, "Clear every saved profile reference, including the active selection? No configuration file will be deleted. Unreadable profile settings will be replaced.", "Clear profile list", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var empty = new ProfileSettings(1, [], null);
        await Task.Run(() => store.Save(empty), token);
        settings = empty; settingsUnreadable = false; RefreshProfiles(null); await InspectAsync(token);
    }
    private async Task SavePlacementOnExitAsync()
    {
        if (!initialized || desktopUnreadable || !desktopPreferences.RememberWindow) return;
        try
        {
            var value = desktopPreferences with { Placement = CapturePlacement() };
            await Task.Run(() => desktopStore.Save(value));
        }
        catch (Exception)
        { MessageBox.Show(this, "Window placement could not be saved. Core stop has completed; any unresolved proxy recovery is retained.", "Preferences", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
    private async Task SaveRawLogsAsync(CancellationToken token)
    {
        if (logs.TextLength == 0 || MessageBox.Show(this, "Raw logs may contain credentials, addresses and private paths. Save locally anyway?", "Sensitive data", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        using var dialog = new SaveFileDialog { Filter = "Text logs (*.log)|*.log|Text files (*.txt)|*.txt", FileName = "xray-session.log", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var snapshot = Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, session.Log.ReadAfter(0).Lines) + Environment.NewLine);
        var path = dialog.FileName;
        await Task.Run(() => AtomicFile.Write(path, snapshot), token);
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !desktopDisposed)
        {
            desktopDisposed = true; timer.Dispose(); tips.Dispose(); tray.Visible = false; tray.Dispose(); trayMenu.Dispose();
        }
        base.Dispose(disposing);
    }

}
