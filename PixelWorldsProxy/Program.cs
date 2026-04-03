using Kernys.Bson;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PixelWorldsProxy
{
    class Program
    {
        public static BSONObject CreateChatMessage(string nickname, string userID, string channel, int channelIndex, string message)
        {
            BSONObject bObj = new BSONObject();
            bObj[MsgLabels.ChatMessage.Nickname] = nickname;
            bObj[MsgLabels.ChatMessage.UserID] = userID;
            bObj[MsgLabels.ChatMessage.Channel] = channel;
            bObj["channelIndex"] = channelIndex;
            bObj[MsgLabels.ChatMessage.Message] = message;
            bObj[MsgLabels.ChatMessage.ChatTime] = DateTime.UtcNow;
            return bObj;
        }
        static void LogBSONPacket(BSONValue value, int indent = 0, ConsoleColor? color = null)
        {
            string Indent() => new string(' ', indent * 2);

            // Save current color
            var previousColor = Console.ForegroundColor;

            // Apply new color if provided
            if (color.HasValue)
                Console.ForegroundColor = color.Value;

            if (value is BSONObject obj)
            {
                AsyncLogger.Log(Indent() + "{");
                foreach (var key in obj.Keys)
                {
                    Console.Write(Indent() + $"  \"{key}\": ");
                    LogBSONPacket(obj[key], indent + 1, color);
                }
                AsyncLogger.Log(Indent() + "}");
            }
            else if (value is BSONArray arr)
            {
                AsyncLogger.Log(Indent() + "[");
                for (int i = 0; i < arr.Count; i++)
                {
                    LogBSONPacket(arr[i], indent + 1, color);
                }
                AsyncLogger.Log(Indent() + "]");
            }
            else
            {
                string output = value.valueType switch
                {
                    BSONValue.ValueType.String => $"\"{value.stringValue}\"",
                    BSONValue.ValueType.Boolean => value.boolValue ? "true" : "false",
                    BSONValue.ValueType.Int32 => value.int32Value.ToString(),
                    BSONValue.ValueType.Int64 => value.int64Value.ToString(),
                    BSONValue.ValueType.Double => value.doubleValue.ToString(),
                    BSONValue.ValueType.Binary => $"<binary {value.binaryValue.Length} bytes>",
                    BSONValue.ValueType.UTCDateTime => $"\"{value.dateTimeValue:O}\"",
                    BSONValue.ValueType.None => "null",
                    _ => $"\"{value.stringValue}\""
                };
                AsyncLogger.Log(Indent() + output + ",");
            }

            // Restore original color
            if (color.HasValue)
                Console.ForegroundColor = previousColor;
        }

        const int BufferSize = 1024;
        const string pwserverMainIP = "63.176.210.142";
        const string pwserverDNS = "game-frost.pixelworlds.pw";
        const ushort pwserverPORT = 10001;

        static async Task Main()
        {
            AsyncLogger.Start();
            AsyncLogger.Log("PW Proxy 1.0 - github.com/playingoDEERUX/PixelWorldsProxy");

            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { LingerState = new LingerOption(true, 2) };
            listener.Bind(new IPEndPoint(IPAddress.Any, pwserverPORT));
            listener.Listen(99);

            while (true)
            {
                var client = await listener.AcceptAsync();
                AsyncLogger.Log("Client connected");
                _ = HandleClient(client).ContinueWith(t =>
                {
                    if (t.Exception != null)
                        AsyncLogger.Log("Client task failed: " + t.Exception);
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        class ClientState
        {
            public string LastTargetIP;
            public TaskCompletionSource<bool> OoIPSync; // Ensures client waits until proxy reconnects
            public List<BSONObject> OutgoingInjectionList = new List<BSONObject>(); // For injecting outgoing packets upon next client send
            public List<BSONObject> IncomingInjectionList = new List<BSONObject>(); // For injecting incoming packets upon next server send

            public void InjectPacket(BSONObject obj, bool bToClient = false)
            {
                if (bToClient)
                {
                    lock (IncomingInjectionList)
                    {
                        IncomingInjectionList.Add(obj);
                    }
                }
                else
                {
                    lock (OutgoingInjectionList)
                    {
                        OutgoingInjectionList.Add(obj);
                    }
                }
            }
        }

        static async Task HandleClient(Socket client)
        {
            string currentIP = pwserverMainIP;
            var state = new ClientState { LastTargetIP = pwserverMainIP, OoIPSync = null };

            try
            {
                while (true)
                {
                    var server = await ConnectWithRetries(currentIP);
                    var result = await RunSession(client, server, state);

                    if (!result.reconnect)
                        break;

                    server.Close();

                    // Sync: wait for the client to process OoIP before reconnecting
                    currentIP = result.nextIP;
                    state.OoIPSync = new TaskCompletionSource<bool>();
                    await state.OoIPSync.Task; // wait until client is ready

                   
                }
            }
            catch (Exception ex)
            {
                AsyncLogger.Log("Session error: " + ex.Message);
            }
            finally
            {
                try { 
                    client.Close();
                } catch { }
                AsyncLogger.Log("Client disconnected");
            }
        }

        static async Task<(bool reconnect, string nextIP, bool OoIPModified)> RunSession(Socket client, Socket server, ClientState state)
        {
            using var cts = new CancellationTokenSource();

            var clientTask = Forward(client, server, true, cts.Token, state);
            var serverTask = Forward(server, client, false, cts.Token, state);

            var finished = await Task.WhenAny(clientTask, serverTask);
            cts.Cancel();
            await Task.WhenAll(clientTask, serverTask);

            return finished == serverTask ? serverTask.Result : (false, null, false);
        }

        static async Task<(bool reconnect, string nextIP, bool OoIPModified)> Forward(Socket from, Socket to, bool fromClient, CancellationToken cancellationToken, ClientState state)
        {
            byte[] buffer = new byte[BufferSize];
            byte[] frame = null;
            int read = 0;
            int expected = 0;

            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    int len = await from.ReceiveAsync(buffer, SocketFlags.None, cancellationToken);
                    if (len <= 0) break;

                    int offset = 0;
                    while (offset < len)
                    {
                        if (frame == null)
                        {
                            if (len - offset < 4) break;
                            expected = BitConverter.ToInt32(buffer, offset);
                            if (expected <= 0 || expected > (8 * 1024 * 1024)) break;
                            frame = new byte[expected];
                            read = 0;
                        }

                        int copy = Math.Min(expected - read, len - offset);
                        Buffer.BlockCopy(buffer, offset, frame, read, copy);
                        read += copy;
                        offset += copy;

                        if (read == expected)
                        {
                            if (!fromClient)
                            {
                                // got packet from server
                                var bson = SimpleBSON.Load(frame.Skip(4).ToArray());
                                int mc = 0;

                                if (bson != null)
                                {
                                    mc = bson["mc"];

                                    if (mc > 0)
                                        LogBSONPacket(bson, 0, ConsoleColor.DarkRed);
                                }

                                var result = await HandleServerPacket(bson, state, to, cancellationToken);

                                // If we have packets to inject, start from the message count and append the additional bson objects as necessary and sent a version of the modified frame instead.
                                // Since PW is tick-based, this is pretty much reliable and should work consistently. There will always be a 'next tick' for as long as you're connected.
                                if (state.IncomingInjectionList.Count > 0)
                                {
                                    bson["mc"] = mc + state.IncomingInjectionList.Count;
                                    for (int i = 0; i < mc; i++)
                                        bson["m" + i] = bson["m" + i];
                                    for (int i = 0; i < state.IncomingInjectionList.Count; i++)
                                        bson["m" + (mc + i)] = state.IncomingInjectionList[i];

                                    var data = SimpleBSON.Dump(bson);
                                    byte[] packet = new byte[data.Length + 4];
                                    Buffer.BlockCopy(BitConverter.GetBytes(packet.Length), 0, packet, 0, 4);
                                    Buffer.BlockCopy(data, 0, packet, 4, data.Length);
                                    await SendFull(to, packet, cancellationToken);
                                    state.IncomingInjectionList.Clear();
                                }
                                else if (!result.OoIPModified)
                                {
                                    await SendFull(to, frame, cancellationToken);
                                }

                                if (result.reconnect)
                                    return result;
                            }
                            else
                            {
                                // Wait if OoIP sync is active (pause client-to-server forwarding)
                                // got packet from client
                                if (state.OoIPSync != null)
                                    await state.OoIPSync.Task;

                                var bson = SimpleBSON.Load(frame.Skip(4).ToArray());
                                int mc = 0;

                                if (bson != null)
                                {
                                    mc = bson["mc"];

                                    if (mc > 0)
                                        LogBSONPacket(bson, 0, ConsoleColor.DarkGreen);
                                }

                                await HandleClientPacket(bson, state, to, cancellationToken);
                                    
                                // Now do same as above but for OutgoingInjectionList:

                                if (state.OutgoingInjectionList.Count > 0)
                                {
                                    bson["mc"] = mc + state.OutgoingInjectionList.Count;
                                    for (int i = 0; i < mc; i++)
                                        bson["m" + i] = bson["m" + i];
                                    for (int i = 0; i < state.OutgoingInjectionList.Count; i++)
                                        bson["m" + (mc + i)] = state.OutgoingInjectionList[i];
                                    var data = SimpleBSON.Dump(bson);
                                    byte[] packet = new byte[data.Length + 4];
                                    Buffer.BlockCopy(BitConverter.GetBytes(packet.Length), 0, packet, 0, 4);
                                    Buffer.BlockCopy(data, 0, packet, 4, data.Length);
                                    await SendFull(to, packet, cancellationToken);
                                    state.OutgoingInjectionList.Clear();
                                }
                                else
                                {
                                    await SendFull(to, frame, cancellationToken);
                                }
                            }

                            frame = null;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted) { }
            catch (Exception ex)
            {
                AsyncLogger.Log($"Forward Exception: {ex.Message}");
            }

            return (false, null, false);
        }

        static async Task<(bool reconnect, string nextIP, bool OoIPModified)> HandleServerPacket(BSONObject obj, ClientState state, Socket client, CancellationToken t)
        {
            int mc = obj["mc"];
            string newTargetIP = null;
            bool bOoIPModified = false;

            for (int i = 0; i < mc; i++)
            {
                var msg = obj["m" + i] as BSONObject;
                string id = msg["ID"];
                //AsyncLogger.Log("[SERVER] " + id);
                switch (id)
                {
                    case "OoIP":
                        string serverIP = msg["IP"];

                        if (serverIP == pwserverDNS)
                            serverIP = pwserverMainIP;
                        else if (!IPAddress.TryParse(serverIP, out _))
                        {
                            var ips = await Dns.GetHostAddressesAsync(serverIP);
                            serverIP = ips[0].ToString();
                        }

                        if (serverIP != state.LastTargetIP)
                        {
                            newTargetIP = serverIP;
                            state.LastTargetIP = serverIP;
                            AsyncLogger.Log($"[OoIP] Will reconnect to {serverIP}");
                        }

                        msg["IP"] = pwserverDNS;
                        bOoIPModified = true;

                        var wrapper = new BSONObject();
                        wrapper["mc"] = 2;
                        wrapper["m0"] = msg;
                        wrapper["m1"] = new BSONObject("p");

                        var data = SimpleBSON.Dump(wrapper);
                        byte[] packet = new byte[data.Length + 4];
                        Buffer.BlockCopy(BitConverter.GetBytes(packet.Length), 0, packet, 0, 4);
                        Buffer.BlockCopy(data, 0, packet, 4, data.Length);

                        await Task.Delay(50); // small delay to avoid flooding servers or client

                        // Send OoIP to client before reconnecting internally
                        await SendFull(client, packet, t);

                        return (newTargetIP != null, newTargetIP, bOoIPModified);

                    default:
                        break;
                }
            }

            return (false, null, bOoIPModified);
        }

        private static void HandleWorldChatMessage(BSONObject obj, ClientState state)
        {
            string chatMsg = obj["msg"];
            string[] tokens = chatMsg.Split(" ");
            int tokCount = tokens.Count();

            if (tokCount <= 0)
                return;

            if (tokens[0] == "")
                return;

            if (tokens[0][0] == '/')
            {
                string res = "Unknown command.";
                switch (tokens[0])
                {
                    case "/?":
                    case "/help":
                        res = "Commands >> /? /help /testbuy";
                        break;

                    case "/testbuy":
                        {
                            res = "Test bought a small lock (you need 100 gems)!";
                            BSONObject test = new BSONObject();
                            test["ID"] = "BIPack";
                            test["IPId"] = "SmallLock";
                            state.InjectPacket(test);
                            break;
                        }
                }

                BSONObject gObj = new BSONObject(MsgLabels.Ident.BroadcastGlobalMessage);
                gObj[MsgLabels.ChatMessageBinary] = CreateChatMessage($"<color=#00FAFA>{""}", "PWPROXY", "PWPROXY", 6, res);
                state.InjectPacket(gObj, true);
            }
        }

        static async Task HandleClientPacket(BSONObject obj, ClientState state, Socket client, CancellationToken t)
        {
            int mc = obj["mc"];


            for (int i = 0; i < mc; i++)
            {
                var msg = obj["m" + i] as BSONObject;
                string id = msg["ID"];

                switch (id)
                {
                    case "WCM":
                        {
                            HandleWorldChatMessage(msg, state);
                            break;
                        }

                    default:
                        break;
                }
            }
        }

        static async Task<Socket> Connect(string ip, int timeoutMs = 5000)
        {
            if (!IPAddress.TryParse(ip, out _))
                ip = (await Dns.GetHostAddressesAsync(ip))[0].ToString();

            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { LingerState = new LingerOption(true, 2) };

            using var cts = new CancellationTokenSource(timeoutMs);
            try
            {
                await s.ConnectAsync(ip, pwserverPORT);
                AsyncLogger.Log("Connected to " + ip);
                return s;
            }
            catch (OperationCanceledException)
            {
                s.Close();
                throw new TimeoutException($"Connect to {ip}:{pwserverPORT} timed out.");
            }
        }

        static async Task<Socket> ConnectWithRetries(string ip, int retries = 3, int delayMs = 1000)
        {
            for (int i = 0; i < retries; i++)
            {
                try
                {
                    return await Connect(ip, 5000);
                }
                catch (TimeoutException)
                {
                    AsyncLogger.Log($"Retry {i + 1}/{retries}...");
                    if (i < retries - 1) await Task.Delay(delayMs);
                }
            }
            throw new Exception($"Failed to connect to {ip} after {retries} attempts.");
        }

        static async Task SendFull(Socket s, byte[] data, CancellationToken cancellationToken)
        {
            int sent = 0;
            while (sent < data.Length)
            {
                int n = await s.SendAsync(new ArraySegment<byte>(data, sent, data.Length - sent), SocketFlags.None, cancellationToken);
                if (n == 0) await Task.Delay(1, cancellationToken);
                sent += n;
            }
        }
    }
}
