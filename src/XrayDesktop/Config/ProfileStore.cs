using System.Text.Json;
using XrayDesktop.Storage;

namespace XrayDesktop.Config;

internal sealed record Profile(Guid Id, string Name, string Path, string WorkingDirectory);
internal sealed record ProfileSettings(int Version, List<Profile> Profiles, Guid? ActiveId);

internal sealed class ProfileStore(string dataDirectory)
{
    internal const int MaxConfigBytes = 10 * 1024 * 1024;
    internal string DataDirectory { get; } = Path.GetFullPath(dataDirectory);
    private const int MaxSettingsBytes = 1024 * 1024;
    private string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    internal ProfileSettings Load()
    {
        byte[] bytes;
        try { bytes = AtomicFile.ReadBounded(SettingsPath, MaxSettingsBytes); }
        catch (FileNotFoundException) { return new(1, [], null); }
        catch (DirectoryNotFoundException) { return new(1, [], null); }
        var settings = JsonSerializer.Deserialize<ProfileSettings>(bytes) ?? throw new InvalidDataException("Invalid profile settings.");
        Validate(settings);
        return settings;
    }
    private static void Validate(ProfileSettings settings)
    {
        if (settings.Version != 1 || settings.Profiles is null || settings.Profiles.Count > 500 ||
            settings.Profiles.Any(p => p is null || p.Id == Guid.Empty || string.IsNullOrWhiteSpace(p.Name) ||
                string.IsNullOrWhiteSpace(p.Path) || !Path.IsPathFullyQualified(p.Path) ||
                string.IsNullOrWhiteSpace(p.WorkingDirectory) || !Path.IsPathFullyQualified(p.WorkingDirectory)) ||
            settings.Profiles.Select(p => p.Id).Distinct().Count() != settings.Profiles.Count)
            throw new InvalidDataException("Invalid profile settings. Existing settings were not changed.");
    }

    internal void Save(ProfileSettings settings)
    {
        Validate(settings);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(settings, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > MaxSettingsBytes) throw new InvalidDataException("Profile settings exceed 1 MiB; remove references or shorten paths/names.");
        AtomicFile.Write(SettingsPath, bytes);
    }

    internal Profile Import(string source, bool managedCopy)
    {
        source = Path.GetFullPath(source);
        var bytes = ReadConfig(source);
        var id = Guid.NewGuid();
        var path = managedCopy ? ManagedPath(id) : source;
        if (managedCopy) AtomicFile.Write(path, bytes);
        return new(id, Path.GetFileNameWithoutExtension(source), path, Path.GetDirectoryName(source)!);
    }

    internal Profile Create(string name, byte[] bytes, string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var id = Guid.NewGuid();
        var path = ManagedPath(id);
        SaveConfig(path, bytes);
        return new(id, name.Trim(), path, Path.GetFullPath(workingDirectory));
    }

    private string ManagedPath(Guid id) => Path.Combine(DataDirectory, "profiles", $"{id:N}.json");
    internal static byte[] ReadConfig(string path) => AtomicFile.ReadBounded(path, MaxConfigBytes);
    internal static void SaveConfig(string path, byte[] bytes)
    {
        if (bytes.Length > MaxConfigBytes) throw new InvalidDataException("Configuration exceeds 10 MiB.");
        AtomicFile.Write(path, bytes);
    }
}
