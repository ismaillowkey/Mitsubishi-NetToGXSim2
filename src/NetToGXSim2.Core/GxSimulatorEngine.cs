using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace NetToGXSim2.Core
{
    public class GxSimulatorEngine : IDisposable
    {
        private dynamic? _comObject;
        private readonly BlockingCollection<Action> _staWorkQueue = new BlockingCollection<Action>();
        private readonly Thread _staThread;
        private bool _disposed;

        public bool IsConnected { get; private set; }
        public int LogicalStationNumber { get; set; } = 1;
        public string LastError { get; private set; } = string.Empty;

        public PlcMemoryMirror MemoryMirror { get; }

        public event Action<string>? LogMessage;

        public GxSimulatorEngine(int logicalStationNumber = 1)
        {
            LogicalStationNumber = logicalStationNumber;
            MemoryMirror = new PlcMemoryMirror(this);
            MemoryMirror.LogMessage += (msg) => LogMessage?.Invoke(msg);

            _staThread = new Thread(StaThreadLoop)
            {
                IsBackground = true,
                Name = "GxSimulator_STA_Thread"
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
                    // 1. Direct GX Simulator 2 Connection (Zero-Configuration, direct engine probe)
                    Type? progType = Type.GetTypeFromProgID("ActProgType.ActProgType")
                                  ?? Type.GetTypeFromProgID("ActProgType.ActProgType.1")
                                  ?? Type.GetTypeFromProgID("ActProgType.ActMLProgType")
                                  ?? Type.GetTypeFromProgID("ActProgType64.ActProgWrap");

                    if (progType != null)
                    {
                        dynamic? prog = Activator.CreateInstance(progType);
                        if (prog != null)
                        {
                            // Unit Types:
                            // 0x1A = UNIT_GX_WORKS2_SIM (Direct GX Works 2 / GX Simulator 2 Engine Interface)
                            // 0x0D = UNIT_GPPW_GXSIM (Direct GX Developer / GX Simulator 1 Interface)
                            // 0x30 = UNIT_GX_WORKS3_SIM (Direct GX Works 3 Simulator Interface)
                            // 0x01 = UNIT_CPU
                            int[] unitTypes = new int[] { 0x1A, 0x0D, 0x30, 0x01 };

                            // CPU Types for FX and Q Series:
                            // 0x0204 = FX3U/FX3UC
                            // 0x0205 = FX3G/FX3GC
                            // 0x0206 = FX3S
                            // 0x0203 = FX2N/FX2NC
                            // 0x0202 = FX1N/FX1NC
                            // 0x0201 = FX0N/FX1
                            // 0x0210 = FX5U
                            // 0x0212 = FX5UJ
                            // 0x00A0 = Q00J
                            // 0x00A1 = Q00
                            // 0x00A2 = Q01
                            // 0x0090 = Q02
                            int[] cpuTypes = new int[] { 0x0204, 0x0205, 0x0206, 0x0203, 0x0202, 0x0201, 0x0210, 0x0212, 0x00A0, 0x00A1, 0x0090 };

                            foreach (int u in unitTypes)
                            {
                                foreach (int cpu in cpuTypes)
                                {
                                    for (int sim = 0; sim <= 1; sim++)
                                    {
                                        try
                                        {
                                            prog.ActUnitType = u;
                                            prog.ActCpuType = cpu;
                                            prog.ActTargetSimulator = sim;
                                            prog.ActProtocolType = 0;
                                            prog.ActPortNumber = 0;
                                            prog.ActTimeOut = 1500;

                                            int res = (int)prog.Open();
                                            if (res == 0)
                                            {
                                                _comObject = prog;
                                                IsConnected = true;
                                                LastError = string.Empty;
                                                LogMessage?.Invoke($"[GX SIM] Connected directly to GX Simulator 2 (Direct Mode: Unit=0x{u:X2}, CPU=0x{cpu:X4})");
                                                if (MemoryMirror != null && MemoryMirror.IsActive)
                                                {
                                                    MemoryMirror.Start();
                                                }
                                                return true;
                                            }
                                        }
                                        catch { }
                                    }
                                }
                            }

                            // If direct probe didn't connect, release prog instance
                            try { Marshal.ReleaseComObject(prog); } catch { }
                        }
                    }

                    // 2. Fallback to ActUtlType (Logical Station Mode)
                    Type? utlType = Type.GetTypeFromProgID("ActUtlType.ActUtlType")
                                 ?? Type.GetTypeFromProgID("ActUtlType.ActUtlType.1")
                                 ?? Type.GetTypeFromProgID("ActUtlType.ActMLUtlType")
                                 ?? Type.GetTypeFromProgID("ActUtlType64.ActUtlWrap");

                    if (utlType != null)
                    {
                        dynamic? utl = Activator.CreateInstance(utlType);
                        if (utl != null)
                        {
                            int[] stationsToTry = new int[] { LogicalStationNumber, 1, 2, 3, 4, 5 };
                            foreach (int st in stationsToTry)
                            {
                                try
                                {
                                    utl.ActLogicalStationNumber = st;
                                    int res = (int)utl.Open();
                                    if (res == 0)
                                    {
                                        _comObject = utl;
                                        LogicalStationNumber = st;
                                        IsConnected = true;
                                        LastError = string.Empty;
                                        LogMessage?.Invoke($"[GX SIM] Connected to GX Simulator 2 (Station {st})");
                                        if (MemoryMirror != null && MemoryMirror.IsActive)
                                        {
                                            MemoryMirror.Start();
                                        }
                                        return true;
                                    }
                                }
                                catch { }
                            }
                            try { Marshal.ReleaseComObject(utl); } catch { }
                        }
                    }

                    IsConnected = false;
                    LastError = "Could not connect to GX Simulator 2. Make sure simulation is running in GX Works 2.";
                    LogMessage?.Invoke($"[WARNING] {LastError}");
                    return false;
                }
                catch (Exception ex)
                {
                    IsConnected = false;
                    LastError = ex.Message;
                    LogMessage?.Invoke($"[ERROR] Connection exception: {ex.Message}");
                    return false;
                }
            });
        }

        public void Disconnect()
        {
            RunOnSta(() => DisconnectInternal());
        }

        private void DisconnectInternal()
        {
            MemoryMirror?.Stop();

            if (_comObject != null && IsConnected)
            {
                try
                {
                    _comObject.Close();
                }
                catch { }
            }

            if (_comObject != null)
            {
                try { Marshal.ReleaseComObject(_comObject); } catch { }
                _comObject = null;
            }

            IsConnected = false;
            LogMessage?.Invoke("[GX SIM] Disconnected from GX Simulator");
        }

        /// <summary>
        /// Direct COM block read used by PlcMemoryMirror background sync loop.
        /// </summary>
        public int ReadRawBlockWords(string deviceName, int count, out short[] data)
        {
            short[] outData = new short[count];
            int res = RunOnSta(() =>
            {
                if (!IsConnected || _comObject == null) return -1;
                try
                {
                    return (int)_comObject.ReadDeviceBlock2(deviceName, count, ref outData[0]);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    return -1;
                }
            });
            data = outData;
            return res;
        }

        public int ReadDevice(string deviceName, out int value)
        {
            if (MemoryMirror != null && MemoryMirror.IsActive && MemoryMirror.TryReadSingle(deviceName, out value))
            {
                return 0;
            }

            int outVal = 0;
            int res = RunOnSta(() =>
            {
                if (!IsConnected || _comObject == null) return -1;
                try
                {
                    int v = 0;
                    int r = (int)_comObject.GetDevice(deviceName, out v);
                    if (r == 0) outVal = v;
                    return r;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    return -1;
                }
            });
            value = outVal;
            return res;
        }

        public int WriteDevice(string deviceName, int value)
        {
            int res = RunOnSta(() =>
            {
                if (!IsConnected || _comObject == null) return -1;
                try
                {
                    return (int)_comObject.SetDevice(deviceName, value);
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    return -1;
                }
            });

            if (res == 0)
            {
                MemoryMirror?.NotifyWrite(deviceName, value);
            }
            return res;
        }

        /// <summary>
        /// Reads word registers (e.g. D0-D7999) in optimal 256-word chunks for maximum throughput.
        /// Served instantly from MemoryMirror if the requested range is cached.
        /// </summary>
        public int ReadDeviceBlockWords(string deviceName, int count, out short[] data)
        {
            if (MemoryMirror != null && MemoryMirror.IsActive && MemoryMirror.TryReadWords(deviceName, count, out data))
            {
                return 0;
            }

            short[] outData = new short[count];
            int res = RunOnSta(() =>
            {
                if (!IsConnected || _comObject == null) return -1;
                try
                {
                    const int maxChunk = 256;
                    int offset = 0;
                    while (offset < count)
                    {
                        int chunkSize = Math.Min(maxChunk, count - offset);
                        string chunkDevice = StepDeviceName(deviceName, offset);

                        int r = (int)_comObject.ReadDeviceBlock2(chunkDevice, chunkSize, ref outData[offset]);
                        if (r != 0)
                        {
                            for (int k = 0; k < chunkSize; k++)
                            {
                                string dev = StepDeviceName(chunkDevice, k);
                                int err = ReadDevice(dev, out int v);
                                if (err == 0) outData[offset + k] = (short)v;
                            }
                        }
                        offset += chunkSize;
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    return -1;
                }
            });
            data = outData;
            return res;
        }

        /// <summary>
        /// Writes word registers in optimal 256-word chunks for maximum speed.
        /// </summary>
        public int WriteDeviceBlockWords(string deviceName, int count, short[] data)
        {
            int res = RunOnSta(() =>
            {
                if (!IsConnected || _comObject == null) return -1;
                try
                {
                    const int maxChunk = 256;
                    int offset = 0;
                    while (offset < count)
                    {
                        int chunkSize = Math.Min(maxChunk, count - offset);
                        string chunkDevice = StepDeviceName(deviceName, offset);

                        int r = (int)_comObject.WriteDeviceBlock2(chunkDevice, chunkSize, ref data[offset]);
                        if (r != 0)
                        {
                            for (int k = 0; k < chunkSize; k++)
                            {
                                string dev = StepDeviceName(chunkDevice, k);
                                WriteDevice(dev, data[offset + k]);
                            }
                        }
                        offset += chunkSize;
                    }
                    return 0;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    return -1;
                }
            });

            if (res == 0)
            {
                MemoryMirror?.NotifyWriteWords(deviceName, count, data);
            }
            return res;
        }

        /// <summary>
        /// Reads bit devices (e.g. M0-M7999, X0-X377, Y0-Y377) in BATCH using 256-word block reads (4096 bits per COM call).
        /// Served instantly from MemoryMirror if the requested range is cached.
        /// </summary>
        public int ReadDeviceBlockBits(string deviceName, int count, out byte[] bitValues)
        {
            if (MemoryMirror != null && MemoryMirror.IsActive && MemoryMirror.TryReadBits(deviceName, count, out bitValues))
            {
                return 0;
            }

            byte[] outBits = new byte[count];
            int res = RunOnSta(() =>
            {
                if (!IsConnected || _comObject == null) return -1;

                int totalWordCount = (count + 15) / 16;
                const int maxChunkWords = 256;

                int bitOffset = 0;
                int wordOffset = 0;

                while (wordOffset < totalWordCount)
                {
                    int chunkWords = Math.Min(maxChunkWords, totalWordCount - wordOffset);
                    int chunkBits = Math.Min(chunkWords * 16, count - bitOffset);
                    string chunkDevice = StepDeviceName(deviceName, bitOffset);

                    try
                    {
                        short[] wordBuffer = new short[chunkWords];
                        int r = (int)_comObject.ReadDeviceBlock2(chunkDevice, chunkWords, ref wordBuffer[0]);

                        if (r == 0)
                        {
                            for (int b = 0; b < chunkBits; b++)
                            {
                                int wIdx = b / 16;
                                int bPos = b % 16;
                                if (wIdx < wordBuffer.Length)
                                {
                                    int w = (ushort)wordBuffer[wIdx];
                                    outBits[bitOffset + b] = (byte)((w >> bPos) & 0x01);
                                }
                            }
                        }
                        else
                        {
                            for (int b = 0; b < chunkBits; b++)
                            {
                                string dev = StepDeviceName(chunkDevice, b);
                                if (ReadDevice(dev, out int v) == 0)
                                {
                                    outBits[bitOffset + b] = (byte)(v != 0 ? 1 : 0);
                                }
                            }
                        }
                    }
                    catch
                    {
                        for (int b = 0; b < chunkBits; b++)
                        {
                            string dev = StepDeviceName(chunkDevice, b);
                            if (ReadDevice(dev, out int v) == 0)
                            {
                                outBits[bitOffset + b] = (byte)(v != 0 ? 1 : 0);
                            }
                        }
                    }

                    wordOffset += chunkWords;
                    bitOffset += chunkBits;
                }

                return 0;
            });

            bitValues = outBits;
            return res;
        }

        /// <summary>
        /// Writes bit devices in BATCH using word block writes in optimal 256-word chunks.
        /// </summary>
        public int WriteDeviceBlockBits(string deviceName, int count, byte[] bitValues)
        {
            int res = RunOnSta(() =>
            {
                if (!IsConnected || _comObject == null) return -1;

                int totalWordCount = (count + 15) / 16;
                const int maxChunkWords = 256;

                int bitOffset = 0;
                int wordOffset = 0;

                while (wordOffset < totalWordCount)
                {
                    int chunkWords = Math.Min(maxChunkWords, totalWordCount - wordOffset);
                    int chunkBits = Math.Min(chunkWords * 16, count - bitOffset);
                    string chunkDevice = StepDeviceName(deviceName, bitOffset);

                    short[] wordBuffer = new short[chunkWords];
                    for (int b = 0; b < chunkBits; b++)
                    {
                        if (bitValues[bitOffset + b] != 0)
                        {
                            int wIdx = b / 16;
                            int bPos = b % 16;
                            wordBuffer[wIdx] |= (short)(1 << bPos);
                        }
                    }

                    try
                    {
                        int r = (int)_comObject.WriteDeviceBlock2(chunkDevice, chunkWords, ref wordBuffer[0]);
                        if (r != 0)
                        {
                            for (int b = 0; b < chunkBits; b++)
                            {
                                string dev = StepDeviceName(chunkDevice, b);
                                WriteDevice(dev, bitValues[bitOffset + b] != 0 ? 1 : 0);
                            }
                        }
                    }
                    catch
                    {
                        for (int b = 0; b < chunkBits; b++)
                        {
                            string dev = StepDeviceName(chunkDevice, b);
                            WriteDevice(dev, bitValues[bitOffset + b] != 0 ? 1 : 0);
                        }
                    }

                    wordOffset += chunkWords;
                    bitOffset += chunkBits;
                }

                return 0;
            });

            if (res == 0)
            {
                MemoryMirror?.NotifyWriteBits(deviceName, count, bitValues);
            }
            return res;
        }

        public static string StepDeviceName(string baseDevice, int offset)
        {
            if (string.IsNullOrEmpty(baseDevice)) return baseDevice;
            string prefix = baseDevice.Substring(0, 1);

            if (baseDevice.Length > 2 && char.IsLetter(baseDevice[1]))
            {
                prefix = baseDevice.Substring(0, 2);
            }

            string numPartStr = baseDevice.Substring(prefix.Length);
            bool isOctal = prefix.Equals("X", StringComparison.OrdinalIgnoreCase) || prefix.Equals("Y", StringComparison.OrdinalIgnoreCase);

            if (isOctal)
            {
                try
                {
                    int decVal = Convert.ToInt32(numPartStr, 8) + offset;
                    return prefix + Convert.ToString(decVal, 8);
                }
                catch
                {
                    return baseDevice;
                }
            }
            else
            {
                if (int.TryParse(numPartStr, out int decVal))
                {
                    return prefix + (decVal + offset);
                }
            }
            return baseDevice;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Disconnect();
            _staWorkQueue.CompleteAdding();
        }
    }
}
