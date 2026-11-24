using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using XrayDesktop.Config;
using XrayDesktop.Core;
using XrayDesktop.Windows;

namespace XrayDesktop.Diagnostics;

internal enum Outcome { Pass, Fail, Warning, Skipped, Cancelled }
internal enum ProbePath { Local, Direct, Http, Socks5 }
internal enum ResultCode { Ok, BaselineMismatch, Missing, Invalid, Unavailable, NotRequested, NotApplicable, Ambiguous, NotOwned, Occupied, HttpStatus, Timeout, Cancelled, Dns, Connection, Tls, Proxy, InvalidResponse, Error, External, Recovery, SameIp, DifferentIp }
internal enum DiagnosticScope { All, Local, Remote, Http, Socks5, Direct, PublicIp }
internal sealed record DiagnosticResult(string Id, ProbePath Path, Outcome Outcome, ResultCode Code, long Milliseconds, DateTimeOffset Timestamp, string Detail, string? DetectedCoreVersion = null);
internal sealed record DiagnosticContext(byte[]? Config, string? WorkingDirectory, Inbound? Selected, IReadOnlyList<Inbound> Endpoints, int? Pid, Func<Inbound, bool> IsOwned, Func<bool> IsAlive, Func<ProxyStatus> ReadProxy, RunSnapshot? ActiveSnapshot, int? LastExitCode, bool ConfigReadFailed = false, DateTimeOffset? Started = null, Action<string>? LogValidation = null);

internal sealed class DiagnosticRunner(string baseDirectory, string dataDirectory)
{
    private readonly XrayProcess core = new(baseDirectory);

