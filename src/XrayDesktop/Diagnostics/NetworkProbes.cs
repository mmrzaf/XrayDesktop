using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using XrayDesktop.Config;

namespace XrayDesktop.Diagnostics;

internal sealed record RemoteTarget(string Host, int Port)
{
    public override string ToString() => $"{Host}:{Port}";

    internal static RemoteTarget Parse(string host, int port)
    {
        host = host.Trim();
        if (host.Length is 0 or > 253 || Uri.CheckHostName(host) == UriHostNameType.Unknown || port is < 1 or > 65535)
            throw new ArgumentException("Enter a DNS hostname or IP address and a port from 1 to 65535.");
        return new(host, port);
    }
}

internal sealed record ProbeOptions(Uri Https, Uri IpService, TimeSpan NetworkTimeout, bool Direct, bool PublicIp, RemoteTarget? Remote)
{
    internal static Uri ParseHttps(string value)
    {
        if (value.Length > 2048 || !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Use an absolute HTTPS URL without credentials or a fragment.");
        return uri;
    }
}

// The platform handler must never substitute the system/environment proxy or
// bypass a loopback destination. No credentials, redirects or TLS overrides.
internal sealed class ExplicitProxy(Uri address) : IWebProxy
{
    public ICredentials? Credentials { get; set; }
    public Uri GetProxy(Uri destination) => address;
    public bool IsBypassed(Uri host) => false;
}

internal static class NetworkProbes
{
    internal static SocketsHttpHandler CreateHandler(Inbound? endpoint)
    {
        if (endpoint is not null && (endpoint.Protocol is not ("http" or "socks") ||
            (!endpoint.Manual && endpoint.Authenticated) ||
            (!endpoint.Address.Equals(IPAddress.Loopback) && !endpoint.Address.Equals(IPAddress.IPv6Loopback)) || endpoint.Port is < 1 or > 65535))
            throw new ArgumentException("An anonymous HTTP/SOCKS5 loopback endpoint is required.");
        return new SocketsHttpHandler
        {
            UseProxy = endpoint is not null,
            Proxy = endpoint is null ? null : new ExplicitProxy(new UriBuilder(endpoint.Protocol == "socks" ? "socks5" : "http", endpoint.Address.ToString(), endpoint.Port).Uri),
            AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(10), MaxResponseHeadersLength = 32,
            MaxResponseDrainSize = 0, DefaultProxyCredentials = null
        };
    }

    internal static async Task<int> HttpsAsync(Uri destination, Inbound? endpoint, CancellationToken token)
    {
        using var handler = CreateHandler(endpoint);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.GetAsync(destination, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        return (int)response.StatusCode;
    }

    internal static async Task<IPAddress> PublicIpAsync(Uri destination, Inbound? endpoint, CancellationToken token)
    {
        using var handler = CreateHandler(endpoint);
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using var response = await client.GetAsync(destination, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 128) throw new InvalidDataException("IP response too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        var buffer = new byte[129];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), token).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        if (length > 128) throw new InvalidDataException("IP response too large.");
        return ParseIp(Encoding.ASCII.GetString(buffer, 0, length));
    }

    internal static IPAddress ParseIp(string text)
    {
        var value = text.Trim();
        if (value.Length > 64 || value.Contains('%') || !IPAddress.TryParse(value, out var ip) ||
            (ip.AddressFamily == AddressFamily.InterNetwork && ip.ToString() != value))
            throw new InvalidDataException("IP service did not return a plain IPv4 or IPv6 address.");
        return ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
    }

    internal static Task<IPAddress[]> ResolveAsync(string host, CancellationToken token) => Dns.GetHostAddressesAsync(host, token);

    internal static async Task TcpAsync(RemoteTarget target, CancellationToken token)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(target.Host, target.Port, token).ConfigureAwait(false);
    }
}
