using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace NetToGXSim2.Core
{
    /// <summary>
    /// High-Performance Native Direct Process Memory Bridge for Mitsubishi GX Simulator 2 (FXSimRun2 / QSimRun2 / SimManager).
    /// Communicates directly with the virtual CPU process address space via OpenProcess / ReadProcessMemory / WriteProcessMemory
    /// with zero MX Component dependency, zero COM registered DLL locks, and sub-microsecond latency.
    /// Pure On-Demand Passthrough: Only reads and writes the exact requested memory ranges when an MC client queries them.
    /// </summary>
    public class GxSim2MemoryBridge : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out int lpNumberOfBytesRead);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer, int nSize, out int lpNumberOfBytesWritten);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const int PROCESS_VM_READ = 0x0010;
        private const int PROCESS_VM_WRITE = 0x0020;
        private const int PROCESS_VM_OPERATION = 0x0008;

        // Pointer to Master Virtual PLC Heap Structure in FXSimRun2.exe
        private const long MASTER_PTR_VA = 0x0051DFAC;

        // Base Word Offsets relative to Master Pointer (from live FXSimRun2 Device Table at VA 0x00442A70)
        // Multiply by 2 to get Byte Offsets
        private const long WORD_OFF_X   = 0x0000;    // Input Relays X (Octal 16-bit words) -> Byte 0x0000
        private const long WORD_OFF_Y   = 0x0010;    // Output Relays Y (Octal 16-bit words) -> Byte 0x0020
        private const long WORD_OFF_PY  = 0x0020;    // Pre-output / Force latch Y -> Byte 0x0040
        private const long WORD_OFF_M   = 0x0030;    // Internal Relays M (Decimal 16-bit words) -> Byte 0x0060
        private const long WORD_OFF_PM  = 0x0210;    // Internal Relays PM -> Byte 0x0420
        private const long WORD_OFF_SM  = 0x03F0;    // Special Relays SM (Decimal 16-bit words) -> Byte 0x07E0
        private const long WORD_OFF_PSM = 0x0410;    // Special Relays PSM -> Byte 0x0820
        private const long WORD_OFF_S   = 0x0430;    // State Relays S (Decimal 16-bit words) -> Byte 0x0860
        private const long WORD_OFF_TI  = 0x0540;    // Timer Contacts TI / TS -> Byte 0x0A80
        private const long WORD_OFF_TO  = 0x0570;    // Timer Coils TO / TC -> Byte 0x0AE0
        private const long WORD_OFF_TN  = 0x05A0;    // Timer Current Values TN -> Byte 0x0B40
        private const long WORD_OFF_CI  = 0x0B00;    // Counter Contacts CI / CS -> Byte 0x1600
        private const long WORD_OFF_CO  = 0x0B10;    // Counter Coils CO / CC -> Byte 0x1620
        private const long WORD_OFF_CN  = 0x0B20;    // Counter Current Values CN (16-bit C0-C199) -> Byte 0x1640
        private const long WORD_OFF_CHN = 0x0BE8;    // Counter 32-bit Values CHN (32-bit C200-C255, 2 words per counter) -> Byte 0x17D0
        private const long WORD_OFF_D   = 0x1000;    // Data Registers D (Decimal 16-bit words D0-D8191) -> Byte 0x2000
        private const long WORD_OFF_SD  = 0x3000;    // Special Data Registers SD (SD0-SD255) -> Byte 0x6000
        private const long WORD_OFF_R   = 0x44E00;   // File Registers R (282,112 words = 564,224 bytes, R0-R6999) -> Byte 0x89C00
        private const long WORD_OFF_W   = 0x4800;    // Link Registers W -> Byte 0x9000
        private const long WORD_OFF_B   = 0x4700;    // Link Relays B -> Byte 0x8E00

        private IntPtr _hProcess = IntPtr.Zero;
        private int _trackedProcessId = 0;
        private string _processName = "FXSimRun2 (FX3U/FX3G/FX3S)";
        private long _cachedMasterPtr = 0;
        private bool _disposed;

        public bool IsAttached
        {
            get { return _hProcess != IntPtr.Zero && _trackedProcessId > 0; }
        }

        public string TargetProcessName
        {
            get { return _processName; }
        }

        public event Action<string>? LogMessage;

        public bool Attach()
        {
            if (IsAttached)
            {
                try
                {
                    Process p = Process.GetProcessById(_trackedProcessId);
                    if (!p.HasExited)
                    {
                        if (GetMasterPtr() != 0) return true;
                    }
                }
                catch
                {
                    Detach();
                }
            }

            Process? targetProc = null;
            Process[] fxProcs = Process.GetProcessesByName("FXSimRun2");
            if (fxProcs.Length > 0)
            {
                // Always sort by StartTime descending to pick the newest active simulator instance
                Array.Sort(fxProcs, delegate(Process p1, Process p2)
                {
                    try { return p2.StartTime.CompareTo(p1.StartTime); }
                    catch { return 0; }
                });
                targetProc = fxProcs[0];
                _processName = "FXSimRun2 (FX3U/FX3G/FX3S)";
            }
            else
            {
                Process[] qProcs = Process.GetProcessesByName("QSimRun2");
                if (qProcs.Length > 0)
                {
                    Array.Sort(qProcs, delegate(Process p1, Process p2)
                    {
                        try { return p2.StartTime.CompareTo(p1.StartTime); }
                        catch { return 0; }
                    });
                    targetProc = qProcs[0];
                    _processName = "QSimRun2 (Q-Series)";
                }
                else
                {
                    Process[] aProcs = Process.GetProcessesByName("ASimRun2");
                    if (aProcs.Length > 0)
                    {
                        Array.Sort(aProcs, delegate(Process p1, Process p2)
                        {
                            try { return p2.StartTime.CompareTo(p1.StartTime); }
                            catch { return 0; }
                        });
                        targetProc = aProcs[0];
                        _processName = "ASimRun2 (A-Series)";
                    }
                    else
                    {
                        Process[] smgrProcs = Process.GetProcessesByName("SimManager");
                        if (smgrProcs.Length > 0)
                        {
                            targetProc = smgrProcs[0];
                            _processName = "SimManager (GX Simulator 2)";
                        }
                    }
                }
            }

            if (targetProc == null) return false;

            IntPtr handle = OpenProcess(PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION, false, targetProc.Id);
            if (handle == IntPtr.Zero)
            {
                if (LogMessage != null) LogMessage("[DIRECT ENGINE] Failed to open process handle for " + targetProc.ProcessName + " (PID: " + targetProc.Id + "). Win32 Error: " + Marshal.GetLastWin32Error());
                return false;
            }

            _hProcess = handle;
            _trackedProcessId = targetProc.Id;
            _cachedMasterPtr = 0;

            long master = GetMasterPtr();
            if (LogMessage != null)
            {
                LogMessage(string.Format("[DIRECT ENGINE] Attached directly to {0} (PID: {1}) - Live Memory Base: 0x{2:X8}",
                    targetProc.ProcessName, targetProc.Id, master));
            }
            return true;
        }

        public void Detach()
        {
            if (_hProcess != IntPtr.Zero)
            {
                CloseHandle(_hProcess);
                _hProcess = IntPtr.Zero;
            }
            _trackedProcessId = 0;
            _cachedMasterPtr = 0;
        }

        public bool CheckIsAlive()
        {
            if (!IsAttached) return false;
            try
            {
                Process p = Process.GetProcessById(_trackedProcessId);
                return !p.HasExited;
            }
            catch
            {
                Detach();
                return false;
            }
        }

        private long GetMasterPtr()
        {
            if (_hProcess == IntPtr.Zero) return 0;

            byte[] buf = new byte[4];
            int read;
            if (ReadProcessMemory(_hProcess, (IntPtr)MASTER_PTR_VA, buf, 4, out read) && read == 4)
            {
                int ptr = BitConverter.ToInt32(buf, 0);
                if (ptr != 0)
                {
                    _cachedMasterPtr = (uint)ptr;
                    return _cachedMasterPtr;
                }
            }
            return _cachedMasterPtr;
        }

        #region Single Device Access (ReadDevice / WriteDevice)

        public int ReadDevice(string deviceName, out int value)
        {
            value = 0;
            ParseDeviceCode(deviceName, out string type, out int addr);

            if (Is32BitDevice(type, addr))
            {
                if (ReadDWord(type, addr, out uint dwVal))
                {
                    value = (int)dwVal;
                    return 0;
                }
            }
            else if (IsWordDevice(type))
            {
                if (ReadWord(type, addr, out ushort wVal))
                {
                    value = wVal;
                    return 0;
                }
            }
            else
            {
                if (ReadBit(type, addr, out bool bVal))
                {
                    value = bVal ? 1 : 0;
                    return 0;
                }
            }
            return -1;
        }

        public int WriteDevice(string deviceName, int value)
        {
            ParseDeviceCode(deviceName, out string type, out int addr);

            if (Is32BitDevice(type, addr))
            {
                return WriteDWord(type, addr, (uint)value) ? 0 : -1;
            }
            else if (IsWordDevice(type))
            {
                return WriteWord(type, addr, (ushort)value) ? 0 : -1;
            }
            else
            {
                return WriteBit(type, addr, value != 0) ? 0 : -1;
            }
        }

        #endregion

        #region Block Methods (ReadDeviceBlockWords / ReadDeviceBlockBits)

        public int ReadDeviceBlockWords(string deviceName, int count, out short[] data)
        {
            data = new short[count];
            ParseDeviceCode(deviceName, out string type, out int addr);

            ushort[] uBuf = new ushort[count];
            if (ReadWords(type, addr, uBuf))
            {
                for (int i = 0; i < count; i++) data[i] = (short)uBuf[i];
                return 0;
            }
            return -1;
        }

        public int WriteDeviceBlockWords(string deviceName, int count, short[] data)
        {
            if (data == null || data.Length < count) return -1;
            ParseDeviceCode(deviceName, out string type, out int addr);

            ushort[] uBuf = new ushort[count];
            for (int i = 0; i < count; i++) uBuf[i] = (ushort)data[i];

            return WriteWords(type, addr, uBuf) ? 0 : -1;
        }

        /// <summary>
        /// Reads a block of digital bits on demand. Returns an array of exactly 'count' bytes where each element is 0 or 1.
        /// </summary>
        public int ReadDeviceBlockBits(string deviceName, int count, out byte[] data)
        {
            data = new byte[count];
            ParseDeviceCode(deviceName, out string type, out int addr);

            bool[] bBuf = new bool[count];
            if (ReadBits(type, addr, bBuf))
            {
                for (int i = 0; i < count; i++)
                {
                    data[i] = (byte)(bBuf[i] ? 1 : 0);
                }
                return 0;
            }
            return -1;
        }

        /// <summary>
        /// Writes a block of digital bits on demand. Expects an array of at least 'count' bytes where each element is 0 or 1.
        /// </summary>
        public int WriteDeviceBlockBits(string deviceName, int count, byte[] data)
        {
            if (data == null || data.Length < count) return -1;
            ParseDeviceCode(deviceName, out string type, out int addr);

            bool[] bBuf = new bool[count];
            for (int i = 0; i < count; i++)
            {
                bBuf[i] = data[i] != 0;
            }
            return WriteBits(type, addr, bBuf) ? 0 : -1;
        }

        #endregion

        #region Core Read/Write Primitive Implementations (Pure On-Demand)

        public bool ReadWord(string deviceType, int address, out ushort value)
        {
            value = 0;
            if (!IsAttached && !Attach()) return false;
            long master = GetMasterPtr();
            if (master == 0) return false;

            long byteOffset = GetDeviceWordByteOffset(deviceType, address);
            if (byteOffset < 0) return false;

            byte[] buf = new byte[2];
            int read;
            if (ReadProcessMemory(_hProcess, (IntPtr)(master + byteOffset), buf, 2, out read) && read == 2)
            {
                value = BitConverter.ToUInt16(buf, 0);
                return true;
            }

            return false;
        }

        public bool ReadDWord(string deviceType, int address, out uint value)
        {
            value = 0;
            if (!IsAttached && !Attach()) return false;
            long master = GetMasterPtr();
            if (master == 0) return false;

            long byteOffset = GetDeviceWordByteOffset(deviceType, address);
            if (byteOffset < 0) return false;

            byte[] buf = new byte[4];
            int read;
            if (ReadProcessMemory(_hProcess, (IntPtr)(master + byteOffset), buf, 4, out read) && read == 4)
            {
                value = BitConverter.ToUInt32(buf, 0);
                return true;
            }

            return false;
        }

        public bool WriteWord(string deviceType, int address, ushort value)
        {
            if (!IsAttached && !Attach()) return false;
            long master = GetMasterPtr();
            if (master == 0) return false;

            long byteOffset = GetDeviceWordByteOffset(deviceType, address);
            if (byteOffset < 0) return false;

            byte[] raw = BitConverter.GetBytes(value);
            int written;
            return WriteProcessMemory(_hProcess, (IntPtr)(master + byteOffset), raw, 2, out written) && written == 2;
        }

        public bool WriteDWord(string deviceType, int address, uint value)
        {
            if (!IsAttached && !Attach()) return false;
            long master = GetMasterPtr();
            if (master == 0) return false;

            long byteOffset = GetDeviceWordByteOffset(deviceType, address);
            if (byteOffset < 0) return false;

            byte[] raw = BitConverter.GetBytes(value);
            int written;
            return WriteProcessMemory(_hProcess, (IntPtr)(master + byteOffset), raw, 4, out written) && written == 4;
        }

        public bool ReadWords(string deviceType, int startAddress, ushort[] buffer)
        {
            if (buffer == null || buffer.Length == 0) return false;
            if (!IsAttached && !Attach()) return false;
            long master = GetMasterPtr();
            if (master == 0) return false;

            long byteOffset = GetDeviceWordByteOffset(deviceType, startAddress);
            if (byteOffset < 0) return false;

            int byteLen = buffer.Length * 2;
            byte[] raw = new byte[byteLen];
            int read;

            if (ReadProcessMemory(_hProcess, (IntPtr)(master + byteOffset), raw, byteLen, out read) && read == byteLen)
            {
                Buffer.BlockCopy(raw, 0, buffer, 0, byteLen);
                return true;
            }

            return false;
        }

        public bool WriteWords(string deviceType, int startAddress, ushort[] buffer)
        {
            if (buffer == null || buffer.Length == 0) return false;
            if (!IsAttached && !Attach()) return false;
            long master = GetMasterPtr();
            if (master == 0) return false;

            long byteOffset = GetDeviceWordByteOffset(deviceType, startAddress);
            if (byteOffset < 0) return false;

            int byteLen = buffer.Length * 2;
            byte[] raw = new byte[byteLen];
            Buffer.BlockCopy(buffer, 0, raw, 0, byteLen);

            int written;
            return WriteProcessMemory(_hProcess, (IntPtr)(master + byteOffset), raw, byteLen, out written) && written == byteLen;
        }

        public bool ReadBit(string deviceType, int address, out bool value)
        {
            value = false;
            if (!IsAttached && !Attach()) return false;
            long master = GetMasterPtr();
            if (master == 0) return false;

            int bitIndex;
            long byteOffset = GetDeviceBitByteOffset(deviceType, address, out bitIndex);
            if (byteOffset < 0) return false;

            byte[] buf = new byte[2];
            int read;
            if (ReadProcessMemory(_hProcess, (IntPtr)(master + byteOffset), buf, 2, out read) && read == 2)
            {
                ushort wordVal = BitConverter.ToUInt16(buf, 0);
                value = (wordVal & (1 << bitIndex)) != 0;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Reads exactly the requested bits in a single optimized block read from live PLC memory.
        /// </summary>
        public bool ReadBits(string deviceType, int startAddress, bool[] buffer)
        {
            if (buffer == null || buffer.Length == 0) return false;
            if (!IsAttached && !Attach()) return false;
            long master = GetMasterPtr();
            if (master == 0) return false;

            int startDec = isOctalDevice(deviceType) ? OctalToDecimal(startAddress) : startAddress;
            int endDec = isOctalDevice(deviceType) ? OctalToDecimal(AddOctal(startAddress, buffer.Length - 1)) : (startAddress + buffer.Length - 1);

            int startWord = startDec / 16;
            int endWord = endDec / 16;
            int wordCount = endWord - startWord + 1;

            long baseWordOffset = GetDeviceBaseWordOffset(deviceType);
            if (baseWordOffset < 0) return false;

            long byteOffset = (baseWordOffset + startWord) * 2;
            int byteLen = wordCount * 2;
            byte[] raw = new byte[byteLen];
            int read;

            if (ReadProcessMemory(_hProcess, (IntPtr)(master + byteOffset), raw, byteLen, out read) && read == byteLen)
            {
                for (int i = 0; i < buffer.Length; i++)
                {
                    int currentDec = isOctalDevice(deviceType) ? OctalToDecimal(AddOctal(startAddress, i)) : (startAddress + i);
                    int wordIdx = (currentDec / 16) - startWord;
                    int bitIdx = currentDec % 16;
                    ushort wordVal = BitConverter.ToUInt16(raw, wordIdx * 2);
                    buffer[i] = (wordVal & (1 << bitIdx)) != 0;
                }
                return true;
            }
            return false;
        }

        public bool WriteBit(string deviceType, int address, bool value)
        {
            if (!IsAttached && !Attach()) return false;
            long master = GetMasterPtr();
            if (master == 0) return false;

            int bitIndex;
            long byteOffset = GetDeviceBitByteOffset(deviceType, address, out bitIndex);
            if (byteOffset < 0) return false;

            byte[] buf = new byte[2];
            int read, written;
            if (!ReadProcessMemory(_hProcess, (IntPtr)(master + byteOffset), buf, 2, out read) || read != 2)
            {
                buf[0] = 0;
                buf[1] = 0;
            }

            ushort wordVal = BitConverter.ToUInt16(buf, 0);
            if (value)
            {
                wordVal |= (ushort)(1 << bitIndex);
            }
            else
            {
                wordVal &= (ushort)~(1 << bitIndex);
            }

            byte[] outBuf = BitConverter.GetBytes(wordVal);
            return WriteProcessMemory(_hProcess, (IntPtr)(master + byteOffset), outBuf, 2, out written) && written == 2;
        }

        /// <summary>
        /// Writes exactly the requested bits in a single optimized block write into live PLC memory.
        /// Preserves untouched boundary bits in neighboring words.
        /// </summary>
        public bool WriteBits(string deviceType, int startAddress, bool[] buffer)
        {
            if (buffer == null || buffer.Length == 0) return false;
            if (!IsAttached && !Attach()) return false;
            long master = GetMasterPtr();
            if (master == 0) return false;

            int startDec = isOctalDevice(deviceType) ? OctalToDecimal(startAddress) : startAddress;
            int endDec = isOctalDevice(deviceType) ? OctalToDecimal(AddOctal(startAddress, buffer.Length - 1)) : (startAddress + buffer.Length - 1);

            int startWord = startDec / 16;
            int endWord = endDec / 16;
            int wordCount = endWord - startWord + 1;

            long baseWordOffset = GetDeviceBaseWordOffset(deviceType);
            if (baseWordOffset < 0) return false;

            long byteOffset = (baseWordOffset + startWord) * 2;
            int byteLen = wordCount * 2;
            byte[] raw = new byte[byteLen];
            int read, written;

            // Read current spanning words first so we preserve untouched bits in the boundary words
            if (!ReadProcessMemory(_hProcess, (IntPtr)(master + byteOffset), raw, byteLen, out read) || read != byteLen)
            {
                Array.Clear(raw, 0, raw.Length);
            }

            ushort[] words = new ushort[wordCount];
            for (int w = 0; w < wordCount; w++) words[w] = BitConverter.ToUInt16(raw, w * 2);

            for (int i = 0; i < buffer.Length; i++)
            {
                int currentDec = isOctalDevice(deviceType) ? OctalToDecimal(AddOctal(startAddress, i)) : (startAddress + i);
                int wordIdx = (currentDec / 16) - startWord;
                int bitIdx = currentDec % 16;

                if (buffer[i])
                    words[wordIdx] |= (ushort)(1 << bitIdx);
                else
                    words[wordIdx] &= (ushort)~(1 << bitIdx);
            }

            for (int w = 0; w < wordCount; w++)
            {
                byte[] b = BitConverter.GetBytes(words[w]);
                raw[w * 2] = b[0];
                raw[w * 2 + 1] = b[1];
            }

            return WriteProcessMemory(_hProcess, (IntPtr)(master + byteOffset), raw, byteLen, out written) && written == byteLen;
        }

        #endregion

        #region Helper Methods

        public void ParseDeviceCode(string deviceCode, out string type, out int address)
        {
            type = "D";
            address = 0;
            if (string.IsNullOrWhiteSpace(deviceCode)) return;

            string s = deviceCode.Trim().ToUpper();

            // Match known multi-character prefixes first
            if (s.StartsWith("CHN")) type = "CHN";
            else if (s.StartsWith("PSM")) type = "PSM";
            else if (s.StartsWith("PM")) type = "PM";
            else if (s.StartsWith("PY")) type = "PY";
            else if (s.StartsWith("SM")) type = "SM";
            else if (s.StartsWith("SD")) type = "SD";
            else if (s.StartsWith("TS")) type = "TS";
            else if (s.StartsWith("TC")) type = "TC";
            else if (s.StartsWith("TN")) type = "TN";
            else if (s.StartsWith("TI")) type = "TI";
            else if (s.StartsWith("TO")) type = "TO";
            else if (s.StartsWith("CS")) type = "CS";
            else if (s.StartsWith("CC")) type = "CC";
            else if (s.StartsWith("CN")) type = "CN";
            else if (s.StartsWith("CI")) type = "CI";
            else if (s.StartsWith("CO")) type = "CO";
            else if (s.StartsWith("ZR")) type = "ZR";
            else if (s.Length > 0 && char.IsLetter(s[0])) type = s.Substring(0, 1);

            string rest = s.Substring(type.Length);
            if (string.IsNullOrEmpty(rest)) return;

            if (type == "W" || type == "B")
            {
                // Link registers W and Link relays B use Hexadecimal addressing
                try { address = Convert.ToInt32(rest, 16); }
                catch { int.TryParse(rest, out address); }
            }
            else
            {
                // Decimal or Octal (X/Y) address string
                int.TryParse(rest, out address);
            }
        }

        private bool IsWordDevice(string deviceType)
        {
            string dev = deviceType.Trim().ToUpper();
            return dev == "D" || dev == "SD" || dev == "R" || dev == "ZR" || dev == "W" ||
                   dev == "TN" || dev == "T" || dev == "CN" || dev == "C" || dev == "CHN";
        }

        private bool Is32BitDevice(string deviceType, int address)
        {
            string dev = deviceType.Trim().ToUpper();
            if (dev == "CHN") return true;
            if ((dev == "C" || dev == "CN") && address >= 200) return true;
            return false;
        }

        private long GetDeviceBaseWordOffset(string deviceType)
        {
            string dev = deviceType.Trim().ToUpper();
            switch (dev)
            {
                case "X": return WORD_OFF_X;
                case "Y": return WORD_OFF_Y;
                case "PY": return WORD_OFF_PY;
                case "M": return WORD_OFF_M;
                case "PM": return WORD_OFF_PM;
                case "SM": return WORD_OFF_SM;
                case "PSM": return WORD_OFF_PSM;
                case "S": return WORD_OFF_S;
                case "TI": case "TS": case "T": return WORD_OFF_TI;
                case "TO": case "TC": return WORD_OFF_TO;
                case "CI": case "CS": case "C": return WORD_OFF_CI;
                case "CO": case "CC": return WORD_OFF_CO;
                case "B": return WORD_OFF_B;
                default: return -1;
            }
        }

        private long GetDeviceWordByteOffset(string deviceType, int address)
        {
            string dev = deviceType.Trim().ToUpper();
            switch (dev)
            {
                case "D": return (WORD_OFF_D + address) * 2;
                case "SD": return (WORD_OFF_SD + address) * 2;
                case "R":
                case "ZR":
                    return (WORD_OFF_R + address) * 2;
                case "W": return (WORD_OFF_W + address) * 2;
                case "TN":
                case "T":
                    return (WORD_OFF_TN + address) * 2;
                case "CN":
                case "C":
                    if (address >= 200)
                    {
                        // 32-bit counter (C200-C255) stored at CHN (2 words each)
                        return (WORD_OFF_CHN + (address - 200) * 2) * 2;
                    }
                    return (WORD_OFF_CN + address) * 2;
                case "CHN":
                    return (WORD_OFF_CHN + address * 2) * 2;
                case "X":
                    {
                        int dec = OctalToDecimal(address);
                        return (WORD_OFF_X + (dec / 16)) * 2;
                    }
                case "Y":
                    {
                        int dec = OctalToDecimal(address);
                        return (WORD_OFF_Y + (dec / 16)) * 2;
                    }
                case "PY":
                    {
                        int dec = OctalToDecimal(address);
                        return (WORD_OFF_PY + (dec / 16)) * 2;
                    }
                case "M": return (WORD_OFF_M + (address / 16)) * 2;
                case "PM": return (WORD_OFF_PM + (address / 16)) * 2;
                case "SM": return (WORD_OFF_SM + (address / 16)) * 2;
                case "PSM": return (WORD_OFF_PSM + (address / 16)) * 2;
                case "S": return (WORD_OFF_S + (address / 16)) * 2;
                default: return -1;
            }
        }

        private long GetDeviceBitByteOffset(string deviceType, int address, out int bitIndex)
        {
            bitIndex = 0;
            string dev = deviceType.Trim().ToUpper();

            switch (dev)
            {
                case "X":
                    {
                        int decAddr = OctalToDecimal(address);
                        bitIndex = decAddr % 16;
                        return (WORD_OFF_X + (decAddr / 16)) * 2;
                    }
                case "Y":
                    {
                        int decAddr = OctalToDecimal(address);
                        bitIndex = decAddr % 16;
                        return (WORD_OFF_Y + (decAddr / 16)) * 2;
                    }
                case "M":
                    {
                        bitIndex = address % 16;
                        return (WORD_OFF_M + (address / 16)) * 2;
                    }
                case "SM":
                    {
                        bitIndex = address % 16;
                        return (WORD_OFF_SM + (address / 16)) * 2;
                    }
                case "S":
                    {
                        bitIndex = address % 16;
                        return (WORD_OFF_S + (address / 16)) * 2;
                    }
                case "PY":
                    {
                        int decAddr = OctalToDecimal(address);
                        bitIndex = decAddr % 16;
                        return (WORD_OFF_PY + (decAddr / 16)) * 2;
                    }
                case "PM":
                    {
                        bitIndex = address % 16;
                        return (WORD_OFF_PM + (address / 16)) * 2;
                    }
                case "PSM":
                    {
                        bitIndex = address % 16;
                        return (WORD_OFF_PSM + (address / 16)) * 2;
                    }
                case "TI":
                case "TS":
                case "T":
                    {
                        bitIndex = address % 16;
                        return (WORD_OFF_TI + (address / 16)) * 2;
                    }
                case "TO":
                case "TC":
                    {
                        bitIndex = address % 16;
                        return (WORD_OFF_TO + (address / 16)) * 2;
                    }
                case "CI":
                case "CS":
                case "C":
                    {
                        bitIndex = address % 16;
                        return (WORD_OFF_CI + (address / 16)) * 2;
                    }
                case "CO":
                case "CC":
                    {
                        bitIndex = address % 16;
                        return (WORD_OFF_CO + (address / 16)) * 2;
                    }
                case "B":
                    {
                        bitIndex = address % 16;
                        return (WORD_OFF_B + (address / 16)) * 2;
                    }
                default:
                    return -1;
            }
        }

        private bool isOctalDevice(string dev)
        {
            string d = dev.Trim().ToUpper();
            return d == "X" || d == "Y" || d == "PY";
        }

        private int OctalToDecimal(int octal)
        {
            try
            {
                string s = octal.ToString();
                return Convert.ToInt32(s, 8);
            }
            catch
            {
                return octal;
            }
        }

        private int AddOctal(int baseOctal, int count)
        {
            try
            {
                int dec = OctalToDecimal(baseOctal) + count;
                return Convert.ToInt32(Convert.ToString(dec, 8));
            }
            catch
            {
                return baseOctal + count;
            }
        }

        #endregion

        public void Dispose()
        {
            if (!_disposed)
            {
                Detach();
                _disposed = true;
            }
        }
    }
}
