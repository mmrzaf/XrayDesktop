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
            case StartupMode.Guard:
                Bootstrap.TryParseGuard(args, out var parentId, out var journalId);
                return ProxyGuard.Run(parentId, journalId);
            case StartupMode.Desktop:
                ApplicationConfiguration.Initialize();
                try
                {
                    using var instance = new SingleInstance();
                    if (!instance.Acquired)
                    {
                        instance.SignalRunningInstance();
                        return 0;
                    }
                    using var form = new MainForm();
                    instance.OnShowRequested(() => { if (form.IsHandleCreated && !form.IsDisposed) form.BeginInvoke(new Action(form.ShowWindow)); });
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
