using System;
using System.Threading;

namespace NetToGXSim2.Core
{
    /// <summary>
    /// In-Memory Cache Mirror for ultra-fast (sub-millisecond) reads from Mitsubishi GX Simulator 2.
    /// Continuously synchronizes standard PLC device ranges in the background so external clients
    /// (e.g. Unity, IoTClient, SCADA) get instantaneous responses without COM overhead.
    /// </summary>
    public class PlcMemoryMirror : IDisposable
    {
        private readonly GxSimulatorEngine _engine;
        private readonly Thread _syncThread;
        private volatile bool _isRunning = false;
        private bool _disposed;

        // Mirrored Memory Buffers (Thread-safe read/write)
        private readonly byte[] _xBits = new byte[256];   // X0 - X377 (Octal)
        private readonly byte[] _yBits = new byte[256];   // Y0 - Y377 (Octal)
        private readonly byte[] _mBits = new byte[2048];  // M0 - M2047
        private readonly short[] _dWords = new short[1024]; // D0 - D1023

        private readonly object _syncLock = new object();

        public bool IsActive { get; set; } = false;
        public int PollingIntervalMs { get; set; } = 10;
        public long CyclesCompleted { get; private set; }

        public event Action<string>? LogMessage;

        public PlcMemoryMirror(GxSimulatorEngine engine)
        {
            _engine = engine;
            _syncThread = new Thread(SyncLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "NetToGXSim2_MemoryMirror_Thread"
            };
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;
            _syncThread.Start();
            LogMessage?.Invoke("[MIRROR] In-Memory Cache Mirror started (10ms cycle)");
        }

        public void Stop()
        {
            _isRunning = false;
        }

        private void SyncLoop()
        {
            while (_isRunning)
            {
                if (!IsActive || !_engine.IsConnected)
                {
                    Thread.Sleep(50);
                    continue;
                }

                try
                {
                    // 1. Refresh Inputs X0-X177 (128 bits = 8 words)
                    if (_engine.ReadRawBlockWords("X0", 8, out short[] xWords) == 0 && xWords != null)
                    {
                        lock (_syncLock)
                        {
                            for (int w = 0; w < xWords.Length; w++)
                            {
                                int val = (ushort)xWords[w];
                                for (int b = 0; b < 16; b++)
                                {
                                    int bitIdx = w * 16 + b;
                                    if (bitIdx < _xBits.Length)
                                    {
                                        _xBits[bitIdx] = (byte)((val >> b) & 0x01);
                                    }
                                }
                            }
                        }
                    }

                    // 2. Refresh Outputs Y0-Y177 (128 bits = 8 words)
                    if (_engine.ReadRawBlockWords("Y0", 8, out short[] yWords) == 0 && yWords != null)
                    {
                        lock (_syncLock)
                        {
                            for (int w = 0; w < yWords.Length; w++)
                            {
                                int val = (ushort)yWords[w];
                                for (int b = 0; b < 16; b++)
                                {
                                    int bitIdx = w * 16 + b;
                                    if (bitIdx < _yBits.Length)
                                    {
                                        _yBits[bitIdx] = (byte)((val >> b) & 0x01);
                                    }
                                }
                            }
                        }
                    }

                    // 3. Refresh Relays M0-M1023 (1024 bits = 64 words)
                    if (_engine.ReadRawBlockWords("M0", 64, out short[] mWords) == 0 && mWords != null)
                    {
                        lock (_syncLock)
                        {
                            for (int w = 0; w < mWords.Length; w++)
                            {
                                int val = (ushort)mWords[w];
                                for (int b = 0; b < 16; b++)
                                {
                                    int bitIdx = w * 16 + b;
                                    if (bitIdx < _mBits.Length)
                                    {
                                        _mBits[bitIdx] = (byte)((val >> b) & 0x01);
                                    }
                                }
                            }
                        }
                    }

                    // 4. Refresh Registers D0-D511 (2x 256 words)
                    if (_engine.ReadRawBlockWords("D0", 256, out short[] dWords0) == 0 && dWords0 != null)
                    {
                        lock (_syncLock)
                        {
                            Array.Copy(dWords0, 0, _dWords, 0, Math.Min(dWords0.Length, 256));
                        }
                    }

                    if (_engine.ReadRawBlockWords("D256", 256, out short[] dWords1) == 0 && dWords1 != null)
                    {
                        lock (_syncLock)
                        {
                            Array.Copy(dWords1, 0, _dWords, 256, Math.Min(dWords1.Length, 256));
                        }
                    }

                    CyclesCompleted++;
                }
                catch { }

                Thread.Sleep(PollingIntervalMs);
            }
        }

