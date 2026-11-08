using System.Windows;
using Csaver.Application;
using Csaver.Windows;

namespace Csaver.App;

public partial class App : System.Windows.Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (WorkerHost.IsWorker(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var code = 3;
            try
            {
                code = await WorkerHost.RunAsync(e.Args);
            }
            catch (Exception ex)
            {
                WorkerLog.Write(AppContext.BaseDirectory, WorkerHost.OperationIdFromArgs(e.Args), "Unhandled worker exception: " + ex.Message);
                code = 3;
            }
            finally
            {
                Shutdown(code);
            }
            return;
        }

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Csaver startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }
}
