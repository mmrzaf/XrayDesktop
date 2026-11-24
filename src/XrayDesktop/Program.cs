using XrayDesktop.UI;

namespace XrayDesktop;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        switch (Bootstrap.ParseArguments(args))
        {
            case StartupMode.Version:
                Console.WriteLine(Bootstrap.Version);
                return 0;
            case StartupMode.SelfTest:
                return Bootstrap.RunSelfTest(Console.Out, Console.Error);
            case StartupMode.Help:
                Console.WriteLine(Bootstrap.Usage);
                return 0;
            case StartupMode.Desktop:
                ApplicationConfiguration.Initialize();
                using (var form = new MainForm())
                {
                    Application.Run(form);
                }
                return 0;
            default:
                Console.Error.WriteLine(Bootstrap.Usage);
                return 2;
        }
    }
}
