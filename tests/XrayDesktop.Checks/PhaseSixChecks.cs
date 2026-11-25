using System.Text;
using System.Text.Json;
using XrayDesktop.App;
using XrayDesktop.Config;

namespace XrayDesktop.Checks;

internal static class PhaseSixChecks
{
    internal static (string Name, Action Run)[] All =>
    [
        ("VLESS REALITY link becomes a complete client configuration", VlessReality),
        ("VLESS WebSocket TLS link keeps path, host and server name", VlessWebSocket),
        ("VMess link decodes base64 JSON", Vmess),
        ("Trojan link defaults to TLS", Trojan),
        ("Shadowsocks links parse in both encodings", Shadowsocks),
        ("IPv6 server addresses lose their brackets", Ipv6),
        ("Malformed or unsupported links are rejected with a message", Rejected),
        ("Several pasted links parse together and a bad line fails the paste", Many),
        ("Generated configuration exposes anonymous SOCKS and HTTP listeners", Listeners),
        ("Run plan adds an HTTP listener only to the run copy", PlanAddsHttp),
        ("Run plan keeps an existing anonymous HTTP listener", PlanKeepsHttp),
        ("Run plan moves listeners whose ports are taken", PlanMovesPorts),
        ("Run plan rejects TUN configurations", PlanRejectsTun),
        ("Core validation output is reduced to its readable cause", CoreMessage),
        ("Guard arguments parse strictly", GuardArguments),
        ("Profile removal deletes only managed copies", DeleteManaged)
    ];

    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    private static void Throws<T>(Action run) where T : Exception
    {
        try { run(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static JsonElement Outbound(SharedProfile profile, JsonDocument holder) => holder.RootElement.GetProperty("outbounds")[0];
    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
    private const string Uuid = "b831381d-6324-4d53-ad4f-8cda48b30811";

    // One of each supported shape; the real-core integration check validates the generated configurations.
    internal static IEnumerable<(string Name, string Link)> SampleLinks()
    {
        yield return ("vless reality", $"vless://{Uuid}@example.com:443?type=tcp&security=reality&sni=www.microsoft.com&fp=chrome&pbk=ACC07MFOA_7Ivp_U4ez5Z-A0GYAtw470sHV9P4euzuU&sid=ab12cd34&flow=xtls-rprx-vision&encryption=none#r");
        yield return ("vless ws tls", $"vless://{Uuid}@cdn.example.com:443?type=ws&security=tls&host=front.example.com&path=%2Fws&sni=front.example.com&alpn=h2,http/1.1#w");
        yield return ("vless grpc tls", $"vless://{Uuid}@g.example.com:443?type=grpc&security=tls&serviceName=svc&sni=g.example.com#g");
        yield return ("vless tcp tls", $"vless://{Uuid}@t.example.com:8443?type=tcp&security=tls&sni=t.example.com#t");
        yield return ("vmess ws tls", "vmess://" + B64($$"""{"v":"2","ps":"v","add":"vm.example.com","port":"8443","id":"{{Uuid}}","aid":"0","scy":"auto","net":"ws","type":"none","host":"vm.example.com","path":"/v","tls":"tls","sni":"vm.example.com"}"""));
        yield return ("trojan", "trojan://secret@t.example.com:443?sni=t.example.com#t");
        yield return ("shadowsocks", $"ss://{B64("aes-256-gcm:secret")}@ss.example.com:8388#s");
    }

    private static void VlessReality()
    {
        var profile = ShareLink.Parse($"vless://{Uuid}@example.com:443?type=tcp&security=reality&sni=www.microsoft.com&fp=chrome&pbk=KEY&sid=ab12&flow=xtls-rprx-vision&encryption=none#My%20Server");
        Assert(profile.Name == "My Server");
        using var document = JsonDocument.Parse(profile.Config);
        var outbound = Outbound(profile, document);
        Assert(outbound.GetProperty("tag").GetString() == "proxy" && outbound.GetProperty("protocol").GetString() == "vless");
        var user = outbound.GetProperty("settings").GetProperty("vnext")[0].GetProperty("users")[0];
        Assert(user.GetProperty("id").GetString() == Uuid && user.GetProperty("flow").GetString() == "xtls-rprx-vision");
        var stream = outbound.GetProperty("streamSettings");
        Assert(stream.GetProperty("security").GetString() == "reality");
        var reality = stream.GetProperty("realitySettings");
        Assert(reality.GetProperty("publicKey").GetString() == "KEY" && reality.GetProperty("shortId").GetString() == "ab12" && reality.GetProperty("serverName").GetString() == "www.microsoft.com");
    }

    private static void VlessWebSocket()
    {
        var profile = ShareLink.Parse($"vless://{Uuid}@cdn.example.com:443?type=ws&security=tls&host=front.example.com&path=%2Fws&sni=front.example.com&alpn=h2,http/1.1");
        Assert(profile.Name == "cdn.example.com:443");
        using var document = JsonDocument.Parse(profile.Config);
        var stream = Outbound(profile, document).GetProperty("streamSettings");
        Assert(stream.GetProperty("network").GetString() == "ws");
        Assert(stream.GetProperty("wsSettings").GetProperty("path").GetString() == "/ws");
        Assert(stream.GetProperty("wsSettings").GetProperty("headers").GetProperty("Host").GetString() == "front.example.com");
        Assert(stream.GetProperty("tlsSettings").GetProperty("serverName").GetString() == "front.example.com");
        Assert(stream.GetProperty("tlsSettings").GetProperty("alpn").GetArrayLength() == 2);
    }

    private static void Vmess()
    {
        var json = $$"""{"v":"2","ps":"Office","add":"vm.example.com","port":"8443","id":"{{Uuid}}","aid":"0","scy":"auto","net":"ws","type":"none","host":"vm.example.com","path":"/v","tls":"tls","sni":"vm.example.com"}""";
        var profile = ShareLink.Parse("vmess://" + B64(json));
        Assert(profile.Name == "Office");
        using var document = JsonDocument.Parse(profile.Config);
        var outbound = Outbound(profile, document);
        var server = outbound.GetProperty("settings").GetProperty("vnext")[0];
        Assert(server.GetProperty("address").GetString() == "vm.example.com" && server.GetProperty("port").GetInt32() == 8443);
        Assert(outbound.GetProperty("streamSettings").GetProperty("security").GetString() == "tls");
    }

    private static void Trojan()
    {
        var profile = ShareLink.Parse("trojan://p%40ss@t.example.com:443?sni=t.example.com#Trojan");
        using var document = JsonDocument.Parse(profile.Config);
        var outbound = Outbound(profile, document);
        Assert(outbound.GetProperty("settings").GetProperty("servers")[0].GetProperty("password").GetString() == "p@ss");
        Assert(outbound.GetProperty("streamSettings").GetProperty("security").GetString() == "tls");
    }

    private static void Shadowsocks()
    {
        foreach (var link in new[] { $"ss://{B64("aes-256-gcm:secret")}@ss.example.com:8388#SS", $"ss://{B64("aes-256-gcm:secret@ss.example.com:8388")}#SS" })
        {
            var profile = ShareLink.Parse(link);
            Assert(profile.Name == "SS");
            using var document = JsonDocument.Parse(profile.Config);
            var server = Outbound(profile, document).GetProperty("settings").GetProperty("servers")[0];
            Assert(server.GetProperty("method").GetString() == "aes-256-gcm" && server.GetProperty("password").GetString() == "secret");
            Assert(server.GetProperty("address").GetString() == "ss.example.com" && server.GetProperty("port").GetInt32() == 8388);
        }
    }

    private static void Ipv6()
    {
        var profile = ShareLink.Parse($"vless://{Uuid}@[2001:db8::1]:443?security=tls&sni=x.example.com");
        using var document = JsonDocument.Parse(profile.Config);
        var address = Outbound(profile, document).GetProperty("settings").GetProperty("vnext")[0].GetProperty("address").GetString();
        Assert(address == "2001:db8::1");
    }

    private static void Rejected()
    {
        Throws<FormatException>(() => ShareLink.Parse("http://example.com"));
        Throws<FormatException>(() => ShareLink.Parse("vless://example.com:443"));
        Throws<FormatException>(() => ShareLink.Parse($"vless://{Uuid}@example.com"));
        Throws<FormatException>(() => ShareLink.Parse("vless://not-a-uuid@example.com:443"));
        Throws<FormatException>(() => ShareLink.Parse($"vless://{Uuid}@example.com:443?security=reality"));
        Throws<FormatException>(() => ShareLink.Parse($"vless://{Uuid}@example.com:443?type=carrierpigeon"));
        Throws<FormatException>(() => ShareLink.Parse("vmess://@@@"));
        Throws<FormatException>(() => ShareLink.Parse("ss://" + B64("nocolon@host:1")));
        Throws<FormatException>(() => ShareLink.Parse("trojan://@example.com:443"));
        Assert(!ShareLink.LooksLikeLink("hello") && ShareLink.LooksLikeLink("  VLESS://x"));
    }

    private static void Many()
    {
        var good = $"vless://{Uuid}@a.example.com:443?security=tls#A\r\n\r\ntrojan://pw@b.example.com:443#B\n";
        var profiles = ShareLink.ParseMany(good);
        Assert(profiles.Count == 2 && profiles[0].Name == "A" && profiles[1].Name == "B");
        Throws<FormatException>(() => ShareLink.ParseMany(good + "garbage"));
        Throws<FormatException>(() => ShareLink.ParseMany("   "));
    }

    private static void Listeners()
    {
        var profile = ShareLink.Parse($"vless://{Uuid}@a.example.com:443?security=tls");
        var inspection = ConfigInspector.Inspect(profile.Config);
        Assert(inspection.Warnings.Count == 0 && !inspection.HasTun);
        Assert(inspection.Endpoints.Any(e => e.Protocol == "socks" && e.Port == ShareLink.DefaultSocksPort && !e.Authenticated));
        Assert(inspection.Endpoints.Any(e => e.Protocol == "http" && e.Port == ShareLink.DefaultHttpPort && !e.Authenticated));
        Assert(inspection.Outbounds!.Contains("proxy") && inspection.RuleCount == 1);
    }

    private static readonly byte[] SocksOnly = """
        {"inbounds":[{"tag":"s","protocol":"socks","listen":"127.0.0.1","port":10808,"settings":{"auth":"noauth"}}],
         "outbounds":[{"protocol":"freedom","tag":"direct"}]}
        """u8.ToArray();

    private static void PlanAddsHttp()
    {
        var plan = RunPlan.Prepare(SocksOnly, _ => false, _ => 19090);
        Assert(plan.Http.Protocol == "http" && plan.Http.Port == 19090 && plan.Adjustments.Count == 1);
        Assert(ConfigInspector.Inspect(plan.Config).Endpoints.Count == 2);
        Assert(ConfigInspector.Inspect(SocksOnly).Endpoints.Count == 1);
    }

    private static void PlanKeepsHttp()
    {
        var profile = ShareLink.Parse($"vless://{Uuid}@a.example.com:443?security=tls");
        var plan = RunPlan.Prepare(profile.Config, _ => false, _ => throw new InvalidOperationException("No port should be requested."));
        Assert(plan.Http.Port == ShareLink.DefaultHttpPort && plan.Adjustments.Count == 0 && plan.Config.AsSpan().SequenceEqual(profile.Config));
    }

    private static void PlanMovesPorts()
    {
        var profile = ShareLink.Parse($"vless://{Uuid}@a.example.com:443?security=tls");
        var next = 20000;
        var plan = RunPlan.Prepare(profile.Config, port => port == ShareLink.DefaultHttpPort, avoid => { while (avoid.Contains(next)) next++; return next++; });
        Assert(plan.Http.Port != ShareLink.DefaultHttpPort && plan.Adjustments.Count == 1);
        var endpoints = ConfigInspector.Inspect(plan.Config).Endpoints;
        Assert(endpoints.Count == 2 && endpoints.Any(e => e.Protocol == "socks" && e.Port == ShareLink.DefaultSocksPort));
    }

    private static void PlanRejectsTun()
    {
        var tun = """{"inbounds":[{"protocol":"tun","tag":"t"}],"outbounds":[]}"""u8.ToArray();
        Throws<InvalidOperationException>(() => RunPlan.Prepare(tun, _ => false, _ => 19090));
    }

    private static void CoreMessage()
    {
        var chain = "08:11:15 [stdout] Failed to start: main: failed to load config files: [C:\\x\\config.json] > infra/conf: failed to build outbound config with tag proxy > infra/conf: vless without TLS or other encryption is prohibited unless the server address is a private IP or domain";
        Assert(FriendlyError.CoreMessage(["08:11:14 [stdout] banner", chain, " "]) == "vless without TLS or other encryption is prohibited unless the server address is a private IP or domain");
        Assert(FriendlyError.CoreMessage(["10:00:00 [stdout] simple failure"]) == "simple failure");
        Assert(FriendlyError.CoreMessage([]) is null && FriendlyError.CoreMessage([" ", ""]) is null);
    }

    private static void GuardArguments()
    {
        var id = Guid.NewGuid();
        Assert(Bootstrap.ParseArguments(["--guard", "1234", id.ToString("N")]) == StartupMode.Guard);
        Assert(Bootstrap.TryParseGuard(["--guard", "1234", id.ToString("N")], out var pid, out var parsed) && pid == 1234 && parsed == id);
        Assert(Bootstrap.ParseArguments(["--guard", "abc", id.ToString("N")]) == StartupMode.Invalid);
        Assert(Bootstrap.ParseArguments(["--guard", "0", id.ToString("N")]) == StartupMode.Invalid);
        Assert(Bootstrap.ParseArguments(["--guard", "1234", id.ToString()]) == StartupMode.Invalid);
        Assert(Bootstrap.ParseArguments(["--guard"]) == StartupMode.Invalid);
    }

    private static void DeleteManaged()
    {
        var root = Path.Combine(Path.GetTempPath(), "XrayDesktop-check-" + Guid.NewGuid().ToString("N"));
        var elsewhere = Path.Combine(root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        try
        {
            var store = new ProfileStore(Path.Combine(root, "data"));
            var managed = store.Create("managed", "{}"u8.ToArray(), elsewhere);
            var external = Path.Combine(elsewhere, "mine.json");
            File.WriteAllText(external, "{}");
            var referenced = store.Import(external, managedCopy: false);
            Assert(File.Exists(managed.Path));
            store.Delete(managed); store.Delete(referenced);
            Assert(!File.Exists(managed.Path) && File.Exists(external));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
