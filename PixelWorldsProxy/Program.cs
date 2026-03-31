using Kernys.Bson;
using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace PixelWorldsProxy
{
    class Program
    {
        const int BufferSize = 8192;

        static string pwserverMainIP = "63.176.210.142";
        const string pwserverDNS = "game-frost.pixelworlds.pw";
        const ushort pwserverPORT = 10001;

        static async Task Main()
        {
            Console.WriteLine("PW Proxy 1.0 - github.com/playingoDEERUX/PixelWorldsProxy");

            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Any, pwserverPORT));
            listener.Listen(10);

            while (true)
            {
                var client = await listener.AcceptAsync();
                Console.WriteLine("Client connected");

                _ = HandleClient(client);
            }
        }

        static async Task HandleClient(Socket client)
        {
            string currentIP = pwserverMainIP;

            try
            {
                while (true)
                {
                    var server = await Connect(currentIP);

                    var result = await RunSession(client, server);

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

            try { client.Close(); } catch { }
            Console.WriteLine("Client disconnected");
        }

        static async Task<(bool reconnect, string nextIP)> RunSession(Socket client, Socket server)
        {
            var clientTask = Forward(client, server, true);
            var serverTask = Forward(server, client, false);

            var finished = await Task.WhenAny(clientTask, serverTask);

            // if server task finished, check if reconnect requested
            if (finished == serverTask)
                return serverTask.Result;

            return (false, null);
        }

        static async Task<(bool reconnect, string nextIP)> Forward(Socket from, Socket to, bool fromClient)
        {
            byte[] buffer = new byte[BufferSize];

            byte[] frame = null;
            int read = 0;
            int expected = 0;

            try
            {
                while (true)
                {
                    int len = await from.ReceiveAsync(buffer, SocketFlags.None);
                    if (len <= 0)
                        break;

                    int offset = 0;

                    while (offset < len)
                    {
                        if (frame == null)
                        {
                            if (len - offset < 4)
                                return (false, null);

                            expected = BitConverter.ToInt32(buffer, offset);

                            if (expected <= 0 || expected > 1024 * 1024)
                                return (false, null);

                            frame = new byte[expected];
                            read = 0;
                        }

                        int copy = Math.Min(expected - read, len - offset);
                        Buffer.BlockCopy(buffer, offset, frame, read, copy);

                        read += copy;
                        offset += copy;

                        if (read == expected)
                        {
                            var bson = SimpleBSON.Load(frame.Skip(4).ToArray());

                            if (!fromClient)
                            {
                                var result = await HandleServerPacket(bson, to);

                                if (result.reconnect)
                                    return result;
                            }

                            await SendFull(to, frame);

                            frame = null;
                        }
                    }
                }
            }
            catch
            {
            }

            return (false, null);
        }

        static async Task<(bool reconnect, string nextIP)> HandleServerPacket(BSONObject obj, Socket client)
        {
            int mc = obj["mc"];

            for (int i = 0; i < mc; i++)
            {
                var msg = obj["m" + i] as BSONObject;
                string id = msg["ID"];

                Console.WriteLine("[SERVER] " + id);

                if (id == "OoIP")
                {
                    string newIP = msg["IP"];
                    string resolved = newIP;

                    if (newIP == pwserverDNS)
                        resolved = pwserverMainIP;
                    else if (!IPAddress.TryParse(newIP, out _))
                    {
                        var ips = await Dns.GetHostAddressesAsync(newIP);
                        resolved = ips[0].ToString();
                    }

                    Console.WriteLine("Switching to: " + resolved);

                    msg["IP"] = "127.0.0.1";

                    var wrapper = new BSONObject();
                    wrapper["mc"] = 1;
                    wrapper["m0"] = msg;

                    var data = SimpleBSON.Dump(wrapper);
                    byte[] packet = new byte[data.Length + 4];

                    Buffer.BlockCopy(BitConverter.GetBytes(packet.Length), 0, packet, 0, 4);
                    Buffer.BlockCopy(data, 0, packet, 4, data.Length);

                    await SendFull(client, packet);

                    return (true, resolved);
                }
            }

            return (false, null);
        }

        static async Task<Socket> Connect(string ip)
        {
            if (!IPAddress.TryParse(ip, out _))
            {
                var ips = await Dns.GetHostAddressesAsync(ip);
                ip = ips[0].ToString();
            }

            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            await s.ConnectAsync(ip, pwserverPORT);

            Console.WriteLine("Connected to " + ip);

            return s;
        }

        static async Task SendFull(Socket s, byte[] data)
        {
            int sent = 0;

            while (sent < data.Length)
            {
                int n = await s.SendAsync(new ArraySegment<byte>(data, sent, data.Length - sent), SocketFlags.None);
                if (n <= 0) throw new Exception("send failed");
                sent += n;
            }
        }
    }
}
