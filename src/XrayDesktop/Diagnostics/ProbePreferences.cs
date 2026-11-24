using System.Text.Json;
using XrayDesktop.Storage;

namespace XrayDesktop.Diagnostics;

internal sealed record ProbePreferences(int Version, string Https, string IpService, int TimeoutSeconds)
{
    internal static ProbePreferences Defaults { get; } = new(1, "https://www.gstatic.com/generate_204", "https://api.ipify.org", 10);

    internal void Validate()
    {
        if (Version != 1 || Https is null || IpService is null || TimeoutSeconds is < 1 or > 15)
            throw new InvalidDataException("Invalid diagnostic preferences.");
        _ = ProbeOptions.ParseHttps(Https); _ = ProbeOptions.ParseHttps(IpService);
    }
}

// Consent and remote targets are deliberately not persisted. Destinations may
// contain private query parameters, so this file remains local and private.
internal sealed class ProbePreferenceStore(string dataDirectory)
{
    private string FilePath => Path.Combine(dataDirectory, "diagnostics.json");

    internal ProbePreferences Load()
    {
        byte[] bytes;
        try { bytes = AtomicFile.ReadBounded(FilePath, 16 * 1024); }
        catch (FileNotFoundException) { return ProbePreferences.Defaults; }
        catch (DirectoryNotFoundException) { return ProbePreferences.Defaults; }
        var value = JsonSerializer.Deserialize<ProbePreferences>(bytes) ?? throw new InvalidDataException("Invalid diagnostic preferences.");
        value.Validate(); return value;
    }

    internal void Save(ProbePreferences value)
    {
        value.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > 16 * 1024) throw new InvalidDataException("Diagnostic preferences exceed 16 KiB.");
        AtomicFile.Write(FilePath, bytes);
    }
}