        public bool TryReadBits(string deviceName, int count, out byte[] bitValues)
        {
            bitValues = new byte[count];
            if (!IsActive) return false;

            ParseDevice(deviceName, out string prefix, out int startAddr, out bool isOctal);

            lock (_syncLock)
            {
                if (prefix == "X" && startAddr + count <= _xBits.Length)
                {
                    Array.Copy(_xBits, startAddr, bitValues, 0, count);
                    return true;
                }

                if (prefix == "Y" && startAddr + count <= _yBits.Length)
                {
                    Array.Copy(_yBits, startAddr, bitValues, 0, count);
                    return true;
                }

                if (prefix == "M" && startAddr + count <= _mBits.Length)
                {
                    Array.Copy(_mBits, startAddr, bitValues, 0, count);
                    return true;
                }
            }

            return false;
        }

        public bool TryReadWords(string deviceName, int count, out short[] data)
        {
            data = new short[count];
            if (!IsActive) return false;

            ParseDevice(deviceName, out string prefix, out int startAddr, out _);

            if (prefix == "D" && startAddr + count <= _dWords.Length)
            {
                lock (_syncLock)
                {
                    Array.Copy(_dWords, startAddr, data, 0, count);
                }
                return true;
            }

            return false;
        }

        public bool TryReadSingle(string deviceName, out int value)
        {
            value = 0;
            if (!IsActive) return false;

            ParseDevice(deviceName, out string prefix, out int startAddr, out bool isOctal);

            lock (_syncLock)
            {
                if (prefix == "X" && startAddr < _xBits.Length)
                {
                    value = _xBits[startAddr];
                    return true;
                }
                if (prefix == "Y" && startAddr < _yBits.Length)
                {
                    value = _yBits[startAddr];
                    return true;
                }
                if (prefix == "M" && startAddr < _mBits.Length)
                {
                    value = _mBits[startAddr];
                    return true;
                }
                if (prefix == "D" && startAddr < _dWords.Length)
                {
                    value = _dWords[startAddr];
                    return true;
                }
            }

            return false;
        }

        public void NotifyWrite(string deviceName, int value)
        {
            ParseDevice(deviceName, out string prefix, out int startAddr, out _);
            lock (_syncLock)
            {
                if (prefix == "X" && startAddr < _xBits.Length) _xBits[startAddr] = (byte)(value != 0 ? 1 : 0);
                else if (prefix == "Y" && startAddr < _yBits.Length) _yBits[startAddr] = (byte)(value != 0 ? 1 : 0);
                else if (prefix == "M" && startAddr < _mBits.Length) _mBits[startAddr] = (byte)(value != 0 ? 1 : 0);
                else if (prefix == "D" && startAddr < _dWords.Length) _dWords[startAddr] = (short)value;
            }
        }

        public void NotifyWriteWords(string deviceName, int count, short[] data)
        {
            ParseDevice(deviceName, out string prefix, out int startAddr, out _);
            if (prefix == "D" && startAddr < _dWords.Length)
            {
                lock (_syncLock)
                {
                    int copyLen = Math.Min(count, _dWords.Length - startAddr);
                    Array.Copy(data, 0, _dWords, startAddr, copyLen);
                }
            }
        }

        public void NotifyWriteBits(string deviceName, int count, byte[] bitValues)
        {
            ParseDevice(deviceName, out string prefix, out int startAddr, out _);
            lock (_syncLock)
            {
                byte[] target = prefix == "X" ? _xBits : prefix == "Y" ? _yBits : prefix == "M" ? _mBits : null!;
                if (target != null && startAddr < target.Length)
                {
                    int copyLen = Math.Min(count, target.Length - startAddr);
                    for (int i = 0; i < copyLen; i++)
                    {
                        target[startAddr + i] = (byte)(bitValues[i] != 0 ? 1 : 0);
                    }
                }
            }
        }

        private static void ParseDevice(string deviceName, out string prefix, out int addr, out bool isOctal)
        {
            prefix = string.Empty;
            addr = 0;
            isOctal = false;

            if (string.IsNullOrEmpty(deviceName)) return;

            prefix = deviceName.Substring(0, 1).ToUpperInvariant();
            if (deviceName.Length > 2 && char.IsLetter(deviceName[1]))
            {
                prefix = deviceName.Substring(0, 2).ToUpperInvariant();
            }

            string numPart = deviceName.Substring(prefix.Length);
            isOctal = prefix == "X" || prefix == "Y";

            if (isOctal)
            {
                try { addr = Convert.ToInt32(numPart, 8); } catch { addr = 0; }
            }
            else
            {
                int.TryParse(numPart, out addr);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop();
        }
    }
}
