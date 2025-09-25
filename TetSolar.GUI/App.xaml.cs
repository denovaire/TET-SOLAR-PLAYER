using System;
using System.Threading;
using System.Windows;
using System.IO;

namespace TetSolar.GUI
{
    public partial class App : Application
    {
        private static Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            _singleInstanceMutex = new Mutex(true, "TetSolar.GUI_SingleInstance", out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("TET SOLAR GUI is already running.", "TET SOLAR",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            // Log any unhandled UI exception to a text file next to the EXE
            this.DispatcherUnhandledException += (s, args) =>
            {
                try
                {
                    var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last_crash.txt");
                    File.WriteAllText(path, args.Exception.ToString());
                }
                catch { /* ignore */ }

                MessageBox.Show("An error occurred. A log was written to 'last_crash.txt' next to the EXE.",
                    "Unhandled UI exception", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true; // prevent immediate crash so you can read the message
            };

            base.OnStartup(e);

            try
            {
                var win = new MainWindow();
                win.Show();
            }
            catch (Exception ex)
            {
                try
                {
                    var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "last_crash.txt");
                    File.WriteAllText(path, ex.ToString());
                }
                catch { /* ignore */ }

                MessageBox.Show("Startup error. See 'last_crash.txt' next to the EXE.");
                Shutdown();
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex = null;
            base.OnExit(e);
        }
    }
}
