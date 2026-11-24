using System.Text.Json;
using XrayDesktop.Config;

namespace XrayDesktop.Checks;

internal static class PhaseFiveChecks
{
    internal static (string Name, Action Run)[] All =>
    [
        ("HTTP listener upgrade preserves the profile and avoids duplicate tags", Upgrade),
        ("HTTP listener upgrade rejects invalid configuration roots", RejectInvalid),
        ("Port upgrade changes only the selected listener", UsePort),
        ("Profile inspection summarizes outbounds and routing rules", Summarize)
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

    private static void Summarize()
    {
        var bytes = """
            {
              "inbounds": [{"tag":"socks","protocol":"socks","listen":"127.0.0.1","port":10808}],
              "outbounds": [
                {"tag":"warp","protocol":"masque"},
                {"tag":"exit","protocol":"vless"},
                {"protocol":"freedom"}
              ],
              "routing": {"rules": [{}, {}, {}]}
            }
            """u8.ToArray();
        var inspection = ConfigInspector.Inspect(bytes);
        Assert(inspection.Outbounds is not null && inspection.Outbounds.SequenceEqual(new[] { "warp", "exit", "freedom" }));
        Assert(inspection.RuleCount == 3 && inspection.Endpoints.Count == 1);
    }

    private static void UsePort()
    {
        var bytes = """
            {"inbounds":[
              {"tag":"socks","protocol":"socks","listen":"127.0.0.1","port":10808},
              {"tag":"http","protocol":"http","listen":"127.0.0.1","port":10809}
            ],"outbounds":[{"tag":"direct","protocol":"freedom"}]}
            """u8.ToArray();
        var inspection = ConfigInspector.Inspect(bytes);
        var updated = ConfigUpgrade.UsePort(bytes, inspection.Endpoints[0], 19091);
        var changed = ConfigInspector.Inspect(updated);
        Assert(changed.Endpoints.Any(endpoint => endpoint.Tag == "socks" && endpoint.Port == 19091));
        Assert(changed.Endpoints.Any(endpoint => endpoint.Tag == "http" && endpoint.Port == 10809));
        Throws<InvalidDataException>(() => ConfigUpgrade.UsePort(bytes, new Inbound("missing", "http", System.Net.IPAddress.Loopback, 19999, false), 19091));
    }
}
