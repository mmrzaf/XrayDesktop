using System.Net.Http;
using XrayDesktop.Config;
using XrayDesktop.Windows;

namespace XrayDesktop.App;

internal enum LinkState { Disconnected, Connecting, Connected, Unstable, Disconnecting }

// What the user sees as one switch: start the core, prove the tunnel works,
// then point Windows at it. Disconnect reverses the order so Windows never
// keeps a proxy that nothing serves. Called serially by the UI command gate.
internal sealed class ConnectionController(SessionController session, ProxyController proxy)
{
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15), HealthTimeout = TimeSpan.FromSeconds(8);
    private const int UnstableAfterFailures = 2;
    private int failures;
    private bool proxyOwned;

    internal LinkState State { get; private set; } = LinkState.Disconnected;
    internal string? ProfileName { get; private set; }
    internal string? Problem { get; private set; }
    internal IReadOnlyList<string> Adjustments { get; private set; } = [];
    internal DateTimeOffset? Since { get; private set; }
    internal bool Active => State is LinkState.Connected or LinkState.Unstable;

    // Returns false when the user declined a confirmation or the attempt failed
    // (Problem explains why). Everything started is undone before returning.
    internal async Task<bool> ConnectAsync(string name, byte[] config, string workingDirectory, Uri probe,
        Func<RunPlan, bool> confirmWarnings, Func<ProxySettings, bool> confirmExternalProxy, CancellationToken token)
    {
        State = LinkState.Connecting; Problem = null; ProfileName = name; Adjustments = [];
        try
        {
            if (await Task.Run(() => proxy.HasPending, token).ConfigureAwait(true))
            {
                try { await Task.Run(proxy.Restore, token).ConfigureAwait(true); }
                catch (Exception) { throw new InvalidOperationException("Windows proxy settings need attention before connecting. Open the proxy review from the notice on the main screen."); }
            }
            if (session.Version is null) await Task.Run(() => session.CheckAsync(token), token).ConfigureAwait(true);
            var plan = await Task.Run(() => RunPlan.Prepare(config), token).ConfigureAwait(true);
            Adjustments = plan.Adjustments;
            foreach (var line in plan.Adjustments) session.Log.Add("app", line);
            if (plan.Warnings.Count > 0 && !confirmWarnings(plan)) return Declined();
            await Task.Run(() => session.StartAsync(plan.Config, workingDirectory, plan.Http, token), token).ConfigureAwait(true);
            await Task.Run(session.VerifyHttpOwner, token).ConfigureAwait(true);
            await Task.Run(() => HttpProxyProbe.VerifyAsync(plan.Http, probe, ProbeTimeout, token), token).ConfigureAwait(true);
            var original = await Task.Run(proxy.PrepareAcquire, token).ConfigureAwait(true);
            if (original.External && !confirmExternalProxy(original)) return await DeclinedAsync().ConfigureAwait(true);
            token.ThrowIfCancellationRequested();
            // The transaction itself is never interrupted by cancellation.
            await Task.Run(() => proxy.Acquire(original, plan.Http, session.VerifyHttpOwner), CancellationToken.None).ConfigureAwait(true);
            proxyOwned = true;
            if (!ProxyGuard.Launch(proxy.Status.Journal?.Id)) session.Log.Add("proxy", "Crash guard could not start; the proxy will be restored the next time the app opens.");
            failures = 0; Since = DateTimeOffset.Now; State = LinkState.Connected;
            session.Log.Add("app", $"Connected through '{name}'.");
            return true;
        }
        catch (OperationCanceledException)
        {
            await UndoAsync().ConfigureAwait(true);
            State = LinkState.Disconnected;
            throw;
        }
        catch (Exception ex)
        {
            session.Log.Add("error", ex.ToString());
            Problem = FriendlyError.Describe(ex, session);
            await UndoAsync().ConfigureAwait(true);
            State = LinkState.Disconnected;
            return false;
        }

        bool Declined() { State = LinkState.Disconnected; return false; }
        async Task<bool> DeclinedAsync() { await UndoAsync().ConfigureAwait(true); State = LinkState.Disconnected; return false; }
    }

    // False means Windows proxy restoration could not be verified; the core is
    // left running so the proxy still works. The caller decides what to offer.
    internal async Task<bool> DisconnectAsync(bool ignoreProxyFailure)
    {
        var previous = State;
        State = LinkState.Disconnecting;
        try
        {
            try { await Task.Run(proxy.Restore).ConfigureAwait(true); proxyOwned = false; }
            catch (Exception ex)
            {
                session.Log.Add("proxy", $"Restoration not verified: {ex.Message}");
                if (!ignoreProxyFailure)
                {
                    State = previous;
                    Problem = "Windows proxy settings could not be restored automatically, so the connection was kept running. Retry, or restore them from the review dialog.";
                    return false;
                }
            }
            if (session.HasChild) await Task.Run(session.StopAsync).ConfigureAwait(true);
            State = LinkState.Disconnected; Since = null; Problem = null;
            return true;
        }
        catch (Exception ex)
        {
            session.Log.Add("error", ex.ToString());
            State = LinkState.Disconnected; Since = null; Problem = FriendlyError.Describe(ex, session);
            return true;
        }
    }

    // Xray ended without being asked to. Windows must stop pointing at it first.
    internal async Task HandleCoreExitAsync()
    {
        var wasActive = Active;
        try { await Task.Run(proxy.Restore).ConfigureAwait(true); proxyOwned = false; }
        catch (Exception) { session.Log.Add("proxy", "Core exited; proxy recovery incomplete."); }
        finally { await Task.Run(session.PollAsync).ConfigureAwait(true); }
        State = LinkState.Disconnected; Since = null;
        if (wasActive) Problem = FriendlyError.CoreExited(session);
    }

    // One cheap request through the tunnel. Two misses in a row means unstable.
    internal async Task<bool> CheckHealthAsync(Uri probe, CancellationToken token)
    {
        if (!Active || session.ActiveEndpoint is not { } endpoint) return true;
        try
        {
            await Task.Run(() => HttpProxyProbe.VerifyAsync(endpoint, probe, HealthTimeout, token), token).ConfigureAwait(true);
            if (!Active) return true; // Disconnected while the probe was in flight.
            if (State == LinkState.Unstable) session.Log.Add("app", "Connection recovered.");
            failures = 0; State = LinkState.Connected;
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
        {
            failures++;
            if (failures >= UnstableAfterFailures && State == LinkState.Connected)
            { State = LinkState.Unstable; session.Log.Add("app", "The server stopped answering."); }
            return false;
        }
    }

    // Synchronous best effort for Windows shutdown/logoff, where there is no
    // time for the async path.
    internal void ShutdownNow()
    {
        try { proxy.Restore(); } catch (Exception) { session.Log.Add("proxy", "Shutdown restore not verified; recovery continues at next launch."); }
        try { Task.Run(session.StopAsync).Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { /* The job object ends Xray with this process. */ }
    }

    private async Task UndoAsync()
    {
        try { if (proxyOwned || await Task.Run(() => proxy.HasPending).ConfigureAwait(true)) await Task.Run(proxy.Restore).ConfigureAwait(true); proxyOwned = false; }
        catch (Exception) { session.Log.Add("proxy", "Cleanup after a failed connect could not verify the Windows proxy; recovery continues at next launch."); }
        try { if (session.HasChild) await Task.Run(session.StopAsync).ConfigureAwait(true); }
        catch (Exception ex) { session.Log.Add("error", ex.ToString()); }
    }
}

internal static class FriendlyError
{
    internal static string Describe(Exception ex, SessionController session)
    {
        if (ex is HttpRequestException or TimeoutException || ex.InnerException is HttpRequestException or TimeoutException)
            return "The local engine started, but the server did not answer. Check that your internet works and that the link or server is still valid.";
        if (ex.Message.Contains("HTTPS proxy test returned", StringComparison.Ordinal))
            return "The server answered with an error instead of passing traffic. The link may be expired or blocked.";
        if (ex.Message.Contains("owned HTTP loopback listener", StringComparison.Ordinal))
            return "The engine started but did not open its local port in time. Try connecting again.";
        if (ex.Message.Contains("exited during startup", StringComparison.Ordinal))
            return "The engine stopped right after starting." + Detail(session);
        if (ex.Message.Contains("rejected the configuration", StringComparison.Ordinal))
            return "This profile is not a valid Xray configuration." + (session.LastValidationOutput.LastOrDefault(l => l.Trim().Length > 0) is { } line ? $" Xray says: {line.Trim()}" : "");
        if (ex is FileNotFoundException && ex.Message.Contains("xray.exe", StringComparison.OrdinalIgnoreCase))
            return "xray.exe is missing next to this app. Extract the whole ZIP again and keep the files together.";
        if (ex is UnauthorizedAccessException)
            return "The app cannot write to its folder. Move it to a folder you own, such as Documents, and try again.";
        if (ex.Message.StartsWith("Proxy policy", StringComparison.Ordinal) || ex.Message.Contains("restricted by policy", StringComparison.Ordinal))
            return "Windows proxy settings are locked by your organization, so the app cannot route Windows through the connection.";
        return ex.Message;
    }

    internal static string CoreExited(SessionController session) =>
        $"The connection stopped unexpectedly (code {session.LastExitCode?.ToString() ?? "unknown"}). Windows was restored to its previous proxy settings." + Detail(session);

    private static string Detail(SessionController session)
    {
        var line = session.Log.ReadAfter(0).Lines
            .Reverse()
            .FirstOrDefault(l => l.Contains("failed", StringComparison.OrdinalIgnoreCase) || l.Contains("error", StringComparison.OrdinalIgnoreCase) || l.Contains("invalid", StringComparison.OrdinalIgnoreCase));
        return line is null ? "" : $" Xray says: {line[(line.IndexOf(']') + 1)..].Trim()}";
    }
}
