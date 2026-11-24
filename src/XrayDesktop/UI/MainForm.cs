using System.Drawing;
using System.Windows.Forms;

namespace XrayDesktop.UI;

internal sealed class MainForm : Form
{
    internal MainForm()
    {
        Text = Bootstrap.ProductName;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        MinimumSize = new Size(640, 360);
        ClientSize = new Size(760, 420);

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24),
            ColumnCount = 1,
            RowCount = 5,
            AutoScroll = true
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 4; row++)
        {
            content.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        content.Controls.Add(CreateLabel("Xray Desktop"), 0, 0);
        content.Controls.Add(CreateLabel($"Version {Bootstrap.Version} | Target Xray {Bootstrap.TargetCoreVersion}"), 0, 1);
        content.Controls.Add(CreateLabel("Phase 0 - development preview"), 0, 2);
        content.Controls.Add(CreateLabel("Core control and configuration editing are not implemented yet. This preview does not start Xray, access the network or change Windows proxy settings."), 0, 3);
        var close = new Button
        {
            Text = "Close",
            AutoSize = true,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right
        };
        close.Click += (_, _) => Close();
        content.Controls.Add(close, 0, 4);
        CancelButton = close;
        Controls.Add(content);
    }

    private static Label CreateLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Dock = DockStyle.Top,
        Margin = new Padding(0, 0, 0, 12)
    };
}
