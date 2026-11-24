using System.Net;
using System.Net.Sockets;
using System.Text;
using XrayDesktop.Config;
using XrayDesktop.Windows;

namespace XrayDesktop.Checks;

internal static class PhaseTwoChecks
{
    private static readonly ProxySettings Original = new(ProxySettings.Direct | ProxySettings.AutoUrl | ProxySettings.AutoDetect, "old:8080", "old-bypass", "https://example.invalid/private.pac");
    private static readonly ProxySettings Applied = new(3, "127.0.0.1:12345", ProxySettings.ManagedBypass, Original.Pac);
    private static readonly ProxySettings External = new(3, "outside:9999", "outside-bypass", "");
    private static readonly Inbound Endpoint = new("test", "http", IPAddress.Loopback, 12345, false);
    internal static (string Name, Action Run)[] All =>
    [
        ("Proxy acquisition journals before write and restores all LAN fields", AcquireRestore),
        ("No journal means no proxy ownership or writes", NoJournal),
        ("Identical external proxy is not claimed", IdenticalExternal),
        ("Startup recovers exact interrupted settings", StartupRecovery),
        ("Startup clears already-restored transaction only after refresh", AlreadyRestored),
        ("External edits block automatic restore", Conflict),
        ("Reviewed conflict rejects later settings changes", StaleReview),
        ("Explicit keep external settings does not write Windows", KeepExternal),
        ("Explicit reviewed restore can resolve conflict", ExplicitRestore),
        ("Policy denial blocks all acquisition writes", Policy),
        ("Journal creation failure blocks all acquisition writes", JournalFailure),
        ("Stale acquisition review cannot overwrite external changes", StaleAcquire),
        ("Policy change after acquisition retains recovery", RestorePolicy),
        ("Pre-write ownership failure never restores a third-party proxy", PreWriteOwner),
        ("Refresh failure rolls back exact applied state", ApplyRefreshFailure),
        ("Partial native write retains journal and does not overwrite mixed state", PartialWrite),
        ("Ownership loss after write rolls back", PostWriteOwner),
        ("Restoration failure retains journal for retry", FailedRestore),
        ("Unreadable recovery blocks acquisition and restore", CorruptJournal),
        ("Authenticated and SOCKS endpoints cannot acquire system proxy", IneligibleEndpoints),
        ("IPv6 managed proxy syntax is canonical", Ipv6),
        ("DPAPI journal round trip refuses overwrite and corruption", EncryptedJournal),
        ("HTTPS probe sends anonymous CONNECT only to the explicit proxy", ProxyProbe)
    ];

    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed."); }
    private static void Throws(Action run)
    {
        try { run(); } catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception || ex is InvalidDataException) { return; }
        throw new InvalidOperationException("Expected a guarded failure.");
    }
    private static ProxyJournal Journal(ProxySettings? original = null, ProxySettings? applied = null) => new(1, Guid.NewGuid(), "test-user", DateTimeOffset.UtcNow, original ?? Original, applied ?? Applied, "prepared");
    private sealed class MemoryStore : IProxyJournalStore
    {
        internal ProxyJournal? Pending;
        internal bool FailLoad, FailCreate;
        public string UserSid => "test-user";
        public ProxyJournal? Load() => FailLoad ? throw new InvalidDataException("unreadable journal") : Pending;
        public void Create(ProxyJournal journal)
        {
            if (FailCreate || Pending is not null) throw new IOException("journal creation failed");
            Pending = journal;
        }
        public void Delete() => Pending = null;
    }
    private sealed class FakeSystem(MemoryStore store) : ISystemProxy
    {
        internal ProxySettings Current = Original;
        internal int Writes, Refreshes;
        internal bool Denied, Partial;
        internal int RefreshFailures;
        public ProxySettings Read() => Current;
        public void EnsureWritable() { if (Denied) throw new UnauthorizedAccessException("policy"); }
        public void Write(ProxySettings settings)
        {
            if (store.Pending is null) throw new Exception("UNJOURNALED WRITE");
            EnsureWritable(); Writes++;
            if (Partial) { Current = Current with { Server = settings.Server }; throw new IOException("partial write"); }
            Current = settings;
        }
        public void Refresh()
        {
            Refreshes++;
            if (RefreshFailures > 0) { RefreshFailures--; throw new IOException("refresh failed"); }
        }
    }
    private sealed class Fixture
    {
        internal readonly MemoryStore Store = new();
        internal readonly FakeSystem System;
        internal readonly ProxyController Controller;
        internal Fixture() { System = new(Store); Controller = new(System, Store); }
        internal void Acquire() => Controller.Acquire(Original, Endpoint, () => { });
        internal void Pending(ProxySettings current) { Store.Pending = Journal(); System.Current = current; }
    }
    private static void AcquireRestore()
    {
        var f = new Fixture(); f.Acquire();
        Assert(f.System.Current == Applied && f.Store.Pending?.Original == Original && f.Controller.Status.State == ProxyState.Managed && f.System.Writes == 1);
        f.Controller.Restore(); Assert(f.System.Current == Original && f.Store.Pending is null && f.System.Writes == 2 && f.System.Refreshes == 2);
        Assert(f.Controller.Status.State == ProxyState.External);
    }
    private static void NoJournal()
    {
        var f = new Fixture(); f.System.Current = Applied; f.Controller.Restore();
        Assert(f.System.Writes == 0 && f.System.Refreshes == 0 && f.Controller.Status.State == ProxyState.External);
    }
    private static void IdenticalExternal()
    {
        var f = new Fixture(); f.System.Current = Applied;
        Throws(() => f.Controller.Acquire(Applied, Endpoint, () => { })); Assert(f.System.Writes == 0 && f.Store.Pending is null);
    }
    private static void StartupRecovery()
    {
        var f = new Fixture(); f.Pending(Applied); Assert(f.Controller.Inspect().State == ProxyState.RecoveryRequired);
        f.Controller.Restore(); Assert(f.System.Current == Original && f.Store.Pending is null && f.System.Writes == 1);
    }
    private static void AlreadyRestored()
    {
        var f = new Fixture(); f.Pending(Original); f.System.RefreshFailures = 1;
        Throws(f.Controller.Restore); Assert(f.Store.Pending is not null && f.System.Writes == 0);
        f.Controller.Restore(); Assert(f.Store.Pending is null && f.System.Writes == 0 && f.System.Refreshes == 2);
    }
    private static void Conflict()
    {
        var f = new Fixture(); f.Acquire(); f.System.Current = External;
        Throws(f.Controller.Restore); Assert(f.Controller.Status.State == ProxyState.Conflict && f.Store.Pending is not null && f.System.Writes == 1 && f.System.Current == External);
    }
    private static void StaleReview()
    {
        var f = new Fixture(); f.Pending(External); var reviewed = f.Controller.Inspect(); f.System.Current = External with { Bypass = "new" };
        Throws(() => f.Controller.ResolveReviewed(reviewed, false)); Throws(() => f.Controller.ResolveReviewed(reviewed, true));
        Assert(f.System.Writes == 0 && f.Store.Pending is not null);
    }
    private static void KeepExternal()
    {
        var f = new Fixture(); f.Pending(External); f.Controller.ResolveReviewed(f.Controller.Inspect(), true);
        Assert(f.System.Writes == 0 && f.Store.Pending is null && f.System.Current == External);
    }
    private static void ExplicitRestore()
    {
        var f = new Fixture(); f.Pending(External); f.Controller.ResolveReviewed(f.Controller.Inspect(), false);
        Assert(f.System.Writes == 1 && f.Store.Pending is null && f.System.Current == Original);
    }
    private static void Policy()
    {
        var f = new Fixture(); f.System.Denied = true; Throws(f.Acquire); Assert(f.System.Writes == 0 && f.Store.Pending is null);
    }
    private static void JournalFailure()
    {
        var f = new Fixture(); f.Store.FailCreate = true; Throws(f.Acquire); Assert(f.System.Writes == 0 && f.System.Current == Original);
    }
    private static void StaleAcquire()
    {
        var f = new Fixture(); var reviewed = f.Controller.PrepareAcquire(); f.System.Current = External;
        Throws(() => f.Controller.Acquire(reviewed, Endpoint, () => { })); Assert(f.System.Writes == 0 && f.Store.Pending is null && f.System.Current == External);
    }
    private static void RestorePolicy()
    {
        var f = new Fixture(); f.Acquire(); f.System.Denied = true; Throws(f.Controller.Restore);
        Assert(f.System.Current == Applied && f.Store.Pending is not null && f.System.Writes == 1);
        f.System.Denied = false; f.Controller.Restore(); Assert(f.System.Current == Original && f.Store.Pending is null);
    }
    private static void PreWriteOwner()
    {
        var f = new Fixture(); var checks = 0;
        Throws(() => f.Controller.Acquire(Original, Endpoint, () => { if (++checks == 2) { f.System.Current = Applied; throw new InvalidOperationException("ownership lost"); } }));
        Assert(f.System.Writes == 0 && f.Store.Pending is null && f.System.Current == Applied);
    }
    private static void ApplyRefreshFailure()
    {
        var f = new Fixture(); f.System.RefreshFailures = 1; Throws(f.Acquire);
        Assert(f.System.Current == Original && f.Store.Pending is null && f.System.Writes == 2);
    }
    private static void PartialWrite()
    {
        var f = new Fixture(); f.System.Partial = true; Throws(f.Acquire);
        Assert(f.System.Writes == 1 && f.Store.Pending is not null && f.Controller.Status.State == ProxyState.Conflict && f.System.Current == (Original with { Server = Applied.Server }));
    }
    private static void PostWriteOwner()
    {
        var f = new Fixture(); var checks = 0;
        Throws(() => f.Controller.Acquire(Original, Endpoint, () => { if (++checks == 3) throw new InvalidOperationException("ownership lost"); }));
        Assert(f.System.Current == Original && f.Store.Pending is null && f.System.Writes == 2);
    }
    private static void FailedRestore()
    {
        var f = new Fixture(); f.Acquire(); f.System.RefreshFailures = 1; Throws(f.Controller.Restore);
        Assert(f.Store.Pending is not null && f.System.Current == Original && f.Controller.Status.State == ProxyState.RecoveryRequired);
        f.Controller.Restore(); Assert(f.Store.Pending is null && f.System.Writes == 2);
    }
    private static void CorruptJournal()
    {
        var f = new Fixture(); f.Store.FailLoad = true; Throws(f.Acquire); Throws(f.Controller.Restore);
        Assert(f.Controller.Status.State == ProxyState.RecoveryRequired && f.System.Writes == 0);
    }
    private static void IneligibleEndpoints()
    {
        var f = new Fixture(); Throws(() => f.Controller.Acquire(Original, Endpoint with { Authenticated = true }, () => { }));
        Throws(() => f.Controller.Acquire(Original, Endpoint with { Protocol = "socks" }, () => { }));
        Throws(() => f.Controller.Acquire(Original, Endpoint with { Address = IPAddress.Any }, () => { })); Assert(f.System.Writes == 0);
    }
    private static void Ipv6()
    {
        var f = new Fixture(); f.Controller.Acquire(Original, Endpoint with { Address = IPAddress.IPv6Loopback }, () => { });
        Assert(f.System.Current.Server == "[::1]:12345" && ProxySettings.IsManagedServer(f.System.Current.Server));
        Assert(!ProxySettings.IsManagedServer("localhost:12345") && !ProxySettings.IsManagedServer("127.0.0.1:00080") && !ProxySettings.IsManagedServer("127.0.0.1:65536")); f.Controller.Restore();
    }
    private static void EncryptedJournal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"XrayDesktop proxy check {Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        try
        {
            var store = new ProxyRecovery(path, "test-user"); var journal = Journal();
            Assert(store.Load() is null); store.Create(journal); Assert(store.Load() == journal);
            var file = Path.Combine(path, "pending.dpapi"); var before = File.ReadAllBytes(file);
            Assert(!Encoding.UTF8.GetString(before).Contains(Original.Pac, StringComparison.Ordinal));
            Throws(() => store.Create(Journal())); Assert(File.ReadAllBytes(file).SequenceEqual(before));
            var wrongUser = new ProxyRecovery(path, "other-user"); Throws(() => wrongUser.Load());
            File.WriteAllBytes(file, [1, 2, 3]); Throws(() => store.Load()); Assert(File.Exists(file));
            store.Delete(); Assert(store.Load() is null);
        }
        finally { Directory.Delete(path, true); }
    }
    internal static void ReadNativeSettings()
    {
        var settings = new SystemProxy().Read();
        settings.Validate(); // never log sensitive server, bypass or PAC values
    }
    private static void ProxyProbe() => ProxyProbeAsync().GetAwaiter().GetResult();
    private static async Task ProxyProbeAsync()
    {
        // No public traffic: the synthetic proxy rejects CONNECT without forwarding.
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var endpoint = Endpoint with { Port = ((IPEndPoint)listener.LocalEndpoint).Port };
        var probe = HttpProxyProbe.VerifyAsync(endpoint, timeout.Token);
        try
        {
            using var client = await listener.AcceptTcpClientAsync(timeout.Token);
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            var request = await reader.ReadLineAsync(timeout.Token);
            Assert(request == "CONNECT example.com:443 HTTP/1.1");
            for (var i = 0; i < 30; i++)
            {
                var line = await reader.ReadLineAsync(timeout.Token);
                Assert(line is not null && !line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase));
                if (line == "") break;
                if (i == 29) throw new InvalidDataException("Too many request headers.");
            }
            await stream.WriteAsync("HTTP/1.1 407 Proxy Authentication Required\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), timeout.Token);
            try { await probe; }
            catch (System.Net.Http.HttpRequestException) { return; }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("Rejected proxy request unexpectedly succeeded.");
        }
        finally { await timeout.CancelAsync(); try { await probe; } catch (Exception) { /* observe completion */ } }
    }
}
