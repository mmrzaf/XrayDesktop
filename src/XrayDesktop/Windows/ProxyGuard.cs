using System.ComponentModel;
using System.Diagnostics;

namespace XrayDesktop.Windows;

// A second copy of this executable that outlives a crashed or killed GUI and
// puts the recorded Windows proxy back. It acts only on the journal it was told
// about and never overrides settings changed by someone else.
internal static class ProxyGuard
{
    internal static bool Launch(Guid? journalId)
    {
        if (journalId is not { } id || Environment.ProcessPath is not { } executable) return false;
        try
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--guard");
            info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            info.ArgumentList.Add(id.ToString("N"));
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return false; }
    }

    internal static int Run(int parentId, Guid journalId)
    {
        try { using var parent = Process.GetProcessById(parentId); parent.WaitForExit(); }
        catch (ArgumentException) { /* Parent already gone. */ }
        var controller = new ProxyController(new SystemProxy(), ProxyRecovery.ForCurrentUser());
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var status = controller.Inspect();
                if (status.Journal is null || status.Journal.Id != journalId || status.State == ProxyState.Conflict) return 0;
                controller.Restore();
                return 0;
            }
            catch (Exception) { Thread.Sleep(1000); }
        }
        return 1;
    }
}
