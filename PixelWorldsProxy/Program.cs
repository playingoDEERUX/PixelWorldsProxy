using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Kernys.Bson;

namespace PixelWorldsProxy
{
    class Program
    {
        public class StateObject
        {
            public const int BufferSize = 4096;
            public byte[] buffer = new byte[BufferSize];
            //BSONObject bsonObject = new BSONObject(); // received bson object, which is the only thing PW uses for their client/server communication
            public Socket currentSocket;
            public string currentIP; // for subserver switching to keep track of IP.
            public ushort currentPort; // for subserver switching to keep track of Port.
            public byte[] data;
            public int readPos = 0; // for msg framing
            public StateObject(Socket sock = null)
            {
                if (sock != null)
                    currentSocket = sock;
            }
        }

        // much messier than server code
        public static string pwserverIP = "44.194.163.69"; // has yet to be set.
        public const string pwserverDNS = "prod.gamev80.portalworldsgame.com";
        public const ushort pwserverPORT = 10001;
        public static Socket serverSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        public static Socket clientSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        public static Socket currentClientSocket; // pw game client
        public static PlayerInfo pInfo = new PlayerInfo();

        static void Main(string[] args)
        {
            Console.Title = "PW Proxy";
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Pixel Worlds Proxy v0.1 by playingo/DEERUX. ");

            if (pwserverIP != "")
            {
                Console.WriteLine($"PW masterserver IP: {pwserverIP}.");
                // We know we got an IP!
                clientSocket.BeginConnect(new IPEndPoint(IPAddress.Parse(pwserverIP), pwserverPORT), HandleConnectFromServer, new StateObject());
                serverSocket.Bind(new IPEndPoint(IPAddress.Any, pwserverPORT));
                serverSocket.Listen(10);
                serverSocket.BeginAccept(HandleConnectFromClient, new StateObject());


                while (true)
                {
                    // do stuff in the background.
                    Thread.Sleep(1000);
                    // anything..
                }
            }
            else
            {
                Console.WriteLine("Error obtaining IP from hostname.");
            }
        }

