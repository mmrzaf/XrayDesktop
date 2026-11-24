using System.Reflection;
using System.Runtime.InteropServices;

namespace XrayDesktop;

internal enum StartupMode
{
    Desktop,
    Version,
    SelfTest,
    Help,
    Invalid
}

internal static class Bootstrap
{
    internal const string ProductName = "Xray Desktop";
    internal const string TargetCoreVersion = "26.11.9";
    internal const string Usage = "Usage: XrayDesktop.exe [--version | --self-test | --help]";

    internal static string Version { get; } = typeof(Bootstrap).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? throw new InvalidOperationException("Application version metadata is missing.");

    internal static StartupMode ParseArguments(string[] args) => args.Length switch
    {
        0 => StartupMode.Desktop,
        1 => args[0] switch
        {
            "--version" => StartupMode.Version,
            "--self-test" => StartupMode.SelfTest,
            "--help" => StartupMode.Help,
            _ => StartupMode.Invalid
        },
        _ => StartupMode.Invalid
    };

    internal static int RunSelfTest(TextWriter output, TextWriter error)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            error.WriteLine("FAIL: Windows x64 process required.");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(Version) || !Path.IsPathFullyQualified(AppContext.BaseDirectory))
        {
            error.WriteLine("FAIL: Application metadata or base directory is invalid.");
            return 1;
        }

        output.WriteLine($"PASS: {ProductName} {Version}; Windows x64; target Xray {TargetCoreVersion}.");
        return 0;
    }
}
