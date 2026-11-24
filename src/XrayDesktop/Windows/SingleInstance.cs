using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace XrayDesktop.Windows;

internal sealed class SingleInstance : IDisposable
{
    private readonly Mutex mutex;
    internal bool Acquired { get; }
    internal SingleInstance()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new InvalidOperationException("Cannot determine current user.");
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid)));
        mutex = new Mutex(false, $@"Global\XrayDesktop-{key}");
        try { Acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { Acquired = true; }
        catch { mutex.Dispose(); throw; }
    }
    public void Dispose() { if (Acquired) mutex.ReleaseMutex(); mutex.Dispose(); }
}