        public static void HandleConnectFromClient(IAsyncResult AR)
        {
            StateObject stateObj = AR.AsyncState as StateObject;
            try
            {
                stateObj.currentSocket = serverSocket.EndAccept(AR);
            }
            catch (ObjectDisposedException ex)
            {
                Console.WriteLine(ex.Message);
                return;
            }

            Console.WriteLine("Client connected to our internal proxy server.");
            if (!clientSocket.Connected)
            {
                clientSocket.BeginConnect(new IPEndPoint(IPAddress.Parse(pwserverIP), pwserverPORT), HandleConnectFromServer, new StateObject());

                int x = 0;
                while (!clientSocket.Connected)
                {
                    Thread.Sleep(100);
                }

                currentClientSocket = stateObj.currentSocket;
                stateObj.currentSocket.BeginReceive(stateObj.buffer, 0, stateObj.buffer.Length, SocketFlags.None, HandleReceiveFromClient, stateObj);
                serverSocket.BeginAccept(HandleConnectFromClient, new StateObject());
                return;
            }
           
            currentClientSocket = stateObj.currentSocket;
            stateObj.currentSocket.BeginReceive(stateObj.buffer, 0, stateObj.buffer.Length, SocketFlags.None, HandleReceiveFromClient, stateObj);
            serverSocket.BeginAccept(HandleConnectFromClient, new StateObject());
        }
        public static void HandleReceiveFromClient(IAsyncResult AR)
        {
            lock (clientSocket) 
            {
                lock (currentClientSocket)
                {
                    StateObject stateObj = AR.AsyncState as StateObject;

                    Socket client = stateObj.currentSocket;

                    int num;

                    try
                    {
                        num = client.EndReceive(AR);

                        if (num > 4)
                        {

                            int allegedLength = BitConverter.ToInt32(stateObj.buffer, 0);

                            if (allegedLength != num)
                                throw new Exception("Length of message that client claims is not same as length of TCP packet, huh? Skipping...");

                            byte[] array = new byte[num];
                            Buffer.BlockCopy(stateObj.buffer, 0, array, 0, num);

                            //Console.WriteLine("We received some data from the pw game client: " + Encoding.Default.GetString(array));

                            ProcessBSONFromClient(SimpleBSON.Load(array.Skip(4).ToArray()), allegedLength);

                            if (clientSocket.Connected)
                                clientSocket.Send(array);
                            else
                                Console.WriteLine("Client socket wasn't connected to any server, aborted.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }

                    Array.Fill<byte>(stateObj.buffer, 0);
                    lock (stateObj.currentSocket)
                    {
                        try
                        {
                            stateObj.currentSocket.BeginReceive(stateObj.buffer, 0, stateObj.buffer.Length, SocketFlags.None, HandleReceiveFromClient, stateObj);
                        }
                        catch (SocketException ex)
                        {
                            Console.WriteLine(ex.Message);
                        }
                    }
                }
            }
        }
        public static void HandleConnectFromServer(IAsyncResult AR)
        {
            StateObject stateObj = AR.AsyncState as StateObject;
            Console.WriteLine("Internal proxy client just connected to external pw servers!");

            lock (clientSocket)
            {
                if (clientSocket.Connected)
                    clientSocket.BeginReceive(stateObj.buffer, 0, stateObj.buffer.Length, SocketFlags.None, HandleReceiveFromServer, stateObj);
            }
        }
        public static void HandleReceiveFromServer(IAsyncResult AR)
        {
            lock (clientSocket)
            {
                if (currentClientSocket == null) return;
                lock (currentClientSocket)
                {
                    StateObject stateObj = AR.AsyncState as StateObject;

                    int num;
                    try
                    {
                        num = clientSocket.EndReceive(AR);

                        if (num > 4)
                        {
                            int allegedLength = 0;
                            if (stateObj.data == null)
                            {
                                allegedLength = BitConverter.ToInt32(stateObj.buffer, 0);
                                if (allegedLength < 4 || allegedLength > 8192000)
                                    throw new Exception($"Bad alleged length from server: {allegedLength}");
                            }
                            else
                            {
                                allegedLength = stateObj.data.Length;
                            }

                            if (allegedLength != num)
                            {
                                Console.WriteLine($"Chunk len: {num}");
                                if (stateObj.data == null) stateObj.data = new byte[allegedLength];
                                Buffer.BlockCopy(stateObj.buffer, 0, stateObj.data, stateObj.readPos, num);
                                stateObj.readPos += num;
                                if (stateObj.readPos == allegedLength)
                                {
                                    bool sends = ProcessBSONFromServer(SimpleBSON.Load(stateObj.data.Skip(4).ToArray()), allegedLength);

                                    if (currentClientSocket.Connected && sends)
                                        currentClientSocket.Send(stateObj.data);
                                    else
                                        Console.WriteLine("Packet sending to client aborted for good reason.");
                                    // do it from here then...
                                    stateObj.readPos = 0;
                                    stateObj.data = null;
                                    throw new Exception("(Message framing is done)");
                                }
                                throw new Exception("There are apparently more data chunks, trying to obtain em...");
                            }

                            byte[] array = new byte[num];
                            Buffer.BlockCopy(stateObj.buffer, 0, array, 0, num);

                            //Console.WriteLine("We received some data from the pw server: " + Encoding.Default.GetString(array));

                            bool send = ProcessBSONFromServer(SimpleBSON.Load(array.Skip(4).ToArray()), allegedLength); // indicates whether response should be sent

                            if (currentClientSocket.Connected && send)
                                currentClientSocket.Send(array);
                            else
                                Console.WriteLine("Packet sending to client aborted, send boolean: " + send.ToString());

                            stateObj.data = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }

                    Array.Fill<byte>(stateObj.buffer, 0);
                    try
                    {
                        if (clientSocket.Connected)
                            clientSocket.BeginReceive(stateObj.buffer, 0, stateObj.buffer.Length, SocketFlags.None, HandleReceiveFromServer, stateObj);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
            }
        }

        public static void HandleDisconnectFromServer(IAsyncResult AR)
        {
            Console.WriteLine("Disconnected successfully.");

            lock (clientSocket)
            {
                clientSocket.EndDisconnect(AR);
            }
        }
        public static bool ProcessBSONFromServer(BSONObject bObj, int allegedLen = 0) // allegedLen for message framing I guess?
        {
            try
            {
                int msgCount = bObj["mc"];

                for (int i = 0; i < msgCount; i++)
                {
                    BSONObject current = bObj["m" + i.ToString()] as BSONObject;

                    string messageId = current["ID"];

                    Console.WriteLine("[SERVER] >> MESSAGE ID: " + messageId);

                    foreach (string key in current.Keys)
                    {
                        BSONValue bVal = current[key];

                        switch (bVal.valueType)
                        {
                            case BSONValue.ValueType.String:
                                Console.WriteLine("[SERVER] >> KEY: " + key + " VALUE: " + current[key].stringValue);
                                break;
                            case BSONValue.ValueType.Object:
                                {
                                    Console.WriteLine("[SERVER] >> KEY: " + key);
                                    // that object related shit is more complex so im gonna leave that for later
                                    break;
                                }
                            case BSONValue.ValueType.Int32:
                                Console.WriteLine("[SERVER] >> KEY: " + key + " VALUE: " + current[key].int32Value);
                                break;
                            case BSONValue.ValueType.Int64:
                                Console.WriteLine("[SERVER] >> KEY: " + key + " VALUE: " + current[key].int64Value);
                                break;
                            case BSONValue.ValueType.Double:
                                Console.WriteLine("[SERVER] >> KEY: " + key + " VALUE: " + current[key].doubleValue);
                                break;
                            case BSONValue.ValueType.Boolean:
                                Console.WriteLine("[SERVER] >> KEY: " + key + " VALUE: " + current[key].boolValue.ToString());
                                break;
                            default:
                                Console.WriteLine("[SERVER] >> KEY: " + key);
                                break;
                        }
                    }

                    switch (messageId)
                    {
                        case "OoIP":
                            {
                                string dns = current["IP"];
                                string IP;

                                if (dns != pwserverDNS)
                                    IP = Resolver.GetIPFromDNS(current["IP"].stringValue);
                                else
                                    IP = "44.194.163.69";

                                Console.WriteLine("Resolved subserver IP: " + IP);
                                pwserverIP = IP;

                                pInfo.WorldName = current["WN"];

                                lock (currentClientSocket)
                                {
                                    lock (clientSocket)
                                    {
                                        clientSocket.BeginDisconnect(true, HandleDisconnectFromServer, null);
                                    }
                                }

                                while (clientSocket.Connected) 
                                    Thread.Sleep(100);

                                current["IP"] = "localhost";

                                BSONObject mObj = new BSONObject();
                                mObj["mc"] = 1;
                                mObj["m0"] = current;

                                byte[] mObjData = SimpleBSON.Dump(mObj);
                                byte[] mData = new byte[mObjData.Length + 4];
                                Array.Copy(BitConverter.GetBytes(mData.Length), mData, 4);
                                Buffer.BlockCopy(mObjData, 0, mData, 4, mObjData.Length);

                                if (currentClientSocket == null)
                                    Console.WriteLine("currentClientSocket was null wtf?");
                                else if (currentClientSocket.Connected)
                                    currentClientSocket.Send(mData);
                                else
                                    Console.WriteLine("currentClientSocket wasnt connected wtf?");

                                return false;
                            }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Catched exception in ProcessBSONFromServer: " + ex.Message);
            }
            return true;
        }
        public static bool ProcessBSONFromClient(BSONObject bObj, int allegedLen = 0)
        {
            try
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
                            case BSONValue.ValueType.Object:
                                {
                                    Console.WriteLine("[CLIENT] >> KEY: " + key);
                                    // that object related shit is more complex so im gonna leave that for later
                                    break;
                                }
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
                                Console.WriteLine("[CLIENT] >> KEY: " + key + " VALUE: " + current[key].boolValue.ToString());
                                break;
                            default:
                                Console.WriteLine("[CLIENT] >> KEY: " + key);
                                break;
                        }
                    }

                    switch (messageId)
                    {
                        case "TTjW":
                            {
                                pInfo.WorldName = current["W"];
                                break;
                            }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Catched exception in ProcessBSONFromClient: " + ex.Message);
            }
            return true;
        }
    }
}
