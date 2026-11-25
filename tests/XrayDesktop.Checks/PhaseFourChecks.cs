using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using XrayDesktop.Config;
using XrayDesktop.Storage;
using XrayDesktop.UI;

namespace XrayDesktop.Checks;

internal static class PhaseFourChecks
{
    internal static (string Name, Action Run)[] All =>
    [
        ("Desktop defaults do not hide the window", Defaults),
        ("Desktop preferences round-trip exactly", RoundTrip),
        ("Corrupt preferences remain unchanged after load failure", Corrupt),
        ("Oversized preferences are rejected without mutation", Oversized),
        ("Invalid preference save preserves previous file", InvalidSave),
        ("Disabled placement cannot retain window bounds", DisabledPlacement),
        ("Window placement respects negative monitor coordinates", NegativeMonitor),
        ("Disconnected monitor placement is centered on primary", DisconnectedMonitor),
        ("Partially visible placement is clamped to working area", PartialMonitor),
        ("Small working area wins over nominal minimum", SmallMonitor),
        ("Profile settings write respects the read size limit", ProfileSize),
        ("Profile reset preserves referenced configuration files", ProfileReset),
        ("Log buffer bound includes Windows line separators", LogBound)
    ];

    private static readonly Rectangle Primary = new(0, 0, 1920, 1040);
    private static readonly Rectangle Left = new(-1280, 0, 1280, 984);
    private static readonly Size Minimum = new(620, 460);

    private static void Assert(bool value) { if (!value) throw new InvalidOperationException("Assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static void WithDirectory(Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), "XrayDesktop-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try { action(path); }
        finally { Directory.Delete(path, recursive: true); }
    }
    private static void Defaults() => WithDirectory(root =>
    {
        var value = new DesktopPreferenceStore(root).Load();
        Assert(value == DesktopPreferences.Defaults && !value.MinimizeToTray && value.RememberWindow && value.AutoScrollLogs && value.Placement is null);
        Assert(!File.Exists(Path.Combine(root, "desktop.json")));
    });
    private static void RoundTrip() => WithDirectory(root =>
    {
        var store = new DesktopPreferenceStore(root);
        var value = new DesktopPreferences(1, true, true, false, new(-1050, 20, 900, 720, true));
        store.Save(value);
        Assert(store.Load() == value);
        Assert(Directory.GetFiles(root).Length == 1);
    });
    private static void Corrupt() => WithDirectory(root =>
    {
        var path = Path.Combine(root, "desktop.json");
        var bytes = "{not valid json"u8.ToArray(); File.WriteAllBytes(path, bytes);
        Throws<JsonException>(() => new DesktopPreferenceStore(root).Load());
        Assert(File.ReadAllBytes(path).SequenceEqual(bytes));
    });
    private static void Oversized() => WithDirectory(root =>
    {
        var path = Path.Combine(root, "desktop.json");
        var bytes = new byte[16 * 1024 + 1]; File.WriteAllBytes(path, bytes);
        Throws<InvalidDataException>(() => new DesktopPreferenceStore(root).Load());
        Assert(File.ReadAllBytes(path).SequenceEqual(bytes));
    });
    private static void InvalidSave() => WithDirectory(root =>
    {
        var store = new DesktopPreferenceStore(root); store.Save(DesktopPreferences.Defaults);
        var path = Path.Combine(root, "desktop.json"); var bytes = File.ReadAllBytes(path);
        Throws<InvalidDataException>(() => store.Save(new(2, true, true, false, null)));
        Throws<InvalidDataException>(() => store.Save(new(1, false, true, true, new(0, 0, -1, 800, false))));
        Assert(File.ReadAllBytes(path).SequenceEqual(bytes));
    });
    private static void DisabledPlacement() => WithDirectory(root =>
    {
        var store = new DesktopPreferenceStore(root);
        Throws<InvalidDataException>(() => store.Save(new(1, false, false, true, new(0, 0, 800, 600, false))));
        var value = new DesktopPreferences(1, false, false, true, null); store.Save(value); Assert(store.Load() == value);
    });
    private static void NegativeMonitor()
    {
        var value = new WindowPlacement(-1150, 100, 900, 700, true);
        Assert(value.Fit([Primary, Left], Primary, Minimum) == value.Bounds);
    }
    private static void DisconnectedMonitor()
    {
        var value = new WindowPlacement(-3000, 50, 1000, 760, false);
        Assert(value.Fit([Primary], Primary, Minimum) == new Rectangle(460, 140, 1000, 760));
    }
    private static void PartialMonitor()
    {
        var value = new WindowPlacement(1600, 800, 800, 600, false);
        var fitted = value.Fit([Primary, Left], Primary, Minimum);
        Assert(fitted == new Rectangle(1120, 440, 800, 600) && Primary.Contains(fitted));
    }
    private static void SmallMonitor()
    {
        var small = new Rectangle(10, 20, 500, 380);
        Assert(new WindowPlacement(20, 30, 900, 600, false).Fit([small], small, Minimum) == small);
    }
    private static void ProfileSize() => WithDirectory(root =>
    {
        var store = new ProfileStore(root); store.Save(new(1, [], null));
        var path = Path.Combine(root, "settings.json"); var before = File.ReadAllBytes(path);
        var profile = new Profile(Guid.NewGuid(), new string('x', 1024 * 1024), Path.Combine(root, "config.json"), root);
        Throws<InvalidDataException>(() => store.Save(new(1, [profile], profile.Id)));
        Assert(File.ReadAllBytes(path).SequenceEqual(before));
    });
    private static void ProfileReset() => WithDirectory(root =>
    {
        var store = new ProfileStore(root); var path = Path.Combine(root, "user.json"); File.WriteAllText(path, "{}");
        var profile = store.Import(path, false); var copy = store.Import(path, true);
        store.Save(new(1, [profile, copy], profile.Id)); store.Save(new(1, [], null));
        Assert(store.Load().Profiles.Count == 0 && File.ReadAllText(path) == "{}" && File.ReadAllText(copy.Path) == "{}");
    });
    private static void LogBound()
    {
        var log = new LogBuffer();
        for (var i = 0; i < 6000; i++) log.Add("fixture", new string('x', 4000));
        var batch = log.ReadAfter(0);
        Assert(batch.Lines.Length <= 5000 && batch.Lines.Sum(line => line.Length + 2) <= 2 * 1024 * 1024);
    }

    // Opt-in only: construct an invisible HWND on an STA. Never show or Close the
    // main form, since Shown/Closing perform real proxy recovery. Dispose only.
    internal static void ConstructHiddenForm()
    {
        Assert(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA);
        _ = Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        using var form = new MainForm();
        Assert(form.Handle != IntPtr.Zero && !form.Visible);
        var tabs = Descendants(form).OfType<TabControl>().Single();
        Assert(tabs.TabPages.Cast<TabPage>().Select(page => page.Text).SequenceEqual(new[] { "Connect", "Logs", "Diagnostics", "Settings" }));
        form.PerformLayout();
        Assert(!form.Visible);
    }
    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
