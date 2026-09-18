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
                    Type? comType = Type.GetTypeFromProgID("ActUtlType.ActUtlType");
                    if (comType == null)
                    {
                        comType = Type.GetTypeFromProgID("ActUtlType.ActUtlType.1");
                    }

                    if (comType == null)
                    {
                        LastError = "ActUtlType COM component is not registered on this system.";
                        LogMessage?.Invoke($"[ERROR] {LastError}");
                        return false;
                    }

                    _comObject = Activator.CreateInstance(comType);
                    if (_comObject == null)
                    {
                        LastError = "Failed to create ActUtlType COM instance.";
                        LogMessage?.Invoke($"[ERROR] {LastError}");
                        return false;
                    }

                    _comObject.ActLogicalStationNumber = LogicalStationNumber;
                    int result = (int)_comObject.Open();

                    if (result == 0)
                    {
                        IsConnected = true;
                        LastError = string.Empty;
                        LogMessage?.Invoke($"[GX SIM] Connected to GX Simulator 2 (Station {LogicalStationNumber})");
                        if (MemoryMirror != null && MemoryMirror.IsActive)
                        {
                            MemoryMirror.Start();
                        }
                        return true;
                    }
                    else
                    {
                        IsConnected = false;
                        LastError = $"Open() failed with code 0x{result:X8}";
                        LogMessage?.Invoke($"[ERROR] {LastError}");
                        return false;
                    }
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
