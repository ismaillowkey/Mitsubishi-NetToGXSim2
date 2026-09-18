using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using NetToGXSim2.Core;
using NetToGXSim2.Wpf.Services;

namespace NetToGXSim2.Wpf.Views
{
    public partial class MainWindow : Window
    {
        private readonly GxSimulatorEngine _simEngine;
        private readonly McProtocolServer _mcServer1;
        private readonly McProtocolServer _mcServer2;
        private readonly DispatcherTimer _pollTimer;

        // UI components for Inputs X0 to X7 (8 octal inputs)
        private readonly ToggleButton[] _inputToggles = new ToggleButton[8];
        private readonly Ellipse[] _inputLeds = new Ellipse[8];
        private bool _isUpdatingInputsFromPlc = false;

        // UI components for Outputs Y0 to Y7 (8 octal outputs)
        private readonly Ellipse[] _outputLamps = new Ellipse[8];
        private readonly DropShadowEffect[] _outputGlows = new DropShadowEffect[8];
        private readonly TextBlock[] _outputStateTexts = new TextBlock[8];

        private bool _isPollingBusy = false;
        private DateTime _lastConnectAttempt = DateTime.MinValue;

        public MainWindow()
        {
            InitializeComponent();

            _simEngine = new GxSimulatorEngine(1);
            _simEngine.LogMessage += (msg) => Dispatcher.InvokeAsync(() => Log(msg));

            InitializeInputRack();
            InitializeOutputRack();

            // Server 1 (Top): Port 5000 (Started by default, auto-increment if in use)
            int s1Port = McProtocolServer.GetNextAvailablePort(5000);
            _mcServer1 = new McProtocolServer(_simEngine, "MC Server 1", s1Port);
            _mcServer1.LogMessage += (msg) => Dispatcher.InvokeAsync(() => Log(msg));
            _mcServer1.Start(s1Port);
            UpdateServer1Ui();

            // Server 2 (Bottom): Port 6000 (Stopped by default, check available port)
            int s2Port = McProtocolServer.GetNextAvailablePort(6000);
            _mcServer2 = new McProtocolServer(_simEngine, "MC Server 2", s2Port);
            _mcServer2.LogMessage += (msg) => Dispatcher.InvokeAsync(() => Log(msg));
            UpdateServer2Ui();

            Task.Run(() =>
            {
                try
                {
                    _lastConnectAttempt = DateTime.UtcNow;
                    bool ok = _simEngine.Connect(1);
                    Dispatcher.InvokeAsync(() => UpdateSimStatusDisplay(ok));
                }
                catch (Exception ex)
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        UpdateSimStatusDisplay(false);
                        Log($"[WARNING] Initial connection attempt: {ex.Message}");
                    });
                }
            });

            // Smooth polling timer (100ms) with gentle background execution
            _pollTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _pollTimer.Tick += PollTimer_Tick;
            _pollTimer.Start();

            // Auto-check for updates in background on startup
            Task.Run(async () =>
            {
                await Task.Delay(2000);
                var result = await UpdateCheckerService.CheckForUpdatesAsync();
                Dispatcher.Invoke(() =>
                {
                    if (result.Success)
                    {
                        if (result.HasUpdate)
                        {
                            TxtUpdateStatus.Text = $"Update available ({result.LatestVersion})";
                            TxtUpdateStatus.Foreground = new SolidColorBrush(Color.FromRgb(2, 132, 199));
                            UpdateStatusLed.Fill = new SolidColorBrush(Color.FromRgb(2, 132, 199));

                            var answer = MessageBox.Show(
                                $"A new version of GX2 Bridge is available!\n\n" +
                                $"Current Version: v{UpdateCheckerService.CurrentVersion}\n" +
                                $"Latest Version: {result.LatestVersion}\n\n" +
                                $"Would you like to open the download page to update now?",
                                "Update Available - GX2 Bridge",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Information);

                            if (answer == MessageBoxResult.Yes)
                            {
                                try
                                {
                                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                    {
                                        FileName = result.ReleaseUrl,
                                        UseShellExecute = true
                                    });
                                }
                                catch { }
                            }
                        }
                        else
                        {
                            TxtUpdateStatus.Text = "No update available";
                            TxtUpdateStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                            UpdateStatusLed.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                            Log($"[UPDATE] Application is up to date (v{UpdateCheckerService.CurrentVersion}). No update available.");
                        }
                    }
                    else
                    {
                        TxtUpdateStatus.Text = "No update available";
                        TxtUpdateStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                        UpdateStatusLed.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                    }
                });
            });

            Log($"GX2 Bridge ready. Server 1 on port {_mcServer1.Port} (TCP/UDP).");
        }

        private void InitializeInputRack()
        {
            InputGrid.Children.Clear();

            for (int i = 0; i < 8; i++)
            {
                int bitIndex = i;
                string devName = $"X{i}";

                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                    CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(2),
                    Padding = new Thickness(4, 5, 4, 5),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                    BorderThickness = new Thickness(1)
                };

                var sp = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var txtBit = new TextBlock
                {
                    Text = devName,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 3)
                };

                var led = new Ellipse
                {
                    Width = 7,
                    Height = 7,
                    Fill = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 4),
                    Effect = new DropShadowEffect
                    {
                        Color = Color.FromRgb(2, 132, 199),
                        BlurRadius = 6,
                        ShadowDepth = 0,
                        Opacity = 0
                    }
                };
                _inputLeds[bitIndex] = led;

                var btn = new ToggleButton
                {
                    Content = "OFF",
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold,
                    Padding = new Thickness(4, 2, 4, 2),
                    Height = 22,
                    MinWidth = 42,
                    Style = (Style)FindResource("ToggleInputStyle")
                };

                btn.Click += (s, e) =>
                {
                    if (_isUpdatingInputsFromPlc) return;

                    bool isChecked = btn.IsChecked ?? false;
                    btn.Content = isChecked ? "ON" : "OFF";
                    int val = isChecked ? 1 : 0;

                    // Immediately update local UI LED
                    _inputLeds[bitIndex].Fill = new SolidColorBrush(isChecked ? Color.FromRgb(37, 99, 235) : Color.FromRgb(203, 213, 225));
                    ((DropShadowEffect)_inputLeds[bitIndex].Effect).Opacity = isChecked ? 1 : 0;

                    Task.Run(() =>
                    {
                        _simEngine.WriteDevice(devName, val);
                        // Instant readback of Outputs Y0-Y7 after PLC ladder scan
                        System.Threading.Thread.Sleep(40);
                        byte[] yBits;
                        if (_simEngine.ReadDeviceBlockBits("Y0", 8, out yBits) == 0)
                        {
                            Dispatcher.InvokeAsync(() => UpdateOutputsUi(yBits));
                        }
                    });

                    Log($"[INPUT] Set {devName} = {val}");
                };

                _inputToggles[bitIndex] = btn;

                sp.Children.Add(txtBit);
                sp.Children.Add(led);
                sp.Children.Add(btn);
                card.Child = sp;
                InputGrid.Children.Add(card);
            }
        }

        private void UpdateOutputsUi(byte[] yBits)
        {
            if (yBits == null) return;
            for (int i = 0; i < Math.Min(8, yBits.Length); i++)
            {
                bool isOn = yBits[i] != 0;
                _outputLamps[i].Fill = new SolidColorBrush(isOn ? Color.FromRgb(34, 197, 94) : Color.FromRgb(203, 213, 225));
                _outputLamps[i].Stroke = new SolidColorBrush(isOn ? Color.FromRgb(22, 163, 74) : Color.FromRgb(148, 163, 184));
                _outputGlows[i].Opacity = isOn ? 1 : 0;
                _outputStateTexts[i].Text = isOn ? "ON" : "OFF";
                _outputStateTexts[i].Foreground = new SolidColorBrush(isOn ? Color.FromRgb(22, 163, 74) : Color.FromRgb(148, 163, 184));
            }
        }

        private void InitializeOutputRack()
        {
            OutputGrid.Children.Clear();

            for (int i = 0; i < 8; i++)
            {
                int bitIndex = i;
                string devName = $"Y{i}";

                var card = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(248, 250, 252)),
                    CornerRadius = new CornerRadius(6),
                    Margin = new Thickness(2),
                    Padding = new Thickness(4, 5, 4, 5),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(226, 232, 240)),
                    BorderThickness = new Thickness(1)
                };

                var sp = new StackPanel
                {
                    HorizontalAlignment = HorizontalAlignment.Center
                };

                var txtBit = new TextBlock
                {
                    Text = devName,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 3)
                };

                var glow = new DropShadowEffect
                {
                    Color = Color.FromRgb(34, 197, 94),
                    BlurRadius = 8,
                    ShadowDepth = 0,
                    Opacity = 0
                };
                _outputGlows[bitIndex] = glow;

                var lamp = new Ellipse
                {
                    Width = 16,
                    Height = 16,
                    Fill = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                    Stroke = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                    StrokeThickness = 1.2,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 3),
                    Effect = glow
                };
                _outputLamps[bitIndex] = lamp;

                var txtState = new TextBlock
                {
                    Text = "OFF",
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                _outputStateTexts[bitIndex] = txtState;

                sp.Children.Add(txtBit);
                sp.Children.Add(lamp);
                sp.Children.Add(txtState);
                card.Child = sp;
                OutputGrid.Children.Add(card);
            }
        }

        private async void PollTimer_Tick(object? sender, EventArgs e)
        {
            if (_isPollingBusy) return;
            _isPollingBusy = true;

            await Task.Run(() =>
            {
                try
                {
                    if (!_simEngine.IsConnected)
                    {
                        if ((DateTime.UtcNow - _lastConnectAttempt).TotalSeconds < 3) return;
                        _lastConnectAttempt = DateTime.UtcNow;

                        bool reconnected = _simEngine.Connect(1);
                        Dispatcher.InvokeAsync(() => UpdateSimStatusDisplay(reconnected));
                        if (!reconnected) return;
                    }

                    // Batch read Inputs X0 to X7
                    byte[] xBits;
                    if (_simEngine.ReadDeviceBlockBits("X0", 8, out xBits) == 0)
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            _isUpdatingInputsFromPlc = true;
                            try
                            {
                                for (int i = 0; i < 8; i++)
                                {
                                    bool isOn = xBits[i] != 0;
                                    if (_inputToggles[i].IsChecked != isOn)
                                    {
                                        _inputToggles[i].IsChecked = isOn;
                                        _inputToggles[i].Content = isOn ? "ON" : "OFF";
                                    }
                                    _inputLeds[i].Fill = new SolidColorBrush(isOn ? Color.FromRgb(37, 99, 235) : Color.FromRgb(203, 213, 225));
                                    ((DropShadowEffect)_inputLeds[i].Effect).Opacity = isOn ? 1 : 0;
                                }
                            }
                            finally
                            {
                                _isUpdatingInputsFromPlc = false;
                            }
                        });
                    }

                    // Batch read Outputs Y0 to Y7
                    byte[] yBits;
                    if (_simEngine.ReadDeviceBlockBits("Y0", 8, out yBits) == 0)
                    {
                        Dispatcher.InvokeAsync(() => UpdateOutputsUi(yBits));
                    }
                }
                catch { }
                finally
                {
                    _isPollingBusy = false;
                }
            });
        }

        private void UpdateSimStatusDisplay(bool isConnected)
        {
            SimStatusLed.Fill = new SolidColorBrush(isConnected ? Color.FromRgb(34, 197, 94) : Color.FromRgb(239, 68, 68));
            TxtSimStatus.Text = isConnected ? "GX Sim 2: Connected" : "GX Sim 2: Offline";
            TxtSimStatus.Foreground = new SolidColorBrush(isConnected ? Color.FromRgb(22, 163, 74) : Color.FromRgb(220, 38, 38));
        }

        private void BtnReadCustom_Click(object sender, RoutedEventArgs e)
        {
            string addr = TxtCustomAddress.Text.Trim();
            if (string.IsNullOrEmpty(addr)) return;

            Task.Run(() =>
            {
                if (_simEngine.ReadDevice(addr, out int val) == 0)
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        TxtCustomResult.Text = $"Bit: [{(val != 0 ? "ON" : "OFF")}] | Value: {val} (0x{val:X4})";
                        Log($"[INSPECTOR] Read {addr} = {val}");
                    });
                }
                else
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        TxtCustomResult.Text = $"Error reading {addr}";
                        Log($"[INSPECTOR ERROR] Failed to read {addr}");
                    });
                }
            });
        }

        private void BtnToggleCustom_Click(object sender, RoutedEventArgs e)
        {
            string addr = TxtCustomAddress.Text.Trim();
            if (string.IsNullOrEmpty(addr)) return;

            Task.Run(() =>
            {
                if (_simEngine.ReadDevice(addr, out int val) == 0)
                {
                    int newVal = val != 0 ? 0 : 1;
                    _simEngine.WriteDevice(addr, newVal);
                    Dispatcher.InvokeAsync(() =>
                    {
                        TxtCustomResult.Text = $"Bit: [{(newVal != 0 ? "ON" : "OFF")}] | Value: {newVal}";
                        Log($"[INSPECTOR] Toggled {addr} -> {newVal}");
                    });
                }
            });
        }

        private void BtnWriteCustomWord_Click(object sender, RoutedEventArgs e)
        {
            string addr = TxtCustomAddress.Text.Trim();
            if (string.IsNullOrEmpty(addr)) return;

            if (int.TryParse(TxtWriteWordValue.Text.Trim(), out int val))
            {
                Task.Run(() =>
                {
                    if (_simEngine.WriteDevice(addr, val) == 0)
                    {
                        Dispatcher.InvokeAsync(() =>
                        {
                            TxtCustomResult.Text = $"Value Written: {val}";
                            Log($"[INSPECTOR] Wrote {addr} = {val}");
                        });
                    }
                });
            }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            TxtLog.Clear();
        }

        private void Log(string message)
        {
            string time = DateTime.Now.ToString("HH:mm:ss.fff");
            TxtLog.AppendText($"[{time}] {message}\n");
            TxtLog.ScrollToEnd();
        }

        private void BtnToggleServer1_Click(object sender, RoutedEventArgs e)
        {
            if (_mcServer1.IsRunning)
            {
                _mcServer1.Stop();
            }
            else
            {
                int port = 5000;
                if (int.TryParse(TxtPortServer1.Text.Trim(), out int p)) port = p;

                int availablePort = McProtocolServer.GetNextAvailablePort(port);
                if (availablePort != port)
                {
                    Log($"[MC Server 1] Port {port} sedang terpakai! Otomatis dinaikkan ke port {availablePort}.");
                }
                _mcServer1.Start(availablePort);
            }
            UpdateServer1Ui();
        }

        private void BtnToggleServer2_Click(object sender, RoutedEventArgs e)
        {
            if (_mcServer2.IsRunning)
            {
                _mcServer2.Stop();
            }
            else
            {
                int port = 6000;
                if (int.TryParse(TxtPortServer2.Text.Trim(), out int p)) port = p;

                int availablePort = McProtocolServer.GetNextAvailablePort(port);
                if (availablePort != port)
                {
                    Log($"[MC Server 2] Port {port} sedang terpakai! Otomatis dinaikkan ke port {availablePort}.");
                }
                _mcServer2.Start(availablePort);
            }
            UpdateServer2Ui();
        }

        private void UpdateServer1Ui()
        {
            TxtPortServer1.Text = _mcServer1.Port.ToString();
            TxtPortServer1.IsEnabled = !_mcServer1.IsRunning;
            BtnToggleServer1.Content = _mcServer1.IsRunning ? "Stop" : "Start";
            Server1Led.Fill = new SolidColorBrush(_mcServer1.IsRunning ? Color.FromRgb(34, 197, 94) : Color.FromRgb(148, 163, 184));
        }

        private void UpdateServer2Ui()
        {
            TxtPortServer2.Text = _mcServer2.Port.ToString();
            TxtPortServer2.IsEnabled = !_mcServer2.IsRunning;
            BtnToggleServer2.Content = _mcServer2.IsRunning ? "Stop" : "Start";
            Server2Led.Fill = new SolidColorBrush(_mcServer2.IsRunning ? Color.FromRgb(34, 197, 94) : Color.FromRgb(148, 163, 184));
        }

        private async void BtnExitGxSim_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log("[ACTION] Terminating GX Simulator 2 (SimManager.exe)...");

                // Disconnect COM engine first
                _simEngine.Disconnect();
                UpdateSimStatusDisplay(false);

                await Task.Run(() =>
                {
                    string[] targetProcesses = new[] { "SimManager", "IOSystem" };
                    int killedCount = 0;

                    foreach (var procName in targetProcesses)
                    {
                        try
                        {
                            var processes = System.Diagnostics.Process.GetProcessesByName(procName);
                            foreach (var p in processes)
                            {
                                try
                                {
                                    p.Kill();
                                    p.WaitForExit(1000);
                                    killedCount++;
                                }
                                catch { }
                            }
                        }
                        catch { }

                        // Fallback using taskkill
                        try
                        {
                            var psi = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = "taskkill",
                                Arguments = $"/F /IM {procName}.exe",
                                CreateNoWindow = true,
                                UseShellExecute = false
                            };
                            System.Diagnostics.Process.Start(psi)?.WaitForExit(1000);
                        }
                        catch { }
                    }

                    Dispatcher.InvokeAsync(() =>
                    {
                        if (killedCount > 0)
                        {
                            Log($"[SUCCESS] GX Simulator 2 terminated ({killedCount} process(es) closed).");
                        }
                        else
                        {
                            Log("[INFO] GX Simulator 2 (SimManager.exe) closed.");
                        }
                        UpdateSimStatusDisplay(false);
                    });
                });
            }
            catch (Exception ex)
            {
                Log($"[ERROR] Failed to exit GX Simulator 2: {ex.Message}");
            }
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void MenuToggleLog_Click(object sender, RoutedEventArgs e)
        {
            TabAdvancedInspector.Focus();
        }

        private void MenuGuide_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "GX2 Bridge Guide:\n\n" +
                "1. Inputs X0-X7 can be toggled using switches in Tab 1.\n" +
                "2. Outputs Y0-Y7 display real-time PLC output states.\n" +
                "3. MC Protocol Servers: Server 1 (port 5000) and Server 2 (port 6000).\n" +
                "4. Ports auto-increment if conflict detected (e.g. 5000 -> 5001).\n",
                "User Guide", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void MenuCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            Mouse.OverrideCursor = Cursors.Wait;
            TxtUpdateStatus.Text = "Checking for updates...";
            TxtUpdateStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));
            UpdateStatusLed.Fill = new SolidColorBrush(Color.FromRgb(148, 163, 184));

            try
            {
                var result = await UpdateCheckerService.CheckForUpdatesAsync();
                if (result.Success)
                {
                    if (result.HasUpdate)
                    {
                        TxtUpdateStatus.Text = $"Update available ({result.LatestVersion})";
                        TxtUpdateStatus.Foreground = new SolidColorBrush(Color.FromRgb(2, 132, 199));
                        UpdateStatusLed.Fill = new SolidColorBrush(Color.FromRgb(2, 132, 199));

                        var answer = MessageBox.Show(
                            $"A new version of GX2 Bridge is available!\n\n" +
                            $"Current Version: v{UpdateCheckerService.CurrentVersion}\n" +
                            $"Latest Version: {result.LatestVersion}\n\n" +
                            $"Would you like to open the download page to update now?",
                            "Update Available - GX2 Bridge",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Information);

                        if (answer == MessageBoxResult.Yes)
                        {
                            try
                            {
                                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                                {
                                    FileName = result.ReleaseUrl,
                                    UseShellExecute = true
                                });
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        TxtUpdateStatus.Text = "No update available";
                        TxtUpdateStatus.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                        UpdateStatusLed.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));

                        MessageBox.Show(
                            $"You are using the latest version of GX2 Bridge (v{UpdateCheckerService.CurrentVersion}).",
                            "Check for Updates",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    }
                }
                else
                {
                    var answer = MessageBox.Show(
                        $"Unable to check for updates at this time:\n{result.ErrorMessage}\n\nWould you like to open the GitHub releases page manually?",
                        "Check for Updates",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);

                    if (answer == MessageBoxResult.Yes)
                    {
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = UpdateCheckerService.ReleasesPageUrl,
                                UseShellExecute = true
                            });
                        }
                        catch { }
                    }
                }
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        private void MenuWhatsNew_Click(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedItem = TabWhatsNew;
        }

        private void BtnViewReleasesGithub_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = UpdateCheckerService.ReleasesPageUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void MenuAbout_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show($"GX2 Bridge v{UpdateCheckerService.CurrentVersion}\nMitsubishi GX Works 2 Simulator Network Bridge\n\nDeveloped by Ismail Lowkey", "About GX2 Bridge", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        protected override void OnClosed(EventArgs e)
        {
            _pollTimer.Stop();
            _mcServer1?.Stop();
            _mcServer2?.Stop();
            _simEngine?.Dispose();
            base.OnClosed(e);
        }
    }
}
