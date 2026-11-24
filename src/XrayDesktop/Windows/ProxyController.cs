using XrayDesktop.Config;

namespace XrayDesktop.Windows;

internal enum ProxyState { Unavailable, Unmanaged, External, Managed, RecoveryRequired, Conflict }
internal sealed record ProxyStatus(ProxyState State, string Detail, ProxySettings? Current = null, ProxyJournal? Journal = null);

// Called serially by the session's UI command gate. No network I/O and no
// unconditional restore: any third-party change requires a fresh user decision.
internal sealed class ProxyController(ISystemProxy system, IProxyJournalStore store)
{
    private Guid? ownedId;
    internal ProxyStatus Status { get; private set; } = new(ProxyState.Unavailable, "Not checked");
    internal bool HasPending => store.Load() is not null; // throws on corrupt/unreadable journal

    internal ProxyStatus Inspect()
    {
        ProxyJournal? journal;
        try { journal = store.Load(); }
        catch (Exception) { return Status = new(ProxyState.RecoveryRequired, "Recovery journal is unreadable; no proxy changes permitted."); }
        try
        {
            var current = system.Read();
            if (journal is null)
            {
                ownedId = null;
                return Status = new(current.External ? ProxyState.External : ProxyState.Unmanaged, current.External ? "External proxy or automatic configuration" : "No active LAN proxy", current);
            }
            if (current == journal.Original) return Status = new(ProxyState.RecoveryRequired, "Original settings present; verify notification and finish recovery.", current, journal);
            if (current == journal.Applied)
                return Status = new(ownedId == journal.Id ? ProxyState.Managed : ProxyState.RecoveryRequired, ownedId == journal.Id ? "Managed LAN proxy" : "Interrupted proxy transaction", current, journal);
            return Status = new(ProxyState.Conflict, "Windows settings changed outside this transaction; review required.", current, journal);
        }
        catch (Exception)
        {
            return Status = new(journal is null ? ProxyState.Unavailable : ProxyState.RecoveryRequired, "Cannot read Windows LAN proxy settings; no changes permitted.", Journal: journal);
        }
    }

    internal void Acquire(ProxySettings reviewed, Inbound endpoint, Action verifyOwner)
    {
        if (endpoint.Protocol != "http" || (!endpoint.Manual && endpoint.Authenticated) || !System.Net.IPAddress.IsLoopback(endpoint.Address) || endpoint.Port is < 1 or > 65535)
            throw new InvalidOperationException("A verified unauthenticated HTTP loopback endpoint is required.");
        try
        {
            if (store.Load() is not null) throw new InvalidOperationException("Resolve the pending proxy recovery first.");
            system.EnsureWritable();
            if (system.Read() != reviewed) throw new InvalidOperationException("Proxy settings changed since review; inspect and try again.");
            reviewed.Validate();
            var host = endpoint.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{endpoint.Address}]" : endpoint.Address.ToString();
            var applied = new ProxySettings(ProxySettings.Direct | ProxySettings.Manual, $"{host}:{endpoint.Port}", "<local>;localhost;127.0.0.1;[::1]", reviewed.Pac);
            if (applied == reviewed) throw new InvalidOperationException("An identical external proxy already exists; it cannot be claimed as ours.");
            verifyOwner();
            var journal = new ProxyJournal(1, Guid.NewGuid(), store.UserSid, DateTimeOffset.UtcNow, reviewed, applied, "prepared");
            store.Create(journal); // durable intent before any OS mutation
            try
            {
                if (system.Read() != reviewed) throw new InvalidOperationException("Windows proxy changed during preparation.");
                verifyOwner();
                system.Write(applied);
                system.Refresh();
                if (system.Read() != applied) throw new InvalidOperationException("Windows did not return the intended proxy settings.");
                verifyOwner();
                ownedId = journal.Id;
            }
            catch
            {
                // Restore only exact original/applied values. Mixed or external state
                // remains journaled; a failed write is not proof of ownership.
                try { Restore(); } catch (Exception) { /* Inspect exposes retained recovery. */ }
                throw;
            }
        }
        finally { Inspect(); }
    }

    // Used at startup, disable, stop, exit and unexpected child exit. Never
    // changes an unrelated localhost proxy when there is no journal.
    internal void Restore() => Resolve(null, keepCurrent: false);

    // Reviewed conflict resolution. Reject even a second external change that
    // happened while the confirmation dialog was open.
    internal void ResolveReviewed(ProxyStatus reviewed, bool keepCurrent)
    {
        if (reviewed.Current is null || reviewed.Journal is null) throw new InvalidOperationException("Read the pending transaction before resolving it.");
        Resolve(reviewed, keepCurrent);
    }

    private void Resolve(ProxyStatus? reviewed, bool keepCurrent)
    {
        try
        {
            var journal = store.Load();
            if (journal is null)
            {
                if (reviewed is not null) throw new InvalidOperationException("Recovery transaction no longer exists.");
                ownedId = null; return;
            }
            var current = system.Read();
            if (reviewed is not null && (reviewed.Journal != journal || reviewed.Current != current))
                throw new InvalidOperationException("Recovery settings changed since review; review again.");
            if (keepCurrent)
            {
                // Explicit relinquishment, not restoration. Do not write Windows settings.
                if (system.Read() != current) throw new InvalidOperationException("Settings changed before relinquishment.");
                store.Delete(); ownedId = null; return;
            }
            if (current != journal.Original && current != journal.Applied && reviewed is null)
                throw new InvalidOperationException("Proxy conflict: review Windows settings before restoring or keeping them.");
            if (current != journal.Original)
            {
                system.EnsureWritable();
                if (system.Read() != current) throw new InvalidOperationException("Settings changed before restoration.");
                system.Write(journal.Original);
            }
            // Retry notification even if a previous restoration wrote the original
            // values but failed to notify/verify. Do not clear the journal early.
            system.Refresh();
            if (system.Read() != journal.Original) throw new InvalidOperationException("Original Windows proxy settings could not be verified.");
            store.Delete(); ownedId = null;
        }
        finally { Inspect(); }
    }
}
