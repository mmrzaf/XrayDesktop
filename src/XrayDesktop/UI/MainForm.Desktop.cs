using System.Text;
using XrayDesktop.App;
using XrayDesktop.Config;
using XrayDesktop.Diagnostics;
using XrayDesktop.Storage;

namespace XrayDesktop.UI;

internal sealed partial class MainForm
{
    private readonly DesktopPreferenceStore desktopStore = new(DataDirectory);
    private DesktopPreferences desktopPreferences = DesktopPreferences.Defaults;
    private bool desktopUnreadable;
    private readonly CheckBox minimizeToTray = new() { Text = "Hide to the notification area when minimized", AutoSize = true };
    private readonly CheckBox rememberWindow = new() { Text = "Remember window location and size", AutoSize = true, Checked = true };
    private readonly CheckBox autoScroll = new() { Text = "Auto-scroll logs", AutoSize = true, Checked = true };
    private readonly Label desktopNotice = new() { AutoSize = true, Text = "Preferences are stored next to the app. Nothing is logged to disk." };
    private readonly NotifyIcon tray = new() { Icon = StatusIcons.Off, Text = Bootstrap.ProductName };
    private readonly ContextMenuStrip trayMenu = new();
    private readonly ToolStripMenuItem trayShow = new("Open Xray Desktop"), trayToggle = new("Connect"), trayExit = new("Exit");
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
        trayMenu.Items.AddRange([trayShow, trayToggle, new ToolStripSeparator(), trayExit]);
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += (_, _) => ShowWindow();
        trayShow.Click += (_, _) => ShowWindow();
        trayToggle.Click += async (_, _) =>
        {
            if (busy || closing) return;
            ShowWindow(); // Confirmations and errors keep a visible owner.
            await RunAsync(ToggleConnectionAsync);
        };
        trayExit.Click += (_, _) => { if (closing) return; exitRequested = true; ShowWindow(); Close(); };
    }
    private void UpdateTray(LinkState state, bool idle)
    {
        trayToggle.Text = connection.Active || session.HasChild ? "Disconnect" : "Connect";
        trayToggle.Enabled = idle && (connection.Active || session.HasChild || Selected is not null);
        trayExit.Enabled = !closing;
        var icon = state switch { LinkState.Connected => StatusIcons.On, LinkState.Disconnected => StatusIcons.Off, _ => StatusIcons.Busy };
        if (!ReferenceEquals(tray.Icon, icon)) tray.Icon = icon;
        var tooltip = state switch
        {
            LinkState.Connected => $"{Bootstrap.ProductName} — connected",
            LinkState.Unstable => $"{Bootstrap.ProductName} — connection problem",
            LinkState.Connecting => $"{Bootstrap.ProductName} — connecting…",
            _ => $"{Bootstrap.ProductName} — not connected"
        };
        if (tray.Text != tooltip) tray.Text = tooltip;
    }
    internal void ShowWindow()
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
        if (desktopUnreadable && MessageBox.Show(this, "Replace the unreadable window preferences?", "Preferences", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var value = new DesktopPreferences(1, minimizeToTray.Checked, rememberWindow.Checked, autoScroll.Checked, rememberWindow.Checked ? CapturePlacement() : null);
        await Task.Run(() => desktopStore.Save(value), token);
        desktopPreferences = value; desktopUnreadable = false;
        desktopNotice.Text = "Preferences saved.";
    }
    private async Task ResetDesktopAsync(CancellationToken token)
    {
        if (MessageBox.Show(this, "Reset window and diagnostic preferences to defaults? Profiles and proxy recovery data are not touched.", "Reset preferences", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await Task.Run(() => desktopStore.Save(DesktopPreferences.Defaults), token);
        desktopPreferences = DesktopPreferences.Defaults; desktopUnreadable = false; ApplyDesktopPreferences();
        await Task.Run(() => probePreferences.Save(ProbePreferences.Defaults), token);
        probePreferencesUnreadable = false; diagnostics.ApplyPreferences(ProbePreferences.Defaults);
        desktopNotice.Text = "Preferences reset.";
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
        { MessageBox.Show(this, "Window position could not be saved. The connection was stopped and your Windows settings were handled.", "Preferences", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
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
