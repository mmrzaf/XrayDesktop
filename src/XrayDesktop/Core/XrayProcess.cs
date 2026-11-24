using System.Diagnostics;
using System.Text.RegularExpressions;
using XrayDesktop.Config;
using XrayDesktop.Storage;

namespace XrayDesktop.Core;

internal sealed record CoreCheck(string Version, bool IsBaseline);
internal sealed record CommandResult(int ExitCode, TimeSpan Elapsed, string[] Output);

internal sealed class XrayProcess(string baseDirectory)
{
    internal string Executable { get; } = Path.Combine(Path.GetFullPath(baseDirectory), "xray.exe");
    internal bool Exists => File.Exists(Executable);
    internal ProcessStartInfo BuildStartInfo(string workingDirectory, params string[] args)
    {
        var info = new ProcessStartInfo(Executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        foreach (var key in info.Environment.Keys.Where(k => k.Equals("XRAY_LOCATION_CONFIG", StringComparison.OrdinalIgnoreCase) || k.Equals("XRAY_LOCATION_CONFDIR", StringComparison.OrdinalIgnoreCase) || k.Equals("xray.location.config", StringComparison.OrdinalIgnoreCase) || k.Equals("xray.location.confdir", StringComparison.OrdinalIgnoreCase) || k.Equals("xray.location.asset", StringComparison.OrdinalIgnoreCase)).ToArray()) info.Environment.Remove(key);
        info.Environment["XRAY_LOCATION_ASSET"] = Path.GetDirectoryName(Executable)!;
        return info;
    }

    internal async Task<CoreCheck> CheckAsync(CancellationToken token)
    {
        if (!Exists) throw new FileNotFoundException("Put a trusted xray.exe beside XrayDesktop.exe.");
        var result = await ExecuteAsync(BuildStartInfo(Path.GetDirectoryName(Executable)!, "version"), TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException($"Xray version command exited with code {result.ExitCode}.");
        var version = ParseVersion(result.Output);
        return new(version, version == Bootstrap.TargetCoreVersion);
    }

    internal static string ParseVersion(IEnumerable<string> output)
    {
        foreach (var line in output)
        {
            var match = Regex.Match(line, @"\bXray\s+v?(\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?)\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            if (match.Success) return match.Groups[1].Value;
        }
        throw new InvalidDataException("Could not recognize Xray version output.");
    }

    internal Task<CommandResult> ValidateAsync(RunSnapshot snapshot, CancellationToken token) => ExecuteAsync(BuildStartInfo(snapshot.WorkingDirectory, "run", "-test", "-format", "json", "-c", snapshot.Path), TimeSpan.FromSeconds(30), token);
    internal OwnedProcess Start(RunSnapshot snapshot, LogBuffer log)
    {
        snapshot.Verify();
        return new OwnedProcess(BuildStartInfo(snapshot.WorkingDirectory, "run", "-format", "json", "-c", snapshot.Path), log);
    }

    private static async Task<CommandResult> ExecuteAsync(ProcessStartInfo info, TimeSpan limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(limit);
        var log = new LogBuffer();
        var watch = Stopwatch.StartNew();
        // A short-lived version/test process may exit before job assignment. Fail closed on assignment failure.
        await using var child = new OwnedProcess(info, log);
        try
        {
            var exit = await child.WaitAsync(timeout.Token).ConfigureAwait(false);
            return new(exit, watch.Elapsed, log.ReadAfter(0).Lines);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new TimeoutException($"Xray command timed out after {limit.TotalSeconds:0} seconds."); }
    }
}
