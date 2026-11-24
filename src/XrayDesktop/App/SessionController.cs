using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using XrayDesktop.Config;
using XrayDesktop.Core;
using XrayDesktop.Storage;
using XrayDesktop.Windows;

namespace XrayDesktop.App;

internal enum CoreState { Missing, Checking, Stopped, Validating, Starting, Running, Stopping, Faulted }

// All commands are called serially by the UI command gate; poll never overlaps a command.
internal sealed class SessionController(string baseDirectory, string dataDirectory)
{
    private readonly XrayProcess core = new(baseDirectory);
    private OwnedProcess? child;
    private RunSnapshot? snapshot;
    internal LogBuffer Log { get; } = new();
    internal CoreState State { get; private set; } = CoreState.Stopped;
    internal CoreCheck? Version { get; private set; }
    internal int? Pid => child?.Id;
    internal bool HasChild => child is not null;
    internal bool ChildExited => child?.HasExited == true;
    internal string Readiness { get; private set; } = "Not running";
    internal DateTimeOffset? Started { get; private set; }
    internal int? LastExitCode { get; private set; }
    internal Inbound? ActiveEndpoint { get; private set; }
    internal bool CanProbeHttp => HasChild && !ChildExited && ActiveEndpoint is { Protocol: "http" } endpoint && (!endpoint.Authenticated || endpoint.Manual);

    internal void VerifyHttpOwner()
    {
        if (!CanProbeHttp || child is null || ActiveEndpoint is not { } endpoint || !TcpListeners.IsOwned(endpoint, child.Id) || child.HasExited)
            throw new InvalidOperationException("A live, owned HTTP loopback listener is required; check the selected endpoint.");
    }

    internal async Task CheckAsync(CancellationToken token)
    {
        if (HasChild) throw new InvalidOperationException("Stop Xray before checking the executable.");
        State = CoreState.Checking; Version = null;
        try
        {
            Version = await core.CheckAsync(token).ConfigureAwait(false);
            Log.Add("app", $"Detected Xray {Version.Version}{(Version.IsBaseline ? "" : " (outside tested baseline)")}");
            var processes = Process.GetProcessesByName("xray");
            try { if (processes.Length > 0) Log.Add("app", $"Found {processes.Length} existing Xray process(es); they will not be stopped."); }
            finally { foreach (var process in processes) process.Dispose(); }
            State = CoreState.Stopped;
        }
        catch { State = core.Exists ? CoreState.Faulted : CoreState.Missing; throw; }
    }

    internal async Task<CommandResult> ValidateAsync(byte[] bytes, string workingDirectory, CancellationToken token)
    {
        if (HasChild) throw new InvalidOperationException("Stop Xray before validating another configuration.");
        State = CoreState.Validating;
        try
        {
            using var candidate = new RunSnapshot(dataDirectory, bytes, workingDirectory);
            var result = await ValidateSnapshotAsync(candidate, token).ConfigureAwait(false);
            State = result.ExitCode == 0 ? CoreState.Stopped : CoreState.Faulted;
            return result;
        }
        catch { State = CoreState.Faulted; throw; }
    }

    private async Task<CommandResult> ValidateSnapshotAsync(RunSnapshot candidate, CancellationToken token)
    {
        var result = await core.ValidateAsync(candidate, token).ConfigureAwait(false);
        foreach (var line in result.Output) Log.Add("validation", line);
        Log.Add("app", $"Validation exit {result.ExitCode}, {result.Elapsed.TotalMilliseconds:0} ms.");
        return result;
    }

    internal async Task StartAsync(byte[] bytes, string workingDirectory, Inbound? selected, CancellationToken token)
    {
        if (HasChild) throw new InvalidOperationException("An owned Xray instance is already running.");
        LastExitCode = null; State = CoreState.Validating; Readiness = "Not running";
        try
        {
            snapshot = new RunSnapshot(dataDirectory, bytes, workingDirectory);
            if (snapshot.Inspection.HasTun) throw new InvalidOperationException("TUN configurations are outside Phase 1 scope.");
            var result = await ValidateSnapshotAsync(snapshot, token).ConfigureAwait(false);
            if (result.ExitCode != 0) throw new InvalidOperationException("Xray rejected the configuration; inspect local logs.");
            var endpoints = snapshot.Inspection.Endpoints;
            if (selected is { Manual: false } && !endpoints.Contains(selected)) throw new InvalidOperationException("Selected endpoint no longer matches the saved configuration.");
            var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
            foreach (var endpoint in endpoints)
            {
                if (listeners.Any(l => l.Port == endpoint.Port && (l.Address.Equals(endpoint.Address) || l.Address.Equals(IPAddress.Any) || l.Address.Equals(IPAddress.IPv6Any))))
                    Log.Add("app", $"Warning: port {endpoint.Port} may already be in use; Xray will decide whether binding succeeds.");
            }
            token.ThrowIfCancellationRequested();
            State = CoreState.Starting;
            child = core.Start(snapshot, Log);
            Started = DateTimeOffset.Now;
            var deadline = Stopwatch.StartNew();
            var verified = false;
            do
            {
                await Task.Delay(200, token).ConfigureAwait(false);
                if (child.HasExited) { LastExitCode = child.ExitCode; throw new InvalidOperationException($"Xray exited during startup (code {LastExitCode})."); }
                if (selected is null) break;
                try { verified = TcpListeners.IsOwned(selected, child.Id); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidDataException or InvalidOperationException)
                { Log.Add("app", $"Listener ownership check unavailable: {ex.Message}"); break; }
            } while (!verified && deadline.Elapsed < TimeSpan.FromSeconds(5));
            Readiness = selected is null ? "Not checked (local-only)" : verified ? "Selected TCP listener owned by Xray" : "Selected listener ownership not verified";
            ActiveEndpoint = selected;
            State = CoreState.Running;
            Log.Add("app", $"Xray process {child.Id} running. {Readiness}. Connectivity not tested.");
        }
        catch
        {
            try { await ReleaseAsync().ConfigureAwait(false); }
            finally { State = CoreState.Faulted; Readiness = "Not running / check logs"; }
            throw;
        }
    }

    internal async Task StopAsync()
    {
        State = CoreState.Stopping;
        try { await ReleaseAsync().ConfigureAwait(false); State = CoreState.Stopped; Readiness = "Not running"; Log.Add("app", "Owned core stopped."); }
        catch { State = CoreState.Faulted; throw; }
    }

    internal async Task PollAsync()
    {
        if (child is null || !child.HasExited) return;
        LastExitCode = child.ExitCode;
        Log.Add("app", $"Xray exited unexpectedly (code {LastExitCode}).");
        try { await ReleaseAsync().ConfigureAwait(false); }
        finally { State = CoreState.Faulted; Readiness = "Not running"; }
    }

    private async Task ReleaseAsync()
    {
        ActiveEndpoint = null;
        if (child is not null)
        {
            var owned = child;
            // If cleanup fails, retain the snapshot rather than deleting a potentially active config.
            try { await owned.DisposeAsync().ConfigureAwait(false); }
            catch { child = null; Started = null; snapshot = null; throw; }
            child = null;
        }
        Started = null;
        if (snapshot is not null) { var candidate = snapshot; snapshot = null; candidate.Dispose(); }
    }
}
