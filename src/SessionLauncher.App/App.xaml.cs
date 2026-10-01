using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace SessionLauncher.App
{
    public partial class App : Application
    {
        /// <summary>Crash log beside the exe. A GUI app that dies silently is undebuggable.</summary>
        private static string LogPath => Path.Combine(
            AppContext.BaseDirectory, "SessionLauncher.crash.log");

        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
                Write(args.ExceptionObject as Exception);

            DispatcherUnhandledException += (_, args) =>
            {
                Write(args.Exception);
                args.Handled = true;   // show the log instead of vanishing
            };

            base.OnStartup(e);
        }

        private static void Write(Exception? ex)
        {
            try
            {
                File.AppendAllText(LogPath,
                    $"--- {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} ---\n{ex}\n\n");
            }
            catch
            {
                // Never let logging be the thing that crashes the app.
            }
        }
    }
}
