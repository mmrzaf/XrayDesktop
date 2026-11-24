using System.Text;
using System.Text.Json;
using XrayDesktop.Config;

namespace XrayDesktop.UI;

internal sealed class ConfigEditorForm : Form
{
    private readonly TextBox editor = new() { Multiline = true, AcceptsTab = true, AcceptsReturn = true, WordWrap = false, ScrollBars = ScrollBars.Both, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 10), MaxLength = ProfileStore.MaxConfigBytes };
    private readonly byte[] original;
    private readonly string initialText;
    private readonly bool bom;
    private readonly Func<byte[], Task<string>> validate;
    private bool busy;
    internal byte[]? SavedBytes { get; private set; }

    internal ConfigEditorForm(byte[] bytes, Func<byte[], Task<string>> validate)
    {
        original = bytes;
        this.validate = validate;
        bom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
        initialText = new UTF8Encoding(false, true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        Text = "Configuration editor (local plaintext)"; StartPosition = FormStartPosition.CenterParent;
        AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(500, 340); Size = new Size(850, 650);
        editor.Text = initialText;
        initialText = editor.Text;
        var hint = new Label { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 6, 8, 4), ForeColor = SystemColors.GrayText,
            Text = "Edit the raw local configuration. Save writes the file and closes this window; Validate asks Xray to check the current text without saving. The file is plaintext, so keep the folder private." };
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(8), WrapContents = true };
        Add(bar, "Save changes", () => { try { SavedBytes = GetBytes(); DialogResult = DialogResult.OK; Close(); } catch (Exception ex) { ShowError(ex); } }, primary: true);
        Add(bar, "Format strict JSON", Format);
        var check = new Button { Text = "Validate with Xray", AutoSize = true };
        check.Click += async (_, _) =>
        {
            busy = true; bar.Enabled = false; editor.ReadOnly = true;
            try { MessageBox.Show(this, await validate(GetBytes()), "Validation", MessageBoxButtons.OK); }
            catch (Exception ex) { ShowError(ex); }
            finally { busy = false; bar.Enabled = true; editor.ReadOnly = false; }
        };
        bar.Controls.Add(check);
        Add(bar, "Cancel", Close);
        Controls.Add(editor); Controls.Add(hint); Controls.Add(bar);
        FormClosing += (_, e) =>
        {
            if (busy) { e.Cancel = true; return; }
            if (SavedBytes is null && editor.Text != initialText && MessageBox.Show(this, "Discard unsaved changes?", "Editor", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) e.Cancel = true;
        };
    }

    private byte[] GetBytes()
    {
        if (editor.Text == initialText) return original;
        var bytes = new UTF8Encoding(false, true).GetBytes(editor.Text);
        if (bom) bytes = [0xef, 0xbb, 0xbf, .. bytes];
        if (bytes.Length > ProfileStore.MaxConfigBytes) throw new InvalidDataException("Configuration exceeds 10 MiB.");
        return bytes;
    }
    private void Format()
    {
        try
        {
            using var json = JsonDocument.Parse(editor.Text);
            editor.Text = JsonSerializer.Serialize(json.RootElement, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { MessageBox.Show(this, "Formatting requires strict JSON. JSONC comments/trailing commas are not removed automatically.", "Format", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
    private static Button Add(FlowLayoutPanel bar, string text, Action action, bool primary = false)
    {
        var button = new Button { Text = text, AutoSize = true };
        if (primary) { button.Font = new Font(button.Font, FontStyle.Bold); button.MinimumSize = new Size(130, 32); }
        button.Click += (_, _) => action();
        bar.Controls.Add(button);
        return button;
    }
    private void ShowError(Exception ex) => MessageBox.Show(this, ex.Message, "Configuration", MessageBoxButtons.OK, MessageBoxIcon.Error);
}
