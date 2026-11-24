using XrayDesktop;

namespace XrayDesktop.Checks;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.SequenceEqual(new[] { "--child-exit" })) { Console.WriteLine("test stdout"); Console.Error.WriteLine("test stderr"); return 7; }
        if (args.SequenceEqual(new[] { "--child-wait" })) { Thread.Sleep(TimeSpan.FromMinutes(2)); return 0; }
        if (args.Length != 0) return 2;
        var checks = new (string Name, Action Run)[]
        {
            ("No arguments selects desktop", () => Expect(StartupMode.Desktop, Bootstrap.ParseArguments([]))),
            ("Version selects headless mode", () => Expect(StartupMode.Version, Bootstrap.ParseArguments(["--version"]))),
            ("Self-test selects headless mode", () => Expect(StartupMode.SelfTest, Bootstrap.ParseArguments(["--self-test"]))),
            ("Help selects headless mode", () => Expect(StartupMode.Help, Bootstrap.ParseArguments(["--help"]))),
            ("Unknown argument is rejected", () => Expect(StartupMode.Invalid, Bootstrap.ParseArguments(["--unknown"]))),
            ("Multiple arguments are rejected", () => Expect(StartupMode.Invalid, Bootstrap.ParseArguments(["--version", "--self-test"]))),
            ("Version metadata matches preview", () => Expect("0.1.0-preview.1", Bootstrap.Version)),
            ("Core compatibility baseline is pinned", () => Expect("26.11.9", Bootstrap.TargetCoreVersion)),
            ("Headless self-test reports success", CheckSelfTest)
        };

        checks = [.. checks, .. PhaseOneChecks.All];
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
                Console.Error.WriteLine($"FAIL: {check.Name}: {exception.Message}");
            }
        }
        Console.WriteLine($"{checks.Length - failures}/{checks.Length} checks passed.");
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
