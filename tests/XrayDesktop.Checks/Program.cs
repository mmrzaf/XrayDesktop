using System.Reflection;
using XrayDesktop;

namespace XrayDesktop.Checks;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--child-exit" })) { Console.WriteLine("test stdout"); Console.Error.WriteLine("test stderr"); return 7; }
        if (args.SequenceEqual(new[] { "--child-wait" })) { Thread.Sleep(TimeSpan.FromMinutes(2)); return 0; }
        if (args.Length == 2 && args[0] == "--check-core")
        {
            try
            {
                PhaseOneChecks.RealCore(Path.Combine(args[1], "xray.exe"));
                Console.WriteLine("PASS: packaged Xray integration.");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL: packaged Xray integration: {exception}");
                return 1;
            }
        }
        if (args.Length != 0) return 2;
        var checks = new (string Name, Action Run)[]
        {
            ("No arguments selects desktop", () => Expect(StartupMode.Desktop, Bootstrap.ParseArguments([]))),
            ("Version selects headless mode", () => Expect(StartupMode.Version, Bootstrap.ParseArguments(["--version"]))),
            ("Self-test selects headless mode", () => Expect(StartupMode.SelfTest, Bootstrap.ParseArguments(["--self-test"]))),
            ("Help selects headless mode", () => Expect(StartupMode.Help, Bootstrap.ParseArguments(["--help"]))),
            ("Unknown argument is rejected", () => Expect(StartupMode.Invalid, Bootstrap.ParseArguments(["--unknown"]))),
            ("Multiple arguments are rejected", () => Expect(StartupMode.Invalid, Bootstrap.ParseArguments(["--version", "--self-test"]))),
            ("Application and test version metadata match", () => Expect(typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion, Bootstrap.Version)),
            ("Core compatibility baseline is pinned", () => Expect("26.11.9", Bootstrap.TargetCoreVersion)),
            ("Headless self-test reports success", CheckSelfTest)
        };

        checks = [.. checks, .. PhaseOneChecks.All, .. PhaseTwoChecks.All, .. PhaseThreeChecks.All, .. PhaseFourChecks.All, .. PhaseFiveChecks.All];
        var failures = 0;
        foreach (var check in checks)
        {
            try
            {
                check.Run();
                Console.WriteLine($"PASS: {check.Name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL: {check.Name}: {exception}");
            }
        }
        Console.WriteLine($"{checks.Length - failures}/{checks.Length} mandatory checks passed.");
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("XRAY_TEST_CORE")))
        {
            if (Environment.GetEnvironmentVariable("XRAY_REQUIRE_TEST_CORE") == "1")
            {
                failures++;
                Console.Error.WriteLine("FAIL: required real Xray integration core was not supplied.");
            }
            else Console.WriteLine("SKIP: real Xray integration (XRAY_TEST_CORE not set).");
        }
        else
        {
            try { PhaseOneChecks.RealCore(); Console.WriteLine("PASS: real Xray integration."); }
            catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL: real Xray integration: {exception}"); }
        }
        if (Environment.GetEnvironmentVariable("XRAY_TEST_WININET_READ") == "1")
        {
            try { PhaseTwoChecks.ReadNativeSettings(); Console.WriteLine("PASS: native WinINet read-only check."); }
            catch (Exception) { failures++; Console.Error.WriteLine("FAIL: native WinINet read-only check."); }
        }
        else Console.WriteLine("SKIP: native WinINet read-only check (XRAY_TEST_WININET_READ not set).");
        if (Environment.GetEnvironmentVariable("XRAY_TEST_UI") == "1")
        {
            try { PhaseFourChecks.ConstructHiddenForm(); Console.WriteLine("PASS: hidden WinForms construction check."); }
            catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL: hidden WinForms construction: {exception.Message}"); }
        }
        else Console.WriteLine("SKIP: hidden WinForms construction (XRAY_TEST_UI not set).");
        return failures == 0 ? 0 : 1;
    }

    private static void CheckSelfTest()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Expect(0, Bootstrap.RunSelfTest(output, error));
        Expect(true, output.ToString().StartsWith("PASS: Xray Desktop ", StringComparison.Ordinal));
        Expect(string.Empty, error.ToString());
    }

    private static void Expect<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
        }
    }
}
