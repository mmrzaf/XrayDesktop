using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.Http;
using System.Text;
using XrayDesktop.App;
using XrayDesktop.Config;
using XrayDesktop.Core;
using XrayDesktop.Diagnostics;
using XrayDesktop.Storage;
using XrayDesktop.Windows;

namespace XrayDesktop.Checks;

internal static class PhaseOneChecks
{
    internal static (string Name, Action Run)[] All =>
    [
        ("JSONC discovery preserves dynamic ports and IPv6", Inspect),
        ("Unsafe or ambiguous endpoints are not selected", UnsafeEndpoints),
        ("Malformed JSON inspection is advisory", () => Assert(ConfigInspector.Inspect("{"u8.ToArray()).Warnings.Count > 0)),
        ("Profile references and copies preserve exact bytes", Profiles),
        ("Run snapshot is immutable and cleaned", Snapshot),
        ("Log buffer bounds count and line length", Logs),
        ("Core version parser and command arguments", Commands),
        ("Missing core fails without launch", MissingCore),
        ("Windows TCP listener owner lookup", ListenerOwners),
        ("Owned child exits and drains streams", ChildExit),
        ("Owned child is terminated on disposal", ChildStop)
    ];

    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    private static void Throws<T>(Action run) where T : Exception
    {
        try { run(); } catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
    private static void WithDirectory(Action<string> run)
    {
        var path = Path.Combine(Path.GetTempPath(), $"Xray Desktop – {Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        Exception? failure = null;
        try { run(path); }
        catch (Exception ex) { failure = ex; throw; }
        finally
        {
            try { Directory.Delete(path, recursive: true); }
            catch (Exception cleanup) when (failure is not null)
            { throw new AggregateException("Fixture and temporary-directory cleanup both failed.", failure, cleanup); }
        }
    }
    private static void Inspect()
    {
        var bytes = """
            { // comment
              "inbounds": [
                {"tag":"http-a","protocol":"http","listen":"127.0.0.1","port":43210},
                {"tag":"socks-b","protocol":"socks","listen":"::1","port":"54321","settings":{"auth":"noauth"}},
              ], "unknown":true,
            }
            """u8.ToArray();
        var inspection = ConfigInspector.Inspect(bytes);
        Assert(inspection.Endpoints.Count == 2 && inspection.Warnings.Count == 0);
        Assert(inspection.Endpoints[0].Port == 43210 && inspection.Endpoints[1].Address.Equals(IPAddress.IPv6Loopback));
    }
    private static void UnsafeEndpoints()
    {
        var bytes = """
            {"inbounds":[
              {"protocol":"http","port":1234},
              {"protocol":"http","listen":"0.0.0.0","port":1235},
              {"protocol":"socks","listen":"127.0.0.1","port":"1-2"},
              {"protocol":"http","listen":"127.0.0.1","port":5678,"settings":{"accounts":[{}]}},
              {"protocol":"http","listen":"127.0.0.1","port":9876,"port":9877},
              {"protocol":"http","listen":"127.0.0.1","port":5432,"streamSettings":{"network":"ws"}}
            ]}
            """u8.ToArray();
        var inspection = ConfigInspector.Inspect(bytes);
        Assert(inspection.Endpoints.Count == 1 && inspection.Endpoints[0].Authenticated && inspection.Warnings.Count == 5);
    }
    private static void Profiles() => WithDirectory(root =>
    {
        var source = Path.Combine(root, "source.jsonc"); var bytes = "{ /* comment */ \"unknown\": 1 }\n"u8.ToArray(); File.WriteAllBytes(source, bytes);
        var store = new ProfileStore(Path.Combine(root, "data"));
        var reference = store.Import(source, false); var copy = store.Import(source, true);
        Assert(reference.Path == source && copy.Path != source && copy.WorkingDirectory == root);
        Assert(File.ReadAllBytes(copy.Path).SequenceEqual(bytes));
        store.Save(new(1, [reference, copy], copy.Id)); var loaded = store.Load();
        Assert(loaded.ActiveId == copy.Id && loaded.Profiles.SequenceEqual(new[] { reference, copy }));
        ProfileStore.SaveConfig(copy.Path, "{}"u8.ToArray()); Assert(File.ReadAllBytes(source).SequenceEqual(bytes));
        File.WriteAllText(Path.Combine(store.DataDirectory, "settings.json"), "invalid"); Throws<System.Text.Json.JsonException>(() => store.Load());
    });
    private static void Snapshot() => WithDirectory(root =>
    {
        string path;
        using (var snapshot = new RunSnapshot(root, "{}"u8.ToArray(), root))
        {
            path = snapshot.Path; snapshot.Verify(); File.WriteAllText(path, "modified"); Throws<InvalidDataException>(snapshot.Verify);
        }
        Assert(!File.Exists(path));
    });
    private static void Logs()
    {
        var log = new LogBuffer();
        for (var i = 0; i < 6000; i++) log.Add("test", new string('x', 5000));
        var batch = log.ReadAfter(0); Assert(batch.Lines.Length <= 5000 && batch.Lines.Sum(l => l.Length) <= 2 * 1024 * 1024);
        Assert(batch.Lines.All(l => l.Length < 4200)); Assert(log.ReadAfter(batch.Id).Lines.Length == 0); log.Clear(); Assert(log.ReadAfter(0).Lines.Length == 0);
    }
    private static void Commands() => WithDirectory(root =>
    {
        Assert(XrayProcess.ParseVersion(["Xray 26.11.9 (Xray, Penetrates Everything.)"]) == Bootstrap.TargetCoreVersion);
        Throws<InvalidDataException>(() => XrayProcess.ParseVersion(["other 26.11.9"]));
        var core = new XrayProcess(root); var config = Path.Combine(root, "config space.json"); var info = core.BuildStartInfo(root, "run", "-c", config);
        Assert(info.FileName == Path.Combine(root, "xray.exe") && info.ArgumentList.SequenceEqual(new[] { "run", "-c", config }) && !info.UseShellExecute);
    });
    private static void MissingCore() => WithDirectory(root =>
    {
        var session = new SessionController(root, root);
        Throws<FileNotFoundException>(() => session.CheckAsync(CancellationToken.None).GetAwaiter().GetResult()); Assert(session.State == CoreState.Missing && !session.HasChild);
    });
    private static void ListenerOwners()
    {
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            using var listener = new TcpListener(address, 0); listener.Start();
            var endpoint = new Inbound("test", "http", address, ((IPEndPoint)listener.LocalEndpoint).Port, false);
            Assert(TcpListeners.IsOwned(endpoint, Environment.ProcessId)); Assert(!TcpListeners.IsOwned(endpoint, 0));
        }
    }
    private static ProcessStartInfo TestChild(string arg)
    {
        // dotnet run uses the checks apphost. When hosted by dotnet directly, pass the assembly explicitly.
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing current executable.");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(PhaseOneChecks).Assembly.Location);
        info.ArgumentList.Add(arg); return info;
    }
    private static void ChildExit() => ChildExitAsync().GetAwaiter().GetResult();
    private static async Task ChildExitAsync()
    {
        var log = new LogBuffer(); await using var child = new OwnedProcess(TestChild("--child-exit"), log);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); Assert(await child.WaitAsync(timeout.Token) == 7);
        Assert(log.ReadAfter(0).Lines.Any(l => l.Contains("test stdout", StringComparison.Ordinal)) && log.ReadAfter(0).Lines.Any(l => l.Contains("test stderr", StringComparison.Ordinal)));
    }
    private static void ChildStop() => ChildStopAsync().GetAwaiter().GetResult();
    private static async Task ChildStopAsync()
    {
        var child = new OwnedProcess(TestChild("--child-wait"), new LogBuffer()); using var process = Process.GetProcessById(child.Id);
        _ = process.SafeHandle; // Retain a handle before the job terminates its child.
        await child.DisposeAsync(); Assert(process.WaitForExit(5000));
    }
    internal static void RealCore()
    {
        var executable = Environment.GetEnvironmentVariable("XRAY_TEST_CORE");
        if (string.IsNullOrWhiteSpace(executable)) { Console.WriteLine("SKIP: XRAY_TEST_CORE not supplied; real Xray acceptance pending."); return; }
        RealCore(executable);
    }
    internal static void RealCore(string executable)
    {
        // The supplied core was already verified by CI. Run it in place, leaving
        // its executable and assets outside the disposable configuration directory.
        // Windows can retain an executable image/AV handle after process exit.
        executable = Path.GetFullPath(executable);
        if (!Path.GetFileName(executable).Equals("xray.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new FileNotFoundException("XRAY_TEST_CORE must point to an existing xray.exe.", executable);
        WithDirectory(root => RealCoreAsync(root, executable).GetAwaiter().GetResult());
    }
    private static async Task RealCoreAsync(string root, string executable)
    {
        // The supplied core was already verified by CI. Run it in place, leaving
        // its executable and assets outside the disposable configuration directory.
        // Windows can retain an executable image/AV handle after process exit.
        executable = Path.GetFullPath(executable);
        if (!Path.GetFileName(executable).Equals("xray.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new FileNotFoundException("XRAY_TEST_CORE must point to an existing xray.exe.", executable);
        var session = new SessionController(Path.GetDirectoryName(executable)!, Path.Combine(root, "data"));
        using var token = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        Console.WriteLine("Integration: core version and malformed configuration");
        await session.CheckAsync(token.Token); Assert(session.Version?.IsBaseline == true);
        var invalid = await session.ValidateAsync("{"u8.ToArray(), root, token.Token);
        Assert(invalid.ExitCode != 0 && !session.HasChild);
        int httpPort, socksPort;
        using var origin = new TcpListener(IPAddress.Loopback, 0);
        origin.Start();
        using (var http = new TcpListener(IPAddress.Loopback, 0))
        using (var socks = new TcpListener(IPAddress.Loopback, 0))
        {
            http.Start(); socks.Start();
            httpPort = ((IPEndPoint)http.LocalEndpoint).Port;
            socksPort = ((IPEndPoint)socks.LocalEndpoint).Port;
        }
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            inbounds = new[]
            {
                new { tag = "http-test", listen = "127.0.0.1", port = httpPort, protocol = "http" },
                new { tag = "socks-test", listen = "127.0.0.1", port = socksPort, protocol = "socks" }
            },
            outbounds = new[] { new { protocol = "freedom" } }
        });
        var endpoints = ConfigInspector.Inspect(bytes).Endpoints;
        var endpoint = endpoints.Single(e => e.Protocol == "http");
        try
        {
            Console.WriteLine("Integration: validate and start synthetic configuration");
            var result = await session.ValidateAsync(bytes, root, token.Token); Assert(result.ExitCode == 0);
            await session.StartAsync(bytes, root, endpoint, token.Token);
            Assert(session.State == CoreState.Running && session.Pid is int pid && TcpListeners.IsOwned(endpoint, pid));
            foreach (var proxy in endpoints)
            {
                Console.WriteLine($"Integration: verify {proxy.Protocol} traffic");
                Assert(TcpListeners.IsOwned(proxy, session.Pid!.Value));
                await VerifyLoopbackTrafficAsync(origin, proxy, token.Token);
            }
            Console.WriteLine("Integration: stop and restart owned core");
            await session.StopAsync(); Assert(session.State == CoreState.Stopped && !session.HasChild);
            // Restart the same snapshot inputs, then simulate an unexpected owned-core exit.
            await session.StartAsync(bytes, root, endpoint, token.Token);
            using var process = Process.GetProcessById(session.Pid!.Value);
            _ = process.SafeHandle;
            process.Kill();
            await process.WaitForExitAsync(token.Token);
            await session.PollAsync();
            Assert(session.State == CoreState.Faulted && !session.HasChild && session.LastExitCode is not null);
            await session.StartAsync(bytes, root, endpoint, token.Token);
            Assert(session.State == CoreState.Running && session.HasChild);
            await session.StopAsync(); Assert(!session.HasChild);
        }
        catch
        {
            // This fixture contains only generated loopback configuration, no private data.
            foreach (var line in session.Log.ReadAfter(0).Lines) Console.Error.WriteLine(line);
            throw;
        }
        finally { if (session.HasChild) await session.StopAsync(); }
    }

    private static async Task VerifyLoopbackTrafficAsync(TcpListener origin, Inbound proxy, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(TimeSpan.FromSeconds(10));
        var marker = Guid.NewGuid().ToString("N");
        var server = ServeOnceAsync(origin, marker, limit.Token);
        try
        {
            using var handler = NetworkProbes.CreateHandler(proxy);
            using var client = new HttpClient(handler);
            // Plain HTTP is intentional for this local fixture, with no remote DNS or public traffic.
            var response = await client.GetStringAsync(new Uri($"http://127.0.0.1:{((IPEndPoint)origin.LocalEndpoint).Port}/{marker}"), limit.Token);
            await server;
            Assert(response == marker);
        }
        finally
        {
            limit.Cancel();
            // Observe the server task even if the proxy request failed first.
            try { await server; } catch (OperationCanceledException) when (limit.IsCancellationRequested) { }
        }
    }

    private static async Task ServeOnceAsync(TcpListener listener, string marker, CancellationToken token)
    {
        using var connection = await listener.AcceptTcpClientAsync(token);
        await using var stream = connection.GetStream();
        var header = new byte[8192];
        var length = 0;
        while (length < header.Length)
        {
            var read = await stream.ReadAsync(header.AsMemory(length, 1), token);
            if (read == 0) throw new EndOfStreamException("Loopback request ended before its headers.");
            length += read;
            if (length >= 4 && header[length - 4] == 13 && header[length - 3] == 10 && header[length - 2] == 13 && header[length - 1] == 10) break;
        }
        var request = Encoding.ASCII.GetString(header, 0, length);
        Assert(request.StartsWith($"GET /{marker} HTTP/", StringComparison.Ordinal));
        Assert(request.EndsWith("\r\n\r\n", StringComparison.Ordinal));
        var body = Encoding.ASCII.GetBytes(marker);
        var responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(responseHeader, token);
        await stream.WriteAsync(body, token);
    }
}
