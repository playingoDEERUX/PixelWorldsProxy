using Kernys.Bson;
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PixelWorldsProxy
{
    class Program
    {
        const int BufferSize = 8192;
        const string pwserverMainIP = "63.176.210.142";
        const string pwserverDNS = "game-frost.pixelworlds.pw";
        const ushort pwserverPORT = 10001;

        static async Task Main()
        {
            Console.WriteLine("PW Proxy 1.0 - github.com/playingoDEERUX/PixelWorldsProxy");

            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            listener.Bind(new IPEndPoint(IPAddress.Any, pwserverPORT));
            listener.Listen(10);

            while (true)
            {
                var client = await listener.AcceptAsync();
                Console.WriteLine("Client connected");
                _ = HandleClient(client).ContinueWith(t =>
                {
                    if (t.Exception != null)
                        Console.WriteLine("Client task failed: " + t.Exception);
                }, TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        class ClientState
        {
            public string LastTargetIP;
            public bool OoIPPending; // Track ongoing OoIP to avoid duplicate reconnects
        }

        static async Task HandleClient(Socket client)
        {
            string currentIP = pwserverMainIP;
            var state = new ClientState { LastTargetIP = pwserverMainIP, OoIPPending = false };

            try
            {
                client.NoDelay = true;

                while (true) // Keep reconnecting if OoIP requires
                {
                    var server = await Connect(currentIP);
                    var result = await RunSession(client, server, state);
                    server.Close();

                    if (!result.reconnect)
                        break;

                    Console.WriteLine($"Reconnecting to {result.nextIP}");
                    currentIP = result.nextIP;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Session error: " + ex.Message);
            }
            finally
            {
                try { client.Close(); } catch { }
                Console.WriteLine("Client disconnected");
            }
        }

        static async Task<(bool reconnect, string nextIP, bool OoIPModified)> RunSession(Socket client, Socket server, ClientState state)
        {
            using var cts = new CancellationTokenSource();

            var clientTask = Forward(client, server, true, cts.Token, state);
            var serverTask = Forward(server, client, false, cts.Token, state);

            var finished = await Task.WhenAny(clientTask, serverTask);
            cts.Cancel(); // Stop the other task cleanly
            await Task.WhenAll(clientTask, serverTask); // Wait for full cleanup

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
                            if (len - offset < 4)
                                break;

                            expected = BitConverter.ToInt32(buffer, offset);
                            if (expected <= 0 || expected > 1024 * 1024)
                                break;

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
                                var bson = SimpleBSON.Load(frame.Skip(4).ToArray());
                                var result = await HandleServerPacket(bson, state, to, cancellationToken);

                                // Always send modified packet
                                if (!result.OoIPModified)
                                {
                                    await SendFull(to, frame, cancellationToken);
                                }

                                if (result.reconnect)
                                    return result;
                            }
                            else
                            {
                                await SendFull(to, frame, cancellationToken);
                            }

                            frame = null;
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown, do nothing
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.OperationAborted)
            {
                // Normal when socket is closed
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Forward Exception: {ex.Message}");
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
                Console.WriteLine("[SERVER] " + id);

                if (id == "OoIP")
                {
                    // Skip if already processing an OoIP reconnect
                    if (state.OoIPPending)
                    {
                        Console.WriteLine("[OoIP] Reconnect already pending, ignoring duplicate.");
                        continue;
                    }

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
                        state.OoIPPending = true; // mark reconnect in progress
                        Console.WriteLine($"[OoIP] Will reconnect to {serverIP}");
                    }

                    msg["IP"] = "127.0.0.1";
                    bOoIPModified = true;

                    var wrapper = new BSONObject();
                    wrapper["mc"] = 2;
                    wrapper["m0"] = msg;
                    wrapper["m1"] = new BSONObject("p");

                    var data = SimpleBSON.Dump(wrapper);
                    byte[] packet = new byte[data.Length + 4];

                    Buffer.BlockCopy(BitConverter.GetBytes(packet.Length), 0, packet, 0, 4);
                    Buffer.BlockCopy(data, 0, packet, 4, data.Length);

                    await SendFull(client, packet, t);

                    return (newTargetIP != null, newTargetIP, bOoIPModified);
                }
            }

            return (newTargetIP != null, newTargetIP, bOoIPModified);
        }

        static async Task<Socket> Connect(string ip)
        {
            if (!IPAddress.TryParse(ip, out _))
            {
                var ips = await Dns.GetHostAddressesAsync(ip);
                ip = ips[0].ToString();
            }

            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await s.ConnectAsync(ip, pwserverPORT);
            Console.WriteLine("Connected to " + ip);
            return s;
        }

        static async Task SendFull(Socket s, byte[] data, CancellationToken cancellationToken)
        {
            int sent = 0;
            while (sent < data.Length)
            {
                try
                {
                    int n = await s.SendAsync(new ArraySegment<byte>(data, sent, data.Length - sent), SocketFlags.None, cancellationToken);
                    if (n == 0)
                    {
                        // Prevent tight loop
                        await Task.Delay(1, cancellationToken);
                        continue;
                    }
                    sent += n;
                }
                catch (SocketException ex)
                {
                    throw new Exception("Send failed: " + ex.Message, ex);
                }
            }
        }
    }
}
