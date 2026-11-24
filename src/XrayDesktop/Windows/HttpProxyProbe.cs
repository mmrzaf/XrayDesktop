using XrayDesktop.Config;
using XrayDesktop.Diagnostics;

namespace XrayDesktop.Windows;

internal static class HttpProxyProbe
{
    internal static readonly Uri TestUri = new("https://example.com/");
    internal static Task VerifyAsync(Inbound endpoint, CancellationToken token) => VerifyAsync(endpoint, TestUri, token);

    // Explicit approved request only. Share the no-bypass handler with diagnostics.
    internal static async Task VerifyAsync(Inbound endpoint, Uri destination, CancellationToken token)
    {
        if (endpoint.Protocol != "http") throw new InvalidOperationException("HTTP listener required.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var status = await NetworkProbes.HttpsAsync(destination, endpoint, timeout.Token).ConfigureAwait(false);
        if (status is < 200 or >= 300) throw new InvalidOperationException($"HTTPS proxy test returned HTTP {status}; Windows proxy was not enabled.");
    }
}
