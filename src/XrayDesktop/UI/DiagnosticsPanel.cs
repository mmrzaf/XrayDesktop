using System.Text;
using XrayDesktop.Diagnostics;
using XrayDesktop.Storage;

namespace XrayDesktop.UI;

internal sealed class DiagnosticsPanel : UserControl
{
    private readonly TextBox https = new() { Width = 340, Text = ProbePreferences.Defaults.Https, MaxLength = 2048 };
    private readonly TextBox ip = new() { Width = 340, Text = ProbePreferences.Defaults.IpService, MaxLength = 2048 };
    private readonly ComboBox remote = new() { Width = 210, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly TextBox host = new() { Width = 190, MaxLength = 253 };
    private readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Value = 443, Width = 75 };
    private readonly NumericUpDown timeout = new() { Minimum = 1, Maximum = 15, Value = 10, Width = 55 };
    private readonly CheckBox direct = new() { AutoSize = true, Text = "Allow direct DNS/TCP/HTTPS (reveals source IP)" };
    private readonly CheckBox publicIp = new() { AutoSize = true, Text = "Include public IP lookup" };
    private readonly ComboBox scope = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 140 };
    private readonly Button selected = new() { Text = "Run selected", AutoSize = true };
    private readonly Button all = new() { Text = "Run all", AutoSize = true };
    private readonly Button savePreferences = new() { Text = "Save destinations locally", AutoSize = true };
    private readonly Button report = new() { Text = "Preview / export report", AutoSize = true, Enabled = false };
    private readonly Label summary = new() { AutoSize = true, Text = "No diagnostics run." };
    private readonly TableLayoutPanel options = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1 };
    private readonly ListView list = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false, MultiSelect = false, ShowItemToolTips = true };
    private readonly List<DiagnosticResult> results = [];
    private bool running;
    internal event Action<DiagnosticScope>? RunRequested;
    internal event Action? SavePreferencesRequested;
    internal Uri HttpsDestination => ProbeOptions.ParseHttps(https.Text);

    internal DiagnosticsPanel()
    {
        Dock = DockStyle.Fill;
        scope.Items.AddRange(Enum.GetValues<DiagnosticScope>().Where(x => x != DiagnosticScope.All).Cast<object>().ToArray());
        scope.SelectedItem = DiagnosticScope.Local;
        remote.SelectedIndexChanged += (_, _) => { if (remote.SelectedItem is RemoteTarget target) { host.Text = target.Host; port.Value = target.Port; } };
        selected.Click += (_, _) => { if (scope.SelectedItem is DiagnosticScope group) RunRequested?.Invoke(group); };
        all.Click += (_, _) => RunRequested?.Invoke(DiagnosticScope.All);
        report.Click += (_, _) => PreviewReport();
        savePreferences.Click += (_, _) => SavePreferencesRequested?.Invoke();
        AddRow("HTTPS", https, LabelFor("Timeout (s)"), timeout);
        AddRow("IP service", ip, publicIp);
        AddRow("Remote", remote, host, port);
        AddRow("Consent", direct);
        AddRow("Run", scope, selected, all, report, savePreferences);
        options.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(800, 0), Text = "Run sends requests only for the selected group(s). Proxy tests use the selected protocol endpoint or its unique anonymous inbound; selected-path comparison prefers HTTP when none is selected. Direct requests bypass Windows proxy. TLS stays verified; Xray routing determines outbound. Results below are private; report export omits addresses and raw text." });
        list.Columns.Add("Probe", 190); list.Columns.Add("Path", 75); list.Columns.Add("Result", 80); list.Columns.Add("ms", 65); list.Columns.Add("Detail (local only)", 650);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(options, 0, 0); layout.Controls.Add(summary, 0, 1); layout.Controls.Add(list, 0, 2);
        Controls.Add(layout);
    }

    private static Label LabelFor(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(3, 7, 3, 0) };
    private void AddRow(string label, params Control[] controls)
    {
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        var caption = LabelFor(label); caption.AutoSize = false; caption.Width = 72; bar.Controls.Add(caption);
        bar.Controls.AddRange(controls); options.Controls.Add(bar);
    }

    internal void SetTargets(IReadOnlyList<RemoteTarget> targets)
    {
        remote.Items.Clear(); remote.Items.Add("Manual / not selected");
        foreach (var target in targets) remote.Items.Add(target);
        remote.SelectedIndex = targets.Count == 1 ? 1 : 0;
        if (targets.Count != 1) { host.Clear(); port.Value = 443; }
    }

    internal ProbeOptions ReadOptions(DiagnosticScope group)
    {
        var target = !direct.Checked || group is not (DiagnosticScope.All or DiagnosticScope.Remote) || string.IsNullOrWhiteSpace(host.Text)
            ? null : RemoteTarget.Parse(host.Text, (int)port.Value);
        var useHttps = (group is DiagnosticScope.All or DiagnosticScope.Http or DiagnosticScope.Socks5) || (group == DiagnosticScope.Direct && direct.Checked);
        var useIp = publicIp.Checked && (group is DiagnosticScope.All or DiagnosticScope.PublicIp);
        return new(useHttps ? HttpsDestination : new Uri(ProbePreferences.Defaults.Https),
            useIp ? ProbeOptions.ParseHttps(ip.Text) : new Uri(ProbePreferences.Defaults.IpService),
            TimeSpan.FromSeconds((double)timeout.Value), direct.Checked, publicIp.Checked, target);
    }

    internal ProbePreferences ReadPreferences()
    {
        var value = new ProbePreferences(1, https.Text, ip.Text, (int)timeout.Value);
        value.Validate(); return value;
    }

    internal void ApplyPreferences(ProbePreferences value)
    {
        https.Text = value.Https; ip.Text = value.IpService; timeout.Value = value.TimeoutSeconds;
    }

    internal void SetBusy(bool busy)
    {
        options.Enabled = !busy;
        report.Enabled = !busy && !running && results.Count > 0;
    }

    internal void BeginRun()
    {
        running = true; results.Clear(); list.Items.Clear(); report.Enabled = false;
        summary.Text = "Diagnostics running — use Cancel operation above to stop.";
    }

    internal void AddResult(DiagnosticResult row)
    {
        if (!running) return;
        results.Add(row);
        var item = new ListViewItem(new[] { row.Id + " " + ProbeName(row.Id), row.Path.ToString(), row.Outcome.ToString(), row.Milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture), row.Detail }) { ToolTipText = row.Timestamp.ToLocalTime().ToString("G") + " — " + row.Detail };
        list.Items.Add(item);
    }

    private static string ProbeName(string id) => id switch
    {
        "D01" => "Core version", "D02" => "JSON inspection", "D03" => "Native validation",
        "D04" => "Remote discovery", "D05" => "DNS resolution", "D06" => "TCP handshake",
        "D07" => "Local listener", "D08" => "HTTP HTTPS", "D09" => "SOCKS5 HTTPS",
        "D10" => "Direct HTTPS", "D11" => "Selected path latency", "D12a" => "Direct IP",
        "D12b" => "Proxied IP", "D12c" => "IP comparison", "D13" => "Windows proxy",
        "D14" => "Process health", _ => "Unknown"
    };

    internal void EndRun(IReadOnlyList<DiagnosticResult>? final)
    {
        if (final is not null)
        {
            results.Clear(); list.Items.Clear();
            foreach (var row in final) AddResult(row);
        }
        running = false;
        summary.Text = $"Last run: {DateTimeOffset.Now:g} | {results.Count(r => r.Outcome == Outcome.Pass)} pass, {results.Count(r => r.Outcome == Outcome.Fail)} fail, {results.Count(r => r.Outcome == Outcome.Warning)} warning, {results.Count(r => r.Outcome is Outcome.Skipped or Outcome.Cancelled)} skipped/cancelled. Not continuous monitoring.";
        report.Enabled = results.Count > 0;
    }

    private void PreviewReport()
    {
        var content = DiagnosticReport.Create(results);
        using var dialog = new Form { Text = "Sanitized report preview", Size = new Size(760, 580), MinimumSize = new Size(500, 350), StartPosition = FormStartPosition.CenterParent };
        var text = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, Text = content };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
        var copy = new Button { Text = "Copy", AutoSize = true };
        var save = new Button { Text = "Save JSON…", AutoSize = true };
        var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.Cancel };
        copy.Click += (_, _) => Attempt(() => Clipboard.SetText(content));
        save.Click += (_, _) => Attempt(() =>
        {
            using var file = new SaveFileDialog { Filter = "JSON report (*.json)|*.json", FileName = "xray-diagnostics.json", OverwritePrompt = true };
            if (file.ShowDialog(dialog) == DialogResult.OK) AtomicFile.Write(file.FileName, Encoding.UTF8.GetBytes(content));
        });
        bar.Controls.AddRange([copy, save, close]); dialog.Controls.Add(text); dialog.Controls.Add(bar); dialog.CancelButton = close;
        dialog.ShowDialog(FindForm());
        void Attempt(Action action) { try { action(); } catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Report export", MessageBoxButtons.OK, MessageBoxIcon.Error); } }
    }
}
