using System.Text.Json;
using System.Text.Json.Serialization;

namespace XrayDesktop.Diagnostics;

internal static class DiagnosticReport
{
    private static readonly HashSet<string> KnownIds = ["D01", "D02", "D03", "D04", "D05", "D06", "D07", "D08", "D09", "D10", "D11", "D12a", "D12b", "D12c", "D13", "D14"];
    private static string? NumericVersion(string? value) => value is { Length: <= 16 } &&
        Version.TryParse(value, out var parsed) && parsed.Build >= 0 && parsed.Revision < 0 ? parsed.ToString(3) : null;

    internal static string Create(IReadOnlyList<DiagnosticResult> rows)
    {
        var data = new
        {
            schema = 1, application = Bootstrap.Version, targetCore = Bootstrap.TargetCoreVersion,
            os = Environment.OSVersion.Version.ToString(),
            detectedCore = NumericVersion(rows.FirstOrDefault(r => r.Id == "D01")?.DetectedCoreVersion),
            results = rows.Where(r => KnownIds.Contains(r.Id)).Select(r => new
            {
                id = r.Id, path = r.Path, outcome = r.Outcome, code = r.Code,
                elapsedMs = Math.Max(0, r.Milliseconds), utc = r.Timestamp.ToUniversalTime()
            }).ToArray()
        };
        return JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });
    }
}
