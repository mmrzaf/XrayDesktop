using System.Diagnostics;
using System.Text;
using XrayDesktop.Storage;

namespace XrayDesktop.Core;

internal sealed class OwnedProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly JobObject job;
    private readonly Task stdout, stderr;
    private bool disposed;
    internal int Id => process.Id;
    internal bool HasExited => process.HasExited;
    internal int ExitCode => process.ExitCode;

    internal OwnedProcess(ProcessStartInfo info, LogBuffer log)
    {
        job = new JobObject();
        process = new Process { StartInfo = info };
        var started = false;
        try
        {
            started = process.Start();
            if (!started) throw new InvalidOperationException("Could not start child process.");
            try { job.Assign(process); }
            catch (Exception ex) when ((ex is System.ComponentModel.Win32Exception or InvalidOperationException) && process.HasExited) { /* Already exited; no live child to manage. */ }
            stdout = DrainAsync(process.StandardOutput, "stdout", log);
            stderr = DrainAsync(process.StandardError, "stderr", log);
        }
        catch
        {
            try { if (started && !process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
            finally { job.Dispose(); process.Dispose(); }
            throw;
        }
    }

    private static async Task DrainAsync(StreamReader reader, string source, LogBuffer log)
    {
        var buffer = new char[2048];
        var line = new StringBuilder(4096);
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) != 0)
        {
            for (var i = 0; i < count; i++)
            {
                var c = buffer[i];
                if (c == '\n') { log.Add(source, line.ToString().TrimEnd('\r') + (truncated ? " [truncated]" : "")); line.Clear(); truncated = false; }
                else if (line.Length < 4000) line.Append(c);
                else truncated = true;
            }
        }
        if (line.Length != 0) log.Add(source, line.ToString() + (truncated ? " [truncated]" : ""));
    }

    internal async Task<int> WaitAsync(CancellationToken cancellationToken)
    {
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        return process.ExitCode;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        // Closing this private job terminates only its assigned processes.
        job.Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await WaitAsync(timeout.Token).ConfigureAwait(false); }
        finally { disposed = true; process.Dispose(); }
    }
}
