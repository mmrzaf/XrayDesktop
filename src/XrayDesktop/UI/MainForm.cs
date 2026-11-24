using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using XrayDesktop.App;
using XrayDesktop.Config;
using XrayDesktop.Core;
using XrayDesktop.Diagnostics;
using XrayDesktop.Windows;

namespace XrayDesktop.UI;

internal sealed partial class MainForm : Form
{
    private readonly ProfileStore store = new(Path.Combine(AppContext.BaseDirectory, "data"));
    private readonly SessionController session = new(AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "data"));
    private readonly DiagnosticsPanel diagnostics = new();
    private readonly ProbePreferenceStore probePreferences = new(Path.Combine(AppContext.BaseDirectory, "data"));
    private bool probePreferencesUnreadable;
    private readonly DiagnosticRunner diagnosticRunner = new(AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "data"));
    private bool diagnosing;
    private readonly ProxyController proxy = new(new SystemProxy(), ProxyRecovery.ForCurrentUser());
    private readonly FlowLayoutPanel proxyBar = Bar();
    private readonly Label proxyStatus = new() { AutoSize = true, MaximumSize = new Size(380, 0) };
    private readonly CheckBox enableOnStart = new() { Text = "Enable proxy after Start (ask first)", AutoSize = true, Checked = false };
    private long lastProxyPoll;
    private Task<ProxyStatus>? proxyPoll;
    private readonly ComboBox profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, DisplayMember = "Name" };
    private readonly ComboBox endpoints = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    private readonly Label status = new() { AutoSize = true };
    private readonly Label headline = new() { AutoSize = true, MaximumSize = new Size(780, 0) };
    private readonly Label nextStep = new() { AutoSize = true, MaximumSize = new Size(780, 0) };
    private readonly Label proxyHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(780, 0) };
    private readonly Label details = new() { AutoSize = true, MaximumSize = new Size(780, 0) };
    private readonly Label profileSummary = new() { AutoSize = true, MaximumSize = new Size(780, 0), ForeColor = SystemColors.GrayText };
    private readonly Label warnings = new() { AutoSize = true, MaximumSize = new Size(780, 0) };
    private readonly Label validationStatus = new() { AutoSize = true, MaximumSize = new Size(780, 0) };
    private readonly TextBox validationOutput = new() { Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, Dock = DockStyle.Top, Height = 110, Visible = false, Font = new Font(FontFamily.GenericMonospace, 9) };
    private readonly Panel errorPanel = new() { Dock = DockStyle.Top, AutoSize = true, Visible = false, Padding = new Padding(10), BackColor = Color.FromArgb(253, 231, 233) };
    private readonly Label errorText = new() { AutoSize = true, MaximumSize = new Size(760, 0) };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage logTab = new("Logs");
    private readonly TabPage diagnosticTab = new("Diagnostics");
    private readonly ToolTip tips = new() { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 100, ShowAlways = true };
    private string? lastError;
    private IPEndPoint[] tcpListeners = [];
    private readonly TextBox logs = new() { Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9) };
    private readonly FlowLayoutPanel profileBar = Bar(), endpointBar = Bar(), coreBar = Bar(), logBar = Bar(), settingsBar = Bar();
    private readonly Button manual = new() { Text = "Manual endpoint", AutoSize = true };
    private readonly Button cancel = new() { Text = "Cancel operation", AutoSize = true, Enabled = false };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 300 };
    private ProfileSettings settings = new(1, [], null);
    private CancellationTokenSource? operation;
    private bool busy, closing, allowClose, updating, settingsUnreadable;
    private long logId;
    private int displayedLines;
    private Profile? Selected => profiles.SelectedItem as Profile;

    internal MainForm()
    {
        Text = $"{Bootstrap.ProductName} — {Bootstrap.Version}"; StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        headline.Font = new Font(Font, FontStyle.Bold);
        MinimumSize = new Size(700, 520); Size = new Size(1040, 760);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 3 };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        // A persistent summary keeps the current state, the next useful action and
        // the last error visible on every tab instead of hiding them in Logs.
        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = new Padding(0, 0, 0, 4) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var summary = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        summary.Controls.AddRange([headline, nextStep, status]);
        var headerActions = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(10, 0, 0, 0) };
        cancel.Click += (_, _) => operation?.Cancel();
        headerActions.Controls.AddRange([cancel, proxyStatus]);
        header.Controls.Add(summary, 0, 0); header.Controls.Add(headerActions, 1, 0);

        var errorActions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
        var troubleshoot = new Button { Text = "Run Local diagnostics", AutoSize = true };
        troubleshoot.Click += async (_, _) => { tabs.SelectedTab = diagnosticTab; await RunAsync(token => RunDiagnosticsAsync(DiagnosticScope.Local, token)); };
        var openLogs = new Button { Text = "Open Logs", AutoSize = true };
        openLogs.Click += (_, _) => tabs.SelectedTab = logTab;
        var dismissError = new Button { Text = "Dismiss", AutoSize = true };
        dismissError.Click += (_, _) => { lastError = null; Render(); };
        errorActions.Controls.AddRange([troubleshoot, openLogs, dismissError]);
        var errorLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        errorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        errorLayout.Controls.Add(errorText, 0, 0); errorLayout.Controls.Add(errorActions, 0, 1);
        errorPanel.Controls.Add(errorLayout);

        profileBar.Controls.Add(new Label { Text = "Profile:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) });
        profileBar.Controls.Add(profiles);
        Add(profileBar, "Import configuration…", ImportAsync, "Import", primary: true);
        Add(profileBar, "New configuration…", NewAsync, "New");
        Add(profileBar, "Edit…", EditAsync, "Edit");
        Add(profileBar, "Save as…", SaveAsAsync, "Save As");
        Add(profileBar, "Reload", InspectAsync, "Reload");
        Add(profileBar, "Remove reference", RemoveAsync, "Remove");
        endpointBar.Controls.Add(new Label { Text = "Listener to verify:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) }); endpointBar.Controls.Add(endpoints); endpointBar.Controls.Add(manual);
        Add(endpointBar, "Copy address", _ => CopyListenerAddress(), "Copy listener");
        Add(endpointBar, "Use free port…", FixPortAsync, "Use free port");
        manual.Click += async (_, _) => await RunAsync(_ => { SelectManualEndpoint(); return Task.CompletedTask; });
        Add(coreBar, "Check core", CheckAsync, "Check core");
        Add(coreBar, "Validate configuration", ValidateAsync, "Validate");
        Add(coreBar, "Run local checks", RunLocalChecksAsync, "Run local checks");
        Add(coreBar, "Start Xray", StartAsync, "Start", primary: true);
        Add(coreBar, "Stop Xray", async _ => { await StopCoreAsync(); }, "Stop");
        Add(coreBar, "Restart Xray", async token => { if (await StopCoreAsync()) await StartAsync(token); }, "Restart");
        Add(proxyBar, "Enable system proxy", EnableProxyAsync, "Enable system proxy");
        Add(proxyBar, "Restore system proxy", _ => RestoreProxyAsync(), "Restore system proxy");
        Add(proxyBar, "Review / resolve", _ => ReviewProxyAsync(), "Review / resolve");
        var addHttp = Add(proxyBar, "Add HTTP listener…", AddHttpListenerAsync, "Add HTTP listener");
        tips.SetToolTip(addHttp, "Create a managed copy with a local HTTP listener so Windows system proxy can be enabled. The original profile is not changed.");
        proxyBar.Controls.Add(enableOnStart);
        Add(logBar, "Find…", _ => { FindLog(); return Task.CompletedTask; }, "Find logs");
        Add(logBar, "Clear logs", _ => { session.Log.Clear(); logs.Clear(); displayedLines = 0; return Task.CompletedTask; }, "Clear logs");
        Add(logBar, "Copy raw logs", _ => { if (logs.TextLength > 0 && MessageBox.Show(this, "Raw logs may contain secrets, addresses and private paths. Copy anyway?", "Sensitive data", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) Clipboard.SetText(logs.Text); return Task.CompletedTask; }, "Copy raw logs");
        Add(logBar, "Save raw logs…", SaveRawLogsAsync, "Save raw logs");
        logBar.Controls.Add(autoScroll);

        var connectionTab = new TabPage("Start");
        AddScrollablePage(connectionTab, [
            Section("1. Choose a configuration", profileBar, details, profileSummary, warnings,
                Hint("Tip: drag a .json or .jsonc file onto this window to import it.")),
            Section("2. Choose the listener to verify (optional)", endpointBar,
                Hint("Pick the local HTTP or SOCKS port Xray will open. Choose None for a local-only run. An HTTP listener is required only if you plan to enable the Windows system proxy.")),
            Section("3. Validate and run Xray",
                Hint("Validate asks Xray to check the configuration. Start runs it without changing any Windows proxy setting."), coreBar, validationStatus, validationOutput),
            Section("4. Route Windows apps through Xray (optional)",
                Hint("Enable asks permission for one HTTPS test, then applies the Windows LAN proxy and records your previous settings so they can be restored."), proxyBar, proxyHint)
        ]);
        logBar.Dock = DockStyle.Bottom; logTab.Controls.Add(logs); logTab.Controls.Add(logBar);
        diagnosticTab.Controls.Add(diagnostics);
        var settingsTab = new TabPage("Settings");
        Add(settingsBar, "Save desktop preferences", SaveDesktopAsync, "Save desktop preferences");
        Add(settingsBar, "Reset preferences", ResetDesktopAsync, "Reset preferences");
        Add(settingsBar, "Clear profile list", ClearProfilesAsync, "Clear profile list");
        AddScrollablePage(settingsTab, [new Label { Text = "Desktop preferences", AutoSize = true }, minimizeToTray, rememberWindow,
            desktopNotice, settingsBar, new Label { Text = "Log auto-scroll is on the Logs tab; Save desktop preferences remembers it. Raw logs are in memory unless explicitly exported.\nProbe URLs and timeouts are saved on Diagnostics; enable-after-Start and network consent are session-only.\nClearing the profile list never deletes configuration files, logs on disk, or the proxy recovery journal.", AutoSize = true, Name = "settingsHelp" },
            new Label { Text = "Shortcuts: Ctrl+O import, Ctrl+N new, Ctrl+E edit, Ctrl+R reload, F5 validate, F6 start/stop, F7 check core, F8 enable proxy, F9 restore proxy, Ctrl+F find logs, Ctrl+L Logs, Ctrl+D Diagnostics, Ctrl+, Settings. Drag a .json/.jsonc file onto the window to import it.", AutoSize = true, Name = "settingsShortcuts" }]);
        tabs.TabPages.AddRange([connectionTab, logTab, diagnosticTab, settingsTab]);
        content.Controls.Add(header, 0, 0); content.Controls.Add(errorPanel, 0, 1); content.Controls.Add(tabs, 0, 2);
        content.SizeChanged += (_, _) =>
        {
            var width = Math.Max(120, content.ClientSize.Width - content.Padding.Horizontal - 12);
            headline.MaximumSize = new Size(width, 0);
            nextStep.MaximumSize = new Size(width, 0);
            status.MaximumSize = new Size(width, 0);
            errorText.MaximumSize = new Size(Math.Max(120, width - 24), 0);
            proxyStatus.MaximumSize = new Size(Math.Max(180, Math.Min(380, width / 3)), 0);
        };
        tips.SetToolTip(profiles, "Select the configuration profile to use.");
        tips.SetToolTip(manual, "Enter a loopback port manually when the JSON cannot be inspected.");
        tips.SetToolTip(enableOnStart, "After a successful Start, ask before changing the Windows proxy.");
        tips.SetToolTip(endpoints, "The app verifies that this local port is owned by the Xray process it starts.");
        tips.SetToolTip(endpointBar.Controls.OfType<Button>().Single(button => button.Name == "Copy listener"), "Copy the selected SOCKS/HTTP host:port for use in another app.");
        tips.SetToolTip(BarButton(profileBar, "Import"), "Import a .json or .jsonc configuration (Ctrl+O). You can also drag a file onto the window.");
        tips.SetToolTip(BarButton(profileBar, "New"), "Create a new configuration file (Ctrl+N).");
        tips.SetToolTip(BarButton(profileBar, "Edit"), "Edit the selected configuration (Ctrl+E).");
        tips.SetToolTip(BarButton(profileBar, "Reload"), "Reload the selected configuration from disk (Ctrl+R).");
        tips.SetToolTip(BarButton(coreBar, "Check core"), "Check the bundled xray.exe and its version (F7).");
        tips.SetToolTip(BarButton(coreBar, "Validate"), "Ask Xray to validate the selected configuration (F5).");
        tips.SetToolTip(BarButton(coreBar, "Run local checks"), "Check the core, validate the profile and run local diagnostics without sending test traffic.");
        tips.SetToolTip(BarButton(coreBar, "Start"), "Start Xray without changing Windows proxy settings (F6).");
        tips.SetToolTip(BarButton(coreBar, "Stop"), "Stop the owned Xray process and restore any managed proxy (F6).");
        tips.SetToolTip(BarButton(proxyBar, "Enable system proxy"), "Run one approved HTTPS test, then enable the Windows LAN proxy (F8).");
        tips.SetToolTip(BarButton(proxyBar, "Restore system proxy"), "Restore the recorded Windows proxy settings (F9).");
        tips.SetToolTip(BarButton(logBar, "Find logs"), "Find text in the current log buffer (Ctrl+F).");
        tips.SetToolTip(BarButton(endpointBar, "Use free port"), "Create a managed copy with the selected listener moved to a free port. The original profile is not changed.");
        diagnostics.RunRequested += async scope => await RunAsync(token => RunDiagnosticsAsync(scope, token));
        diagnostics.SavePreferencesRequested += async () => await RunAsync(SaveProbePreferencesAsync);
        diagnostics.ReportRequested += async () => await RunAsync(_ => { diagnostics.PreviewReport(); return Task.CompletedTask; });
        Controls.Add(content);
        profiles.SelectedIndexChanged += async (_, _) => { if (!updating && !busy) await RunAsync(async token => { await PersistAsync(); await InspectAsync(token); }); };
        ConfigureTray();
        Resize += (_, _) =>
        {
            if (WindowState != FormWindowState.Minimized) lastVisibleState = WindowState;
            else if (initialized && !busy && !closing && desktopPreferences.MinimizeToTray && tray.Visible && Visible) Hide();
        };
        Shown += async (_, _) =>
        {
            tray.Visible = true;
            await RunAsync(InitializeAsync);
            initialized = true;
            if (!closing && !IsDisposed) timer.Start();
        };
        timer.Tick += async (_, _) =>
        {
            Render();
            if (busy || closing) { if (diagnosing && session.ChildExited) operation?.Cancel(); return; }
            if (proxyPoll is not null) return;
            try
            {
                if (session.ChildExited) await RunAsync(_ => HandleChildExitAsync(), quiet: true);
                else if (Environment.TickCount64 - lastProxyPoll >= 3000)
                {
                    lastProxyPoll = Environment.TickCount64;
                    // Read-only polling must not disable controls or steal focus while typing.
                    // Mutating commands await this task before accessing the proxy controller.
                    var pending = Task.Run(proxy.Inspect);
                    proxyPoll = pending;
                    try { await pending; }
                    finally { proxyPoll = null; if (!IsDisposed) Render(); }
                }
            }
            catch (Exception ex) { session.Log.Add("app", $"Session poll failed: {ex.Message}"); }
        };
        FormClosing += OnClosing;
        AllowDrop = true;
        DragEnter += (_, e) => { if (DroppedConfig(e.Data) is not null) e.Effect = DragDropEffects.Copy; };
        DragDrop += async (_, e) => { if (DroppedConfig(e.Data) is { } path) await RunAsync(token => ImportFileAsync(path, token)); };
        EnableConfigDrop(this);
        KeyPreview = true;
        KeyDown += OnShortcut;

        Render();
    }

    private static FlowLayoutPanel Bar() => new() { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 2, 0, 2) };
    private static Label Hint(string text) => new() { Text = text, AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(780, 0), Margin = new Padding(3, 2, 3, 6) };
    private static GroupBox Section(string title, params Control[] controls)
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Padding = new Padding(0), Margin = new Padding(0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < controls.Length; i++)
        {
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(controls[i], 0, i);
        }
        var group = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Padding = new Padding(10, 6, 10, 10), Margin = new Padding(0, 4, 0, 8) };
        group.Controls.Add(layout);
        return group;
    }
    private Button Add(FlowLayoutPanel bar, string text, Func<CancellationToken, Task> command, string? name = null, bool primary = false)
    {
        var button = new Button { Text = text, AutoSize = true, Name = name ?? text };
        if (primary)
        {
            button.Font = new Font(button.Font, FontStyle.Bold);
            button.MinimumSize = new Size(130, 34);
            button.Padding = new Padding(8, 4, 8, 4);
        }
        button.Click += async (_, _) => await RunAsync(command);
        bar.Controls.Add(button);
        return button;
    }
    private static Button BarButton(FlowLayoutPanel bar, string name) => bar.Controls.OfType<Button>().Single(button => button.Name == name);
    private static string? DroppedConfig(IDataObject? data)
    {
        if (data?.GetData(DataFormats.FileDrop) is not string[] files) return null;
        return files.FirstOrDefault(path => File.Exists(path) &&
            (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jsonc", StringComparison.OrdinalIgnoreCase)));
    }
    private void EnableConfigDrop(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            if (child is not (TextBoxBase or ComboBox or NumericUpDown))
            {
                child.AllowDrop = true;
                child.DragEnter += (_, e) => { if (DroppedConfig(e.Data) is not null) e.Effect = DragDropEffects.Copy; };
                child.DragDrop += async (_, e) => { if (DroppedConfig(e.Data) is { } path) await RunAsync(token => ImportFileAsync(path, token)); };
            }
            EnableConfigDrop(child);
        }
    }
    private static Control? FocusedControl(Control parent)
    {
        if (parent.Focused) return parent;
        foreach (Control child in parent.Controls)
            if (FocusedControl(child) is { } found) return found;
        return null;
    }
    private async void OnShortcut(object? sender, KeyEventArgs e)
    {
        var focused = FocusedControl(this);
        if ((focused is TextBoxBase or ComboBox or NumericUpDown) && e.KeyData != (Keys.Control | Keys.F)) return;
        switch (e.KeyData)
        {
            case Keys.Control | Keys.O: await RunAsync(ImportAsync); break;
            case Keys.Control | Keys.N: await RunAsync(NewAsync); break;
            case Keys.Control | Keys.E: await RunAsync(EditAsync); break;
            case Keys.Control | Keys.R: await RunAsync(InspectAsync); break;
            case Keys.F5: await RunAsync(ValidateAsync); break;
            case Keys.F6:
                if (session.HasChild) await RunAsync(async _ => { await StopCoreAsync(); });
                else await RunAsync(StartAsync);
                break;
            case Keys.F7: await RunAsync(CheckAsync); break;
            case Keys.F8: await RunAsync(EnableProxyAsync); break;
            case Keys.F9: await RunAsync(_ => RestoreProxyAsync()); break;
            case Keys.Control | Keys.F: FindLog(); break;
            case Keys.Control | Keys.L: tabs.SelectedTab = logTab; break;
            case Keys.Control | Keys.D: tabs.SelectedTab = diagnosticTab; break;
            case Keys.Control | Keys.Oemcomma: tabs.SelectedTab = settingsTab; break;
            default: return;
        }
        e.Handled = true; e.SuppressKeyPress = true;
    }
    private void FindLog()
    {
        using var dialog = new Form
        {
            Text = "Find in logs", AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi,
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, Size = new Size(430, 160)
        };
        var box = new TextBox { Dock = DockStyle.Top, PlaceholderText = "Text to find" };
        var next = new Button { Text = "Find next", AutoSize = true, DialogResult = DialogResult.OK };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        var bar = Bar(); bar.Dock = DockStyle.Bottom; bar.Controls.AddRange([next, close]);
        dialog.Controls.Add(box); dialog.Controls.Add(bar); dialog.AcceptButton = next; dialog.CancelButton = close;
        if (dialog.ShowDialog(this) != DialogResult.OK || string.IsNullOrWhiteSpace(box.Text)) return;
        var start = Math.Min(logs.TextLength, logs.SelectionStart + logs.SelectionLength);
        var index = logs.Text.IndexOf(box.Text, start, StringComparison.OrdinalIgnoreCase);
        if (index < 0) index = logs.Text.IndexOf(box.Text, 0, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            MessageBox.Show(this, "Text not found in the current log buffer. The buffer is bounded, so older lines may have been removed.", "Find in logs", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        logs.Select(index, box.Text.Length); logs.ScrollToCaret(); logs.Focus();
    }
    private async Task RunAsync(Func<CancellationToken, Task> command, bool quiet = false)
    {
        if (busy || closing) return;
        busy = true; using var source = new CancellationTokenSource(); operation = source; lastError = null; Render();
        try
        {
            await WaitForProxyPollAsync();
            source.Token.ThrowIfCancellationRequested();
            await command(source.Token);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { session.Log.Add("app", "Operation cancelled."); }
        catch (Exception ex) { if (!quiet && !closing) ShowError(ex); else session.Log.Add("app", ex.Message); }
        finally { operation = null; busy = false; Render(); if (closing && !IsDisposed) BeginInvoke((Action)Close); }
    }
    private async Task WaitForProxyPollAsync()
    {
        if (proxyPoll is { } pending)
        {
            try { await pending; }
            catch (Exception) { /* Timer records poll failure; commands perform fresh checks. */ }
        }
    }
    private async Task InitializeAsync(CancellationToken token)
    {
        // Recovery precedes core detection, including when xray.exe is missing.
        try
        {
            var initial = await Task.Run(proxy.Inspect);
            await Task.Run(proxy.Restore);
            if (initial.Journal is not null) session.Log.Add("proxy", "Interrupted proxy transaction recovered and verified.");
        }
        catch (Exception) { session.Log.Add("proxy", "Startup recovery incomplete; review Windows proxy before starting."); }
        try
        {
            desktopPreferences = await Task.Run(desktopStore.Load, token);
            ApplyDesktopPreferences();
            if (desktopPreferences is { RememberWindow: true, Placement: { } placement }) RestorePlacement(placement);
            else if (WindowState == FormWindowState.Normal) RestorePlacement(CapturePlacement());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidDataException)
        {
            desktopUnreadable = true; desktopPreferences = DesktopPreferences.Defaults; ApplyDesktopPreferences();
            desktopNotice.Text = "Desktop preferences unreadable; defaults in use. Original file retained until explicit save or reset.";
            session.Log.Add("app", "Desktop preferences unreadable; original retained.");
        }
        try { settings = await Task.Run(store.Load, token); }
        catch (Exception ex) { settingsUnreadable = true; session.Log.Add("app", $"Settings unreadable; original retained: {ex.Message}"); }
        RefreshProfiles(settings.ActiveId); await InspectAsync(token);
        try { diagnostics.ApplyPreferences(await Task.Run(probePreferences.Load, token)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidDataException)
        { probePreferencesUnreadable = true; session.Log.Add("app", "Diagnostic preferences unreadable; defaults used and original retained."); }
        session.Log.Add("app", "Use trusted core/config files only. Local configurations and logs can contain credentials. No automatic core start. Proxy settings are managed only after explicit enablement or recovery.");
        await CheckAsync(token);
    }
    private void RefreshProfiles(Guid? selected)
    {
        updating = true;
        try { profiles.Items.Clear(); foreach (var p in settings.Profiles) profiles.Items.Add(p); profiles.SelectedItem = settings.Profiles.FirstOrDefault(p => p.Id == selected); if (profiles.SelectedIndex < 0 && profiles.Items.Count > 0) profiles.SelectedIndex = 0; }
        finally { updating = false; }
    }
    private async Task PersistAsync()
    {
        if (settingsUnreadable)
        {
            if (MessageBox.Show(this, "Existing settings could not be read. Replace them with current profiles?", "Settings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        }
        settings = settings with { ActiveId = Selected?.Id };
        await Task.Run(() => store.Save(settings)); settingsUnreadable = false;
    }
    private async Task ImportAsync(CancellationToken token)
    {
        using var dialog = new OpenFileDialog { Filter = "Xray configuration (*.json;*.jsonc)|*.json;*.jsonc|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await ImportFileAsync(dialog.FileName, token);
    }
    private async Task ImportFileAsync(string path, CancellationToken token)
    {
        EnsureCapacity();
        if (!File.Exists(path)) throw new FileNotFoundException("Configuration file not found.", path);
        var choice = MessageBox.Show(this, "Create a managed copy?\nYes: copy into app data. No: reference the original.\nRelative paths use the original directory; verify them before starting.", "Import", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel) return;
        var profile = await Task.Run(() => store.Import(path, choice == DialogResult.Yes), token);
        settings.Profiles.Add(profile); RefreshProfiles(profile.Id); await PersistAsync(); await InspectAsync(token);
        session.Log.Add("app", $"Imported profile '{profile.Name}' from {profile.Path}.");
    }
    private async Task NewAsync(CancellationToken token)
    {
        EnsureCapacity();
        using var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "profile.json", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var path = dialog.FileName; var directory = Path.GetDirectoryName(path)!;
        using var editor = new ConfigEditorForm("{\n  \"inbounds\": [],\n  \"outbounds\": []\n}\n"u8.ToArray(), bytes => ValidateEditorAsync(bytes, directory, token));
        if (editor.ShowDialog(this) != DialogResult.OK || editor.SavedBytes is not { } saved) return;
        await Task.Run(() => ProfileStore.SaveConfig(path, saved), token);
        var profile = await Task.Run(() => store.Import(path, false), token);
        settings.Profiles.Add(profile); RefreshProfiles(profile.Id); await PersistAsync(); await InspectAsync(token);
    }
    private async Task EditAsync(CancellationToken token)
    {
        var profile = RequireProfile(); var bytes = await Task.Run(() => ProfileStore.ReadConfig(profile.Path), token);
        using var editor = new ConfigEditorForm(bytes, content => ValidateEditorAsync(content, profile.WorkingDirectory, token));
        if (editor.ShowDialog(this) != DialogResult.OK || editor.SavedBytes is not { } saved || bytes.AsSpan().SequenceEqual(saved)) return;
        if (MessageBox.Show(this, $"Save changes to {profile.Path}?", "Save configuration", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await Task.Run(() => { if (!ProfileStore.ReadConfig(profile.Path).AsSpan().SequenceEqual(bytes)) throw new IOException("File changed outside the editor; reload before saving."); ProfileStore.SaveConfig(profile.Path, saved); }, token);
        await InspectAsync(token);
    }
    private async Task<string> ValidateEditorAsync(byte[] bytes, string directory, CancellationToken token)
    {
        var result = await Task.Run(() => session.ValidateAsync(bytes, directory, token), token);
        return ValidationSummary(result);
    }
    private async Task RemoveAsync(CancellationToken token)
    {
        var profile = RequireProfile();
        if (MessageBox.Show(this, "Remove the profile reference? The original file will not be deleted.", "Remove", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        settings.Profiles.Remove(profile); RefreshProfiles(null); await PersistAsync(); await InspectAsync(token);
    }
    private async Task InspectAsync(CancellationToken token)
    {
        var previous = endpoints.SelectedItem as Inbound; endpoints.Items.Clear(); endpoints.Items.Add("None (local-only)"); endpoints.SelectedIndex = 0; warnings.Text = "";
        validationStatus.Text = "No validation run for this profile.";
        validationStatus.ForeColor = SystemColors.ControlText;
        validationOutput.Text = ""; validationOutput.Visible = false;
        diagnostics.SetTargets([]);
        if (Selected is not { } profile) { details.Text = "Select or import a raw Xray JSON configuration."; profileSummary.Text = ""; return; }
        details.Text = profile.Path; profileSummary.Text = "";
        try
        {
            var bytes = await Task.Run(() => ProfileStore.ReadConfig(profile.Path), token);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            details.Text = $"{profile.Path}\nWorking directory: {profile.WorkingDirectory}\n{bytes.Length:N0} bytes · SHA-256 {hash[..16]}…";
            var inspection = await Task.Run(() => ConfigInspector.Inspect(bytes), token);
            diagnostics.SetTargets(await Task.Run(() => RemoteDiscovery.Discover(bytes), token));
            foreach (var endpoint in inspection.Endpoints) endpoints.Items.Add(endpoint);
            if (previous is not null && inspection.Endpoints.Contains(previous)) endpoints.SelectedItem = previous;
            else if (inspection.Endpoints.Count == 1) endpoints.SelectedIndex = 1;
            tcpListeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            var portsInUse = inspection.Endpoints
                .Where(endpoint => PortInUse(endpoint, tcpListeners))
                .Select(endpoint => endpoint.Port).Distinct().Order().ToArray();
            var warningLines = new List<string>();
            if (portsInUse.Length > 0)
                warningLines.Add($"Port {string.Join(", ", portsInUse)} already has a TCP listener; Start may fail. Change the port or stop the other app.");
            warningLines.AddRange(inspection.Warnings);
            warnings.Text = string.Join(" ", warningLines.Take(6));
            var listenerText = inspection.Endpoints.Count == 0
                ? "none verifiable"
                : string.Join(", ", inspection.Endpoints.Select(endpoint => $"{endpoint.Protocol.ToUpperInvariant()} {endpoint.Tag} {EndpointAddress(endpoint)}"));
            var outbounds = inspection.Outbounds is { Count: > 0 } declared ? string.Join(", ", declared) : "none declared";
            profileSummary.Text = $"Listeners: {listenerText}\nOutbounds: {outbounds} · Routing rules: {inspection.RuleCount}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Text = ex.Message; }
    }
    private async Task SaveProbePreferencesAsync(CancellationToken token)
    {
        var value = diagnostics.ReadPreferences();
        var warning = probePreferencesUnreadable ? "Existing diagnostic preferences are unreadable and will be replaced.\n\n" : "";
        if (MessageBox.Show(this, warning + "Save probe URLs and timeout locally? URLs may contain private query parameters. Direct/public-IP consent and remote targets will not be saved.", "Diagnostic preferences", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await Task.Run(() => probePreferences.Save(value), token); probePreferencesUnreadable = false;
        session.Log.Add("app", "Diagnostic destinations and timeout saved locally.");
    }
    private async Task RunDiagnosticsAsync(DiagnosticScope scope, CancellationToken token)
    {
        var options = diagnostics.ReadOptions(scope);
        var profile = Selected; var selected = endpoints.SelectedItem as Inbound;
        diagnosing = true; diagnostics.BeginRun();
        IReadOnlyList<DiagnosticResult>? rows = null;
        try
        {
            var context = await Task.Run(() => session.CaptureDiagnostics(profile, selected, proxy.Inspect), token);
            var progress = new Progress<DiagnosticResult>(diagnostics.AddResult);
            rows = await Task.Run(() => diagnosticRunner.RunAsync(context, options, scope, progress, token), token);
        }
        finally
        {
            diagnostics.EndRun(rows); diagnosing = false;
            // An exit during probes cancels them and restores immediately after the command completes.
            if (session.ChildExited) await HandleChildExitAsync();
        }
    }
    private Task CheckAsync(CancellationToken token) => Task.Run(() => session.CheckAsync(token), token);
    private async Task ValidateAsync(CancellationToken token)
    {
        var profile = RequireProfile();
        var bytes = await Task.Run(() => ProfileStore.ReadConfig(profile.Path), token);
        var result = await Task.Run(() => session.ValidateAsync(bytes, profile.WorkingDirectory, token), token);
        ShowValidation(result);
        MessageBox.Show(this, ValidationSummary(result), "Validation", MessageBoxButtons.OK, result.ExitCode == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }
    private void ShowValidation(CommandResult result)
    {
        validationStatus.Text = result.ExitCode == 0
            ? $"Xray accepted this configuration ({result.Elapsed.TotalMilliseconds:0} ms)."
            : $"Xray rejected this configuration (exit {result.ExitCode}).";
        validationStatus.ForeColor = result.ExitCode == 0 ? SystemColors.ControlText : SystemColors.HotTrack;
        ShowValidationOutput(result.Output);
    }
    private void ShowLastValidationOutput()
    {
        validationStatus.Text = "Xray rejected this configuration.";
        validationStatus.ForeColor = SystemColors.HotTrack;
        ShowValidationOutput(session.LastValidationOutput);
    }
    private void ShowValidationOutput(IEnumerable<string> output)
    {
        var text = string.Join(Environment.NewLine, output).Trim();
        validationOutput.Text = text;
        validationOutput.Visible = text.Length > 0;
    }
    private static string ValidationSummary(CommandResult result) => result.ExitCode == 0
        ? $"Configuration is valid. Xray completed validation in {result.Elapsed.TotalMilliseconds:0} ms."
        : $"Xray rejected the configuration (exit {result.ExitCode}) after {result.Elapsed.TotalMilliseconds:0} ms. The validation output is shown on the Start tab and in Logs.";
    private async Task RunLocalChecksAsync(CancellationToken token)
    {
        await CheckAsync(token);
        if (Selected is { } profile)
        {
            var bytes = await Task.Run(() => ProfileStore.ReadConfig(profile.Path), token);
            var result = await Task.Run(() => session.ValidateAsync(bytes, profile.WorkingDirectory, token), token);
            ShowValidation(result);
        }
        tabs.SelectedTab = diagnosticTab;
        await RunDiagnosticsAsync(DiagnosticScope.Local, token);
    }
    private async Task StartAsync(CancellationToken token)
    {
        if (await Task.Run(() => proxy.HasPending, token)) throw new InvalidOperationException("Resolve pending proxy recovery before starting another core.");
        var profile = RequireProfile(); var selected = endpoints.SelectedItem as Inbound;
        await CheckAsync(token);
        if (session.Version is { IsBaseline: false } version && MessageBox.Show(this, $"Xray {version.Version} is outside the {Bootstrap.TargetCoreVersion} baseline. Continue?", "Compatibility", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var bytes = await Task.Run(() => ProfileStore.ReadConfig(profile.Path), token);
        var inspection = await Task.Run(() => ConfigInspector.Inspect(bytes), token);
        diagnostics.SetTargets(await Task.Run(() => RemoteDiscovery.Discover(bytes), token));
        if (inspection.Warnings.Count > 0 && MessageBox.Show(this, string.Join("\n", inspection.Warnings.Take(10)) + "\n\nStart this trusted configuration anyway?", "Configuration warnings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await Task.Run(() => session.StartAsync(bytes, profile.WorkingDirectory, selected, token), token);
        if (enableOnStart.Checked && session.CanProbeHttp) await EnableProxyAsync(token);
        else if (enableOnStart.Checked) session.Log.Add("proxy", "Automatic enable skipped: no eligible HTTP listener selected.");
    }
    private static int FindFreeLoopbackPort(IEnumerable<int>? avoidPorts = null)
    {
        var avoid = avoidPorts is null ? new HashSet<int>() : new HashSet<int>(avoidPorts);
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        for (var port = 10809; port <= 11999; port++)
            if (!avoid.Contains(port) && listeners.All(listener => listener.Port != port) && CanListen(port)) return port;
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var fallback = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        if (!avoid.Contains(fallback) && listeners.All(listener => listener.Port != fallback)) return fallback;
        throw new InvalidOperationException("Could not find a free loopback port. Close other proxy apps or choose a manual endpoint.");
        static bool CanListen(int port)
        {
            try
            {
                using var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch (SocketException) { return false; }
        }
    }
    private static bool PortInUse(Inbound endpoint, IReadOnlyCollection<IPEndPoint> listeners) =>
        listeners.Any(listener => listener.Port == endpoint.Port &&
            (listener.Address.Equals(endpoint.Address) || listener.Address.Equals(IPAddress.Any) || listener.Address.Equals(IPAddress.IPv6Any)));
    private async Task FixPortAsync(CancellationToken token)
    {
        var profile = RequireProfile();
        if (endpoints.SelectedItem is not Inbound endpoint)
            throw new InvalidOperationException("Select a listener before choosing a free port.");
        if (!PortInUse(endpoint, tcpListeners))
        {
            MessageBox.Show(this, $"{EndpointAddress(endpoint)} is not currently used by another TCP listener.", "Use free port", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var port = FindFreeLoopbackPort(endpoints.Items.OfType<Inbound>().Select(candidate => candidate.Port));
        if (MessageBox.Show(this,
            $"Create a managed copy with the selected {endpoint.Protocol.ToUpperInvariant()} listener moved to 127.0.0.1:{port}?\n\n" +
            "• The original profile is not modified.\n" +
            "• The copy uses normalized JSON, so comments and formatting may be lost.\n" +
            "• Apps using the old port must be pointed at the new one.\n" +
            "• Xray validates the generated copy before it is saved.",
            "Use free port", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var bytes = await Task.Run(() => ProfileStore.ReadConfig(profile.Path), token);
        var updated = ConfigUpgrade.UsePort(bytes, endpoint, port);
        var result = await Task.Run(() => session.ValidateAsync(updated, profile.WorkingDirectory, token), token);
        if (result.ExitCode != 0)
        {
            ShowValidation(result);
            throw new InvalidOperationException($"Xray rejected the generated configuration (exit {result.ExitCode}). The original profile was not changed.");
        }
        var created = await Task.Run(() => store.Create($"{profile.Name} (port {port})", updated, profile.WorkingDirectory), token);
        settings.Profiles.Add(created); RefreshProfiles(created.Id); await PersistAsync(); await InspectAsync(token);
        var moved = endpoints.Items.OfType<Inbound>().FirstOrDefault(candidate => candidate.Port == port && candidate.Protocol == endpoint.Protocol);
        if (moved is not null) endpoints.SelectedItem = moved;
        ShowValidation(result);
        session.Log.Add("app", $"Created managed profile '{created.Name}' with {endpoint.Protocol.ToUpperInvariant()} listener 127.0.0.1:{port}. Original profile unchanged.");
    }
    private async Task AddHttpListenerAsync(CancellationToken token)
    {
        var profile = RequireProfile();
        if (endpoints.Items.OfType<Inbound>().Any(endpoint => endpoint.Protocol == "http"))
            throw new InvalidOperationException("This profile already has an HTTP listener.");
        var port = FindFreeLoopbackPort(endpoints.Items.OfType<Inbound>().Select(endpoint => endpoint.Port));
        if (MessageBox.Show(this,
            $"Create a new managed profile with an HTTP listener on 127.0.0.1:{port}?\n\n" +
            "• The original profile is not modified.\n" +
            "• The copy uses normalized JSON, so comments and formatting may be lost.\n" +
            "• Xray validates the generated copy before it is saved.\n" +
            "• If your routing rules depend on inbound tags, review them before enabling the proxy.",
            "Add HTTP listener", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        var bytes = await Task.Run(() => ProfileStore.ReadConfig(profile.Path), token);
        var updated = ConfigUpgrade.AddLoopbackHttp(bytes, port);
        var result = await Task.Run(() => session.ValidateAsync(updated, profile.WorkingDirectory, token), token);
        if (result.ExitCode != 0)
        {
            ShowValidation(result);
            throw new InvalidOperationException($"Xray rejected the generated configuration (exit {result.ExitCode}). The original profile was not changed.");
        }
        var created = await Task.Run(() => store.Create(profile.Name + " (HTTP)", updated, profile.WorkingDirectory), token);
        settings.Profiles.Add(created); RefreshProfiles(created.Id); await PersistAsync(); await InspectAsync(token);
        var http = endpoints.Items.OfType<Inbound>().FirstOrDefault(endpoint => endpoint.Protocol == "http" && !endpoint.Authenticated);
        if (http is not null) endpoints.SelectedItem = http;
        ShowValidation(result);
        session.Log.Add("app", $"Created managed profile '{created.Name}' with HTTP listener 127.0.0.1:{port}. Original profile unchanged.");
    }
    private async Task EnableProxyAsync(CancellationToken token)
    {
        var endpoint = session.ActiveEndpoint ?? throw new InvalidOperationException("Start Xray with a selected HTTP loopback listener first.");
        await Task.Run(session.VerifyHttpOwner, token);
        var original = await Task.Run(proxy.PrepareAcquire, token);
        var destination = diagnostics.HttpsDestination;
        var notice = original.External ? "Existing manual proxy or automatic configuration will be temporarily replaced. Select No and use Review / resolve to inspect the current settings first.\n\n" : "";
        if (MessageBox.Show(this, notice + "Allow one HTTPS request to " + destination + " through the selected Xray HTTP listener? If it succeeds, Windows LAN proxy will be enabled.\n\nLocal/intranet hosts bypass the proxy. This is not a VPN.\nExisting settings will be journaled and restored on stop.", "Enable Windows proxy", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await Task.Run(session.VerifyHttpOwner, token);
        await Task.Run(() => HttpProxyProbe.VerifyAsync(endpoint, destination, token), token);
        token.ThrowIfCancellationRequested();
        // Once the transaction starts, do not interrupt journal/write/rollback.
        await Task.Run(() => { token.ThrowIfCancellationRequested(); proxy.Acquire(original, endpoint, session.VerifyHttpOwner); });
        session.Log.Add("proxy", "Windows LAN proxy enabled after explicit HTTPS test and read-back verification.");
        if (tray.Visible && (!Visible || WindowState == FormWindowState.Minimized))
            tray.ShowBalloonTip(5000, "System proxy enabled", "Windows LAN proxy now points to the owned Xray listener. Stop Xray or Restore system proxy to undo it.", ToolTipIcon.Info);
    }

    private async Task RestoreProxyAsync()
    {
        await Task.Run(proxy.Restore);
        session.Log.Add("proxy", "No pending proxy transaction remains.");
    }

    private async Task<bool> StopCoreAsync()
    {
        try { await RestoreProxyAsync(); }
        catch (Exception)
        {
            session.Log.Add("proxy", "Restoration not verified; recovery journal retained where available.");
            if (MessageBox.Show(this, "Windows proxy restoration could not be verified. Keep Xray running and use Review / resolve, or force stop and risk losing connectivity?\n\nYes: force stop; No: keep running. Recovery data is not discarded.", "Proxy recovery required", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return false;
        }
        if (session.HasChild) await Task.Run(session.StopAsync);
        return true;
    }

    private async Task HandleChildExitAsync()
    {
        try { await RestoreProxyAsync(); }
        catch (Exception) { session.Log.Add("proxy", "Core exited; proxy recovery incomplete. Use Review / resolve or Windows Settings."); }
        finally { await Task.Run(session.PollAsync); }
        if (tray.Visible)
        {
            var code = session.LastExitCode is { } exit ? $" (code {exit})" : "";
            tray.ShowBalloonTip(6000, "Xray stopped unexpectedly", $"The owned Xray process exited{code}. Windows proxy settings were restored when possible.", ToolTipIcon.Warning);
        }
    }

    private async Task ReviewProxyAsync()
    {
        var reviewed = await Task.Run(proxy.Inspect);
        static string Describe(ProxySettings? value) => value is null ? "Unavailable" : $"Flags: 0x{value.Flags:X} (direct=1, manual=2, PAC=4, autodetect=8)\r\nServer: {value.Server}\r\nBypass: {value.Bypass}\r\nPAC: {value.Pac}";
        using var dialog = new Form { Text = "Windows proxy review — local sensitive data", AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi, StartPosition = FormStartPosition.CenterParent, Size = new Size(720, 500), MinimumSize = new Size(520, 380) };
        var text = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false,
            Text = reviewed.Detail + "\r\n\r\nCURRENT\r\n" + Describe(reviewed.Current) + "\r\n\r\nORIGINAL\r\n" + Describe(reviewed.Journal?.Original) + "\r\n\r\nINTENDED\r\n" + Describe(reviewed.Journal?.Applied) + "\r\n\r\nValues may contain private URLs or credentials; do not share. If recovery is unavailable, open Windows Settings > Network & Internet > Proxy and restore your previous settings manually." };
        var choices = Bar(); choices.Dock = DockStyle.Bottom;
        var restore = new Button { Text = "Restore recorded original", AutoSize = true, DialogResult = DialogResult.Yes, Enabled = reviewed.Current is not null && reviewed.Journal is not null };
        var keep = new Button { Text = "Keep current external settings", AutoSize = true, DialogResult = DialogResult.No, Enabled = reviewed.State == ProxyState.Conflict };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        choices.Controls.AddRange([restore, keep, close]); dialog.Controls.Add(text); dialog.Controls.Add(choices); dialog.CancelButton = close;
        var choice = dialog.ShowDialog(this);
        if (choice is not (DialogResult.Yes or DialogResult.No)) return;
        var keepCurrent = choice == DialogResult.No;
        var question = keepCurrent ? "Keep the current external settings and discard the recovery journal? This does not change Windows settings. If they still reference Xray, stopping it may break connectivity." : "Restore the recorded original settings, even if another application changed them?";
        if (MessageBox.Show(this, question, "Confirm recovery decision", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await Task.Run(() => proxy.ResolveReviewed(reviewed, keepCurrent));
        session.Log.Add("proxy", keepCurrent ? "User retained external proxy settings." : "Recorded proxy settings restored and verified.");
    }

    private void EnsureCapacity()
    {
        if (settings.Profiles.Count >= 500) throw new InvalidOperationException("Profile limit (500) reached; remove a reference first.");
    }
    private async Task SaveAsAsync(CancellationToken token)
    {
        EnsureCapacity();
        var profile = RequireProfile();
        var bytes = await Task.Run(() => ProfileStore.ReadConfig(profile.Path), token);
        using var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json|JSONC (*.jsonc)|*.jsonc", FileName = profile.Name + ".json", OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (string.Equals(Path.GetFullPath(dialog.FileName), Path.GetFullPath(profile.Path), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Use Edit to save to the existing profile.");
        var path = dialog.FileName;
        using var editor = new ConfigEditorForm(bytes, content => ValidateEditorAsync(content, Path.GetDirectoryName(path)!, token));
        if (editor.ShowDialog(this) != DialogResult.OK || editor.SavedBytes is not { } saved) return;
        await Task.Run(() => ProfileStore.SaveConfig(path, saved), token);
        var created = await Task.Run(() => store.Import(path, false), token);
        settings.Profiles.Add(created); RefreshProfiles(created.Id); await PersistAsync(); await InspectAsync(token);
    }
    private static string EndpointAddress(Inbound endpoint)
    {
        var host = endpoint.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
            ? $"[{endpoint.Address}]"
            : endpoint.Address.ToString();
        return $"{host}:{endpoint.Port}";
    }
    private Task CopyListenerAddress()
    {
        if (endpoints.SelectedItem is not Inbound endpoint)
        {
            session.Log.Add("app", "Select a listener before copying its address.");
            return Task.CompletedTask;
        }
        var address = EndpointAddress(endpoint);
        Clipboard.SetText(address);
        session.Log.Add("app", $"Copied {endpoint.Protocol.ToUpperInvariant()} listener address {address}.");
        return Task.CompletedTask;
    }
    private void SelectManualEndpoint()
    {
        using var dialog = new Form { Text = "Manual listener verification", AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi, StartPosition = FormStartPosition.CenterParent, Size = new Size(440, 240), FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(12) };
        var protocol = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 }; protocol.Items.AddRange(["http", "socks"]); protocol.SelectedIndex = 0;
        var address = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 }; address.Items.AddRange(["127.0.0.1", "::1"]); address.SelectedIndex = 0;
        var port = new NumericUpDown { Minimum = 1, Maximum = 65535, Value = 1, Width = 140 };
        var ok = new Button { Text = "Verify this endpoint", AutoSize = true, DialogResult = DialogResult.OK };
        panel.Controls.Add(new Label { Text = "Loopback only; no JSON changes or capability claims.", AutoSize = true }); panel.Controls.Add(protocol); panel.Controls.Add(address); panel.Controls.Add(port); panel.Controls.Add(ok);
        dialog.Controls.Add(panel); dialog.AcceptButton = ok;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var endpoint = new Inbound("(manual)", protocol.Text, IPAddress.Parse(address.Text), (int)port.Value, Authenticated: true, Manual: true);
        for (var i = endpoints.Items.Count - 1; i >= 0; i--) if (endpoints.Items[i] is Inbound { Manual: true }) endpoints.Items.RemoveAt(i);
        endpoints.Items.Add(endpoint); endpoints.SelectedItem = endpoint;
    }
    private Profile RequireProfile() => Selected ?? throw new InvalidOperationException("Select a profile first.");
    private void Render()
    {
        var running = !busy && session.HasChild;
        if (busy) status.Text = $"Operation in progress… | Last observed core: {session.State} | Connectivity: not monitored";
        else
        {
            var uptime = session.Started is { } started ? (DateTimeOffset.Now - started).ToString(@"d\.hh\:mm\:ss") : "—";
            status.Text = $"Core: {session.State} | Version: {session.Version?.Version ?? "unknown"} | PID: {session.Pid?.ToString() ?? "—"} | Uptime: {uptime}\n{session.Readiness} | Last exit: {session.LastExitCode?.ToString() ?? "—"} | Connectivity: not monitored";
        }
        profileBar.Enabled = !busy && !running && !closing; endpoints.Enabled = !busy && !running && !closing; manual.Enabled = endpoints.Enabled && Selected is not null;
        foreach (Control control in profileBar.Controls)
            if (control is Button { Name: "Edit" or "Save As" or "Remove" or "Reload" })
                control.Enabled = !busy && !running && !closing && Selected is not null;
        foreach (Control control in endpointBar.Controls)
            if (control.Name == "Copy listener")
                control.Enabled = !closing && endpoints.SelectedItem is Inbound;
            else if (control.Name == "Use free port")
                control.Enabled = !busy && !running && !closing && endpoints.SelectedItem is Inbound endpoint && PortInUse(endpoint, tcpListeners);
        foreach (Control control in coreBar.Controls) control.Enabled = !busy && !closing && (control.Name switch { "Stop" or "Restart" => running, "Check core" => !running, _ => !running && Selected is not null });
        var currentProxy = proxy.Status;
        proxyStatus.Text = $"Windows LAN proxy: {currentProxy.State} — {currentProxy.Detail}";
        proxyStatus.ForeColor = currentProxy.State is ProxyState.Conflict or ProxyState.RecoveryRequired or ProxyState.Unavailable ? SystemColors.HotTrack : SystemColors.ControlText;
        var hasHttpListener = endpoints.Items.OfType<Inbound>().Any(endpoint => endpoint.Protocol == "http");
        foreach (Control control in proxyBar.Controls) control.Enabled = !busy && !closing && (control.Name switch
        {
            "Enable system proxy" => running && session.CanProbeHttp && (currentProxy.State is ProxyState.Unmanaged or ProxyState.External),
            "Restore system proxy" => currentProxy.State is ProxyState.Managed or ProxyState.RecoveryRequired or ProxyState.Conflict,
            "Review / resolve" => true,
            "Add HTTP listener" => !running && Selected is not null && !hasHttpListener,
            _ => !running
        });
        proxyHint.Text = Selected is null
            ? "Import a configuration to see which local listeners are available."
            : running
                ? session.CanProbeHttp
                    ? "An owned anonymous HTTP listener is ready. Enable applies the Windows LAN proxy after one approved HTTPS test."
                    : "No eligible anonymous HTTP listener is active. Windows system proxy requires one, so it stays unavailable for this run. Stop Xray and use Add HTTP listener… if this profile is SOCKS/local-only."
                : hasHttpListener
                    ? "Start Xray with an HTTP listener selected, then Enable can apply the Windows LAN proxy."
                    : "This profile has no HTTP listener. Xray can start and apps can use the SOCKS port, but Windows system proxy cannot be enabled. Add HTTP listener… creates a validated managed copy with one.";
        UpdateGuidance(running, currentProxy, hasHttpListener);
        cancel.Enabled = busy && !closing;
        diagnostics.SetBusy(busy || closing);
        settingsBar.Enabled = !busy && !closing;
        foreach (Control control in logBar.Controls) control.Enabled = !closing && (control == autoScroll || !busy);
        minimizeToTray.Enabled = rememberWindow.Enabled = !busy && !closing;
        foreach (Control control in settingsBar.Controls)
            if (control.Name == "Clear profile list") control.Enabled = !running && !busy && !closing;
        trayShow.Enabled = !closing;
        trayStart.Enabled = CommandEnabled(coreBar, "Start");
        trayStop.Enabled = CommandEnabled(coreBar, "Stop");
        trayEnable.Enabled = CommandEnabled(proxyBar, "Enable system proxy");
        trayRestore.Enabled = CommandEnabled(proxyBar, "Restore system proxy");
        trayChecks.Enabled = !busy && !closing && Selected is not null;
        trayExit.Enabled = !busy && !closing;
        var tooltip = $"Xray Desktop | {session.State} | Proxy: {currentProxy.State}";
        if (tray.Text != tooltip) tray.Text = tooltip;
        var batch = session.Log.ReadAfter(logId); logId = batch.Id;
        if (batch.Lines.Length > 0)
        {
            var selection = logs.SelectionStart; var selectionLength = logs.SelectionLength;
            if (displayedLines + batch.Lines.Length > 5000 || logs.TextLength + batch.Lines.Sum(l => l.Length + 2) > 2 * 1024 * 1024)
            {
                var retained = session.Log.ReadAfter(0);
                logs.Text = string.Join(Environment.NewLine, retained.Lines) + Environment.NewLine;
                displayedLines = retained.Lines.Length; logId = retained.Id;
            }
            else { logs.AppendText(string.Join(Environment.NewLine, batch.Lines) + Environment.NewLine); displayedLines += batch.Lines.Length; }
            if (autoScroll.Checked) { logs.SelectionStart = logs.TextLength; logs.ScrollToCaret(); }
            else { logs.Select(Math.Min(selection, logs.TextLength), Math.Min(selectionLength, Math.Max(0, logs.TextLength - selection))); logs.ScrollToCaret(); }
        }
    }
    private void UpdateGuidance(bool running, ProxyStatus currentProxy, bool hasHttpListener)
    {
        errorPanel.Visible = !string.IsNullOrWhiteSpace(lastError);
        errorText.Text = lastError ?? string.Empty;

        if (closing)
        {
            headline.Text = "Closing…";
            nextStep.Text = "Stopping the owned core and restoring any managed Windows proxy setting.";
            return;
        }
        if (busy)
        {
            headline.Text = "Working…";
            nextStep.Text = "Wait for this operation to finish, or click Cancel operation.";
            return;
        }
        if (lastError is not null)
        {
            headline.Text = "Action needed";
            nextStep.Text = "Review the error below, fix it, then try the operation again. Open Logs for the full detail.";
            return;
        }
        if (running && currentProxy.State == ProxyState.Managed)
        {
            headline.Text = "Xray is running with the Windows proxy enabled";
            nextStep.Text = "Use Stop Xray when finished; your previous Windows proxy settings are restored first.";
            return;
        }
        if (!running && currentProxy.State == ProxyState.Managed)
        {
            headline.Text = "Windows proxy is still enabled";
            nextStep.Text = "Click Restore system proxy to return to your previous Windows settings.";
            return;
        }
        if (currentProxy.State is ProxyState.RecoveryRequired or ProxyState.Conflict)
        {
            headline.Text = "Windows proxy needs attention";
            nextStep.Text = "Open Review / resolve before starting Xray. Your previous settings are retained locally.";
            return;
        }
        if (Selected is null)
        {
            headline.Text = "No configuration selected";
            nextStep.Text = "Click Import configuration… to open an existing Xray JSON file, or New configuration… to create one. Nothing starts automatically.";
            return;
        }
        if (running && !session.CanProbeHttp)
        {
            headline.Text = "Xray is running (SOCKS/local only)";
            nextStep.Text = "Windows system proxy needs an HTTP listener, so Enable stays disabled. Use the SOCKS port in your apps, or stop Xray and use Add HTTP listener… to create a managed copy with one.";
            return;
        }
        if (running)
        {
            headline.Text = "Xray is running";
            nextStep.Text = "To send Windows apps through Xray, click Enable system proxy. Otherwise use Stop Xray when finished.";
        }
        else if (session.State == CoreState.Missing)
        {
            headline.Text = "Xray core is missing";
            nextStep.Text = "Restore xray.exe, geoip.dat and geosite.dat beside XrayDesktop.exe, then click Check core.";
        }
        else if (session.State == CoreState.Faulted)
        {
            headline.Text = "Xray core could not be checked";
            nextStep.Text = "Click Check core and review the error in Logs. The tested core is Xray " + Bootstrap.TargetCoreVersion + ".";
        }
        else if (!hasHttpListener)
        {
            headline.Text = "Ready to start (no HTTP listener)";
            nextStep.Text = "Xray can run, but Windows system proxy cannot be enabled without an HTTP inbound. Start Xray and use the SOCKS port, or use Add HTTP listener… to create a validated managed copy.";
        }
        else
        {
            headline.Text = "Ready to start";
            nextStep.Text = "Click Validate configuration to test it, choose an HTTP listener if you want the system proxy, then click Start Xray.";
        }
        if (session.Version is { IsBaseline: false } version)
            nextStep.Text += $" Detected Xray {version.Version}; the tested baseline is {Bootstrap.TargetCoreVersion}. Starting asks for confirmation.";
    }
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true; closing = true; operation?.Cancel(); Render();
        if (!Visible || WindowState == FormWindowState.Minimized) ShowWindow();
        if (busy) return;
        busy = true; timer.Stop();
        try
        {
            await WaitForProxyPollAsync();
            if (await StopCoreAsync())
            {
                await SavePlacementOnExitAsync();
                allowClose = true; BeginInvoke((Action)Close);
            }
            else { closing = false; timer.Start(); }
        }
        catch (Exception ex) { ShowError(ex); closing = false; timer.Start(); }
        finally { busy = false; if (!allowClose) Render(); }
    }
    private void ShowError(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("exited during startup", StringComparison.Ordinal))
            message += " A local listener port may already be in use. Open Logs for the Xray error, then choose another port or stop the conflicting app.";
        else if (message.Contains("rejected the configuration", StringComparison.Ordinal))
        {
            ShowLastValidationOutput();
            message += " The exact validation output is shown on the Start tab and in Logs.";
        }
        else if (ex is UnauthorizedAccessException)
            message += " Move the portable folder to a writable private location, or close the app using the file.";
        lastError = message;
        session.Log.Add("error", ex.ToString());
        if (tray.Visible && (!Visible || WindowState == FormWindowState.Minimized))
            tray.ShowBalloonTip(5000, "Xray Desktop needs attention", message, ToolTipIcon.Error);
    }
}
