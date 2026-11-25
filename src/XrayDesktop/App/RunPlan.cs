using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using XrayDesktop.Config;

namespace XrayDesktop.App;

// The configuration Xray will actually run plus the anonymous HTTP loopback
// listener the Windows proxy points at. Only this run copy is adjusted; the
// user's profile file is never touched.
internal sealed record RunPlan(byte[] Config, Inbound Http, IReadOnlyList<string> Warnings, IReadOnlyList<string> Adjustments)
{
    internal static RunPlan Prepare(byte[] config, Func<int, bool> portInUse, Func<ISet<int>, int> freePort)
    {
        var adjustments = new List<string>();
        var inspection = ConfigInspector.Inspect(config);
        if (inspection.HasTun) throw new InvalidOperationException("TUN configurations are not supported.");
        // Move any listener whose port is taken so Xray does not fail to bind.
        foreach (var endpoint in inspection.Endpoints.Where(e => portInUse(e.Port)).ToList())
        {
            var avoid = inspection.Endpoints.Select(e => e.Port).ToHashSet();
            var port = freePort(avoid);
            config = ConfigUpgrade.UsePort(config, endpoint, port);
            adjustments.Add($"{endpoint.Protocol.ToUpperInvariant()} listener moved from port {endpoint.Port} to {port} because it was in use.");
            inspection = ConfigInspector.Inspect(config);
        }
        var http = inspection.Endpoints.FirstOrDefault(e => e.Protocol == "http" && !e.Authenticated);
        if (http is null)
        {
            var port = freePort(inspection.Endpoints.Select(e => e.Port).ToHashSet());
            config = ConfigUpgrade.AddLoopbackHttp(config, port);
            inspection = ConfigInspector.Inspect(config);
            http = inspection.Endpoints.FirstOrDefault(e => e.Protocol == "http" && !e.Authenticated && e.Port == port)
                ?? throw new InvalidOperationException("Could not add a local HTTP listener to this configuration.");
            adjustments.Add($"A local HTTP listener was added on port {port} for this session.");
        }
        return new(config, http, inspection.Warnings, adjustments);
    }

    internal static RunPlan Prepare(byte[] config) => Prepare(config, PortFinder.InUse, PortFinder.FindFree);
}

internal static class PortFinder
{
    internal static bool InUse(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(listener => listener.Port == port);

    internal static int FindFree(ISet<int> avoid)
    {
        var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(l => l.Port).ToHashSet();
        for (var port = ShareLink.DefaultHttpPort; port <= 11999; port++)
            if (!avoid.Contains(port) && !listeners.Contains(port) && CanListen(port)) return port;
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var fallback = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        if (!avoid.Contains(fallback)) return fallback;
        throw new InvalidOperationException("Could not find a free local port. Close other proxy apps and try again.");
    }

    private static bool CanListen(int port)
    {
        try
        {
            using var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException) { return false; }
    }
}
