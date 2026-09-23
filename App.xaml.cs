using System.Windows;
using WarehousePacking.Services;
using WarehousePacking.Views;

namespace WarehousePacking;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Any(a => string.Equals(a, "--smoke", StringComparison.OrdinalIgnoreCase)))
        {
            Smoke.Run();
            Shutdown();
            return;
        }

        PeerPrint.Start();
        var login = new LoginWindow();
        if (login.ShowDialog() == true && login.Client is { } client)
        {
            var main = new MainWindow(login.Config, client, login.UserName);
            MainWindow = main;
            main.Show();
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            return;
        }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        PeerPrint.Stop();
        base.OnExit(e);
    }
}
