using System;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace NetToGXSim2.Wpf
{
    public partial class App : Application
    {
        private const string MutexName = @"Global\NetToGXSim2_SingleInstance_Mutex";
        private Mutex? _mutex;
        private bool _isPrimaryInstance;

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private const int SW_RESTORE = 9;
        private const int SW_SHOW = 5;

        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                _mutex = new Mutex(true, MutexName, out _isPrimaryInstance);
            }
            catch (AbandonedMutexException)
            {
                // Mutex was abandoned by previous instance crash - this instance is now primary
                _isPrimaryInstance = true;
            }
            catch (Exception)
            {
                // Fallback to local mutex in restricted environments
                try
                {
                    _mutex = new Mutex(true, @"NetToGXSim2_SingleInstance_Mutex", out _isPrimaryInstance);
                }
                catch (AbandonedMutexException)
                {
                    _isPrimaryInstance = true;
                }
                catch
                {
                    _isPrimaryInstance = true;
                }
            }

            if (!_isPrimaryInstance)
            {
                BringExistingInstanceToFront();

                MessageBox.Show(
                    "NetToGXSim2 is already running!\nOnly one instance of NetToGXSim2 can run at a time.",
                    "Error - NetToGXSim2",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                Shutdown();
                return;
            }

            base.OnStartup(e);

            var mainWindow = new Views.MainWindow();
            mainWindow.Show();
        }

        private static void BringExistingInstanceToFront()
        {
            try
            {
                var current = Process.GetCurrentProcess();
                var existingProcess = Process.GetProcessesByName(current.ProcessName)
                    .FirstOrDefault(p => p.Id != current.Id);

                if (existingProcess != null && existingProcess.MainWindowHandle != IntPtr.Zero)
                {
                    IntPtr handle = existingProcess.MainWindowHandle;
                    if (IsIconic(handle))
                    {
                        ShowWindow(handle, SW_RESTORE);
                    }
                    else
                    {
                        ShowWindow(handle, SW_SHOW);
                    }
                    SetForegroundWindow(handle);
                }
            }
            catch
            {
                // Ignore any error when focusing existing window
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_isPrimaryInstance && _mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch { }
                _mutex.Dispose();
                _mutex = null;
            }
            base.OnExit(e);
        }
    }
}
