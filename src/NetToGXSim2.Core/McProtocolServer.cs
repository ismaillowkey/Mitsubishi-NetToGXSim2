using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NetToGXSim2.Core
{
    public class McProtocolServer
    {
        private Socket? _listener;
        private UdpClient? _udpClient;
        private CancellationTokenSource? _cts;
        private readonly GxSimulatorEngine _engine;
        private readonly List<Socket> _clients = new List<Socket>();

        public string Name { get; set; } = "MC Protocol (TCP/UDP)";
        public int Port { get; private set; } = 5000;
        public bool IsRunning { get; private set; }
        public long RequestsProcessed { get; private set; }

        public event Action<string>? LogMessage;

        public McProtocolServer(GxSimulatorEngine engine, string name = "MC Protocol (TCP/UDP)", int defaultPort = 5000)
        {
            _engine = engine;
            Name = name;
            Port = defaultPort;
        }

        public static bool IsPortAvailable(int port)
        {
            try
            {
                var ipGlobal = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();

                // 1. Check all active TCP listeners
                var listeners = ipGlobal.GetActiveTcpListeners();
                foreach (var ep in listeners)
                {
                    if (ep.Port == port) return false;
                }

                // 2. Check active TCP connections
                var connections = ipGlobal.GetActiveTcpConnections();
                foreach (var conn in connections)
                {
                    if (conn.LocalEndPoint.Port == port) return false;
                }

                // 3. Check active UDP listeners
                var udpListeners = ipGlobal.GetActiveUdpListeners();
                foreach (var ep in udpListeners)
                {
                    if (ep.Port == port) return false;
                }

                // 4. Test binding exclusively TCP
                using (var testSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
                {
                    testSocket.ExclusiveAddressUse = true;
                    testSocket.Bind(new IPEndPoint(IPAddress.Any, port));
                    testSocket.Close();
                }

                // 5. Test binding exclusively UDP
                using (var testUdp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
                {
                    testUdp.ExclusiveAddressUse = true;
                    testUdp.Bind(new IPEndPoint(IPAddress.Any, port));
                    testUdp.Close();
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        public static int GetNextAvailablePort(int startingPort)
        {
            int p = Math.Max(1, startingPort);
            while (p <= 65535)
            {
                if (IsPortAvailable(p)) return p;
                p++;
            }
            return startingPort;
        }

        public int Start(int requestedPort = 5000)
        {
            if (IsRunning) return Port;

            // Proactively check if port is available, auto-increment until a free port is found
            int testPort = GetNextAvailablePort(requestedPort);
            while (testPort <= 65535)
            {
                try
                {
                    // 1. Start TCP Listener
                    _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    _listener.ExclusiveAddressUse = true;
                    _listener.Bind(new IPEndPoint(IPAddress.Any, testPort));
                    _listener.Listen(100);

                    // 2. Start UDP Listener on the exact same port
                    _udpClient = new UdpClient();
                    _udpClient.ExclusiveAddressUse = true;
                    _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, testPort));

                    // Fix Windows UDP WSAECONNRESET (10054) issue
                    try
                    {
                        const int SIO_UDP_CONNRESET = -1744830452;
                        _udpClient.Client.IOControl((IOControlCode)SIO_UDP_CONNRESET, new byte[] { 0, 0, 0, 0 }, null);
                    }
                    catch { }

                    Port = testPort;
                    IsRunning = true;
                    _cts = new CancellationTokenSource();

                    if (testPort != requestedPort)
                    {
                        LogMessage?.Invoke($"[{Name}] Port {requestedPort} in use! Automatically switched to port {Port}.");
                    }
                    LogMessage?.Invoke($"[{Name}] Started listening on TCP & UDP Port {Port}");

                    Task.Run(() => AcceptLoop(_cts.Token));
                    Task.Run(() => UdpReceiveLoop(_cts.Token));
                    return Port;
                }
                catch (SocketException)
                {
                    CleanupSockets();
                    testPort = GetNextAvailablePort(testPort + 1);
                }
                catch (Exception ex)
                {
                    CleanupSockets();
                    IsRunning = false;
                    LogMessage?.Invoke($"[ERROR] {Name} failed to start on port {testPort}: {ex.Message}");
                    return -1;
                }
            }

            IsRunning = false;
            LogMessage?.Invoke($"[ERROR] {Name}: No available ports found starting from {requestedPort}");
            return -1;
        }

        private void CleanupSockets()
        {
            try { _listener?.Close(); } catch { }
            _listener = null;

            try { _udpClient?.Close(); } catch { }
            _udpClient = null;
        }

        public void Stop()
        {
            if (!IsRunning) return;
            IsRunning = false;
            _cts?.Cancel();

            CleanupSockets();

            lock (_clients)
            {
                foreach (var client in _clients)
                {
                    try { client.Shutdown(SocketShutdown.Both); client.Close(); } catch { }
                }
                _clients.Clear();
            }

            LogMessage?.Invoke($"[{Name}] Server stopped (TCP/UDP)");
        }

        #region TCP Handling
        private async Task AcceptLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && IsRunning)
            {
                try
                {
                    if (_listener == null) break;
                    Socket client = await _listener.AcceptAsync();
                    lock (_clients) _clients.Add(client);
                    LogMessage?.Invoke($"[MC Protocol TCP] Client connected from {client.RemoteEndPoint}");

                    _ = Task.Run(() => ProcessClient(client, token));
                }
                catch
                {
                    if (!IsRunning) break;
                }
            }
        }

        private void ProcessClient(Socket client, CancellationToken token)
        {
            try
            {
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 10000;

                while (client.Connected && !token.IsCancellationRequested && IsRunning)
                {
                    // Read first 2 bytes to detect frame format
                    byte[] header = new byte[2];
                    if (!ReadExact(client, header, 0, 2)) break;

                    // 1. QnA 3E Binary (Subheader 0x50 0x00)
                    if (header[0] == 0x50 && header[1] == 0x00)
                    {
                        byte[] hdrRest = new byte[7];
                        if (!ReadExact(client, hdrRest, 0, 7)) break;

                        byte netNo = hdrRest[0];
                        byte pcNo = hdrRest[1];
                        ushort destIo = BitConverter.ToUInt16(hdrRest, 2);
                        byte destStation = hdrRest[4];
                        ushort dataLen = BitConverter.ToUInt16(hdrRest, 5);

                        byte[] payload = new byte[dataLen];
                        if (!ReadExact(client, payload, 0, dataLen)) break;

                        byte[] resp = HandleQna3EBinary(netNo, pcNo, destIo, destStation, payload, "[TCP]");
                        client.Send(resp);
                    }
                    // 2. QnA 3E ASCII (Starts with "50")
                    else if (header[0] == 0x35 && header[1] == 0x30)
                    {
                        byte[] subHdrRest = new byte[2];
                        if (!ReadExact(client, subHdrRest, 0, 2)) break;

                        byte[] hdrAscii = new byte[14];
                        if (!ReadExact(client, hdrAscii, 0, 14)) break;

                        string sDataLen = System.Text.Encoding.ASCII.GetString(hdrAscii, 10, 4);
                        int dataLen = Convert.ToInt32(sDataLen, 16);

                        byte[] payloadAscii = new byte[dataLen];
                        if (!ReadExact(client, payloadAscii, 0, dataLen)) break;

                        string fullAscii = "50" + System.Text.Encoding.ASCII.GetString(subHdrRest) +
                                           System.Text.Encoding.ASCII.GetString(hdrAscii) +
                                           System.Text.Encoding.ASCII.GetString(payloadAscii);

                        byte[] resp = HandleQna3EAsciiString(fullAscii, "[TCP]");
                        client.Send(resp);
                    }
                    // 3. A-1E Binary (Subcommand 0x00 to 0x03)
                    else if (header[0] <= 0x03)
                    {
                        byte subCommand = header[0];
                        byte pcNo = header[1];

                        byte[] rest1E = new byte[10];
                        if (!ReadExact(client, rest1E, 0, 10)) break;

                        ushort timer = BitConverter.ToUInt16(rest1E, 0);
                        int startAddr = BitConverter.ToInt32(rest1E, 2);
                        ushort devCode = BitConverter.ToUInt16(rest1E, 6);
                        int points = BitConverter.ToUInt16(rest1E, 8);
                        if (points == 0) points = 256;

                        byte[]? writeData = null;
                        if (subCommand == 0x02) // Write bits
                        {
                            int writeLen = (points + 1) / 2;
                            writeData = new byte[writeLen];
                            if (!ReadExact(client, writeData, 0, writeLen)) break;
                        }
                        else if (subCommand == 0x03) // Write words
                        {
                            int writeLen = points * 2;
                            writeData = new byte[writeLen];
                            if (!ReadExact(client, writeData, 0, writeLen)) break;
                        }

                        byte[] resp = HandleA1EBinary(subCommand, pcNo, timer, startAddr, devCode, points, writeData, "[TCP]");
                        client.Send(resp);
                    }
                    // 4. A-1E ASCII
                    else if (header[0] == 0x30 && (header[1] >= 0x30 && header[1] <= 0x33))
                    {
                        byte[] restAscii = new byte[22];
                        if (!ReadExact(client, restAscii, 0, 22)) break;

                        string fullAscii = System.Text.Encoding.ASCII.GetString(header) +
                                           System.Text.Encoding.ASCII.GetString(restAscii);

                        byte[] resp = HandleA1EAsciiString(fullAscii, "[TCP]");
                        client.Send(resp);
                    }
                    else
                    {
                        byte[] discard = new byte[1];
                        if (client.Receive(discard, 0, 1, SocketFlags.None) <= 0) break;
                    }
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"[{Name}] [TCP] Connection closed: {ex.Message}");
            }
            finally
            {
                lock (_clients) _clients.Remove(client);
                try { client.Close(); } catch { }
            }
        }
        #endregion

        #region UDP Handling
        private async Task UdpReceiveLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && IsRunning && _udpClient != null)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync().ConfigureAwait(false);
                    byte[] b = result.Buffer;
                    if (b == null || b.Length < 2) continue;

                    byte[]? resp = null;

                    // 1. QnA 3E Binary (0x50 0x00)
                    if (b[0] == 0x50 && b[1] == 0x00 && b.Length >= 9)
                    {
                        byte netNo = b[2];
                        byte pcNo = b[3];
                        ushort destIo = BitConverter.ToUInt16(b, 4);
                        byte destStation = b[6];
                        ushort dataLen = BitConverter.ToUInt16(b, 7);

                        if (b.Length >= 9 + dataLen)
                        {
                            byte[] payload = new byte[dataLen];
                            Buffer.BlockCopy(b, 9, payload, 0, dataLen);
                            resp = HandleQna3EBinary(netNo, pcNo, destIo, destStation, payload, "[UDP]");
                        }
                    }
                    // 2. QnA 3E ASCII ("5000")
                    else if (b.Length >= 18 && b[0] == 0x35 && b[1] == 0x30 && b[2] == 0x30 && b[3] == 0x30)
                    {
                        string fullAscii = System.Text.Encoding.ASCII.GetString(b);
                        resp = HandleQna3EAsciiString(fullAscii, "[UDP]");
                    }
                    // 3. A-1E Binary (Subcommand 0x00 to 0x03)
                    else if (b[0] <= 0x03 && b.Length >= 12)
                    {
                        byte subCommand = b[0];
                        byte pcNo = b[1];
                        ushort timer = BitConverter.ToUInt16(b, 2);
                        int startAddr = BitConverter.ToInt32(b, 4);
                        ushort devCode = BitConverter.ToUInt16(b, 8);
                        int points = BitConverter.ToUInt16(b, 10);
                        if (points == 0) points = 256;

                        byte[]? writeData = null;
                        if (subCommand == 0x02) // Write bits
                        {
                            int writeLen = (points + 1) / 2;
                            if (b.Length >= 12 + writeLen)
                            {
                                writeData = new byte[writeLen];
                                Buffer.BlockCopy(b, 12, writeData, 0, writeLen);
                            }
                        }
                        else if (subCommand == 0x03) // Write words
                        {
                            int writeLen = points * 2;
                            if (b.Length >= 12 + writeLen)
                            {
                                writeData = new byte[writeLen];
                                Buffer.BlockCopy(b, 12, writeData, 0, writeLen);
                            }
                        }

                        resp = HandleA1EBinary(subCommand, pcNo, timer, startAddr, devCode, points, writeData, "[UDP]");
                    }
                    // 4. A-1E ASCII
                    else if (b.Length >= 24 && b[0] == 0x30 && (b[1] >= 0x30 && b[1] <= 0x33))
                    {
                        string fullAscii = System.Text.Encoding.ASCII.GetString(b);
                        resp = HandleA1EAsciiString(fullAscii, "[UDP]");
                    }

                    if (resp != null && _udpClient != null)
                    {
                        await _udpClient.SendAsync(resp, resp.Length, result.RemoteEndPoint).ConfigureAwait(false);
                    }
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (SocketException)
                {
                    if (!IsRunning) break;
                }
                catch (Exception ex)
                {
                    if (!IsRunning) break;
                    LogMessage?.Invoke($"[{Name}] [UDP Error] {ex.Message}");
                }
            }
        }
        #endregion

        #region Protocol Frame Handlers
        private byte[] HandleQna3EBinary(byte netNo, byte pcNo, ushort destIo, byte destStation, byte[] payload, string protoPrefix = "")
        {
            if (payload.Length < 4) return Create3EErrorResponse(netNo, pcNo, destIo, destStation, 0xC059);

            ushort timer = BitConverter.ToUInt16(payload, 0);
            ushort command = BitConverter.ToUInt16(payload, 2);
            ushort subCommand = payload.Length >= 6 ? BitConverter.ToUInt16(payload, 4) : (ushort)0;

            RequestsProcessed++;

            // Command 0x0101: Read CPU Type / Model (Sent by HMIs like Weintek on initial connection)
            if (command == 0x0101)
            {
                byte[] modelBytes = System.Text.Encoding.ASCII.GetBytes("Q02UCPU         "); // 16 bytes
                ushort cpuCode = 0x0022;

                ushort respDataLen = 20; // 2 (end code) + 16 (name) + 2 (code)
                byte[] response = new byte[11 + 18];
                response[0] = 0xD0; response[1] = 0x00;
                response[2] = netNo; response[3] = pcNo;
                response[4] = (byte)(destIo & 0xFF); response[5] = (byte)((destIo >> 8) & 0xFF);
                response[6] = destStation;
                response[7] = (byte)(respDataLen & 0xFF); response[8] = (byte)((respDataLen >> 8) & 0xFF);
                response[9] = 0x00; response[10] = 0x00; // End code: 0
                Buffer.BlockCopy(modelBytes, 0, response, 11, 16);
                response[27] = (byte)(cpuCode & 0xFF); response[28] = (byte)((cpuCode >> 8) & 0xFF);

                LogMessage?.Invoke($"[{Name}] {protoPrefix} [3E] Read CPU Type -> OK (Q02UCPU)");
                return response;
            }

            // Command 0x0401: Batch Read
            if (command == 0x0401 && payload.Length >= 12)
            {
                int startAddr = payload[6] | (payload[7] << 8) | (payload[8] << 16);
                byte devCode = payload[9];
                ushort points = BitConverter.ToUInt16(payload, 10);
                string startDevName = GxSimulatorEngine.StepDeviceName(GetQnaDevicePrefix(devCode) + "0", startAddr);

                if (subCommand == 0x0001) // Bit Read
                {
                    byte[] bitVals;
                    _engine.ReadDeviceBlockBits(startDevName, points, out bitVals);

                    byte[] bitPayload = new byte[(points + 1) / 2];
                    for (int i = 0; i < points; i++)
                    {
                        int byteIdx = i / 2;
                        if (i % 2 == 0) bitPayload[byteIdx] |= (byte)((bitVals[i] & 0x01) << 4);
                        else bitPayload[byteIdx] |= (byte)(bitVals[i] & 0x01);
                    }

                    ushort respDataLen = (ushort)(2 + bitPayload.Length);
                    byte[] response = new byte[11 + bitPayload.Length];
                    response[0] = 0xD0; response[1] = 0x00;
                    response[2] = netNo; response[3] = pcNo;
                    response[4] = (byte)(destIo & 0xFF); response[5] = (byte)((destIo >> 8) & 0xFF);
                    response[6] = destStation;
                    response[7] = (byte)(respDataLen & 0xFF); response[8] = (byte)((respDataLen >> 8) & 0xFF);
                    response[9] = 0x00; response[10] = 0x00;
                    Buffer.BlockCopy(bitPayload, 0, response, 11, bitPayload.Length);

                    LogMessage?.Invoke($"[{Name}] {protoPrefix} [3E] Read Bit {startDevName} x{points} -> OK");
                    return response;
                }
                else // Word Read (subCommand == 0x0000)
                {
                    short[] wordVals;
                    _engine.ReadDeviceBlockWords(startDevName, points, out wordVals);

                    byte[] wordPayload = new byte[points * 2];
                    Buffer.BlockCopy(wordVals, 0, wordPayload, 0, wordPayload.Length);

                    ushort respDataLen = (ushort)(2 + wordPayload.Length);
                    byte[] response = new byte[11 + wordPayload.Length];
                    response[0] = 0xD0; response[1] = 0x00;
                    response[2] = netNo; response[3] = pcNo;
                    response[4] = (byte)(destIo & 0xFF); response[5] = (byte)((destIo >> 8) & 0xFF);
                    response[6] = destStation;
                    response[7] = (byte)(respDataLen & 0xFF); response[8] = (byte)((respDataLen >> 8) & 0xFF);
                    response[9] = 0x00; response[10] = 0x00;
                    Buffer.BlockCopy(wordPayload, 0, response, 11, wordPayload.Length);

                    LogMessage?.Invoke($"[{Name}] {protoPrefix} [3E] Read Word {startDevName} x{points} -> OK ({wordVals[0]})");
                    return response;
                }
            }

            // Command 0x0403: Random Read (Word & DWord)
            if (command == 0x0403 && payload.Length >= 8)
            {
                byte wordPoints = payload[6];
                byte dwordPoints = payload[7];

                short[] wordVals = new short[wordPoints];
                int[] dwordVals = new int[dwordPoints];
                int offset = 8;

                for (int i = 0; i < wordPoints && offset + 4 <= payload.Length; i++)
                {
                    int addr = payload[offset] | (payload[offset + 1] << 8) | (payload[offset + 2] << 16);
                    byte devCode = payload[offset + 3];
                    offset += 4;
                    string devName = GxSimulatorEngine.StepDeviceName(GetQnaDevicePrefix(devCode) + "0", addr);
                    _engine.ReadDevice(devName, out int val);
                    wordVals[i] = (short)val;
                }

                for (int i = 0; i < dwordPoints && offset + 4 <= payload.Length; i++)
                {
                    int addr = payload[offset] | (payload[offset + 1] << 8) | (payload[offset + 2] << 16);
                    byte devCode = payload[offset + 3];
                    offset += 4;
                    string devLow = GxSimulatorEngine.StepDeviceName(GetQnaDevicePrefix(devCode) + "0", addr);
                    string devHigh = GxSimulatorEngine.StepDeviceName(GetQnaDevicePrefix(devCode) + "0", addr + 1);
                    _engine.ReadDevice(devLow, out int valLow);
                    _engine.ReadDevice(devHigh, out int valHigh);
                    dwordVals[i] = (valLow & 0xFFFF) | ((valHigh & 0xFFFF) << 16);
                }

                int totalBytes = wordPoints * 2 + dwordPoints * 4;
                ushort respDataLen = (ushort)(2 + totalBytes);
                byte[] response = new byte[11 + totalBytes];
                response[0] = 0xD0; response[1] = 0x00;
                response[2] = netNo; response[3] = pcNo;
                response[4] = (byte)(destIo & 0xFF); response[5] = (byte)((destIo >> 8) & 0xFF);
                response[6] = destStation;
                response[7] = (byte)(respDataLen & 0xFF); response[8] = (byte)((respDataLen >> 8) & 0xFF);
                response[9] = 0x00; response[10] = 0x00;

                int outIdx = 11;
                for (int i = 0; i < wordPoints; i++)
                {
                    response[outIdx++] = (byte)(wordVals[i] & 0xFF);
                    response[outIdx++] = (byte)((wordVals[i] >> 8) & 0xFF);
                }
                for (int i = 0; i < dwordPoints; i++)
                {
                    response[outIdx++] = (byte)(dwordVals[i] & 0xFF);
                    response[outIdx++] = (byte)((dwordVals[i] >> 8) & 0xFF);
                    response[outIdx++] = (byte)((dwordVals[i] >> 16) & 0xFF);
                    response[outIdx++] = (byte)((dwordVals[i] >> 24) & 0xFF);
                }

                LogMessage?.Invoke($"[{Name}] {protoPrefix} [3E] Random Read {wordPoints} words, {dwordPoints} dwords -> OK");
                return response;
            }

            // Command 0x1401: Batch Write
            if (command == 0x1401 && payload.Length >= 12)
            {
                int startAddr = payload[6] | (payload[7] << 8) | (payload[8] << 16);
                byte devCode = payload[9];
                ushort points = BitConverter.ToUInt16(payload, 10);
                string startDevName = GxSimulatorEngine.StepDeviceName(GetQnaDevicePrefix(devCode) + "0", startAddr);

                if (subCommand == 0x0001) // Bit Write
                {
                    byte[] bitVals = new byte[points];
                    for (int i = 0; i < points; i++)
                    {
                        byte b = payload[12 + i / 2];
                        bitVals[i] = (byte)((i % 2 == 0) ? ((b >> 4) & 0x01) : (b & 0x01));
                    }
                    _engine.WriteDeviceBlockBits(startDevName, points, bitVals);
                    LogMessage?.Invoke($"[{Name}] {protoPrefix} [3E] Write Bit {startDevName} x{points} -> OK");
                }
                else // Word Write
                {
                    short[] wordVals = new short[points];
                    Buffer.BlockCopy(payload, 12, wordVals, 0, points * 2);
                    _engine.WriteDeviceBlockWords(startDevName, points, wordVals);
                    LogMessage?.Invoke($"[{Name}] {protoPrefix} [3E] Write Word {startDevName} x{points} -> OK ({wordVals[0]})");
                }

                ushort respDataLen = 2;
                byte[] response = new byte[11];
                response[0] = 0xD0; response[1] = 0x00;
                response[2] = netNo; response[3] = pcNo;
                response[4] = (byte)(destIo & 0xFF); response[5] = (byte)((destIo >> 8) & 0xFF);
                response[6] = destStation;
                response[7] = (byte)(respDataLen & 0xFF); response[8] = (byte)((respDataLen >> 8) & 0xFF);
                response[9] = 0x00; response[10] = 0x00;
                return response;
            }

            // Command 0x1402: Random Write
            if (command == 0x1402)
            {
                if (subCommand == 0x0001) // Bit units
                {
                    if (payload.Length >= 7)
                    {
                        byte bitPoints = payload[6];
                        int offset = 7;
                        for (int i = 0; i < bitPoints && offset + 5 <= payload.Length; i++)
                        {
                            int addr = payload[offset] | (payload[offset + 1] << 8) | (payload[offset + 2] << 16);
                            byte devCode = payload[offset + 3];
                            byte val = payload[offset + 4];
                            offset += 5;
                            string devName = GxSimulatorEngine.StepDeviceName(GetQnaDevicePrefix(devCode) + "0", addr);
                            _engine.WriteDevice(devName, val != 0 ? 1 : 0);
                        }
                        LogMessage?.Invoke($"[{Name}] {protoPrefix} [3E] Random Write {bitPoints} bits -> OK");
                    }
                }
                else // Word & DWord units (subCommand == 0x0000)
                {
                    if (payload.Length >= 8)
                    {
                        byte wordPoints = payload[6];
                        byte dwordPoints = payload[7];
                        int offset = 8;

                        for (int i = 0; i < wordPoints && offset + 6 <= payload.Length; i++)
                        {
                            int addr = payload[offset] | (payload[offset + 1] << 8) | (payload[offset + 2] << 16);
                            byte devCode = payload[offset + 3];
                            short val = BitConverter.ToInt16(payload, offset + 4);
                            offset += 6;
                            string devName = GxSimulatorEngine.StepDeviceName(GetQnaDevicePrefix(devCode) + "0", addr);
                            _engine.WriteDevice(devName, (int)(ushort)val);
                        }

                        for (int i = 0; i < dwordPoints && offset + 8 <= payload.Length; i++)
                        {
                            int addr = payload[offset] | (payload[offset + 1] << 8) | (payload[offset + 2] << 16);
                            byte devCode = payload[offset + 3];
                            int val = BitConverter.ToInt32(payload, offset + 4);
                            offset += 8;
                            string devLow = GxSimulatorEngine.StepDeviceName(GetQnaDevicePrefix(devCode) + "0", addr);
                            string devHigh = GxSimulatorEngine.StepDeviceName(GetQnaDevicePrefix(devCode) + "0", addr + 1);
                            _engine.WriteDevice(devLow, val & 0xFFFF);
                            _engine.WriteDevice(devHigh, (val >> 16) & 0xFFFF);
                        }
                        LogMessage?.Invoke($"[{Name}] {protoPrefix} [3E] Random Write {wordPoints} words, {dwordPoints} dwords -> OK");
                    }
                }

                ushort respDataLen = 2;
                byte[] response = new byte[11];
                response[0] = 0xD0; response[1] = 0x00;
                response[2] = netNo; response[3] = pcNo;
                response[4] = (byte)(destIo & 0xFF); response[5] = (byte)((destIo >> 8) & 0xFF);
                response[6] = destStation;
                response[7] = (byte)(respDataLen & 0xFF); response[8] = (byte)((respDataLen >> 8) & 0xFF);
                response[9] = 0x00; response[10] = 0x00;
                return response;
            }

            // Default / Other command: Return clean error code so client does not hang
            LogMessage?.Invoke($"[{Name}] {protoPrefix} [3E] Command 0x{command:X4} returned response code 0xC059");
            return Create3EErrorResponse(netNo, pcNo, destIo, destStation, 0xC059);
        }

        private byte[] Create3EErrorResponse(byte netNo, byte pcNo, ushort destIo, byte destStation, ushort errorCode)
        {
            byte[] response = new byte[11];
            response[0] = 0xD0; response[1] = 0x00;
            response[2] = netNo; response[3] = pcNo;
            response[4] = (byte)(destIo & 0xFF); response[5] = (byte)((destIo >> 8) & 0xFF);
            response[6] = destStation;
            response[7] = 0x02; response[8] = 0x00;
            response[9] = (byte)(errorCode & 0xFF); response[10] = (byte)((errorCode >> 8) & 0xFF);
            return response;
        }

        private byte[] HandleA1EBinary(byte subCommand, byte pcNo, ushort timer, int startAddr, ushort devCode, int points, byte[]? writeData, string protoPrefix = "")
        {
            string devPrefix = GetA1EDevicePrefix(devCode);
            string startDevName = GxSimulatorEngine.StepDeviceName(devPrefix + "0", startAddr);

            RequestsProcessed++;
            byte[]? response = null;

            switch (subCommand)
            {
                case 0x00: // Read Bits
                    {
                        byte[] bitVals;
                        _engine.ReadDeviceBlockBits(startDevName, points, out bitVals);

                        byte[] payload = new byte[(points + 1) / 2];
                        for (int i = 0; i < points; i++)
                        {
                            int byteIdx = i / 2;
                            if (i % 2 == 0) payload[byteIdx] |= (byte)((bitVals[i] & 0x01) << 4);
                            else payload[byteIdx] |= (byte)(bitVals[i] & 0x01);
                        }

                        response = new byte[2 + payload.Length];
                        response[0] = 0x80; response[1] = 0x00;
                        Buffer.BlockCopy(payload, 0, response, 2, payload.Length);
                        LogMessage?.Invoke($"[{Name}] {protoPrefix} [1E] Read Bit {startDevName} x{points} -> OK");
                    }
                    break;

                case 0x01: // Read Words
                    {
                        short[] wordVals;
                        _engine.ReadDeviceBlockWords(startDevName, points, out wordVals);

                        byte[] payload = new byte[points * 2];
                        Buffer.BlockCopy(wordVals, 0, payload, 0, payload.Length);

                        response = new byte[2 + payload.Length];
                        response[0] = 0x81; response[1] = 0x00;
                        Buffer.BlockCopy(payload, 0, response, 2, payload.Length);
                        LogMessage?.Invoke($"[{Name}] {protoPrefix} [1E] Read Word {startDevName} x{points} -> OK ({wordVals[0]})");
                    }
                    break;

                case 0x02: // Write Bits
                    {
                        if (writeData != null)
                        {
                            byte[] bitVals = new byte[points];
                            for (int i = 0; i < points && (i / 2) < writeData.Length; i++)
                            {
                                byte b = writeData[i / 2];
                                bitVals[i] = (byte)((i % 2 == 0) ? ((b >> 4) & 0x01) : (b & 0x01));
                            }
                            _engine.WriteDeviceBlockBits(startDevName, points, bitVals);
                        }
                        response = new byte[] { 0x82, 0x00 };
                        LogMessage?.Invoke($"[{Name}] {protoPrefix} [1E] Write Bit {startDevName} x{points} -> OK");
                    }
                    break;

                case 0x03: // Write Words
                    {
                        if (writeData != null)
                        {
                            short[] wordVals = new short[points];
                            int copyLen = Math.Min(writeData.Length, points * 2);
                            Buffer.BlockCopy(writeData, 0, wordVals, 0, copyLen);
                            _engine.WriteDeviceBlockWords(startDevName, points, wordVals);
                        }
                        response = new byte[] { 0x83, 0x00 };
                        LogMessage?.Invoke($"[{Name}] {protoPrefix} [1E] Write Word {startDevName} x{points} -> OK");
                    }
                    break;

                default:
                    response = new byte[] { (byte)(subCommand | 0x80), 0x5B };
                    break;
            }

            return response;
        }

        private byte[] HandleQna3EAsciiString(string s, string protoPrefix = "")
        {
            if (s.Length < 18) return System.Text.Encoding.ASCII.GetBytes("D00000FF03FF000004C059");

            string sNetNo = s.Substring(4, 2);
            string sPcNo = s.Substring(6, 2);
            string sDestIo = s.Substring(8, 4);
            string sDestStation = s.Substring(12, 2);
            string sDataLen = s.Substring(14, 4);

            int dataLen = 0;
            try { dataLen = Convert.ToInt32(sDataLen, 16); } catch { }
            if (s.Length < 18 + dataLen || dataLen < 8)
            {
                return System.Text.Encoding.ASCII.GetBytes("D000" + sNetNo + sPcNo + sDestIo + sDestStation + "0004C059");
            }

            string sPayload = s.Substring(18, dataLen);
            string sTimer = sPayload.Substring(0, 4);
            string sCommand = sPayload.Substring(4, 4);
            string sSubCommand = sPayload.Length >= 12 ? sPayload.Substring(8, 4) : "0000";

            RequestsProcessed++;

            // CPU Type Read in ASCII: "0101"
            if (sCommand == "0101")
            {
                string cpuModelAscii = "51303255435055202020202020202020"; // "Q02UCPU         " in hex ASCII
                string cpuCodeAscii = "0022";
                string respData = "0000" + cpuModelAscii + cpuCodeAscii;
                string sRespLen = (respData.Length).ToString("X4");

                string respStr = "D000" + sNetNo + sPcNo + sDestIo + sDestStation + sRespLen + respData;
                LogMessage?.Invoke($"[{Name}] {protoPrefix} [3E ASCII] Read CPU Type -> OK (Q02UCPU)");
                return System.Text.Encoding.ASCII.GetBytes(respStr);
            }

            // Batch Read in ASCII: "0401"
            if (sCommand == "0401" && sPayload.Length >= 22)
            {
                string sDevCode = sPayload.Substring(12, 2);
                string sAddr = sPayload.Substring(14, 6);
                string sPoints = sPayload.Substring(20, 4);

                int startAddr = Convert.ToInt32(sAddr, 10);
                int points = Convert.ToInt32(sPoints, 16);

                string devPrefix = "D";
                if (sDevCode == "D*" || sDevCode == "A8") devPrefix = "D";
                else if (sDevCode == "M*" || sDevCode == "90") devPrefix = "M";
                else if (sDevCode == "X*" || sDevCode == "9C") devPrefix = "X";
                else if (sDevCode == "Y*" || sDevCode == "9D") devPrefix = "Y";

                string startDevName = GxSimulatorEngine.StepDeviceName(devPrefix + "0", startAddr);

                if (sSubCommand == "0001") // Bit Read
                {
                    byte[] bitVals;
                    _engine.ReadDeviceBlockBits(startDevName, points, out bitVals);
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < points; i++) sb.Append(bitVals[i] != 0 ? "1" : "0");
                    string dataStr = sb.ToString();
                    string respStr = "D000" + sNetNo + sPcNo + sDestIo + sDestStation + (dataStr.Length + 4).ToString("X4") + "0000" + dataStr;
                    LogMessage?.Invoke($"[{Name}] {protoPrefix} [3E ASCII] Read Bit {startDevName} x{points} -> OK");
                    return System.Text.Encoding.ASCII.GetBytes(respStr);
                }
                else // Word Read
                {
                    short[] wordVals;
                    _engine.ReadDeviceBlockWords(startDevName, points, out wordVals);
                    var sb = new System.Text.StringBuilder();
                    for (int i = 0; i < points; i++) sb.Append(((ushort)wordVals[i]).ToString("X4"));
                    string dataStr = sb.ToString();
                    string respStr = "D000" + sNetNo + sPcNo + sDestIo + sDestStation + (dataStr.Length + 4).ToString("X4") + "0000" + dataStr;
                    LogMessage?.Invoke($"[{Name}] {protoPrefix} [3E ASCII] Read Word {startDevName} x{points} -> OK ({wordVals[0]})");
                    return System.Text.Encoding.ASCII.GetBytes(respStr);
                }
            }

            // Default ASCII error response
            string defaultResp = "D000" + sNetNo + sPcNo + sDestIo + sDestStation + "0004C059";
            return System.Text.Encoding.ASCII.GetBytes(defaultResp);
        }

        private byte[] HandleA1EAsciiString(string s, string protoPrefix = "")
        {
            if (s.Length < 24) return System.Text.Encoding.ASCII.GetBytes("805B");

            string subCmd = s.Substring(0, 2);
            RequestsProcessed++;

            // Read Words "01"
            if (subCmd == "01")
            {
                string sAddr = s.Substring(10, 6);
                string sPoints = s.Substring(18, 2);

                int startAddr = Convert.ToInt32(sAddr, 10);
                int points = Convert.ToInt32(sPoints, 16);
                if (points == 0) points = 256;

                string startDevName = $"D{startAddr}";
                short[] wordVals;
                _engine.ReadDeviceBlockWords(startDevName, points, out wordVals);

                var sb = new System.Text.StringBuilder();
                sb.Append("8100");
                for (int i = 0; i < points; i++) sb.Append(((ushort)wordVals[i]).ToString("X4"));
                LogMessage?.Invoke($"[{Name}] {protoPrefix} [1E ASCII] Read Word {startDevName} x{points} -> OK");
                return System.Text.Encoding.ASCII.GetBytes(sb.ToString());
            }

            return System.Text.Encoding.ASCII.GetBytes("805B");
        }
        #endregion

        private static bool ReadExact(Socket socket, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = socket.Receive(buffer, offset + totalRead, count - totalRead, SocketFlags.None);
                if (read <= 0) return false;
                totalRead += read;
            }
            return true;
        }

        private static string GetA1EDevicePrefix(int devCode)
        {
            switch (devCode)
            {
                case 0x5820: case 0x58: case 0x9C: return "X";
                case 0x5920: case 0x59: case 0x9D: return "Y";
                case 0x4D20: case 0x4D: case 0x90: return "M";
                case 0x4420: case 0x44: case 0xA8: return "D";
                case 0x5320: case 0x53: case 0x98: return "S";
                case 0x5420: case 0x54: case 0xC2: case 0xC0: case 0xC1: return "T";
                case 0x4320: case 0x43: case 0xC5: case 0xC3: case 0xC4: return "C";
                case 0xB4: return "W";
                case 0xAF: return "R";
                case 0xB0: return "ZR";
                default: return "D";
            }
        }

        private static string GetQnaDevicePrefix(byte devCode) => GetA1EDevicePrefix(devCode);
    }
}
