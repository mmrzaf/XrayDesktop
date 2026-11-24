using System.Security.Cryptography;
using XrayDesktop.Storage;

namespace XrayDesktop.Config;

internal sealed class RunSnapshot : IDisposable
{
    private readonly string directory;
    internal string Path { get; }
    internal string Hash { get; }
    internal string WorkingDirectory { get; }
    internal Inspection Inspection { get; }

    internal RunSnapshot(string dataDirectory, byte[] bytes, string workingDirectory)
    {
        WorkingDirectory = System.IO.Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(WorkingDirectory)) throw new DirectoryNotFoundException("Profile working directory does not exist.");
        if (bytes.Length > ProfileStore.MaxConfigBytes) throw new InvalidDataException("Configuration exceeds 10 MiB.");
        directory = System.IO.Path.Combine(dataDirectory, "runtime", Guid.NewGuid().ToString("N"));
        Path = System.IO.Path.Combine(directory, "config.json");
        Hash = Convert.ToHexString(SHA256.HashData(bytes));
        Inspection = ConfigInspector.Inspect(bytes);
        AtomicFile.Write(Path, bytes);
    }

    internal void Verify() { if (Hash != Convert.ToHexString(SHA256.HashData(ProfileStore.ReadConfig(Path)))) throw new InvalidDataException("Runtime snapshot changed after validation."); }
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
