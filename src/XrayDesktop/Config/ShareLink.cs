using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XrayDesktop.Config;

internal sealed record SharedProfile(string Name, byte[] Config);

// Turns the share links people actually receive (vless://, vmess://, trojan://,
// ss://) into a complete Xray client configuration. Anything the link format
// cannot express safely is rejected with a plain message instead of guessed.
internal static class ShareLink
{
    internal const int DefaultSocksPort = 10808, DefaultHttpPort = 10809;
    private static readonly string[] Schemes = ["vless://", "vmess://", "trojan://", "ss://"];

    internal static bool LooksLikeLink(string text) =>
        Schemes.Any(scheme => text.Trim().StartsWith(scheme, StringComparison.OrdinalIgnoreCase));

    // One link per line; blank lines are ignored. Fails on the first bad line so a
    // partial paste never silently drops a server.
    internal static IReadOnlyList<SharedProfile> ParseMany(string text)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || lines.Length > 100) throw new FormatException("Paste between 1 and 100 links.");
        return lines.Select(Parse).ToList();
    }

    internal static SharedProfile Parse(string link)
    {
        link = link.Trim();
        if (link.Length > 8192) throw new FormatException("The link is too long.");
        var (name, outbound) = link.ToLowerInvariant() switch
        {
            var l when l.StartsWith("vless://", StringComparison.Ordinal) => ParseUri(link, "vless"),
            var l when l.StartsWith("trojan://", StringComparison.Ordinal) => ParseUri(link, "trojan"),
            var l when l.StartsWith("vmess://", StringComparison.Ordinal) => ParseVmess(link),
            var l when l.StartsWith("ss://", StringComparison.Ordinal) => ParseShadowsocks(link),
            _ => throw new FormatException("Unsupported link. Use a vless://, vmess://, trojan:// or ss:// link.")
        };
        return new(name, BuildConfig(outbound));
    }

    internal static byte[] BuildConfig(JsonObject outbound, int socksPort = DefaultSocksPort, int httpPort = DefaultHttpPort)
    {
        outbound["tag"] = "proxy";
        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["inbounds"] = new JsonArray
            {
                new JsonObject { ["tag"] = "socks-local", ["listen"] = "127.0.0.1", ["port"] = socksPort, ["protocol"] = "socks", ["settings"] = new JsonObject { ["auth"] = "noauth", ["udp"] = true } },
                new JsonObject { ["tag"] = ConfigUpgrade.DefaultHttpTag, ["listen"] = "127.0.0.1", ["port"] = httpPort, ["protocol"] = "http", ["settings"] = new JsonObject() }
            },
            ["outbounds"] = new JsonArray
            {
                outbound,
                new JsonObject { ["tag"] = "direct", ["protocol"] = "freedom", ["settings"] = new JsonObject() },
                new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole", ["settings"] = new JsonObject() }
            },
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = "IPIfNonMatch",
                ["rules"] = new JsonArray
                {
                    new JsonObject { ["type"] = "field", ["ip"] = new JsonArray("geoip:private"), ["outboundTag"] = "direct" }
                }
            }
        };
        return Encoding.UTF8.GetBytes(config.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    private static (string Name, JsonObject Outbound) ParseUri(string link, string protocol)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.DnsSafeHost) || uri.Port is < 1 or > 65535)
            throw new FormatException($"This {protocol} link is incomplete (server or port missing).");
        var secret = Uri.UnescapeDataString(uri.UserInfo);
        if (secret.Length == 0) throw new FormatException($"This {protocol} link has no user id or password.");
        var query = ParseQuery(uri.Query);
        var address = uri.DnsSafeHost;
        var stream = BuildStream(query, defaultSecurity: protocol == "trojan" ? "tls" : "none");
        var settings = protocol == "vless"
            ? new JsonObject { ["vnext"] = new JsonArray(new JsonObject { ["address"] = address, ["port"] = uri.Port, ["users"] = new JsonArray(VlessUser(secret, query)) }) }
            : new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = address, ["port"] = uri.Port, ["password"] = secret }) };
        var outbound = new JsonObject { ["protocol"] = protocol, ["settings"] = settings, ["streamSettings"] = stream };
        return (DisplayName(uri.Fragment, address, uri.Port), outbound);
    }

    private static JsonObject VlessUser(string id, Dictionary<string, string> query)
    {
        if (!Guid.TryParse(id, out _)) throw new FormatException("The vless user id is not a valid UUID.");
        var user = new JsonObject { ["id"] = id, ["encryption"] = query.GetValueOrDefault("encryption") is { Length: > 0 } enc ? enc : "none" };
        if (query.GetValueOrDefault("flow") is { Length: > 0 } flow) user["flow"] = flow;
        return user;
    }

    private static (string Name, JsonObject Outbound) ParseVmess(string link)
    {
        JsonObject json;
        try
        {
            var payload = DecodeBase64(link["vmess://".Length..].Split('#')[0]);
            json = JsonNode.Parse(payload) as JsonObject ?? throw new FormatException();
        }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
        { throw new FormatException("This vmess link is not valid (it should contain base64 JSON)."); }
        string Text(string key) => json[key] switch { JsonValue v when v.TryGetValue<string>(out var s) => s, JsonValue v => v.ToString(), _ => "" };
        var id = Text("id"); var address = Text("add");
        if (!int.TryParse(Text("port"), NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535 || address.Length == 0 || !Guid.TryParse(id, out _))
            throw new FormatException("This vmess link is incomplete (server, port or id is wrong).");
        _ = int.TryParse(Text("aid"), NumberStyles.None, CultureInfo.InvariantCulture, out var alter);
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = Text("net"), ["headerType"] = Text("type"), ["host"] = Text("host"), ["path"] = Text("path"), ["security"] = Text("tls"),
            ["sni"] = Text("sni"), ["alpn"] = Text("alpn"), ["fp"] = Text("fp"), ["serviceName"] = Text("path"), ["seed"] = Text("path")
        };
        if (query["security"] is not ("tls" or "none" or "")) throw new FormatException("This vmess link uses an unsupported security setting.");
        var user = new JsonObject { ["id"] = id, ["alterId"] = alter, ["security"] = Text("scy") is { Length: > 0 } scy ? scy : "auto" };
        var outbound = new JsonObject
        {
            ["protocol"] = "vmess",
            ["settings"] = new JsonObject { ["vnext"] = new JsonArray(new JsonObject { ["address"] = address, ["port"] = port, ["users"] = new JsonArray(user) }) },
            ["streamSettings"] = BuildStream(query, "none")
        };
        return (DisplayName("#" + Uri.EscapeDataString(Text("ps")), address, port), outbound);
    }

    private static (string Name, JsonObject Outbound) ParseShadowsocks(string link)
    {
        var body = link["ss://".Length..];
        var fragment = body.IndexOf('#');
        var fragmentText = fragment >= 0 ? body[fragment..] : "";
        if (fragment >= 0) body = body[..fragment];
        var queryIndex = body.IndexOf('?');
        if (queryIndex >= 0) body = body[..queryIndex];
        string credentials, hostPort;
        try
        {
            var at = body.LastIndexOf('@');
            if (at >= 0)
            {
                var user = body[..at];
                credentials = user.Contains(':') ? Uri.UnescapeDataString(user) : DecodeBase64(user);
                hostPort = body[(at + 1)..];
            }
            else
            {
                var decoded = DecodeBase64(body);
                var split = decoded.LastIndexOf('@');
                if (split < 0) throw new FormatException();
                credentials = decoded[..split]; hostPort = decoded[(split + 1)..];
            }
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException)
        { throw new FormatException("This ss link is not valid."); }
        var colon = credentials.IndexOf(':');
        if (colon <= 0 || colon == credentials.Length - 1) throw new FormatException("This ss link has no method or password.");
        if (!Uri.TryCreate("ss://" + hostPort, UriKind.Absolute, out var endpoint) || string.IsNullOrEmpty(endpoint.DnsSafeHost) || endpoint.Port is < 1 or > 65535)
            throw new FormatException("This ss link is incomplete (server or port missing).");
        var outbound = new JsonObject
        {
            ["protocol"] = "shadowsocks",
            ["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = endpoint.DnsSafeHost, ["port"] = endpoint.Port, ["method"] = credentials[..colon], ["password"] = credentials[(colon + 1)..] }) }
        };
        return (DisplayName(fragmentText, endpoint.DnsSafeHost, endpoint.Port), outbound);
    }

    private static JsonObject BuildStream(Dictionary<string, string> query, string defaultSecurity)
    {
        string Get(string key) => query.GetValueOrDefault(key) ?? "";
        var type = Get("type").ToLowerInvariant();
        var network = type switch
        {
            "" or "tcp" or "raw" => "tcp", "ws" => "ws", "grpc" => "grpc", "h2" or "http" => "http",
            "httpupgrade" => "httpupgrade", "xhttp" or "splithttp" => "xhttp", "kcp" => "kcp",
            _ => throw new FormatException($"The transport '{type}' is not supported.")
        };
        var security = Get("security").ToLowerInvariant() is { Length: > 0 } s ? s : defaultSecurity;
        if (security is not ("none" or "tls" or "reality")) throw new FormatException($"The security mode '{security}' is not supported.");
        var stream = new JsonObject { ["network"] = network, ["security"] = security };
        var path = Get("path"); var host = Get("host");
        switch (network)
        {
            case "tcp" when Get("headerType") == "http":
                stream["tcpSettings"] = new JsonObject { ["header"] = new JsonObject { ["type"] = "http", ["request"] = new JsonObject
                {
                    ["path"] = new JsonArray(path.Length > 0 ? path : "/"),
                    ["headers"] = new JsonObject { ["Host"] = new JsonArray(host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(h => (JsonNode)h).ToArray()) }
                } } };
                break;
            case "ws":
                stream["wsSettings"] = new JsonObject { ["path"] = path.Length > 0 ? path : "/", ["headers"] = new JsonObject { ["Host"] = host } };
                break;
            case "grpc":
                stream["grpcSettings"] = new JsonObject { ["serviceName"] = Get("serviceName"), ["multiMode"] = Get("mode") == "multi" };
                break;
            case "http":
                stream["httpSettings"] = new JsonObject { ["path"] = path.Length > 0 ? path : "/", ["host"] = new JsonArray(host.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(h => (JsonNode)h).ToArray()) };
                break;
            case "httpupgrade":
                stream["httpupgradeSettings"] = new JsonObject { ["path"] = path.Length > 0 ? path : "/", ["host"] = host };
                break;
            case "xhttp":
                stream["xhttpSettings"] = new JsonObject { ["path"] = path.Length > 0 ? path : "/", ["host"] = host, ["mode"] = Get("mode") is { Length: > 0 } mode ? mode : "auto" };
                break;
            case "kcp":
                stream["kcpSettings"] = new JsonObject { ["header"] = new JsonObject { ["type"] = Get("headerType") is { Length: > 0 } header ? header : "none" }, ["seed"] = Get("seed") };
                break;
        }
        if (security == "tls")
        {
            var tls = new JsonObject { ["serverName"] = Get("sni") is { Length: > 0 } sni ? sni : host };
            if (Get("fp").Length > 0) tls["fingerprint"] = Get("fp");
            if (Get("alpn").Length > 0) tls["alpn"] = new JsonArray(Get("alpn").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(a => (JsonNode)a).ToArray());
            if (Get("allowInsecure") is "1" or "true") tls["allowInsecure"] = true;
            stream["tlsSettings"] = tls;
        }
        else if (security == "reality")
        {
            if (Get("pbk").Length == 0) throw new FormatException("This REALITY link has no public key (pbk).");
            stream["realitySettings"] = new JsonObject
            {
                ["serverName"] = Get("sni"), ["fingerprint"] = Get("fp") is { Length: > 0 } fp ? fp : "chrome",
                ["publicKey"] = Get("pbk"), ["shortId"] = Get("sid"), ["spiderX"] = Get("spx")
            };
        }
        return stream;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            var key = Uri.UnescapeDataString(split < 0 ? pair : pair[..split]);
            result[key] = split < 0 ? "" : Uri.UnescapeDataString(pair[(split + 1)..].Replace('+', ' '));
        }
        return result;
    }

    private static string DisplayName(string fragment, string host, int port)
    {
        var name = Uri.UnescapeDataString(fragment.TrimStart('#')).Trim();
        if (name.Length == 0) name = $"{host}:{port}";
        return name.Length > 80 ? name[..80] : name;
    }

    private static string DecodeBase64(string value)
    {
        value = value.Trim().Replace('-', '+').Replace('_', '/');
        value = value.PadRight(value.Length + (4 - value.Length % 4) % 4, '=');
        return new UTF8Encoding(false, true).GetString(Convert.FromBase64String(value));
    }
}
