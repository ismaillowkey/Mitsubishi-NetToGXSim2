using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NetToGXSim2.Core
{
    /// <summary>
    /// Mitsubishi GX Simulator 2 Engine Coordinator.
    /// Bridges MC Protocol Network requests directly to GX Simulator 2 (FXSimRun2 / QSimRun2 / SimManager)
    /// via GxSim2MemoryBridge with zero MX Component COM dependencies.
    /// </summary>
    public class GxSimulatorEngine : IDisposable
    {
        private readonly BlockingCollection<Action> _staWorkQueue = new BlockingCollection<Action>();
        private readonly Thread _staThread;
        private bool _disposed;

        public bool IsConnected { get; private set; }
        public int LogicalStationNumber { get; set; } = 1;
        public string LastError { get; private set; } = string.Empty;
        public string ConnectedEngineName { get; private set; } = string.Empty;

        public PlcMemoryMirror MemoryMirror { get; }
        public GxSim2MemoryBridge MemoryBridge { get; }

        public event Action<string>? LogMessage;
        public event Action<bool>? ConnectionStateChanged;

        public GxSimulatorEngine(int logicalStationNumber = 1)
        {
            LogicalStationNumber = logicalStationNumber;
            MemoryMirror = new PlcMemoryMirror(this);
            MemoryMirror.LogMessage += (msg) => { if (LogMessage != null) LogMessage(msg); };

            MemoryBridge = new GxSim2MemoryBridge();
            MemoryBridge.LogMessage += (msg) => { if (LogMessage != null) LogMessage(msg); };

            _staThread = new Thread(StaThreadLoop)
            {
                IsBackground = true,
                Name = "GxSimulator2_STA_Thread"
            };
            _staThread.SetApartmentState(ApartmentState.STA);
            _staThread.Start();
        }

        private void StaThreadLoop()
        {
            foreach (var action in _staWorkQueue.GetConsumingEnumerable())
            {
                try { action(); } catch { }
            }
        }

        private T RunOnSta<T>(Func<T> func)
        {
            if (Thread.CurrentThread == _staThread)
            {
                return func();
            }

            var tcs = new TaskCompletionSource<T>();
            _staWorkQueue.Add(() =>
            {
                try
                {
                    T result = func();
                    tcs.SetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            return tcs.Task.Result;
        }

        private void RunOnSta(Action action)
        {
            if (Thread.CurrentThread == _staThread)
            {
                action();
                return;
            }

            var tcs = new TaskCompletionSource<bool>();
            _staWorkQueue.Add(() =>
            {
                try
                {
                    action();
                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            });
            tcs.Task.Wait();
        }

        public bool Connect(int? stationNumber = null)
        {
            if (stationNumber.HasValue) LogicalStationNumber = stationNumber.Value;

            return RunOnSta(() =>
            {
                DisconnectInternal();
                try
                {
                    if (MemoryBridge.Attach())
                    {
                        IsConnected = true;
                        LastError = string.Empty;
                        ConnectedEngineName = MemoryBridge.TargetProcessName;
                        if (LogMessage != null) LogMessage("[GX SIM 2] Connected directly to " + ConnectedEngineName + " (Direct Native Engine)");
                        if (ConnectionStateChanged != null) ConnectionStateChanged(true);

                        if (MemoryMirror != null && MemoryMirror.IsActive)
                        {
                            MemoryMirror.Start();
                        }
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                }

                IsConnected = false;
                ConnectedEngineName = string.Empty;
                LastError = "Could not connect to GX Simulator 2. Make sure simulation is running in GX Works 2 / SimManager.";
                if (LogMessage != null) LogMessage("[WARNING] " + LastError);
                if (ConnectionStateChanged != null) ConnectionStateChanged(false);
                return false;
            });
        }

        public void Disconnect()
        {
            RunOnSta(() => DisconnectInternal());
        }

        private void DisconnectInternal()
        {
            if (IsConnected)
            {
                IsConnected = false;
                ConnectedEngineName = string.Empty;
                MemoryBridge.Detach();
                if (LogMessage != null) LogMessage("[GX SIM 2] Disconnected from GX Simulator 2");
                if (ConnectionStateChanged != null) ConnectionStateChanged(false);
            }
        }

        public bool CheckConnectionAlive()
        {
            if (!IsConnected) return false;
            if (MemoryBridge.IsAttached)
            {
                bool alive = MemoryBridge.CheckIsAlive();
                if (!alive)
                {
                    DisconnectInternal();
                    return false;
                }
                return true;
            }
            return IsConnected;
        }

        #region Device Read / Write Methods (Engine Interface)

        public int ReadDevice(string deviceName, out int value)
        {
            if (!IsConnected && !Connect()) { value = 0; return -1; }
            return MemoryBridge.ReadDevice(deviceName, out value);
        }

        public int WriteDevice(string deviceName, int value)
        {
            if (!IsConnected && !Connect()) return -1;
            return MemoryBridge.WriteDevice(deviceName, value);
        }

        public int ReadDeviceBlockWords(string deviceName, int count, out short[] data)
        {
            if (!IsConnected && !Connect()) { data = new short[count]; return -1; }
            return MemoryBridge.ReadDeviceBlockWords(deviceName, count, out data);
        }

        public int ReadRawBlockWords(string deviceName, int count, out short[] data)
        {
            return ReadDeviceBlockWords(deviceName, count, out data);
        }

        public int WriteDeviceBlockWords(string deviceName, int count, short[] data)
        {
            if (!IsConnected && !Connect()) return -1;
            return MemoryBridge.WriteDeviceBlockWords(deviceName, count, data);
        }

        public int ReadDeviceBlockBits(string deviceName, int count, out byte[] data)
        {
            if (!IsConnected && !Connect()) { data = new byte[count]; return -1; }
            return MemoryBridge.ReadDeviceBlockBits(deviceName, count, out data);
        }

        public int WriteDeviceBlockBits(string deviceName, int count, byte[] data)
        {
            if (!IsConnected && !Connect()) return -1;
            return MemoryBridge.WriteDeviceBlockBits(deviceName, count, data);
        }

        public static string StepDeviceName(string baseDevice, int offset)
        {
            string s = baseDevice.Trim().ToUpper();
            int numIdx = 0;
            while (numIdx < s.Length && !char.IsDigit(s[numIdx])) numIdx++;

            if (numIdx > 0 && numIdx < s.Length)
            {
                string dev = s.Substring(0, numIdx);
                string rest = s.Substring(numIdx);

                if (dev == "X" || dev == "Y")
                {
                    try
                    {
                        int dec = Convert.ToInt32(rest, 8) + offset;
                        return dev + Convert.ToString(dec, 8);
                    }
                    catch
                    {
                        int.TryParse(rest, out int a);
                        return dev + (a + offset);
                    }
                }
                else if (dev == "W" || dev == "B")
                {
                    try
                    {
                        int hex = Convert.ToInt32(rest, 16) + offset;
                        return dev + Convert.ToString(hex, 16).ToUpper();
                    }
                    catch
                    {
                        int.TryParse(rest, out int a);
                        return dev + (a + offset);
                    }
                }
                else
                {
                    int.TryParse(rest, out int addr);
                    return dev + (addr + offset);
                }
            }
            return baseDevice;
        }

        #endregion

        public void Dispose()
        {
            if (!_disposed)
            {
                Disconnect();
                MemoryBridge.Dispose();
                _staWorkQueue.CompleteAdding();
                _disposed = true;
            }
        }
    }
}
