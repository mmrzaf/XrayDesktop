using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace XrayDesktop.Windows;

// One GUI per user. A second launch asks the running one to show itself
// instead of failing, so double-clicking the exe while it sits in the tray works.
internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    private readonly EventWaitHandle showSignal;
    private RegisteredWaitHandle? registration;
    internal bool Acquired { get; }
    internal SingleInstance()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Cannot determine current user.");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)));
        mutex = new Mutex(false, $@"Global\XrayDesktop-{key}");
        showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\XrayDesktop-show-{key}");
        try { Acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { Acquired = true; }
        catch { mutex.Dispose(); showSignal.Dispose(); throw; }
    }
    internal void SignalRunningInstance() => showSignal.Set();
    internal void OnShowRequested(Action callback) =>
        registration = ThreadPool.RegisterWaitForSingleObject(showSignal, (_, _) => callback(), null, Timeout.Infinite, executeOnlyOnce: false);
    public void Dispose()
    {
        registration?.Unregister(null);
        if (Acquired) mutex.ReleaseMutex();
        mutex.Dispose(); showSignal.Dispose();
    }
}
