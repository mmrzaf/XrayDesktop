using System.Net;
using System.Text.Json;

namespace XrayDesktop.Config;

internal sealed record Inbound(string Tag, string Protocol, IPAddress Address, int Port, bool Authenticated, bool Manual = false)
{
    public override string ToString() => $"{Protocol.ToUpperInvariant()} {Tag} — {(Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{Address}]" : Address.ToString())}:{Port}{(Authenticated ? " (auth/unknown)" : "")}";
}
internal sealed record Inspection(IReadOnlyList<Inbound> Endpoints, IReadOnlyList<string> Warnings);

internal static class ConfigInspector
{
    internal static Inspection Inspect(byte[] bytes)
    {
        List<Inbound> endpoints = [];
        List<string> warnings = [];
        try
        {
            ReadOnlyMemory<byte> content = bytes;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) content = content[3..];
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicates(root))
                return new([], ["Root is not an unambiguous JSON object; use native validation."]);
            if (!root.TryGetProperty("inbounds", out var array)) return new([], ["No inbounds declared."]);
            if (array.ValueKind != JsonValueKind.Array) return new([], ["inbounds is not an array."]);
            foreach (var item in array.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || HasDuplicates(item)) { warnings.Add("Ambiguous inbound ignored."); continue; }
                var protocol = ReadString(item, "protocol");
                if (protocol == "tun") { warnings.Add("TUN is not supported by this controller."); continue; }
                var listen = ReadString(item, "listen");
                if (!IPAddress.TryParse(listen, out var address) || !IPAddress.IsLoopback(address) || listen is not ("127.0.0.1" or "::1"))
                { warnings.Add("An inbound has an omitted, non-loopback or unsupported listen address; inspect exposure before starting."); continue; }
                if (protocol is not ("http" or "socks")) continue;
                if (!item.TryGetProperty("port", out var value) || !TryPort(value, out var port))
                { warnings.Add("An HTTP/SOCKS inbound has no single valid port."); continue; }
                if (item.TryGetProperty("streamSettings", out var stream) && (stream.ValueKind != JsonValueKind.Object || HasDuplicates(stream) || !OptionalStringIs(stream, "network", "tcp", "raw") || !OptionalStringIs(stream, "security", "none")))
                { warnings.Add("An inbound has unsupported transport/security for local endpoint selection."); continue; }
                var auth = false;
                if (item.TryGetProperty("settings", out var settings))
                    auth = settings.ValueKind != JsonValueKind.Object || HasDuplicates(settings) || settings.TryGetProperty("accounts", out _) || (settings.TryGetProperty("auth", out var authentication) && (authentication.ValueKind != JsonValueKind.String || authentication.GetString() != "noauth"));
                endpoints.Add(new(ReadString(item, "tag") ?? "(untagged)", protocol, address, port, auth));
            }
            if (endpoints.GroupBy(e => (e.Address, e.Port)).Any(g => g.Count() > 1))
            { warnings.Add("Duplicate local endpoints omitted."); endpoints = endpoints.GroupBy(e => (e.Address, e.Port)).Where(g => g.Count() == 1).Select(g => g.Single()).ToList(); }
        }
        catch (JsonException) { warnings.Add("JSON inspection failed; Xray validation is authoritative."); }
        return new(endpoints, warnings);
    }

    private static bool OptionalStringIs(JsonElement obj, string key, params string[] allowed) => !obj.TryGetProperty(key, out var value) || (value.ValueKind == JsonValueKind.String && allowed.Contains(value.GetString()));
    private static bool HasDuplicates(JsonElement obj) => obj.EnumerateObject().GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase).Any(g => g.Count() > 1);
    private static string? ReadString(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    internal static bool TryPort(JsonElement value, out int port)
    {
        port = 0;
        return (value.ValueKind == JsonValueKind.Number ? value.TryGetInt32(out port) : value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out port)) && port is >= 1 and <= 65535;
    }
}