    internal async Task<IReadOnlyList<DiagnosticResult>> RunAsync(DiagnosticContext context, ProbeOptions options, DiagnosticScope scope, IProgress<DiagnosticResult>? progress, CancellationToken token)
    {
        List<DiagnosticResult> results = [];
        using var suite = CancellationTokenSource.CreateLinkedTokenSource(token);
        suite.CancelAfter(TimeSpan.FromSeconds(45));
        bool Includes(DiagnosticScope group) => scope == DiagnosticScope.All || scope == group;
        var http = Choose(context, "http"); var socks = Choose(context, "socks");
        var selected = context.Selected?.Protocol == "socks" ? socks : context.Selected?.Protocol == "http" ? http : http ?? socks;
        var proxyPath = (selected?.Protocol ?? context.Selected?.Protocol) == "socks" ? ProbePath.Socks5 : ProbePath.Http;
        string? detectedCoreVersion = null;
        IPAddress? directIp = null, proxiedIp = null;
        var localLimit = TimeSpan.FromSeconds(10);
        if (Includes(DiagnosticScope.Local))
        {
            await Run("D01", ProbePath.Local, localLimit, async ct =>
            {
                if (!core.Exists) return Fail(ResultCode.Missing, "Adjacent xray.exe not found.");
                var check = await core.CheckAsync(ct).ConfigureAwait(false);
                detectedCoreVersion = check.Version;
                return check.IsBaseline ? Pass($"Xray {check.Version}") : Warn(ResultCode.BaselineMismatch, $"Xray {check.Version}; outside baseline.");
            });
            await Run("D02", ProbePath.Local, localLimit, _ => Task.FromResult(context.ConfigReadFailed ? Fail(ResultCode.Unavailable, "Configuration could not be read or the runtime snapshot changed.") : Inspect(context.Config)));
            await Run("D03", ProbePath.Local, TimeSpan.FromSeconds(15), async ct =>
            {
                if (context.Config is null || context.WorkingDirectory is null) return Skip(ResultCode.Missing, "No configuration available.");
                if (!core.Exists) return Skip(ResultCode.Missing, "Core missing.");
                CommandResult result;
                if (context.ActiveSnapshot is { } active)
                {
                    active.Verify(); result = await core.ValidateAsync(active, ct).ConfigureAwait(false); active.Verify();
                }
                else
                {
                    using var snapshot = new RunSnapshot(dataDirectory, context.Config, context.WorkingDirectory);
                    snapshot.Verify(); result = await core.ValidateAsync(snapshot, ct).ConfigureAwait(false);
                }
                foreach (var line in result.Output) context.LogValidation?.Invoke(line);
                return result.ExitCode == 0 ? Pass("Native configuration validation succeeded.") : Fail(ResultCode.Invalid, $"Native validation exited {result.ExitCode}; inspect configuration locally.");
            });
            await Run("D07", ProbePath.Local, localLimit, _ => Task.FromResult(Listener(context)));
            await Run("D13", ProbePath.Local, localLimit, _ => Task.FromResult(Proxy(context.ReadProxy())));
            await Run("D14", ProbePath.Local, localLimit, _ => Task.FromResult(context.Pid is int pid && context.IsAlive() ? Pass($"Owned core PID {pid} is alive; uptime {(context.Started is { } started ? (DateTimeOffset.Now - started).ToString(@"d\.hh\:mm\:ss") : "unknown")}.") : Skip(ResultCode.NotApplicable, $"No live owned core. Last exit: {context.LastExitCode?.ToString() ?? "unknown"}.")));
        }
        if (Includes(DiagnosticScope.Remote))
        {
            await Run("D04", ProbePath.Local, localLimit, _ =>
            {
                if (context.Config is null) return Task.FromResult(Skip(ResultCode.Missing, "No configuration."));
                var count = RemoteDiscovery.Discover(context.Config).Count;
                return Task.FromResult(count == 0 ? Skip(ResultCode.NotApplicable, "No recognized outbound server; enter a remote target manually.") : Pass($"{count} candidate remote endpoint(s); discovery is advisory."));
            });
            await Run("D05", ProbePath.Direct, options.NetworkTimeout, async ct =>
            {
                if (!options.Direct || options.Remote is not { } remote) return Skip(ResultCode.NotRequested, "Direct remote probe not selected.");
                if (IPAddress.TryParse(remote.Host, out _)) return Skip(ResultCode.NotApplicable, "IP literal; DNS unnecessary.");
                var addresses = await NetworkProbes.ResolveAsync(remote.Host, ct).ConfigureAwait(false);
                return addresses.Length == 0 ? Fail(ResultCode.Dns, "No addresses returned.") : Pass(string.Join(", ", addresses.Take(8).Select(a => a.ToString())));
            });
            await Run("D06", ProbePath.Direct, options.NetworkTimeout, async ct =>
            {
                if (!options.Direct || options.Remote is not { } remote) return Skip(ResultCode.NotRequested, "Direct remote probe not selected.");
                await NetworkProbes.TcpAsync(remote, ct).ConfigureAwait(false);
                return Pass("Direct TCP handshake succeeded; not a protocol or TLS test.");
            });
        }
        if (Includes(DiagnosticScope.Http)) await Run("D08", ProbePath.Http, options.NetworkTimeout, ct => Https(http, true, ct));
        if (Includes(DiagnosticScope.Socks5)) await Run("D09", ProbePath.Socks5, options.NetworkTimeout, ct => Https(socks, true, ct));
        if (Includes(DiagnosticScope.Direct)) await Run("D10", ProbePath.Direct, options.NetworkTimeout, ct => options.Direct ? Https(null, false, ct) : Task.FromResult(Skip(ResultCode.NotRequested, "Direct HTTPS not approved.")));
        if (scope == DiagnosticScope.All)
        {
            // Reuse the actual HTTP/SOCKS measurement; no duplicate outbound request.
            if (selected is null || suite.IsCancellationRequested)
                await Run("D11", proxyPath, localLimit, _ => Task.FromResult(Skip(ResultCode.Unavailable, "No eligible selected proxy measurement.")));
            else
            {
                var measurement = results.Single(r => r.Id == (proxyPath == ProbePath.Socks5 ? "D09" : "D08"));
                var row = measurement with { Id = "D11", Detail = "Selected path (same measurement): " + measurement.Detail };
                results.Add(row); progress?.Report(row);
            }
        }
        if (Includes(DiagnosticScope.PublicIp))
        {
            await Run("D12a", ProbePath.Direct, options.NetworkTimeout, async ct =>
            {
                if (!options.PublicIp || !options.Direct) return Skip(ResultCode.NotRequested, "Direct IP lookup not approved.");
                directIp = await NetworkProbes.PublicIpAsync(options.IpService, null, ct).ConfigureAwait(false);
                return Pass($"Reported IP: {directIp}");
            });
            await Run("D12b", proxyPath, options.NetworkTimeout, async ct =>
            {
                if (!options.PublicIp) return Skip(ResultCode.NotRequested, "IP lookup not requested.");
                if (selected is null) return Skip(ResultCode.Unavailable, "No unambiguous proxy endpoint.");
                if (!context.IsOwned(selected)) return Fail(ResultCode.NotOwned, "Listener not owned by live core.");
                proxiedIp = await NetworkProbes.PublicIpAsync(options.IpService, selected, ct).ConfigureAwait(false);
                if (!context.IsOwned(selected)) { proxiedIp = null; return Fail(ResultCode.NotOwned, "Listener ownership lost during request."); }
                return Pass($"Reported IP: {proxiedIp}");
            });
            await Run("D12c", ProbePath.Local, localLimit, _ => Task.FromResult(directIp is null || proxiedIp is null ? Skip(ResultCode.Unavailable, "Both IP results required.") : directIp.Equals(proxiedIp) ? Warn(ResultCode.SameIp, "Same IP; this alone does not prove proxy failure.") : (Outcome.Pass, ResultCode.DifferentIp, "Different IPs; this does not prove all traffic is proxied.")));
        }
        return results;

        async Task<(Outcome, ResultCode, string)> Https(Inbound? endpoint, bool proxied, CancellationToken ct)
        {
            if (proxied && endpoint is null) return Skip(ResultCode.Unavailable, "No unambiguous anonymous proxy endpoint.");
            if (endpoint is not null && !context.IsOwned(endpoint)) return Fail(ResultCode.NotOwned, "Listener not owned by live core.");
            var status = await NetworkProbes.HttpsAsync(options.Https, endpoint, ct).ConfigureAwait(false);
            if (endpoint is not null && !context.IsOwned(endpoint)) return Fail(ResultCode.NotOwned, "Listener ownership lost during request.");
            return status is >= 200 and < 300 ? Pass($"HTTPS status {status}; time to response headers, not network RTT.") : Fail(ResultCode.HttpStatus, $"HTTPS status {status}; redirects not followed.");
        }

        async Task Run(string id, ProbePath path, TimeSpan limit, Func<CancellationToken, Task<(Outcome, ResultCode, string)>> probe)
        {
            var timestamp = DateTimeOffset.UtcNow; var watch = Stopwatch.StartNew();
            (Outcome outcome, ResultCode code, string detail) value;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(suite.Token);
            deadline.CancelAfter(limit);
            try { deadline.Token.ThrowIfCancellationRequested(); value = await probe(deadline.Token).ConfigureAwait(false); deadline.Token.ThrowIfCancellationRequested(); }
            catch (OperationCanceledException) { value = token.IsCancellationRequested ? (Outcome.Cancelled, ResultCode.Cancelled, "Cancelled by user or session.") : Fail(ResultCode.Timeout, "Probe or suite deadline exceeded."); }
            catch (Exception ex) { value = Classify(ex); }
            var row = new DiagnosticResult(id, path, value.outcome, value.code, watch.ElapsedMilliseconds, timestamp, value.detail, id == "D01" && (value.outcome is Outcome.Pass or Outcome.Warning) ? detectedCoreVersion : null);
            results.Add(row); progress?.Report(row);
        }
    }

