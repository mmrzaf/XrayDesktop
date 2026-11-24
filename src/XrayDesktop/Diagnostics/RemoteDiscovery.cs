using System.Text.Json;
using XrayDesktop.Config;

namespace XrayDesktop.Diagnostics;

internal static class RemoteDiscovery
{
    // Advisory only: known server containers, never recursive search for arbitrary
    // addresses (which could include credentials, routing rules or DNS servers).
    internal static IReadOnlyList<RemoteTarget> Discover(byte[] bytes)
    {
        List<RemoteTarget> targets = [];
        try
        {
            ReadOnlyMemory<byte> content = bytes;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) content = content[3..];
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (!Property(document.RootElement, "outbounds", out var outbounds) || outbounds.ValueKind != JsonValueKind.Array) return targets;
            foreach (var outbound in outbounds.EnumerateArray())
            {
                if (!Property(outbound, "protocol", out var protocol) || protocol.ValueKind != JsonValueKind.String || !Property(outbound, "settings", out var settings)) continue;
                var container = protocol.GetString() switch { "vless" or "vmess" => "vnext", "trojan" or "shadowsocks" or "socks" or "http" => "servers", _ => null };
                if (container is null) continue;
                // Some core versions accept flat address/port settings as well.
                if (Property(settings, container, out var servers) && servers.ValueKind == JsonValueKind.Array)
                    foreach (var server in servers.EnumerateArray()) { Add(server); if (targets.Count >= 32) return targets; }
                else Add(settings);
                if (targets.Count >= 32) return targets;
            }
        }
        catch (JsonException) { }
        return targets;

        void Add(JsonElement server)
        {
            if (!Property(server, "address", out var address) || address.ValueKind != JsonValueKind.String ||
                !Property(server, "port", out var port) || !ConfigInspector.TryPort(port, out var number)) return;
            try
            {
                var target = RemoteTarget.Parse(address.GetString()!, number);
                if (!targets.Contains(target)) targets.Add(target);
            }
            catch (ArgumentException) { }
        }
    }

    private static bool Property(JsonElement item, string name, out JsonElement value)
    {
        value = default;
        if (item.ValueKind != JsonValueKind.Object) return false;
        var found = false;
        foreach (var property in item.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (found || property.Name != name) return false;
            found = true; value = property.Value;
        }
        return found;
    }
}
