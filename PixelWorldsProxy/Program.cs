using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Kernys.Bson;

namespace PixelWorldsProxy
{
    class Program
    {
        const int BufferSize = 8192;

        static string pwserverMainIP = "63.176.210.142";
        static string pwserverIP = pwserverMainIP;
        const string pwserverDNS = "game-frost.pixelworlds.pw";
        const ushort pwserverPORT = 10001;

        static async Task Main()
        {
            Console.WriteLine("PW Proxy FINAL (Stable + Clean)");

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
            string currentServerIP = pwserverMainIP;

            try
            {
                while (true)
                {
                    var server = await ConnectToServer(currentServerIP);

                    bool reconnectRequested = false;
                    string nextIP = currentServerIP;

                    var clientTask = Pipe(client, server, true, () => reconnectRequested, ip => {
                        reconnectRequested = true;
                        nextIP = ip;
                    });

                    var serverTask = Pipe(server, client, false, () => reconnectRequested, ip => {
                        reconnectRequested = true;
                        nextIP = ip;
                    });

                    await Task.WhenAny(clientTask, serverTask);

                    try { server.Close(); } catch { }

                    if (!reconnectRequested)
                        break;

                    Console.WriteLine($"Reconnecting to {nextIP}...");
                    currentServerIP = nextIP;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Session error: " + ex.Message);
            }

            try { client.Close(); } catch { }

            Console.WriteLine("Connection closed");
        }

        static async Task<Socket> ConnectToServer(string ipOrDns)
        {
            string resolvedIP = ipOrDns;

            // ✅ Resolve DNS if needed
            if (!IPAddress.TryParse(ipOrDns, out _))
            {
                var addresses = await Dns.GetHostAddressesAsync(ipOrDns);
                resolvedIP = addresses[0].ToString();
            }

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true
            };

            await socket.ConnectAsync(IPAddress.Parse(resolvedIP), pwserverPORT);

            Console.WriteLine($"Connected to server: {resolvedIP}");

            return socket;
        }

        static async Task Pipe(
            Socket from,
            Socket to,
            bool fromClient,
            Func<bool> reconnectFlag,
            Action<string> requestReconnect)
        {
            byte[] buffer = new byte[BufferSize];

            byte[] frameBuffer = null;
            int readPos = 0;
            int expectedLen = 0;

            try
            {
                while (true)
                {
                    if (reconnectFlag())
                        break;

                    int received = await from.ReceiveAsync(buffer, SocketFlags.None);
                    if (received <= 0) break;

                    int offset = 0;

                    while (offset < received)
                    {
                        if (frameBuffer == null)
                        {
                            if (received - offset < 4)
                                throw new Exception("Invalid frame header");

                            expectedLen = BitConverter.ToInt32(buffer, offset);
                            frameBuffer = new byte[expectedLen];
                            readPos = 0;
                        }

                        int toCopy = Math.Min(expectedLen - readPos, received - offset);
                        Buffer.BlockCopy(buffer, offset, frameBuffer, readPos, toCopy);

                        readPos += toCopy;
                        offset += toCopy;

                        if (readPos == expectedLen)
                        {
                            bool send = true;

                            var bson = SimpleBSON.Load(frameBuffer.Skip(4).ToArray());

                            if (fromClient)
                            {
                                send = ProcessBSONFromClient(bson);
                            }
                            else
                            {
                                send = await ProcessBSONFromServer(bson, to, requestReconnect);
                            }

                            if (send)
                                await SendFullAsync(to, frameBuffer);

                            frameBuffer = null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Pipe error: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
            }
        }

        // ✅ FULL SEND GUARANTEE
        static async Task SendFullAsync(Socket socket, byte[] data)
        {
            int sent = 0;

            while (sent < data.Length)
            {
                int s = await socket.SendAsync(
                    new ArraySegment<byte>(data, sent, data.Length - sent),
                    SocketFlags.None
                );

                if (s <= 0)
                    throw new Exception("Socket closed during send");

                sent += s;
            }
        }

        // ✅ SERVER PROCESSING WITH DNS + CLEAN RECONNECT
        static async Task<bool> ProcessBSONFromServer(BSONObject bObj, Socket clientSocket, Action<string> requestReconnect)
        {
            int msgCount = bObj["mc"];

            for (int i = 0; i < msgCount; i++)
            {
                var current = bObj["m" + i] as BSONObject;
                string id = current["ID"];

                Console.WriteLine("[SERVER] " + id);

                if (id == "OoIP")
                {
                    string newIP = current["IP"];
                    string resolved = newIP;

                    if (newIP == pwserverDNS)
                        resolved = pwserverMainIP;
                    else if (!IPAddress.TryParse(newIP, out _))
                    {
                        var addresses = await Dns.GetHostAddressesAsync(newIP);
                        resolved = addresses[0].ToString();
                    }

                    Console.WriteLine($"Switching to: {resolved}");

                    // Tell system to reconnect
                    requestReconnect(resolved);

                    // Redirect client back to proxy
                    current["IP"] = "127.0.0.1";

                    var wrapper = new BSONObject();
                    wrapper["mc"] = 1;
                    wrapper["m0"] = current;

                    var bsonData = SimpleBSON.Dump(wrapper);
                    byte[] packet = new byte[bsonData.Length + 4];

                    Buffer.BlockCopy(BitConverter.GetBytes(packet.Length), 0, packet, 0, 4);
                    Buffer.BlockCopy(bsonData, 0, packet, 4, bsonData.Length);

                    // ✅ Send redirect directly to client
                    await SendFullAsync(clientSocket, packet);

                    return false;
                }
            }

            return true;
        }

        // ⚠️ Needed for redirect send (since we removed globals)
        static Socket clientSocketFallback;

        // KEEP YOUR ORIGINAL
        static bool ProcessBSONFromClient(BSONObject bObj)
        {
            int msgCount = bObj["mc"];

            for (int i = 0; i < msgCount; i++)
            {
                BSONObject current = bObj["m" + i.ToString()] as BSONObject;

                string messageId = current["ID"];

                Console.WriteLine("[CLIENT] >> MESSAGE ID: " + messageId);

                foreach (string key in current.Keys)
                {
                    BSONValue bVal = current[key];

                    switch (bVal.valueType)
                    {
                        case BSONValue.ValueType.String:
                            Console.WriteLine("[CLIENT] >> KEY: " + key + " VALUE: " + current[key].stringValue);
                            break;
                        case BSONValue.ValueType.Int32:
                            Console.WriteLine("[CLIENT] >> KEY: " + key + " VALUE: " + current[key].int32Value);
                            break;
                        case BSONValue.ValueType.Int64:
                            Console.WriteLine("[CLIENT] >> KEY: " + key + " VALUE: " + current[key].int64Value);
                            break;
                        case BSONValue.ValueType.Double:
                            Console.WriteLine("[CLIENT] >> KEY: " + key + " VALUE: " + current[key].doubleValue);
                            break;
                        case BSONValue.ValueType.Boolean:
                            Console.WriteLine("[CLIENT] >> KEY: " + key + " VALUE: " + current[key].boolValue);
                            break;
                        default:
                            Console.WriteLine("[CLIENT] >> KEY: " + key);
                            break;
                    }
                }
            }

            return true;
        }
    }
}
