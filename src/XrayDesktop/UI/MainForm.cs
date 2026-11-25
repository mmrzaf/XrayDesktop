using XrayDesktop.App;
using XrayDesktop.Config;
using XrayDesktop.Diagnostics;
using XrayDesktop.Windows;

namespace XrayDesktop.UI;

internal sealed partial class MainForm : Form
{
    private static readonly string DataDirectory = Path.Combine(AppContext.BaseDirectory, "data");
    private static readonly TimeSpan HealthInterval = TimeSpan.FromSeconds(20);
    private readonly ProfileStore store = new(DataDirectory);
    private readonly SessionController session = new(AppContext.BaseDirectory, DataDirectory);
    private readonly ProxyController proxy = new(new SystemProxy(), ProxyRecovery.ForCurrentUser());
    private readonly ConnectionController connection;
    private readonly DiagnosticsPanel diagnostics = new();
    private readonly ProbePreferenceStore probePreferences = new(DataDirectory);
    private readonly DiagnosticRunner diagnosticRunner = new(AppContext.BaseDirectory, DataDirectory);
    private bool probePreferencesUnreadable;
    private bool diagnosing;
    private long lastProxyPoll, lastHealth;
    private Task<ProxyStatus>? proxyPoll;
    private Task? healthPoll;
    private CancellationTokenSource? healthSource;

