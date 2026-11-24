using System.Net;
using XrayDesktop.App;
using XrayDesktop.Config;

namespace XrayDesktop.UI;

internal sealed class MainForm : Form
{
    private readonly ProfileStore store = new(Path.Combine(AppContext.BaseDirectory, "data"));
    private readonly SessionController session = new(AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "data"));
    private readonly ComboBox profiles = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260, DisplayMember = "Name" };
    private readonly ComboBox endpoints = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
    private readonly Label status = new() { AutoSize = true };
    private readonly Label details = new() { AutoSize = true, MaximumSize = new Size(780, 0) };
    private readonly Label warnings = new() { AutoSize = true, MaximumSize = new Size(780, 0) };
    private readonly TextBox logs = new() { Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 9) };
    private readonly FlowLayoutPanel profileBar = Bar(), coreBar = Bar(), logBar = Bar();
    private readonly Button manual = new() { Text = "Manual endpoint", AutoSize = true };
    private readonly Button cancel = new() { Text = "Cancel operation", AutoSize = true, Enabled = false };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 300 };
    private ProfileSettings settings = new(1, [], null);
    private CancellationTokenSource? operation;
    private bool busy, closing, allowClose, updating, settingsUnreadable;
    private long logId;
    private Profile? Selected => profiles.SelectedItem as Profile;

    internal MainForm()
    {
        Text = $"{Bootstrap.ProductName} — {Bootstrap.Version}"; StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi; MinimumSize = new Size(850, 600); Size = new Size(1000, 700);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 8 };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < 6; i++) content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        profileBar.Controls.Add(profiles);
        Add(profileBar, "Import", ImportAsync); Add(profileBar, "New", NewAsync); Add(profileBar, "Edit", EditAsync); Add(profileBar, "Save As", SaveAsAsync); Add(profileBar, "Remove reference", RemoveAsync); Add(profileBar, "Reload", InspectAsync);
        var endpointBar = Bar(); endpointBar.Controls.Add(new Label { Text = "Local endpoint to verify:", AutoSize = true, Margin = new Padding(0, 8, 4, 0) }); endpointBar.Controls.Add(endpoints); endpointBar.Controls.Add(manual);
        manual.Click += (_, _) => SelectManualEndpoint();
        Add(coreBar, "Check core", CheckAsync); Add(coreBar, "Validate", ValidateAsync); Add(coreBar, "Start", StartAsync); Add(coreBar, "Stop", _ => session.StopAsync()); Add(coreBar, "Restart", async token => { await session.StopAsync(); await StartAsync(token); });
        var commands = Bar(); commands.Controls.Add(coreBar); commands.Controls.Add(cancel);
        cancel.Click += (_, _) => operation?.Cancel();
        Add(logBar, "Clear logs", _ => { session.Log.Clear(); logs.Clear(); return Task.CompletedTask; }, gated: false);
        Add(logBar, "Copy raw logs", _ => { if (logs.TextLength > 0 && MessageBox.Show(this, "Raw logs may contain secrets, addresses and private paths. Copy anyway?", "Sensitive data", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) Clipboard.SetText(logs.Text); return Task.CompletedTask; }, gated: false);
        content.Controls.Add(status, 0, 0); content.Controls.Add(profileBar, 0, 1); content.Controls.Add(details, 0, 2); content.Controls.Add(endpointBar, 0, 3); content.Controls.Add(warnings, 0, 4); content.Controls.Add(commands, 0, 5); content.Controls.Add(logs, 0, 6); content.Controls.Add(logBar, 0, 7);
        Controls.Add(content);
        profiles.SelectedIndexChanged += async (_, _) => { if (!updating && !busy) await RunAsync(async token => { await PersistAsync(); await InspectAsync(token); }); };
        Shown += async (_, _) => { await RunAsync(InitializeAsync); timer.Start(); };
        timer.Tick += async (_, _) =>
        {
            Render();
            if (!busy && session.HasChild)
            {
                try { if (session.ChildExited) await RunAsync(_ => session.PollAsync(), quiet: true); }
                catch (Exception ex) { session.Log.Add("app", $"Process poll failed: {ex.Message}"); }
            }
        };
        FormClosing += OnClosing;
        FormClosed += (_, _) => timer.Dispose();
        Render();
    }

    private static FlowLayoutPanel Bar() => new() { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = new Padding(0, 2, 0, 2) };
    private void Add(FlowLayoutPanel bar, string text, Func<CancellationToken, Task> command, bool gated = true)
    {
        var button = new Button { Text = text, AutoSize = true, Name = text };
        button.Click += async (_, _) => { if (gated) await RunAsync(command); else { try { await command(CancellationToken.None); } catch (Exception ex) { ShowError(ex); } } };
        bar.Controls.Add(button);
    }
    private async Task RunAsync(Func<CancellationToken, Task> command, bool quiet = false)
    {
        if (busy || closing) return;
        busy = true; using var source = new CancellationTokenSource(); operation = source; Render();
        try { await command(source.Token); }
        catch (OperationCanceledException) when (source.IsCancellationRequested) { session.Log.Add("app", "Operation cancelled."); }
        catch (Exception ex) { session.Log.Add("app", ex.Message); if (!quiet && !closing) ShowError(ex); }
        finally { operation = null; busy = false; Render(); if (closing && !IsDisposed) BeginInvoke((Action)Close); }
    }
    private async Task InitializeAsync(CancellationToken token)
    {
        try { settings = await Task.Run(store.Load, token); }
        catch (Exception ex) { settingsUnreadable = true; session.Log.Add("app", $"Settings unreadable; original retained: {ex.Message}"); }
        RefreshProfiles(settings.ActiveId); await InspectAsync(token);
        session.Log.Add("app", "Use trusted core/config files only. Local configurations and logs can contain credentials. No system proxy changes are implemented in this preview.");
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
        EnsureCapacity();
        using var dialog = new OpenFileDialog { Filter = "Xray configuration (*.json;*.jsonc)|*.json;*.jsonc|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var choice = MessageBox.Show(this, "Create a managed copy?\nYes: copy into app data. No: reference the original.\nRelative paths use the original directory; verify them before starting.", "Import", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (choice == DialogResult.Cancel) return;
        var profile = await Task.Run(() => store.Import(dialog.FileName, choice == DialogResult.Yes), token);
        settings.Profiles.Add(profile); RefreshProfiles(profile.Id); await PersistAsync(); await InspectAsync(token);
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
        return $"Xray validation exit {result.ExitCode} ({result.Elapsed.TotalMilliseconds:0} ms). See main window logs for local output.";
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
        if (Selected is not { } profile) { details.Text = "Select or import a raw Xray JSON configuration."; return; }
        details.Text = profile.Path;
        try
        {
            var inspection = await Task.Run(() => ConfigInspector.Inspect(ProfileStore.ReadConfig(profile.Path)), token);
            foreach (var endpoint in inspection.Endpoints) endpoints.Items.Add(endpoint);
            if (previous is not null && inspection.Endpoints.Contains(previous)) endpoints.SelectedItem = previous;
            else if (inspection.Endpoints.Count == 1) endpoints.SelectedIndex = 1;
            warnings.Text = string.Join(" ", inspection.Warnings.Take(5));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { warnings.Text = ex.Message; }
    }
    private Task CheckAsync(CancellationToken token) => Task.Run(() => session.CheckAsync(token), token);
    private async Task ValidateAsync(CancellationToken token)
    {
        var profile = RequireProfile(); var bytes = await Task.Run(() => ProfileStore.ReadConfig(profile.Path), token);
        var result = await ValidateEditorAsync(bytes, profile.WorkingDirectory, token); MessageBox.Show(this, result, "Validation");
    }
    private async Task StartAsync(CancellationToken token)
    {
        var profile = RequireProfile(); var selected = endpoints.SelectedItem as Inbound;
        await CheckAsync(token);
        if (session.Version is { IsBaseline: false } version && MessageBox.Show(this, $"Xray {version.Version} is outside the {Bootstrap.TargetCoreVersion} baseline. Continue?", "Compatibility", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        var bytes = await Task.Run(() => ProfileStore.ReadConfig(profile.Path), token);
        var inspection = await Task.Run(() => ConfigInspector.Inspect(bytes), token);
        if (inspection.Warnings.Count > 0 && MessageBox.Show(this, string.Join("\n", inspection.Warnings.Take(10)) + "\n\nStart this trusted configuration anyway?", "Configuration warnings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await Task.Run(() => session.StartAsync(bytes, profile.WorkingDirectory, selected, token), token);
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
    private void SelectManualEndpoint()
    {
        using var dialog = new Form { Text = "Manual listener verification", StartPosition = FormStartPosition.CenterParent, Size = new Size(440, 240), FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false };
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
        if (busy) status.Text = "Operation in progress… | Windows proxy: not managed | Connectivity: not tested";
        else status.Text = $"Core: {session.State} | Version: {session.Version?.Version ?? "unknown"} | PID: {session.Pid?.ToString() ?? "—"} | {session.Readiness}\nWindows proxy: not managed in Phase 1. Connectivity: not tested.";
        profileBar.Enabled = !busy && !running && !closing; endpoints.Enabled = !busy && !running && !closing; manual.Enabled = endpoints.Enabled && Selected is not null;
        foreach (Control control in coreBar.Controls) control.Enabled = !busy && !closing && (control.Name switch { "Stop" or "Restart" => running, "Check core" => !running, _ => !running && Selected is not null });
        cancel.Enabled = busy && !closing;
        var batch = session.Log.ReadAfter(logId); logId = batch.Id;
        if (batch.Lines.Length > 0)
        {
            if (logs.TextLength + batch.Lines.Sum(l => l.Length + 2) > 2 * 1024 * 1024) { logs.Text = string.Join(Environment.NewLine, session.Log.ReadAfter(0).Lines); }
            else logs.AppendText(string.Join(Environment.NewLine, batch.Lines) + Environment.NewLine);
            logs.SelectionStart = logs.TextLength; logs.ScrollToCaret();
        }
    }
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true; closing = true; operation?.Cancel(); Render();
        if (busy) return;
        busy = true; timer.Stop();
        try { if (session.HasChild) await session.StopAsync(); allowClose = true; BeginInvoke((Action)Close); }
        catch (Exception ex) { ShowError(ex); closing = false; timer.Start(); }
        finally { busy = false; }
    }
    private void ShowError(Exception ex) => MessageBox.Show(this, ex.Message, Bootstrap.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
}
