using System.Net;
using System.Net.Http;
using XrayDesktop.Config;

namespace XrayDesktop.Windows;

internal static class HttpProxyProbe
{
    internal static readonly Uri TestUri = new("https://example.com/");

    // Only invoked after explicit user consent. A single HTTPS request proves
    // anonymous HTTP CONNECT capability and current reachability of this target,
    // not that every route uses the intended remote server or public IP.
    internal static async Task VerifyAsync(Inbound endpoint, CancellationToken token)
    {
        var uri = new UriBuilder("http", endpoint.Address.ToString(), endpoint.Port).Uri;
        using var handler = new HttpClientHandler
        {
            UseProxy = true,
            Proxy = new WebProxy(uri, false),
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
            DefaultProxyCredentials = null
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await client.GetAsync(TestUri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTPS proxy test returned HTTP {(int)response.StatusCode}; Windows proxy was not enabled.");
    }
}