    private readonly Label statusDot = new() { Text = "●", AutoSize = true, Font = new Font("Segoe UI", 30f), Margin = new Padding(0, 0, 10, 0) };
    private readonly Label statusTitle = new() { AutoSize = true, Font = new Font("Segoe UI", 20f, FontStyle.Bold), Margin = new Padding(0) };
    private readonly Label statusDetail = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(440, 0), Margin = new Padding(2, 0, 0, 0) };
    private readonly Button connectButton = new() { Dock = DockStyle.Top, Height = 58, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 13f, FontStyle.Bold), Margin = new Padding(0, 14, 0, 14), UseVisualStyleBackColor = false };
    private readonly ComboBox profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill, DisplayMember = "Name" };
    private readonly Button addButton = new() { Text = "Add ▾", AutoSize = true, Name = "Add" };
    private readonly Button moreButton = new() { Text = "More ▾", AutoSize = true, Name = "More" };
    private readonly ContextMenuStrip addMenu = new(), moreMenu = new();
    private readonly ToolStripMenuItem editItem = new("Edit configuration…"), renameItem = new("Rename…"), removeItem = new("Remove profile"), reviewItem = new("Review Windows proxy…");
    private readonly Label profileHint = new() { AutoSize = true, ForeColor = SystemColors.GrayText, MaximumSize = new Size(520, 0), Margin = new Padding(0, 4, 0, 0) };
    private readonly Panel errorPanel = new() { Dock = DockStyle.Top, AutoSize = true, Visible = false, Padding = new Padding(10), BackColor = Color.FromArgb(253, 231, 233) };
    private readonly Label errorText = new() { AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = Color.Black };
    private readonly Panel recoveryPanel = new() { Dock = DockStyle.Top, AutoSize = true, Visible = false, Padding = new Padding(10), BackColor = Color.FromArgb(255, 244, 206) };
    private readonly Label recoveryText = new() { AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = Color.Black };
    private readonly Button cancel = new() { Text = "Cancel operation", AutoSize = true, Visible = false, Margin = new Padding(0, 4, 0, 4) };
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };
    private readonly TabPage connectTab = new("Connect"), logTab = new("Logs"), diagnosticTab = new("Diagnostics"), settingsTab = new("Settings");
    private readonly ToolTip tips = new() { AutoPopDelay = 15000, InitialDelay = 400, ReshowDelay = 100, ShowAlways = true };
    private readonly TextBox logs = new() { Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9) };
    private readonly FlowLayoutPanel logBar = Bar(), settingsBar = Bar();
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 300 };
    private ProfileSettings settings = new(1, [], null);
    private CancellationTokenSource? operation;
    private bool busy, closing, allowClose, exitRequested, updating, settingsUnreadable, trayHinted;
    private string? lastError;
    private long logId;
    private int displayedLines;
    private Profile? Selected => profiles.SelectedItem as Profile;

    internal MainForm()
    {
        connection = new ConnectionController(session, proxy);
        Text = $"{Bootstrap.ProductName} — {Bootstrap.Version}"; StartPosition = FormStartPosition.CenterScreen;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(640, 520); Size = new Size(720, 640);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10), ColumnCount = 1, RowCount = 2 };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var errorActions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
        var troubleshoot = new Button { Text = "Run diagnostics", AutoSize = true };
        troubleshoot.Click += async (_, _) => { tabs.SelectedTab = diagnosticTab; await RunAsync(token => RunDiagnosticsAsync(DiagnosticScope.Local, token)); };
        var openLogs = new Button { Text = "Open logs", AutoSize = true };
        openLogs.Click += (_, _) => tabs.SelectedTab = logTab;
        var dismissError = new Button { Text = "Dismiss", AutoSize = true };
        dismissError.Click += (_, _) => { lastError = null; Render(); };
        errorActions.Controls.AddRange([troubleshoot, openLogs, dismissError]);
        var errorLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        errorLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        errorLayout.Controls.Add(errorText, 0, 0); errorLayout.Controls.Add(errorActions, 0, 1);
        errorPanel.Controls.Add(errorLayout);

        var recoveryActions = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Margin = new Padding(0, 6, 0, 0) };
        var restoreNow = new Button { Text = "Restore my previous settings", AutoSize = true };
        restoreNow.Click += async (_, _) => await RunAsync(_ => FixProxyAsync());
        var reviewNow = new Button { Text = "Review…", AutoSize = true };
        reviewNow.Click += async (_, _) => await RunAsync(_ => ReviewProxyAsync());
        recoveryActions.Controls.AddRange([restoreNow, reviewNow]);
        var recoveryLayout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2 };
        recoveryLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        recoveryLayout.Controls.Add(recoveryText, 0, 0); recoveryLayout.Controls.Add(recoveryActions, 0, 1);
        recoveryPanel.Controls.Add(recoveryLayout);
        var banners = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0) };
        banners.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        banners.Controls.Add(errorPanel, 0, 0); banners.Controls.Add(recoveryPanel, 0, 1); banners.Controls.Add(cancel, 0, 2);
        cancel.Click += (_, _) => operation?.Cancel();

        BuildConnectTab();
        BuildMenus();
        logBar.Dock = DockStyle.Bottom; logTab.Controls.Add(logs); logTab.Controls.Add(logBar);
        Add(logBar, "Find…", _ => { FindLog(); return Task.CompletedTask; }, "Find logs");
        Add(logBar, "Clear logs", _ => { session.Log.Clear(); logs.Clear(); displayedLines = 0; return Task.CompletedTask; }, "Clear logs");
        Add(logBar, "Copy raw logs", _ => { if (logs.TextLength > 0 && MessageBox.Show(this, "Raw logs may contain secrets, addresses and private paths. Copy anyway?", "Sensitive data", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) Clipboard.SetText(logs.Text); return Task.CompletedTask; }, "Copy raw logs");
        Add(logBar, "Save raw logs…", SaveRawLogsAsync, "Save raw logs");
        logBar.Controls.Add(autoScroll);
        diagnosticTab.Controls.Add(diagnostics);
        BuildSettingsTab();
        tabs.TabPages.AddRange([connectTab, logTab, diagnosticTab, settingsTab]);
        content.Controls.Add(banners, 0, 0); content.Controls.Add(tabs, 0, 1);
        content.SizeChanged += (_, _) =>
        {
            var width = Math.Max(160, content.ClientSize.Width - content.Padding.Horizontal - 24);
            errorText.MaximumSize = new Size(width, 0);
            recoveryText.MaximumSize = new Size(width, 0);
        };
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
        timer.Tick += OnTick;
        FormClosing += OnClosing;
        AllowDrop = true;
        DragEnter += (_, e) => { if (DroppedConfig(e.Data) is not null) e.Effect = DragDropEffects.Copy; };
        DragDrop += async (_, e) => { if (DroppedConfig(e.Data) is { } path) await RunAsync(token => ImportFileAsync(path, token)); };
        EnableConfigDrop(this);
        KeyPreview = true;
        KeyDown += OnShortcut;
        Render();
    }

    private void BuildConnectTab()
    {
        var card = new TableLayoutPanel { Width = 540, AutoSize = true, ColumnCount = 1, Padding = new Padding(0, 24, 0, 0) };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var titleStack = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        titleStack.Controls.AddRange([statusTitle, statusDetail]);
        var statusRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0), Anchor = AnchorStyles.Left };
        statusRow.Controls.AddRange([statusDot, titleStack]);
        connectButton.FlatAppearance.BorderSize = 0;
        connectButton.Click += async (_, _) =>
        {
            if (busy && connection.State == ConnectionState.Connecting) operation?.Cancel();
            else await RunAsync(ToggleConnectionAsync);
        };
        var row = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, Margin = new Padding(0) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.Controls.Add(new Label { Text = "Profile", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) }, 0, 0);
        row.Controls.Add(profiles, 1, 0); row.Controls.Add(addButton, 2, 0); row.Controls.Add(moreButton, 3, 0);
        addButton.Click += (_, _) => addMenu.Show(addButton, new Point(0, addButton.Height));
        moreButton.Click += (_, _) => moreMenu.Show(moreButton, new Point(0, moreButton.Height));
        card.Controls.Add(statusRow, 0, 0); card.Controls.Add(connectButton, 0, 1); card.Controls.Add(row, 0, 2); card.Controls.Add(profileHint, 0, 3);
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        viewport.Controls.Add(card);
        viewport.Resize += (_, _) => card.Left = Math.Max(0, (viewport.ClientSize.Width - card.Width) / 2);
        connectTab.Controls.Add(viewport);
        tips.SetToolTip(profiles, "The profile used when you click Connect.");
    }

    private void BuildMenus()
    {
        addMenu.Items.Add("Paste link from clipboard", null, async (_, _) => await RunAsync(PasteLinkAsync));
        addMenu.Items.Add("Import file…", null, async (_, _) => await RunAsync(ImportAsync));
        addMenu.Items.Add("Create manually…", null, async (_, _) => await RunAsync(NewAsync));
        moreMenu.Items.AddRange([editItem, renameItem, removeItem, new ToolStripSeparator(), reviewItem]);
        editItem.Click += async (_, _) => await RunAsync(EditAsync);
        renameItem.Click += async (_, _) => await RunAsync(RenameAsync);
        removeItem.Click += async (_, _) => await RunAsync(RemoveAsync);
        reviewItem.Click += async (_, _) => await RunAsync(_ => ReviewProxyAsync());
    }

    private void BuildSettingsTab()
    {
        Add(settingsBar, "Save preferences", SaveDesktopAsync, "Save desktop preferences");
        Add(settingsBar, "Reset preferences", ResetDesktopAsync, "Reset preferences");
        Add(settingsBar, "Remove all profiles", ClearProfilesAsync, "Clear profile list");
        AddScrollablePage(settingsTab, [new Label { Text = "Window", AutoSize = true, Font = new Font(Font, FontStyle.Bold) }, minimizeToTray, rememberWindow,
            desktopNotice, settingsBar,
            new Label { Text = "Closing the window while connected keeps the connection running in the notification area. Use its menu, or Disconnect and close, to exit. Exiting always restores your previous Windows proxy settings.", AutoSize = true, Margin = new Padding(3, 12, 3, 3) },
            new Label { Text = "Connecting tests the tunnel with one HTTPS request to the destination set on the Diagnostics tab (default https://www.gstatic.com/generate_204), and repeats it every 20 seconds while connected to notice a dead server. Nothing else is sent.", AutoSize = true, Margin = new Padding(3, 8, 3, 3) },
            new Label { Text = "Shortcuts: F6 connect/disconnect, Ctrl+V paste link, Ctrl+O import file, Ctrl+E edit, Ctrl+L logs, Ctrl+D diagnostics, Ctrl+, settings, Ctrl+F find in logs.", AutoSize = true, Margin = new Padding(3, 8, 3, 3), Name = "settingsShortcuts" }]);
    }

    private static FlowLayoutPanel Bar() => new() { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 2, 0, 2) };
    private Button Add(FlowLayoutPanel bar, string text, Func<CancellationToken, Task> command, string? name = null)
    {
        var button = new Button { Text = text, AutoSize = true, Name = name ?? text };
        button.Click += async (_, _) => await RunAsync(command);
        bar.Controls.Add(button);
        return button;
    }
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
        var typing = focused is TextBoxBase or NumericUpDown || focused is ComboBox { DropDownStyle: not ComboBoxStyle.DropDownList };
        if (typing && e.KeyData != (Keys.Control | Keys.F)) return;
        switch (e.KeyData)
        {
            case Keys.F6: if (busy && connection.State == ConnectionState.Connecting) operation?.Cancel(); else await RunAsync(ToggleConnectionAsync); break;
            case Keys.Control | Keys.V: await RunAsync(PasteLinkAsync); break;
            case Keys.Control | Keys.O: await RunAsync(ImportAsync); break;
            case Keys.Control | Keys.E: await RunAsync(EditAsync); break;
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

    // The single command gate: one mutating command at a time, always cancellable,
    // never overlapping the read-only proxy poll.
    private async Task RunAsync(Func<CancellationToken, Task> command, bool quiet = false)
    {
        if (busy || closing) return;
        busy = true; using var source = new CancellationTokenSource(); operation = source; lastError = null; Render();
        try
        {
            healthSource?.Cancel(); // A command must not wait behind a slow health probe.
            await WaitForPollsAsync();
            source.Token.ThrowIfCancellationRequested();
            await command(source.Token);
        }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { session.Log.Add("app", "Operation cancelled."); }
        catch (Exception ex) { if (!quiet && !closing) ShowError(ex); else session.Log.Add("app", ex.Message); }
        finally { operation = null; busy = false; Render(); if (closing && !IsDisposed) BeginInvoke((Action)Close); }
    }
    private async Task WaitForPollsAsync()
    {
        foreach (var pending in new Task?[] { proxyPoll, healthPoll })
        {
            if (pending is null) continue;
            try { await pending; }
            catch (Exception) { /* Pollers record their own failures; commands perform fresh checks. */ }
        }
    }

    private async void OnTick(object? sender, EventArgs e)
    {
        Render();
        if (busy || closing) { if (diagnosing && session.ChildExited) operation?.Cancel(); return; }
        if (proxyPoll is not null || healthPoll is not null) return;
        try
        {
            if (session.ChildExited) { await RunAsync(_ => HandleChildExitAsync(), quiet: true); return; }
            var now = Environment.TickCount64;
            if (connection.Active && now - lastHealth >= HealthInterval.TotalMilliseconds)
            {
                lastHealth = now;
                var before = connection.State;
                using var health = new CancellationTokenSource();
                healthSource = health;
                var task = connection.CheckHealthAsync(diagnostics.HttpsDestination, health.Token);
                healthPoll = task;
                try { await task; }
                finally { healthPoll = null; healthSource = null; if (!IsDisposed) Render(); }
                if (before == ConnectionState.Connected && connection.State == ConnectionState.Unstable) Notify("Connection problem", "The server stopped answering. Open Xray Desktop to disconnect or wait for it to recover.", ToolTipIcon.Warning);
            }
            else if (now - lastProxyPoll >= 3000)
            {
                lastProxyPoll = now;
                // Read-only polling must not disable controls or steal focus while typing.
                var pending = Task.Run(proxy.Inspect);
                proxyPoll = pending;
                try { await pending; }
                finally { proxyPoll = null; if (!IsDisposed) Render(); }
            }
        }
        catch (OperationCanceledException) { /* A command took over the session. */ }
        catch (Exception ex) { session.Log.Add("app", $"Session poll failed: {ex.Message}"); }
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
        catch (Exception) { session.Log.Add("proxy", "Startup recovery incomplete; review Windows proxy settings."); }
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
            desktopNotice.Text = "Window preferences unreadable; defaults in use. The original file is kept until you save or reset.";
            session.Log.Add("app", "Desktop preferences unreadable; original retained.");
        }
        try { settings = await Task.Run(store.Load, token); }
        catch (Exception ex) { settingsUnreadable = true; session.Log.Add("app", $"Settings unreadable; original retained: {ex.Message}"); }
        RefreshProfiles(settings.ActiveId); await InspectAsync(token);
        try { diagnostics.ApplyPreferences(await Task.Run(probePreferences.Load, token)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or InvalidDataException)
        { probePreferencesUnreadable = true; session.Log.Add("app", "Diagnostic preferences unreadable; defaults used and original retained."); }
        await CheckCoreAsync(token);
    }
    private Task CheckCoreAsync(CancellationToken token) => Task.Run(() => session.CheckAsync(token), token);

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
            if (MessageBox.Show(this, "Your saved profile list could not be read. Replace it with the current list?", "Settings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        }
        settings = settings with { ActiveId = Selected?.Id };
        await Task.Run(() => store.Save(settings)); settingsUnreadable = false;
    }
    private async Task InspectAsync(CancellationToken token)
    {
        diagnostics.SetTargets([]);
        if (Selected is not { } profile) return;
        try
        {
            var bytes = await Task.Run(() => ProfileStore.ReadConfig(profile.Path), token);
            diagnostics.SetTargets(await Task.Run(() => RemoteDiscovery.Discover(bytes), token));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        { lastError = $"The configuration file for '{profile.Name}' cannot be read: {ex.Message}"; }
    }
    private void EnsureCapacity()
    {
        if (settings.Profiles.Count >= 500) throw new InvalidOperationException("Profile limit (500) reached; remove a profile first.");
    }
    private void RequireDisconnected()
    {
        if (connection.Active || session.HasChild) throw new InvalidOperationException("Disconnect first, then change your profiles.");
    }
    private Profile RequireProfile() => Selected ?? throw new InvalidOperationException("Add a profile first.");

    // ---- Adding, editing and removing profiles ------------------------------------

    private async Task EnsureValidAsync(string name, byte[] bytes, string workingDirectory, CancellationToken token)
    {
        var result = await Task.Run(() => session.ValidateAsync(bytes, workingDirectory, token), token);
        if (result.ExitCode == 0) return;
        var detail = FriendlyError.CoreMessage(result.Output);
        throw new InvalidDataException($"'{name}' is not a valid Xray configuration." + (detail is null ? "" : $" Xray says: {detail}"));
    }
    private async Task AddProfileAsync(Profile profile, CancellationToken token)
    {
        settings.Profiles.Add(profile); RefreshProfiles(profile.Id); await PersistAsync(); await InspectAsync(token);
        session.Log.Add("app", $"Added profile '{profile.Name}'.");
    }
    private async Task PasteLinkAsync(CancellationToken token)
    {
        RequireDisconnected();
        var text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
        if (!ShareLink.LooksLikeLink(text.TrimStart()))
            throw new InvalidOperationException("The clipboard has no vless://, vmess://, trojan:// or ss:// link. Copy the link first, then try again.");
        var parsed = await Task.Run(() => ShareLink.ParseMany(text), token);
        if (settings.Profiles.Count + parsed.Count > 500) throw new InvalidOperationException("That would exceed the limit of 500 profiles.");
        foreach (var item in parsed) await EnsureValidAsync(item.Name, item.Config, AppContext.BaseDirectory, token);
        foreach (var item in parsed)
        {
            var profile = await Task.Run(() => store.Create(item.Name, item.Config, AppContext.BaseDirectory), token);
            settings.Profiles.Add(profile); RefreshProfiles(profile.Id);
        }
        await PersistAsync(); await InspectAsync(token);
        session.Log.Add("app", $"Added {parsed.Count} profile(s) from the clipboard.");
    }
    private async Task ImportAsync(CancellationToken token)
    {
        using var dialog = new OpenFileDialog { Filter = "Xray configuration (*.json;*.jsonc)|*.json;*.jsonc|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await ImportFileAsync(dialog.FileName, token);
    }
    private async Task ImportFileAsync(string path, CancellationToken token)
    {
        EnsureCapacity(); RequireDisconnected();
        var full = Path.GetFullPath(path);
        var bytes = await Task.Run(() => ProfileStore.ReadConfig(full), token);
        await EnsureValidAsync(Path.GetFileNameWithoutExtension(full), bytes, Path.GetDirectoryName(full)!, token);
        var profile = await Task.Run(() => store.Import(full, managedCopy: true), token);
        await AddProfileAsync(profile, token);
    }
    private async Task NewAsync(CancellationToken token)
    {
        EnsureCapacity(); RequireDisconnected();
        var name = TextPrompt.Ask(this, "Create profile", "Name for the new profile:", "My profile");
        if (name is null) return;
        using var editor = new ConfigEditorForm("{\n  \"inbounds\": [],\n  \"outbounds\": []\n}\n"u8.ToArray(), bytes => ValidateEditorAsync(bytes, AppContext.BaseDirectory, token));
        if (editor.ShowDialog(this) != DialogResult.OK || editor.SavedBytes is not { } saved) return;
        var profile = await Task.Run(() => store.Create(name, saved, AppContext.BaseDirectory), token);
        await AddProfileAsync(profile, token);
    }
    private async Task EditAsync(CancellationToken token)
    {
        RequireDisconnected();
        var profile = RequireProfile();
        var bytes = await Task.Run(() => ProfileStore.ReadConfig(profile.Path), token);
        using var editor = new ConfigEditorForm(bytes, content => ValidateEditorAsync(content, profile.WorkingDirectory, token));
        if (editor.ShowDialog(this) != DialogResult.OK || editor.SavedBytes is not { } saved || bytes.AsSpan().SequenceEqual(saved)) return;
        await Task.Run(() => { if (!ProfileStore.ReadConfig(profile.Path).AsSpan().SequenceEqual(bytes)) throw new IOException("The file changed outside the editor; reopen it and try again."); ProfileStore.SaveConfig(profile.Path, saved); }, token);
        await InspectAsync(token);
    }
    private async Task<string> ValidateEditorAsync(byte[] bytes, string directory, CancellationToken token)
    {
        var result = await Task.Run(() => session.ValidateAsync(bytes, directory, token), token);
        return result.ExitCode == 0
            ? $"Configuration is valid ({result.Elapsed.TotalMilliseconds:0} ms)."
            : $"Xray rejected the configuration (exit {result.ExitCode}):\r\n{string.Join("\r\n", result.Output.TakeLast(8))}";
    }
    private async Task RenameAsync(CancellationToken token)
    {
        var profile = RequireProfile();
        var name = TextPrompt.Ask(this, "Rename profile", "New name:", profile.Name);
        if (name is null || name == profile.Name) return;
        var renamed = profile with { Name = name };
        settings.Profiles[settings.Profiles.FindIndex(p => p.Id == profile.Id)] = renamed;
        RefreshProfiles(renamed.Id); await PersistAsync();
    }
    private async Task RemoveAsync(CancellationToken token)
    {
        var profile = RequireProfile();
        if (MessageBox.Show(this, $"Remove '{profile.Name}'? A configuration the app copied is deleted; a file you referenced elsewhere is never touched.", "Remove profile", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        settings.Profiles.Remove(profile); RefreshProfiles(null); await PersistAsync();
        await Task.Run(() => store.Delete(profile), token);
        await InspectAsync(token);
    }
    private async Task ClearProfilesAsync(CancellationToken token)
    {
        RequireDisconnected();
        if (MessageBox.Show(this, "Remove every profile? Copies made by the app are deleted; files you referenced elsewhere are never touched.", "Remove all profiles", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        var all = settings.Profiles.ToList();
        var empty = new ProfileSettings(1, [], null);
        await Task.Run(() => store.Save(empty), token);
        settings = empty; settingsUnreadable = false; RefreshProfiles(null);
        await Task.Run(() => { foreach (var profile in all) store.Delete(profile); }, token);
        await InspectAsync(token);
    }

    // ---- Connecting ------------------------------------------------------------------

    private async Task ToggleConnectionAsync(CancellationToken token)
    {
        if (connection.Active || session.HasChild) { await TryDisconnectAsync(); return; }
        var profile = RequireProfile();
        await CheckCoreAsync(token);
        if (session.Version is { IsBaseline: false } version && MessageBox.Show(this, $"This Xray core is version {version.Version}, but the app was tested with {Bootstrap.TargetCoreVersion}. Connect anyway?", "Different Xray version", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var bytes = await Task.Run(() => ProfileStore.ReadConfig(profile.Path), token);
        lastHealth = Environment.TickCount64;
        var connected = await connection.ConnectAsync(profile.Name, bytes, profile.WorkingDirectory, diagnostics.HttpsDestination, ConfirmWarnings, ConfirmExternalProxy, token);
        if (!connected) { lastError = connection.Problem; return; }
        Notify("Connected", $"Windows apps now use '{profile.Name}'.", ToolTipIcon.Info);
    }
    private bool ConfirmWarnings(RunPlan plan) =>
        MessageBox.Show(this, "This profile has settings the app cannot fully check, for example a local port that other devices on your network might reach:\n\n• " + string.Join("\n• ", plan.Warnings.Take(6)) + "\n\nOnly continue if you trust where this profile came from. Connect anyway?",
            "Check this profile", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
    private bool ConfirmExternalProxy(ProxySettings original) =>
        MessageBox.Show(this, "Windows is already using a proxy or an automatic proxy script, perhaps from your workplace or another app.\n\nXray Desktop will replace it while connected and put it back exactly as it was when you disconnect.\n\nContinue?",
            "Replace existing proxy?", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    // True when disconnected. False means the user chose to stay connected because
    // Windows could not be put back.
    private async Task<bool> TryDisconnectAsync()
    {
        if (await connection.DisconnectAsync(ignoreProxyFailure: false)) return true;
        var choice = MessageBox.Show(this, "Your previous Windows proxy settings could not be restored automatically.\n\nStay connected: nothing changes, and you can retry.\nDisconnect anyway: the connection stops, and Windows may still point at it until you restore the settings (Windows Settings > Network & internet > Proxy) or reopen this app.\n\nDisconnect anyway?",
            "Could not restore proxy", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (choice != DialogResult.Yes) return false;
        await connection.DisconnectAsync(ignoreProxyFailure: true);
        lastError = "Windows may still be set to use the connection. Reopen this app, or restore your proxy settings in Windows Settings.";
        return true;
    }
    private async Task HandleChildExitAsync()
    {
        await connection.HandleCoreExitAsync();
        lastError = connection.Problem;
        Notify("Connection stopped", "The connection ended unexpectedly. Windows was restored.", ToolTipIcon.Warning);
    }

    // ---- Windows proxy recovery --------------------------------------------------------

    private async Task FixProxyAsync()
    {
        try { await Task.Run(proxy.Restore); session.Log.Add("proxy", "Previous Windows proxy settings restored."); }
        catch (Exception) { await ReviewProxyAsync(); }
    }
    private async Task ReviewProxyAsync()
    {
        var reviewed = await Task.Run(proxy.Inspect);
        static string Describe(ProxySettings? value) => value is null ? "Unavailable" : $"Flags: 0x{value.Flags:X} (direct=1, manual=2, PAC=4, autodetect=8)\r\nServer: {value.Server}\r\nBypass: {value.Bypass}\r\nPAC: {value.Pac}";
        using var dialog = new Form { Text = "Windows proxy review — private data", AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi, StartPosition = FormStartPosition.CenterParent, Size = new Size(720, 500), MinimumSize = new Size(520, 380) };
        var text = new TextBox { Multiline = true, ReadOnly = true, Dock = DockStyle.Fill, ScrollBars = ScrollBars.Both, WordWrap = false,
            Text = reviewed.Detail + "\r\n\r\nCURRENT\r\n" + Describe(reviewed.Current) + "\r\n\r\nORIGINAL (before this app)\r\n" + Describe(reviewed.Journal?.Original) + "\r\n\r\nSET BY THIS APP\r\n" + Describe(reviewed.Journal?.Applied) + "\r\n\r\nValues may contain private URLs or credentials; do not share them. You can always set your proxy by hand in Windows Settings > Network & internet > Proxy." };
        var choices = Bar(); choices.Dock = DockStyle.Bottom;
        var restore = new Button { Text = "Restore the original", AutoSize = true, DialogResult = DialogResult.Yes, Enabled = reviewed.Current is not null && reviewed.Journal is not null };
        var keep = new Button { Text = "Keep what Windows has now", AutoSize = true, DialogResult = DialogResult.No, Enabled = reviewed.State == ProxyState.Conflict };
        var settingsButton = new Button { Text = "Open Windows proxy settings", AutoSize = true };
        settingsButton.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:network-proxy") { UseShellExecute = true });
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        choices.Controls.AddRange([restore, keep, settingsButton, close]); dialog.Controls.Add(text); dialog.Controls.Add(choices); dialog.CancelButton = close;
        var choice = dialog.ShowDialog(this);
        if (choice is not (DialogResult.Yes or DialogResult.No)) return;
        var keepCurrent = choice == DialogResult.No;
        var question = keepCurrent ? "Keep what Windows has now and forget the saved original? Windows settings are not changed. If they still point at this app, you may lose internet access." : "Restore the saved original settings, even if another program changed them since?";
        if (MessageBox.Show(this, question, "Please confirm", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await Task.Run(() => proxy.ResolveReviewed(reviewed, keepCurrent));
        session.Log.Add("proxy", keepCurrent ? "User kept the current Windows proxy settings." : "Saved proxy settings restored and verified.");
    }

    // ---- Diagnostics and logs ----------------------------------------------------------

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
        var profile = Selected; var active = session.ActiveEndpoint;
        diagnosing = true; diagnostics.BeginRun();
        IReadOnlyList<DiagnosticResult>? rows = null;
        try
        {
            var context = await Task.Run(() => session.CaptureDiagnostics(profile, active, proxy.Inspect), token);
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

    // ---- Presentation --------------------------------------------------------------------

    private void ShowError(Exception ex)
    {
        var message = ex is UnauthorizedAccessException
            ? ex.Message + " Move the app folder to a place you can write to, or close the program using the file."
            : ex.Message;
        lastError = message;
        session.Log.Add("error", ex.ToString());
        Notify("Xray Desktop needs attention", message, ToolTipIcon.Error);
    }
    private void Notify(string title, string text, ToolTipIcon icon)
    {
        if (tray.Visible && (!Visible || WindowState == FormWindowState.Minimized)) tray.ShowBalloonTip(5000, title, text, icon);
    }

    private void Render()
    {
        var state = connection.State;
        var proxyState = proxy.Status;
        var idle = !busy && !closing;
        (string Title, string Detail, Color Dot, string Button, Color Back) view = state switch
        {
            ConnectionState.Connecting => ("Connecting…", "Starting the engine and testing the connection.", Color.FromArgb(230, 160, 20), "Cancel", Color.FromArgb(96, 96, 96)),
            ConnectionState.Connected => ("Connected", $"Windows apps use “{connection.ProfileName}” · {Uptime()}", Color.FromArgb(34, 160, 80), "Disconnect", Color.FromArgb(176, 52, 52)),
            ConnectionState.Unstable => ("Connection problem", "The server is not answering. Pages may not load. Wait for it to recover, or disconnect.", Color.FromArgb(230, 160, 20), "Disconnect", Color.FromArgb(176, 52, 52)),
            ConnectionState.Disconnecting => ("Disconnecting…", "Restoring your previous Windows settings.", Color.FromArgb(230, 160, 20), "Disconnecting…", Color.FromArgb(96, 96, 96)),
            _ => ("Not connected", Selected is null ? "Add a profile to get started." : "Windows is using its normal internet settings.", Color.FromArgb(150, 150, 150), "Connect", Color.FromArgb(0, 95, 184))
        };
        statusTitle.Text = view.Title; statusDetail.Text = view.Detail; statusDot.ForeColor = view.Dot;
        connectButton.Text = view.Button; connectButton.BackColor = view.Back; connectButton.ForeColor = Color.White;
        connectButton.Enabled = !closing && state != ConnectionState.Disconnecting &&
            (state == ConnectionState.Connecting ? busy : idle && (connection.Active || session.HasChild || Selected is not null));
        var editable = idle && !connection.Active && !session.HasChild;
        profiles.Enabled = editable; addButton.Enabled = editable; moreButton.Enabled = idle;
        editItem.Enabled = renameItem.Enabled = removeItem.Enabled = editable && Selected is not null;
        reviewItem.Enabled = idle;
        profileHint.Text = settings.Profiles.Count == 0
            ? "No profiles yet. Copy a vless://, vmess://, trojan:// or ss:// link and choose Add > Paste link from clipboard, or drop a .json file here."
            : "";
        errorText.Text = lastError ?? ""; errorPanel.Visible = lastError is not null;
        var needsAttention = !busy && proxyState.State is ProxyState.Conflict or ProxyState.RecoveryRequired;
        recoveryPanel.Visible = needsAttention;
        if (needsAttention) recoveryText.Text = proxyState.State == ProxyState.Conflict
            ? "Another program changed the Windows proxy settings while Xray Desktop was using them. Nothing was overwritten."
            : "Windows is still set to a proxy from a previous session of this app. Restore your previous settings to get back to normal.";
        cancel.Visible = busy && !closing && state != ConnectionState.Connecting;
        diagnostics.SetBusy(busy || closing);
        settingsBar.Enabled = idle;
        foreach (Control control in logBar.Controls) control.Enabled = !closing && (control == autoScroll || !busy);
        minimizeToTray.Enabled = rememberWindow.Enabled = idle;
        foreach (Control control in settingsBar.Controls)
            if (control.Name == "Clear profile list") control.Enabled = idle && !connection.Active;
        UpdateTray(state, idle);
        PumpLogs();
    }
    private string Uptime()
    {
        if (connection.Since is not { } since) return "—";
        var elapsed = DateTimeOffset.Now - since;
        return elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours} h {elapsed.Minutes:00} min" : $"{elapsed.Minutes} min {elapsed.Seconds:00} s";
    }

    private void PumpLogs()
    {
        var batch = session.Log.ReadAfter(logId); logId = batch.Id;
        if (batch.Lines.Length == 0) return;
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

    // ---- Closing ------------------------------------------------------------------------

    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (allowClose) return;
        // Windows is ending the session: no time for the async path, so put the proxy back right now.
        if (e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        {
            closing = true; timer.Stop(); connection.ShutdownNow();
            return;
        }
        // Closing the window while connected keeps the connection alive in the tray.
        if (e.CloseReason == CloseReason.UserClosing && !exitRequested && connection.Active)
        {
            e.Cancel = true; Hide();
            if (!trayHinted) { trayHinted = true; Notify("Still connected", "Xray Desktop keeps running here. Use the tray icon to disconnect or exit.", ToolTipIcon.Info); }
            return;
        }
        e.Cancel = true; closing = true; operation?.Cancel(); healthSource?.Cancel(); Render();
        if (!Visible || WindowState == FormWindowState.Minimized) ShowWindow();
        if (busy) return;
        busy = true; timer.Stop();
        try
        {
            await WaitForPollsAsync();
            if (await TryDisconnectAsync())
            {
                await SavePlacementOnExitAsync();
                allowClose = true; BeginInvoke((Action)Close);
            }
            else { closing = false; exitRequested = false; timer.Start(); }
        }
        catch (Exception ex) { ShowError(ex); closing = false; exitRequested = false; timer.Start(); }
        finally { busy = false; if (!allowClose) Render(); }
    }
}
