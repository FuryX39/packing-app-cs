using System.Windows;
using WarehousePacking.Services;
using WarehousePacking.Views;

namespace WarehousePacking;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var printWorkerIndex = Array.FindIndex(
            e.Args,
            arg => string.Equals(arg, "--print-worker", StringComparison.OrdinalIgnoreCase));
        if (printWorkerIndex >= 0)
        {
            var requestPath = printWorkerIndex + 1 < e.Args.Length
                ? e.Args[printWorkerIndex + 1]
                : "";
            Shutdown(GdiPrinter.RunPrintWorker(requestPath));
            return;
        }
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
