using XrayDesktop.Config;
using XrayDesktop.Diagnostics;

namespace XrayDesktop.Windows;

internal static class HttpProxyProbe
{
    internal static readonly Uri TestUri = new("https://example.com/");
    internal static Task VerifyAsync(Inbound endpoint, CancellationToken token) => VerifyAsync(endpoint, TestUri, token);

    // Explicit approved request only. Share the no-bypass handler with diagnostics.
    internal static Task VerifyAsync(Inbound endpoint, Uri destination, CancellationToken token) => VerifyAsync(endpoint, destination, TimeSpan.FromSeconds(10), token);

    internal static async Task VerifyAsync(Inbound endpoint, Uri destination, TimeSpan limit, CancellationToken token)
    {
        if (endpoint.Protocol != "http") throw new InvalidOperationException("HTTP listener required.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(limit);
        try
        {
            var status = await NetworkProbes.HttpsAsync(destination, endpoint, timeout.Token).ConfigureAwait(false);
            if (status is < 200 or >= 300) throw new InvalidOperationException($"HTTPS proxy test returned HTTP {status}; Windows proxy was not enabled.");
        }
        catch (OperationCanceledException ex) when (!token.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            throw new TimeoutException("HTTPS proxy verification timed out; Windows proxy was not enabled.", ex);
        }
    }
}
