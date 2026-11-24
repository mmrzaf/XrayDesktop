using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XrayDesktop.Config;

internal static class ConfigUpgrade
{
    internal const string DefaultHttpTag = "http-local";

    // Creates a normalized JSON copy with one additional loopback HTTP inbound.
    // The caller validates the result with Xray before saving it as a new profile.
    internal static byte[] AddLoopbackHttp(byte[] bytes, int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var config = Parse(bytes);
        var inbounds = Inbounds(config);
        inbounds.Add(new JsonObject
        {
            ["tag"] = UniqueTag(inbounds),
            ["listen"] = "127.0.0.1",
            ["port"] = port,
            ["protocol"] = "http"
        });
        return Serialize(config);
    }

    // Creates a normalized JSON copy with a different port for one verified listener.
    // Tags and routing stay unchanged; apps using the old port must switch.
    internal static byte[] UsePort(byte[] bytes, Inbound endpoint, int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        var config = Parse(bytes);
        var inbounds = Inbounds(config);
        if (FindInbound(inbounds, endpoint) is not { } target)
            throw new InvalidDataException("The selected listener could not be found in the configuration.");
        target["port"] = port;
        return Serialize(config);
    }

    private static JsonObject Parse(byte[] bytes)
    {
        JsonNode? root;
        try
        {
            var hasBom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
            var text = new UTF8Encoding(false, true).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
            root = JsonNode.Parse(text, new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        { throw new InvalidDataException("The configuration is not valid JSON or JSONC.", ex); }
        return root as JsonObject ?? throw new InvalidDataException("The configuration root must be a JSON object.");
    }
    private static JsonArray Inbounds(JsonObject config) => config["inbounds"] switch
    {
        null => (JsonArray) (config["inbounds"] = new JsonArray()),
        JsonArray existing => existing,
        _ => throw new InvalidDataException("inbounds must be a JSON array.")
    };
    private static JsonObject? FindInbound(JsonArray inbounds, Inbound endpoint)
    {
        JsonObject? fallback = null;
        foreach (var item in inbounds.OfType<JsonObject>())
        {
            var tag = ItemString(item, "tag");
            if (endpoint.Tag != "(untagged)" && string.Equals(tag, endpoint.Tag, StringComparison.Ordinal)) return item;
            if (string.Equals(ItemString(item, "protocol"), endpoint.Protocol, StringComparison.Ordinal) &&
                string.Equals(ItemString(item, "listen"), endpoint.Address.ToString(), StringComparison.Ordinal) &&
                ItemPort(item) == endpoint.Port)
                fallback = item;
        }
        return fallback;
    }
    private static string? ItemString(JsonObject item, string name) =>
        item[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static int? ItemPort(JsonObject item)
    {
        if (item["port"] is not JsonValue value) return null;
        if (value.TryGetValue<int>(out var number)) return number;
        return value.TryGetValue<string>(out var text) && int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number) ? number : null;
    }
    private static byte[] Serialize(JsonObject root)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return Encoding.UTF8.GetBytes(root.ToJsonString(options));
    }
    private static string UniqueTag(JsonArray inbounds)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in inbounds.OfType<JsonObject>())
            if (ItemString(item, "tag") is { } tag) used.Add(tag);
        var name = DefaultHttpTag;
        for (var suffix = 2; used.Contains(name); suffix++) name = $"{DefaultHttpTag}-{suffix}";
        return name;
    }
}
