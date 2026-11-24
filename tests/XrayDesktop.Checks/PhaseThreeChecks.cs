using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using XrayDesktop.Config;
using XrayDesktop.Diagnostics;
using XrayDesktop.Windows;

namespace XrayDesktop.Checks;

internal static class PhaseThreeChecks
{
    internal static (string Name, Action Run)[] All =>
    [
        ("Remote discovery supports known containers and flat settings", Discover),
        ("Remote discovery ignores ambiguous and unrelated addresses", Ambiguous),
        ("HTTPS destination validation rejects unsafe URLs", Urls),
        ("Explicit proxy never bypasses destinations", Handler),
        ("Anonymous loopback endpoint validation", EndpointSafety),
        ("IP response requires a plain address", IpResponse),
        ("Report reconstructs only allowed metadata", Report),
        ("Endpoint selection avoids ambiguous and authenticated candidates", Choose),
        ("Direct consent gates remote and internet probes", () => NoConsent().GetAwaiter().GetResult()),
        ("Cancelled suite performs no probes", () => Cancelled().GetAwaiter().GetResult()),
        ("Unowned listener is not contacted", () => Unowned().GetAwaiter().GetResult()),
        ("Direct TCP probe reaches a local fixture", () => Tcp().GetAwaiter().GetResult()),
        ("HTTP CONNECT rejection never falls back to origin", () => RejectHttp().GetAwaiter().GetResult()),
        ("SOCKS5 rejection never falls back to origin", () => RejectSocks().GetAwaiter().GetResult()),
        ("HTTPS cancellation interrupts a stalled proxy", () => CancelNetwork().GetAwaiter().GetResult()),
        ("Diagnostic errors classify without leaking exception text", Errors)
    ];

    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException($"Expected {typeof(T).Name}."); }
    private static Inbound Endpoint(int port, string protocol = "http") => new("fixture", protocol, IPAddress.Loopback, port, false);
    private static ProbeOptions Options => new(new("https://127.0.0.1:1/"), new("https://127.0.0.1:1/ip"), TimeSpan.FromSeconds(2), false, false, null);
    private static DiagnosticContext Context(Inbound? endpoint = null, Func<Inbound, bool>? owned = null) => new(null, null, endpoint,
        endpoint is null ? [] : [endpoint], null, owned ?? (_ => false), () => false,
        () => new(ProxyState.Unmanaged, "private detail"), null, null);

    private static void Discover()
    {
        var targets = RemoteDiscovery.Discover("""
            { // JSONC
              "outbounds": [
                {"protocol":"vless","settings":{"vnext":[{"address":"example.test","port":443,"users":[{"id":"secret"}]}]}},
                {"protocol":"trojan","settings":{"servers":[{"address":"::1","port":"8443"}]}},
                {"protocol":"vless","settings":{"address":"flat.test","port":444}},
                {"protocol":"vless","settings":{"vnext":[{"address":"example.test","port":443}]}},
              ],
            }
            """u8.ToArray());
        Assert(targets.SequenceEqual(new[] { new RemoteTarget("example.test", 443), new RemoteTarget("::1", 8443), new RemoteTarget("flat.test", 444) }));
    }
    private static void Ambiguous()
    {
        Assert(RemoteDiscovery.Discover("""
            {"outbounds":[{"protocol":"freedom","settings":{"address":"ignored.test","port":443}},
            {"protocol":"vless","settings":{"vnext":[{"address":"a.test","address":"b.test","port":443}]}},
            {"protocol":"trojan","settings":{"servers":[{"address":"https://bad.test/","port":443},{"address":"valid.test","port":0}]}}]}
            """u8.ToArray()).Count == 0);
        Assert(RemoteDiscovery.Discover("{"u8.ToArray()).Count == 0);
    }
    private static void Urls()
    {
        Assert(ProbeOptions.ParseHttps("https://example.test/path?value=1").Scheme == "https");
        foreach (var url in new[] { "http://example.test", "file:///C:/secret", "https://user:secret@example.test", "https://example.test/#secret", "relative" })
            Throws<ArgumentException>(() => ProbeOptions.ParseHttps(url));
        Throws<ArgumentException>(() => RemoteTarget.Parse("example.test/path", 443));
    }
    private static void Handler()
    {
        using var direct = NetworkProbes.CreateHandler(null);
        Assert(!direct.UseProxy && direct.Proxy is null && !direct.AllowAutoRedirect && !direct.UseCookies);
        foreach (var protocol in new[] { "http", "socks" })
        {
            using var handler = NetworkProbes.CreateHandler(Endpoint(12345, protocol));
            var proxy = handler.Proxy ?? throw new InvalidOperationException("Explicit proxy missing.");
            var origin = new Uri("https://127.0.0.1:444/");
            Assert(handler.UseProxy && !proxy.IsBypassed(origin) && proxy.GetProxy(origin)?.Scheme == (protocol == "socks" ? "socks5" : "http") && proxy.Credentials is null);
            Assert(!handler.AllowAutoRedirect && !handler.UseCookies);
        }
    }
    private static void EndpointSafety()
    {
        Throws<ArgumentException>(() => NetworkProbes.CreateHandler(Endpoint(1) with { Authenticated = true }));
        Throws<ArgumentException>(() => NetworkProbes.CreateHandler(Endpoint(1) with { Address = IPAddress.Any }));
        Throws<ArgumentException>(() => NetworkProbes.CreateHandler(Endpoint(0)));
        using var handler = NetworkProbes.CreateHandler(Endpoint(1234, "socks") with { Address = IPAddress.IPv6Loopback });
        Assert(handler.Proxy?.GetProxy(new("https://example.test"))?.Port == 1234);
    }
    private static void IpResponse()
    {
        Assert(NetworkProbes.ParseIp(" 192.0.2.1\n").ToString() == "192.0.2.1");
        Assert(NetworkProbes.ParseIp("::ffff:192.0.2.1").Equals(IPAddress.Parse("192.0.2.1")));
        foreach (var value in new[] { "<html>", "123", "127.1", "192.0.2.1 secret", "fe80::1%1", new string('x', 129) })
            Throws<InvalidDataException>(() => NetworkProbes.ParseIp(value));
    }
    private static void Report()
    {
        const string secret = "secret-uuid https://user:pw@example.test C:\\private 192.0.2.1";
        var rows = new[] { new DiagnosticResult("D12a", ProbePath.Direct, Outcome.Pass, ResultCode.Ok, 12, DateTimeOffset.UtcNow, secret),
            new DiagnosticResult(secret, ProbePath.Local, Outcome.Fail, ResultCode.Error, 0, DateTimeOffset.UtcNow, secret) };
        var text = DiagnosticReport.Create(rows);
        Assert(!text.Contains("secret", StringComparison.Ordinal) && !text.Contains("example.test", StringComparison.Ordinal) && !text.Contains("192.0.2.1", StringComparison.Ordinal));
        using var doc = System.Text.Json.JsonDocument.Parse(text);
        Assert(doc.RootElement.GetProperty("results").GetArrayLength() == 1 && doc.RootElement.GetProperty("results")[0].GetProperty("elapsedMs").GetInt64() == 12);
    }
    private static void Choose()
    {
        var a = Endpoint(1111); var b = Endpoint(2222); var context = Context() with { Endpoints = [a, b] };
        Assert(DiagnosticRunner.Choose(context, "http") is null);
        Assert(DiagnosticRunner.Choose(context with { Selected = b }, "http") == b);
        Assert(DiagnosticRunner.Choose(context with { Endpoints = [a with { Authenticated = true }] }, "http") is null);
    }
    private static async Task NoConsent()
    {
        var runner = new DiagnosticRunner(AppContext.BaseDirectory, Path.GetTempPath());
        var direct = await runner.RunAsync(Context(), Options, DiagnosticScope.Direct, null, CancellationToken.None);
        var remote = await runner.RunAsync(Context(), Options with { Remote = new("127.0.0.1", 1) }, DiagnosticScope.Remote, null, CancellationToken.None);
        var ip = await runner.RunAsync(Context(), Options, DiagnosticScope.PublicIp, null, CancellationToken.None);
        Assert(direct.Single().Outcome == Outcome.Skipped && remote.All(r => r.Outcome == Outcome.Skipped) && ip.All(r => r.Outcome == Outcome.Skipped));
    }
    private static async Task Cancelled()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var rows = await new DiagnosticRunner(AppContext.BaseDirectory, Path.GetTempPath()).RunAsync(Context(), Options, DiagnosticScope.All, null, cancellation.Token);
        Assert(rows.Count == 16 && rows.All(r => r.Outcome == Outcome.Cancelled));
    }
    private static async Task Unowned()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var rows = await new DiagnosticRunner(AppContext.BaseDirectory, Path.GetTempPath()).RunAsync(Context(Endpoint(Port(listener))), Options, DiagnosticScope.Http, null, CancellationToken.None);
        Assert(rows.Single().Code == ResultCode.NotOwned && !listener.Pending());
    }
    private static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;
    private static async Task Tcp()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await NetworkProbes.TcpAsync(new("127.0.0.1", Port(listener)), timeout.Token);
        using var accepted = await listener.AcceptTcpClientAsync(timeout.Token);
    }
    private static async Task RejectHttp()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        using var proxy = new TcpListener(IPAddress.Loopback, 0); proxy.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = NetworkProbes.HttpsAsync(new($"https://127.0.0.1:{Port(origin)}/"), Endpoint(Port(proxy)), timeout.Token);
        try
        {
            using var client = await proxy.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            Assert(await reader.ReadLineAsync(timeout.Token) == $"CONNECT 127.0.0.1:{Port(origin)} HTTP/1.1");
            for (var i = 0; i < 30; i++)
            {
                var line = await reader.ReadLineAsync(timeout.Token);
                Assert(line is not null && !line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
                if (line == "") break;
                if (i == 29) throw new InvalidDataException("Too many headers.");
            }
            await stream.WriteAsync("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
            await ExpectHttpFailure(request);
            Assert(!origin.Pending());
        }
        finally { await timeout.CancelAsync(); await Observe(request); }
    }
    private static async Task RejectSocks()
    {
        using var origin = new TcpListener(IPAddress.Loopback, 0); origin.Start();
        using var proxy = new TcpListener(IPAddress.Loopback, 0); proxy.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = NetworkProbes.HttpsAsync(new($"https://127.0.0.1:{Port(origin)}/"), Endpoint(Port(proxy), "socks"), timeout.Token);
        try
        {
            using var client = await proxy.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, timeout.Token);
            Assert(greeting[0] == 5 && greeting[1] > 0);
            var methods = new byte[greeting[1]]; await stream.ReadExactlyAsync(methods, timeout.Token); Assert(methods.SequenceEqual(new byte[] { 0 }));
            await stream.WriteAsync(new byte[] { 5, 0 }, timeout.Token);
            var header = new byte[4]; await stream.ReadExactlyAsync(header, timeout.Token); Assert(header[0] == 5 && header[1] == 1 && header[2] == 0);
            if (header[3] == 1)
            {
                var address = new byte[4]; await stream.ReadExactlyAsync(address, timeout.Token); Assert(address.SequenceEqual(new byte[] { 127, 0, 0, 1 }));
            }
            else if (header[3] == 3)
            {
                var length = new byte[1]; await stream.ReadExactlyAsync(length, timeout.Token);
                var address = new byte[length[0]]; await stream.ReadExactlyAsync(address, timeout.Token); Assert(Encoding.ASCII.GetString(address) == "127.0.0.1");
            }
            else throw new InvalidDataException("Unexpected SOCKS address type.");
            var port = new byte[2]; await stream.ReadExactlyAsync(port, timeout.Token); Assert((port[0] << 8 | port[1]) == Port(origin));
            await stream.WriteAsync(new byte[] { 5, 5, 0, 1, 0, 0, 0, 0, 0, 0 }, timeout.Token);
            await ExpectHttpFailure(request); Assert(!origin.Pending());
        }
        finally { await timeout.CancelAsync(); await Observe(request); }
    }
    private static async Task CancelNetwork()
    {
        using var proxy = new TcpListener(IPAddress.Loopback, 0); proxy.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = NetworkProbes.HttpsAsync(new("https://127.0.0.1:1/"), Endpoint(Port(proxy)), timeout.Token);
        try
        {
            using var client = await proxy.AcceptTcpClientAsync(timeout.Token);
            await timeout.CancelAsync();
            try { await request; } catch (OperationCanceledException) { return; }
            throw new InvalidOperationException("Cancellation not observed.");
        }
        finally { await timeout.CancelAsync(); await Observe(request); }
    }
    private static async Task ExpectHttpFailure(Task<int> request)
    {
        try { await request; } catch (HttpRequestException) { return; }
        throw new InvalidOperationException("Rejected proxy unexpectedly succeeded.");
    }
    private static async Task Observe(Task<int> request) { try { await request; } catch (Exception) { } }
    private static void Errors()
    {
        var result = DiagnosticRunner.Classify(new HttpRequestException(HttpRequestError.SecureConnectionError, "secret"));
        Assert(result.Item2 == ResultCode.Tls && !result.Item3.Contains("secret", StringComparison.Ordinal));
        Assert(DiagnosticRunner.Classify(new SocketException((int)SocketError.HostNotFound)).Item2 == ResultCode.Dns);
    }
}
