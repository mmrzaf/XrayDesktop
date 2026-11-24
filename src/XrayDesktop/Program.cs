using XrayDesktop.UI;
using XrayDesktop.Windows;

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
                try
                {
                    using var instance = new SingleInstance();
                    if (!instance.Acquired)
                    {
                        MessageBox.Show("Another Xray Desktop instance is already running for this user.", Bootstrap.ProductName);
                        return 1;
                    }
                    using var form = new MainForm();
                    Application.Run(form);
                }
                catch (Exception exception)
                {
                    MessageBox.Show(exception.Message, Bootstrap.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                return 0;
            default:
                Console.Error.WriteLine(Bootstrap.Usage);
                return 2;
        }
    }
}