    internal static Inbound? Choose(DiagnosticContext context, string protocol)
    {
        if (context.Selected is { } selected && selected.Protocol == protocol) return selected.Manual || !selected.Authenticated ? selected : null;
        var candidates = context.Endpoints.Where(e => e.Protocol == protocol && !e.Authenticated).Take(2).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static (Outcome, ResultCode, string) Inspect(byte[]? bytes)
    {
        if (bytes is null) return Skip(ResultCode.Missing, "No configuration.");
        ReadOnlyMemory<byte> content = bytes;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) content = content[3..];
        using var document = JsonDocument.Parse(content, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (document.RootElement.ValueKind != JsonValueKind.Object) return Fail(ResultCode.Invalid, "Root must be an object.");
        var inspection = ConfigInspector.Inspect(bytes);
        return inspection.Warnings.Count == 0 ? Pass("JSON inspection passed; not native validation.") : Warn(ResultCode.Invalid, $"{inspection.Warnings.Count} advisory warning(s); native validation is authoritative.");
    }

    private static (Outcome, ResultCode, string) Listener(DiagnosticContext context)
    {
        if (context.Selected is not { } endpoint) return Skip(ResultCode.NotApplicable, "No selected listener.");
        if (context.Pid is not null) return context.IsOwned(endpoint) ? Pass("Selected TCP listener owned by live core.") : Fail(ResultCode.NotOwned, "Selected listener not owned by live core.");
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
        return listeners.Any(l => l.Port == endpoint.Port) ? Warn(ResultCode.Occupied, "Port in use on a local address; inspect before binding.") : Pass("No TCP listener found on this port; not a binding guarantee.");
    }

    private static (Outcome, ResultCode, string) Proxy(ProxyStatus status)
    {
        var detail = status.Current is { } current ? $" Flags: 0x{current.Flags:X}; server configured: {current.Server.Length > 0}; bypass configured: {current.Bypass.Length > 0}; PAC URL configured: {current.Pac.Length > 0}." : "";
        return status.State switch
        {
            ProxyState.Managed => Pass("Managed Windows LAN proxy." + detail),
            ProxyState.Unmanaged => Pass("No managed or external LAN proxy." + detail),
            ProxyState.External => Warn(ResultCode.External, "External Windows proxy or automatic configuration." + detail),
            ProxyState.Unavailable => Fail(ResultCode.Unavailable, "Windows proxy state unavailable."),
            _ => Warn(ResultCode.Recovery, "Proxy recovery or conflict requires review." + detail)
        };
    }

    internal static (Outcome, ResultCode, string) Classify(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } status } => Fail(ResultCode.HttpStatus, $"HTTP status {(int)status}."),
        HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError } => Fail(ResultCode.Dns, "HTTPS name resolution failed."),
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => Fail(ResultCode.Tls, "TLS negotiation or certificate validation failed."),
        HttpRequestException { HttpRequestError: HttpRequestError.ProxyTunnelError } => Fail(ResultCode.Proxy, "Proxy tunnel failed."),
        HttpRequestException { HttpRequestError: HttpRequestError.ConnectionError } => Fail(ResultCode.Connection, "HTTPS connection failed."),
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain } => Fail(ResultCode.Dns, "OS DNS resolution failed."),
        SocketException socket => Fail(ResultCode.Connection, $"TCP/socket connection failed (code {socket.ErrorCode})."),
        TimeoutException => Fail(ResultCode.Timeout, "Operation timed out."),
        JsonException => Fail(ResultCode.Invalid, "Invalid JSON syntax."),
        InvalidDataException => Fail(ResultCode.InvalidResponse, "Invalid or unexpected data."),
        _ => Fail(ResultCode.Error, "Operation failed; inspect local settings and logs.")
    };
    private static (Outcome, ResultCode, string) Pass(string detail) => (Outcome.Pass, ResultCode.Ok, detail);
    private static (Outcome, ResultCode, string) Fail(ResultCode code, string detail) => (Outcome.Fail, code, detail);
    private static (Outcome, ResultCode, string) Warn(ResultCode code, string detail) => (Outcome.Warning, code, detail);
    private static (Outcome, ResultCode, string) Skip(ResultCode code, string detail) => (Outcome.Skipped, code, detail);
}
