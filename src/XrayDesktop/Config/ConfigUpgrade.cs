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
        JsonNode? root;
        try
        {
            var hasBom = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
            var text = new UTF8Encoding(false, true).GetString(bytes, hasBom ? 3 : 0, bytes.Length - (hasBom ? 3 : 0));
            root = JsonNode.Parse(text, new JsonNodeOptions { PropertyNameCaseInsensitive = false },
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException ex) { throw new InvalidDataException("The configuration is not valid JSON or JSONC.", ex); }
        if (root is not JsonObject config) throw new InvalidDataException("The configuration root must be a JSON object.");
        JsonArray inbounds;
        if (config["inbounds"] is null) { inbounds = []; config["inbounds"] = inbounds; }
        else if (config["inbounds"] is JsonArray existing) inbounds = existing;
        else throw new InvalidDataException("inbounds must be a JSON array.");
        inbounds.Add(new JsonObject
        {
            ["tag"] = UniqueTag(inbounds),
            ["listen"] = "127.0.0.1",
            ["port"] = port,
            ["protocol"] = "http"
        });
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        return System.Text.Encoding.UTF8.GetBytes(root.ToJsonString(options));
    }

    private static string UniqueTag(JsonArray inbounds)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in inbounds)
            if (item is JsonObject inbound && inbound["tag"] is JsonValue value && value.TryGetValue<string>(out var tag))
                used.Add(tag);
        var name = DefaultHttpTag;
        for (var suffix = 2; used.Contains(name); suffix++) name = $"{DefaultHttpTag}-{suffix}";
        return name;
    }
}
