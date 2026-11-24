using System.Text.Json;
using XrayDesktop.Storage;

namespace XrayDesktop.UI;

internal sealed record WindowPlacement(int X, int Y, int Width, int Height, bool Maximized)
{
    internal Rectangle Bounds => new(X, Y, Width, Height);

    internal void Validate()
    {
        if (X is < -100000 or > 100000 || Y is < -100000 or > 100000 ||
            Width is < 200 or > 20000 || Height is < 150 or > 20000)
            throw new InvalidDataException("Invalid saved window placement.");
    }

    // Keep the whole restored window inside a current working area. Select the
    // monitor by largest intersection, otherwise use the supplied primary area.
    internal Rectangle Fit(IReadOnlyList<Rectangle> areas, Rectangle primary, Size minimum)
    {
        Validate();
        var bounds = Bounds;
        var area = primary;
        long best = 0;
        foreach (var candidate in areas)
        {
            var intersection = Rectangle.Intersect(bounds, candidate);
            var overlap = (long)intersection.Width * intersection.Height;
            if (overlap > best) { best = overlap; area = candidate; }
        }
        var width = Math.Min(area.Width, Math.Max(Width, minimum.Width));
        var height = Math.Min(area.Height, Math.Max(Height, minimum.Height));
        var x = best == 0 ? area.X + (area.Width - width) / 2 : Math.Clamp(X, area.Left, area.Right - width);
        var y = best == 0 ? area.Y + (area.Height - height) / 2 : Math.Clamp(Y, area.Top, area.Bottom - height);
        return new(x, y, width, height);
    }
}

internal sealed record DesktopPreferences(int Version, bool MinimizeToTray, bool RememberWindow, bool AutoScrollLogs, WindowPlacement? Placement)
{
    internal static DesktopPreferences Defaults { get; } = new(1, false, true, true, null);
    internal void Validate()
    {
        if (Version != 1 || (!RememberWindow && Placement is not null)) throw new InvalidDataException("Invalid desktop preferences.");
        Placement?.Validate();
    }
}

internal sealed class DesktopPreferenceStore(string directory)
{
    private string FilePath => Path.Combine(directory, "desktop.json");
    internal DesktopPreferences Load()
    {
        if (!File.Exists(FilePath)) return DesktopPreferences.Defaults;
        var value = JsonSerializer.Deserialize<DesktopPreferences>(AtomicFile.ReadBounded(FilePath, 16 * 1024)) ?? throw new InvalidDataException("Invalid desktop preferences.");
        value.Validate(); return value;
    }
    internal void Save(DesktopPreferences value)
    {
        value.Validate();
        AtomicFile.Write(FilePath, JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true }));
    }
}
