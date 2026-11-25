namespace XrayDesktop.UI;

internal static class TextPrompt
{
    // Null when cancelled or left blank.
    internal static string? Ask(IWin32Window owner, string title, string label, string initial)
    {
        using var dialog = new Form
        {
            Text = title, AutoScaleDimensions = new SizeF(96, 96), AutoScaleMode = AutoScaleMode.Dpi,
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, ClientSize = new Size(380, 120)
        };
        var box = new TextBox { Text = initial, MaxLength = 80, Left = 14, Top = 38, Width = 352 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 210, Top = 76, Width = 76 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 290, Top = 76, Width = 76 };
        dialog.Controls.AddRange([new Label { Text = label, Left = 14, Top = 14, AutoSize = true }, box, ok, cancel]);
        dialog.AcceptButton = ok; dialog.CancelButton = cancel;
        box.SelectAll();
        return dialog.ShowDialog(owner) == DialogResult.OK && box.Text.Trim() is { Length: > 0 } text ? text : null;
    }
}
