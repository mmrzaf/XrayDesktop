using System.Text.Json;
using XrayDesktop.Config;

namespace XrayDesktop.Checks;

internal static class PhaseFiveChecks
{
    internal static (string Name, Action Run)[] All =>
    [
        ("HTTP listener upgrade preserves the profile and avoids duplicate tags", Upgrade),
        ("HTTP listener upgrade rejects invalid configuration roots", RejectInvalid)
    ];

    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    private static void Throws<T>(Action run) where T : Exception
    {
        try { run(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    private static void Upgrade()
    {
        var bytes = """
            {
              // The upgrade normalizes comments away, but must preserve the rest.
              "inbounds": [
                {"tag":"http-local","protocol":"socks","listen":"127.0.0.1","port":10808}
              ],
              "outbounds": [{"protocol":"freedom","tag":"direct"}]
            }
            """u8.ToArray();
        var updated = ConfigUpgrade.AddLoopbackHttp(bytes, 19090);
        using var document = JsonDocument.Parse(updated);
        var root = document.RootElement;
        var inbounds = root.GetProperty("inbounds");
        Assert(inbounds.GetArrayLength() == 2);
        var added = inbounds[1];
        Assert(added.GetProperty("protocol").GetString() == "http");
        Assert(added.GetProperty("tag").GetString() == "http-local-2");
        Assert(added.GetProperty("listen").GetString() == "127.0.0.1");
        Assert(added.GetProperty("port").GetInt32() == 19090);
        Assert(root.GetProperty("outbounds")[0].GetProperty("tag").GetString() == "direct");
        var inspection = ConfigInspector.Inspect(updated);
        Assert(inspection.Endpoints.Any(endpoint => endpoint.Protocol == "http" && endpoint.Port == 19090));
    }

    private static void RejectInvalid()
    {
        Throws<InvalidDataException>(() => ConfigUpgrade.AddLoopbackHttp("[]"u8.ToArray(), 19090));
        Throws<InvalidDataException>(() => ConfigUpgrade.AddLoopbackHttp("""{"inbounds":{}}"""u8.ToArray(), 19090));
        Throws<InvalidDataException>(() => ConfigUpgrade.AddLoopbackHttp("{"u8.ToArray(), 19090));
        Throws<ArgumentOutOfRangeException>(() => ConfigUpgrade.AddLoopbackHttp("""{"inbounds":[]}"""u8.ToArray(), 0));
    }
}
