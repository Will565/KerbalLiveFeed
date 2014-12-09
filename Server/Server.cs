//#define DEBUG_OUT
//#define SEND_UPDATES_TO_SENDER

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.Net.Sockets;
using System.Threading;
using System.Net;
using System.IO;
using System.Diagnostics;

using System.Collections.Concurrent;

namespace KLFServer
{
    class Server
    {
        public struct ClientMessage
        {
            public int ClientIndex;
            public KLFCommon.ClientMessageID ID;
            public byte[] Data;
        }

        public const long ClientTimeoutDelay = 8000;
        public const long ClientHandshakeTimeoutDelay = 6000;
        public const int SleepTime = 15;
        public const int MaxScreenshotCount = 10000;
        public const int UdpAckThrottle = 1000;
        public const int MaxSavedThrottleStates = 16;
        public const float NotInFlightUpdateWeight = 1.0f/4.0f;
        public const int ActivityResetDelay = 10000;
        public const String ScreenshotDirectory = "klfScreenshots";
        public const String BanFile = "banned.txt";

        public int NumClients
        {
            private set;
            get;
        }
        public int ClientsInGame
        {
            private set;
            get;
        }
        public int ClientsInFlight
        {
            private set;
            get;
        }

        public bool Quit = false;
        public bool Stop = false;

        public String CurrentThreadExceptionStackTrace;
        public Exception CurrentThreadException;
        //Locks
        public object CurrentThreadExceptionLock = new object();
        public object ClientActivityCountLock = new object();
        public static object ConsoleWriteLock = new object();

        public Thread ListenThread;
        public Thread CommandThread;
        public Thread ConnectionThread;
        public Thread OutgoingMessageThread;

        public TcpListener tcpListener;
        public UdpClient UdpConnection;
        public HttpListener httpListener;

        public ServerClient[] Clients;
        public ConcurrentQueue<ClientMessage> ClientMessageQueue;

        public HashSet<IPAddress> BannedIPs = new HashSet<IPAddress>();

        public Dictionary<IPAddress, ServerClient.ThrottleState> SavedThrottleStates = new Dictionary<IPAddress, ServerClient.ThrottleState>();

        public ServerSettings Configuration;

        public Stopwatch ServerStopwatch = new Stopwatch();

        public long CurrentMillisecond
        {
            get
            {
                return ServerStopwatch.ElapsedMilliseconds;
            }
        }

        public int UpdateInterval
        {
            get
            {
                float relevantPlayerCount = 0;
                lock (ClientActivityCountLock)
                {
                    //Create a weighted count of Clients in-flight and not in-flight to estimate the amount of update traffic
                    relevantPlayerCount = ClientsInFlight + (ClientsInGame - ClientsInFlight) * NotInFlightUpdateWeight;
                }

                if (relevantPlayerCount <= 0)
                    return ServerSettings.MinUpdateInterval;

                //Calculate the value that satisfies updates per second
                int val = (int)Math.Round(1.0f / (Configuration.UpdatesPerSecond / relevantPlayerCount) * 1000);
                //Bound the values by the minimum and maximum
                if (val < ServerSettings.MinUpdateInterval)
                    return ServerSettings.MinUpdateInterval;
                if (val > ServerSettings.MaxUpdateInterval)
                    return ServerSettings.MaxUpdateInterval;
                return val;
            }
        }

        public byte InactiveShipsPerClient
        {
            get
            {
                int relevantPlayerCount = 0;
                lock (ClientActivityCountLock)
                {
                    relevantPlayerCount = ClientsInFlight;
                }
                if (relevantPlayerCount <= 0)
                    return Configuration.TotalInactiveShips;
                if (relevantPlayerCount > Configuration.TotalInactiveShips)
                    return 0;
                return (byte)(Configuration.TotalInactiveShips / relevantPlayerCount);
            }
        }

        //Methods
        public Server(ServerSettings s)
        {
            this.Configuration = s;
        }

        public static void StampedConsoleWriteLine(String message)
        {
            lock (ConsoleWriteLock)
            {
                ConsoleColor defaultColor = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = ConsoleColor.DarkGreen;
                    Console.Write('[');
                    Console.Write(DateTime.Now.ToString("HH:mm:ss"));
                    Console.Write("] ");
                    Console.ForegroundColor = defaultColor;
                    Console.WriteLine(message);
                }
                catch (IOException) { }
                finally
                {
                    Console.ForegroundColor = defaultColor;
                }
            }
        }

        public static void DebugConsoleWriteLine(String message)
        {
#if DEBUG_OUT
            StampedConsoleWriteLine(message);
#endif
        }

        public void ClearState()
        {
            SafeAbort(ListenThread);
            SafeAbort(CommandThread);
            SafeAbort(ConnectionThread);
            SafeAbort(OutgoingMessageThread);

            if (Clients != null)
            {
                for (int i = 0; i < Clients.Length; i++)
                {
                    Clients[i].EndReceivingMessages();
                    if (Clients[i].TcpConnection != null)
                        Clients[i].TcpConnection.Close();
                }
            }

            if (tcpListener != null)
            {
                try
                {
                    tcpListener.Stop();
                }
                catch (System.Net.Sockets.SocketException) {}
            }

            if (httpListener != null)
            {
                try
                {
                    httpListener.Stop();
                    httpListener.Close();
                }
                catch (ObjectDisposedException) {}
            }

            if (UdpConnection != null)
            {
                try
                {
                    UdpConnection.Close();
                }
                catch { }
            }
            UdpConnection = null;

        }

        public void saveScreenshot(Screenshot screenshot, String player)
        {
            if (!Directory.Exists(ScreenshotDirectory))
            {
                //Create the screenshot directory
                try
                {
                    if (!Directory.CreateDirectory(ScreenshotDirectory).Exists)
                        return;
                }
                catch (Exception)
                {
                    return;
                }
            }

            //Build the filename
            StringBuilder sb = new StringBuilder();
            sb.Append(ScreenshotDirectory);
            sb.Append('/');
            sb.Append(KLFCommon.FilteredFileName(player));
            sb.Append(' ');
            sb.Append(System.DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss"));
            sb.Append(".png");

            //Write the screenshot to file
            String filename = sb.ToString();
            if (!File.Exists(filename))
            {
                try
                {
                    File.WriteAllBytes(filename, screenshot.Image);
                }
                catch (Exception) {}
            }
        }

        private void SafeAbort(Thread thread, bool join = false)
        {
            if (thread != null)
            {
                try
                {
                    thread.Abort();
                    if (join)
                        thread.Join();
                }
                catch (ThreadStateException) { }
                catch (ThreadInterruptedException) { }
            }
        }

        public void PassExceptionToMain(Exception e)
        {
            lock (CurrentThreadExceptionLock)
            {
                if (CurrentThreadException == null)
                    CurrentThreadException = e; //Pass exception to main thread
            }
        }

        private void PrintCommands()
        {
            Console.WriteLine("Commands:");
            Console.WriteLine("/quit - quit hosting");
            Console.WriteLine("/exit - quit and exit program");
            Console.WriteLine("/list - list players");
            Console.WriteLine("/count - display player counts");
            Console.WriteLine("/kick <username> - kick a player");
            Console.WriteLine("/ban <username> - ban a player");
            Console.WriteLine("/banip <ip> - ban an ip");
            Console.WriteLine("/unbanip <ip> - unban an ip");
            Console.WriteLine("/clearbans - remove all bans");
        }

        //Threads
        public void HostingLoop()
        {//create all threads
            ClearState();
            ServerStopwatch.Start();
            StampedConsoleWriteLine("Hosting server on port " + Configuration.Port + "...");
            Clients = new ServerClient[Configuration.MaxClients];
            for (int i = 0; i < Clients.Length; i++)
                Clients[i] = new ServerClient(this, i);

            ClientMessageQueue = new ConcurrentQueue<ClientMessage>();
            NumClients = 0;
            ClientsInGame = 0;
            ClientsInFlight = 0;

            ListenThread = new Thread(new ThreadStart(ListenForClients));
            CommandThread = new Thread(new ThreadStart(HandleCommands));
            ConnectionThread = new Thread(new ThreadStart(HandleConnections));
            OutgoingMessageThread = new Thread(new ThreadStart(SendOutgoingMessages));
            CurrentThreadException = null;

            LoadBanList();
            tcpListener = new TcpListener(IPAddress.Any, Configuration.Port);
            ListenThread.Start();
            try
            {
                UdpConnection = new UdpClient(Configuration.Port);
                UdpConnection.BeginReceive(AsyncUdpReceive, null);
            }
            catch
            {
                UdpConnection = null;
            }

            Console.WriteLine("Enter /help to view server commands.");
            CommandThread.Start();
            ConnectionThread.Start();
            OutgoingMessageThread.Start();

            //Begin listening for HTTP requests
            httpListener = new HttpListener(); //Might need a replacement as HttpListener needs admin rights
            try
            {
                httpListener.Prefixes.Add("http://*:" + Configuration.HttpPort + '/');
                httpListener.Start();
                httpListener.BeginGetContext(AsyncHttpCallback, httpListener);
            }
            catch (Exception e)
            {
                StampedConsoleWriteLine("Error starting http server: " + e);
                StampedConsoleWriteLine("Please try running the server as an administrator");
            }

            while (!Quit)
            {
                //Check for exceptions that occur in threads
                lock (CurrentThreadExceptionLock)
                {
                    if (CurrentThreadException != null)
                    {
                        Exception e = CurrentThreadException;
                        CurrentThreadExceptionStackTrace = e.StackTrace;
                        throw e;
                    }
                }
                Thread.Sleep(SleepTime);
            }
            ClearState();
            ServerStopwatch.Stop();
            StampedConsoleWriteLine("Server session ended.");
        }

        private void HandleCommands()
        {
            try
            {
                while (true)
                {
                    String input = Console.ReadLine().ToLower();
                    if (input != null && input.Length > 0)
                    {
                        if (input.ElementAt(0) == '/')
                        {
                            if (input == "/quit" || input == "/exit")
                            {
                                Quit = true;
                                if (input == "/exit")
                                    Stop = true;
                                //Disconnect all Clients
                                for (int i = 0; i < Clients.Length; i++)
                                    DisconnectClient(i, "Server is shutting down");
                                break;
                            }
                            else if (input == "/crash")
                            {
                                Object o = null; //You asked for it!
                                o.ToString();
                            }
                            else if (input.Length > 6 && input.Substring(0, 6) == "/kick ")
                            {
                                String name = input.Substring(6, input.Length - 6).ToLower();
                                int index = GetClientIndexByName(name);
                                if (index >= 0)
                                    DisconnectClient(index, "You were kicked from the server.");
                                else
                                    StampedConsoleWriteLine("Player " + name + " not found.");
                            }
                            else if (input.Length > 5 && input.Substring(0, 5) == "/ban ")
                            {
                                String name = input.Substring(5, input.Length - 5).ToLower();
                                int index = GetClientIndexByName(name);
                                if (index >= 0)
                                    BanClient(index);
                                else
                                    StampedConsoleWriteLine("Player " + name + " not found.");
                            }
                            else if (input.Length > 7 && input.Substring(0, 7) == "/banip ")
                            {
                                String ipString = input.Substring(7, input.Length - 7).ToLower();
                                IPAddress address;
                                if (IPAddress.TryParse(ipString, out address))
                                    BanIP(address);
                                else
                                    StampedConsoleWriteLine("Invalid ip.");
                            }
                            else if (input.Length > 9 && input.Substring(0, 9) == "/unbanip ")
                            {
                                String ipString = input.Substring(9, input.Length - 9).ToLower();
                                IPAddress address;
                                if (IPAddress.TryParse(ipString, out address))
                                    UnBanIP(address);
                                else
                                    StampedConsoleWriteLine("Invalid ip.");
                            }
                            else if (input == "/clearbans")
                            {
                                ClearBans();
                                StampedConsoleWriteLine("All bans cleared.");
                            }
                            else if (input.Length > 4 && input.Substring(0, 4) == "/ip ")
                            {
                                String name = input.Substring(4, input.Length - 4).ToLower();
                                int index = GetClientIndexByName(name);
                                if (index >= 0)
                                {
                                    StampedConsoleWriteLine(Clients[index].Username + " ip: " + GetClientIP(index).ToString());
                                }
                            }
                            else if (input == "/list")
                            {
                                //Display player list
                                StringBuilder sb = new StringBuilder();
                                for (int i = 0; i < Clients.Length; i++)
                                {
                                    if (ClientIsReady(i))
                                    {
                                        sb.Append(Clients[i].Username);
                                        sb.Append(" - ");
                                        sb.Append(Clients[i].CurrentActivity.ToString());
                                        sb.Append('\n');
                                    }
                                }
                                StampedConsoleWriteLine(sb.ToString());
                            }
                            else if (input == "/count")
                            {
                                StampedConsoleWriteLine("Total Clients: " + NumClients);
                                lock (ClientActivityCountLock)
                                {
                                    StampedConsoleWriteLine("In-Game Clients: " + ClientsInGame);
                                    StampedConsoleWriteLine("In-Flight Clients: " + ClientsInFlight);
                                }
                            }
                            else if (input == "/help")
                                PrintCommands();
                        }
                        else
                        {
                            //Send a message to all Clients
                            SendServerMessageToAll(input);
                        }
                    }
                }
            }
            catch (ThreadAbortException) {}
            catch (Exception e)
            {
                PassExceptionToMain(e);
            }
        }

        private void ListenForClients()
        {

            try
            {
                StampedConsoleWriteLine("Listening for Clients...");
                tcpListener.Start(4);

                while (true)
                {

                    TcpClient client = null;
                    String errorMessage = String.Empty;

                    try
                    {
                        if (tcpListener.Pending())
                        {
                            client = tcpListener.AcceptTcpClient(); //Accept a TCP client
                        }
                    }
                    catch (System.Net.Sockets.SocketException e)
                    {
                        if (client != null)
                            client.Close();
                        client = null;
                        errorMessage = e.ToString();
                    }

                    if (client != null && client.Connected)
                    {
                        IPAddress clientAddress = ((IPEndPoint)client.Client.RemoteEndPoint).Address;

                        //Check if the client IP has been banned
                        if (BannedIPs.Contains(clientAddress))
                        {
                            //Client has been banned
                            StampedConsoleWriteLine("Banned client: " + clientAddress.ToString() + " attempted to connect.");
                            SendHandshakeRefusalMessageDirect(client, "You are banned from the server.");
                            client.Close();
                        }
                        else
                        {
                            //Try to add the client
                            int clientIndex = AddClient(client);
                            if (clientIndex >= 0)
                            {
                                if (ValidClient(clientIndex))
                                {
                                    //Send a handshake to the client
                                    StampedConsoleWriteLine("Accepted client. Handshaking...");
                                    SendHandshakeMessage(clientIndex);

                                    try
                                    {
                                        SendMessageHeaderDirect(client, KLFCommon.ServerMessageID.Null, 0);
                                    }
                                    catch (System.IO.IOException) {}
                                    catch (System.ObjectDisposedException) {}
                                    catch (System.InvalidOperationException) {}
                                    //Send the join message to the client
                                    if (Configuration.JoinMessage.Length > 0)
                                        SendServerMessage(clientIndex, Configuration.JoinMessage);
                                }
                                //Send a server setting update to all Clients
                                SendServerSettingsToAll();
                            }
                            else
                            {
                                //Client array is full
                                StampedConsoleWriteLine("Client attempted to connect, but server is full.");
                                SendHandshakeRefusalMessageDirect(client, "Server is currently full");
                                client.Close();
                            }
                        }
                    }
                    else
                    {
                        if (client != null)
                            client.Close();
                        client = null;
                    }

                    if (client == null && errorMessage.Length > 0)
                    {
                        //There was an error accepting the client
                        StampedConsoleWriteLine("Error accepting client: ");
                        StampedConsoleWriteLine(errorMessage);
                    }
                    Thread.Sleep(SleepTime);
                }
            }
            catch (ThreadAbortException) {}
            catch (Exception e)
            {
                PassExceptionToMain(e);
            }
        }

        private void HandleConnections()
        {
            try
            {
                DebugConsoleWriteLine("Starting disconnect thread");
                while (true)
                {
                    //Handle received messages
                    while (ClientMessageQueue.Count > 0)
                    {
                        ClientMessage message;
                        if (ClientMessageQueue.TryDequeue(out message))
                            HandleMessage(message.ClientIndex, message.ID, message.Data);
                        else
                            break;
                    }
                    //Check for Clients that have not sent messages for too long
                    for (int i = 0; i < Clients.Length; i++)
                    {
                        if (ValidClient(i))
                        {
                            long lastReceiveTime = 0;
                            long connectionStartTime = 0;
                            bool handshook = false;

                            lock (Clients[i].TimestampLock)
                            {
                                lastReceiveTime = Clients[i].LastReceiveTime;
                                connectionStartTime = Clients[i].ConnectionStartTime;
                                handshook = Clients[i].ReceivedHandshake;
                            }
                            if (CurrentMillisecond - lastReceiveTime > ClientTimeoutDelay
                            || (!handshook && (CurrentMillisecond - connectionStartTime) > ClientHandshakeTimeoutDelay))
                                DisconnectClient(i, "Timeout");
                            else
                            {
                                bool changed = false;

                                //Reset the client's activity level if the time since last update was too long
                                lock (Clients[i].ActivityLock)
                                {
                                    if (Clients[i].CurrentActivity == ServerClient.Activity.InFlight
                                    && (CurrentMillisecond - Clients[i].LastInFlightActivityTime) > ActivityResetDelay)
                                    {
                                        Clients[i].CurrentActivity = ServerClient.Activity.InGame;
                                        changed = true;
                                    }
                                    if (Clients[i].CurrentActivity == ServerClient.Activity.InGame
                                    && (CurrentMillisecond - Clients[i].LastInGameActivityTime) > ActivityResetDelay)
                                    {
                                        Clients[i].CurrentActivity = ServerClient.Activity.Inactive;
                                        changed = true;
                                    }
                                }
                                if (changed)
                                    ClientActivityChanged(i);
                            }
                        }
                        else if (!Clients[i].CanBeReplaced)
                        {//Client is disconnected but slot has not been cleaned up
                            DisconnectClient(i, "Connection lost");
                        }
                    }
                    Thread.Sleep(SleepTime);
                }
            }
            catch (ThreadAbortException) {}
            catch (Exception e)
            {
                PassExceptionToMain(e);
            }
            DebugConsoleWriteLine("Ending disconnect thread.");
        }

        void SendOutgoingMessages()
        {
            try
            {
                while (true)
                {
                    for (int i = 0; i < Clients.Length; i++)
                        if (ValidClient(i))
                            Clients[i].SendOutgoingMessages();
                    Thread.Sleep(SleepTime);
                }
            }
            catch (ThreadAbortException) {}
            catch (Exception e)
            {
                PassExceptionToMain(e);
            }
        }

        //Clients

        private int AddClient(TcpClient tcpClient)
        {
            if (tcpClient == null || !tcpClient.Connected)
                return -1;
            //Find an open client slot
            for (int i = 0; i < Clients.Length; i++)
            {
                ServerClient client = Clients[i];
                if (client.CanBeReplaced && !ValidClient(i))
                {//Add the client
                    client.TcpConnection = tcpClient;
                    client.IP = ((IPEndPoint)tcpClient.Client.RemoteEndPoint).Address;
                    client.ResetProperties();
                    //If the client's throttle state has been saved, retrieve it
                    if (SavedThrottleStates.TryGetValue(client.IP, out client.CurrentThrottle))
                        SavedThrottleStates.Remove(client.IP);
                    client.StartReceivingMessages();
                    NumClients++;
                    return i;
                }
            }
            return -1;
        }

        //Methods
        public bool ValidClient(int index)
        {
            return (index >= 0
                && index < Clients.Length
                && Clients[index].TcpConnection != null
                && Clients[index].TcpConnection.Connected
                );
        }

        public bool ClientIsReady(int index)
        {
            return ValidClient(index) && Clients[index].ReceivedHandshake;
        }

        public void DisconnectClient(int index, String message)
        {
            //Send a message to client informing them why they were disconnected
            if (Clients[index].TcpConnection.Connected)
                SendConnectionEndMessageDirect(Clients[index].TcpConnection, message);

            lock (Clients[index].TcpClientLock)
            {//Close the socket
                Clients[index].EndReceivingMessages();
                Clients[index].TcpConnection.Close();
                if (Clients[index].CanBeReplaced)
                    return;
                NumClients--;
                //Only send the disconnect message if the client performed handshake successfully
                if (Clients[index].ReceivedHandshake)
                {
                    StampedConsoleWriteLine("Client #" + index + " " + Clients[index].Username + " has disconnected: " + message);

                    if (!Clients[index].MessagesThrottled)
                    {
                        StringBuilder sb = new StringBuilder();
                        //Build disconnect message
                        sb.Clear();
                        sb.Append("User ");
                        sb.Append(Clients[index].Username);
                        sb.Append(" has disconnected : " + message);
                        //Send the disconnect message to all other Clients
                        SendServerMessageToAll(sb.ToString());
                    }
                    MessageFloodIncrement(index);
                }
                else
                    StampedConsoleWriteLine("Client failed to handshake successfully: " + message);

                Clients[index].ReceivedHandshake = false;
                if (Clients[index].CurrentActivity != ServerClient.Activity.Inactive)
                    ClientActivityChanged(index);
                else
                    SendServerSettingsToAll();

                //Save the client's throttle state
                IPAddress ip = GetClientIP(index);
                if (SavedThrottleStates.ContainsKey(ip))
                    SavedThrottleStates[ip] = Clients[index].CurrentThrottle;
                else
                {
                    if (SavedThrottleStates.Count >= MaxSavedThrottleStates)
                        SavedThrottleStates.Clear();
                    SavedThrottleStates.Add(ip, Clients[index].CurrentThrottle);
                }
                Clients[index].Disconnected();
            }
        }

        public void ClientActivityChanged(int index)
        {
            DebugConsoleWriteLine(Clients[index].Username + " activity level is now " + Clients[index].CurrentActivity);
            int inGameCount = 0;
            int inFlightCount = 0;
            for (int i = 0; i < Clients.Length; i++)
                if (ValidClient(i))
                    switch (Clients[i].CurrentActivity)
                    {
                        case ServerClient.Activity.InGame:
                            inGameCount++;
                            break;
                        case ServerClient.Activity.InFlight:
                            inGameCount++;
                            inFlightCount++;
                            break;
                    }
            lock (ClientActivityCountLock)
            {
                ClientsInGame = inGameCount;
                ClientsInFlight = inFlightCount;
            }
            SendServerSettingsToAll();
        }

        private void AsyncUdpReceive(IAsyncResult result)
        {
            try
            {
                IPEndPoint endpoint = new IPEndPoint(IPAddress.Any, Configuration.Port);
                byte[] received = UdpConnection.EndReceive(result, ref endpoint);
                if (received.Length >= KLFCommon.MessageHeaderLength + 4)
                {
                    int index = 0;
                    //Get the sender index
                    int senderIndex = KLFCommon.BytesToInt(received, index);
                    index += 4;
                    //Get the message header data
                    KLFCommon.ClientMessageID id = (KLFCommon.ClientMessageID)KLFCommon.BytesToInt(received, index);
                    index += 4;
                    int dataLength = KLFCommon.BytesToInt(received, index);
                    index += 4;
                    //Get the data
                    byte[] data = null;
                    if (dataLength > 0 && dataLength <= received.Length - index)
                    {
                        data = new byte[dataLength];
                        Array.Copy(received, index, data, 0, data.Length);
                    }

                    if (ClientIsReady(senderIndex))
                    {
                        if ((CurrentMillisecond - Clients[senderIndex].LastUdpAckTime) > UdpAckThrottle)
                        {
                            //Acknowledge the client's message with a TCP message
                            Clients[senderIndex].QueueOutgoingMessage(KLFCommon.ServerMessageID.UdpAcknowledge, null);
                            Clients[senderIndex].LastUdpAckTime = CurrentMillisecond;
                        }
                        //Handle the message
                        HandleMessage(senderIndex, id, data);
                    }
                }
                UdpConnection.BeginReceive(AsyncUdpReceive, null); //Begin receiving the next message
            }
            catch (ThreadAbortException) {}
            catch (Exception e)
            {
                PassExceptionToMain(e);
            }
        }

        private int GetClientIndexByName(String name)
        {
            name = name.ToLower(); //Set name to lowercase to make the search case-insensitive
            for (int i = 0; i < Clients.Length; i++)
                if (ClientIsReady(i) && Clients[i].Username.ToLower() == name)
                    return i;
            return -1;
        }

        private IPAddress GetClientIP(int index)
        {
            return Clients[index].IP;
        }

        //Bans

        private void BanClient(int index)
        {
            if (ClientIsReady(index))
            {
                BanIP(GetClientIP(index));
                SaveBanList();
                DisconnectClient(index, "Banned from the server.");
            }
        }

        private void BanIP(IPAddress address)
        {
            if (BannedIPs.Add(address))
            {
                StampedConsoleWriteLine("Banned ip: " + address.ToString());
                SaveBanList();
            }
            else
                StampedConsoleWriteLine("IP " + address.ToString() + " was already banned.");
        }

        private void UnBanIP(IPAddress address)
        {
            if (BannedIPs.Remove(address))
            {
                StampedConsoleWriteLine("Unbanned ip: " + address.ToString());
                SaveBanList();
            }
            else
                StampedConsoleWriteLine("IP " + address.ToString() + " not found in ban list.");
        }

        private void ClearBans()
        {
            BannedIPs.Clear();
            SaveBanList();
        }

        private void LoadBanList()
        {
            TextReader reader = null;
            try
            {
                BannedIPs.Clear();
                reader = File.OpenText(BanFile);
                String line;
                do
                {
                    line = reader.ReadLine();
                    if (line != null)
                    {
                        IPAddress address;
                        if (IPAddress.TryParse(line, out address))
                            BannedIPs.Add(address);
                    }
                } while (line != null);
            }
            catch { }
            finally
            {
                if (reader != null)
                    reader.Close();
            }
        }

        private void SaveBanList()
        {
            try
            {
                if (File.Exists(BanFile))
                    File.Delete(BanFile);
                TextWriter writer = File.CreateText(BanFile);
                foreach (IPAddress address in BannedIPs)
                    writer.WriteLine(address.ToString());
                writer.Close();
            }
            catch {}
        }

        //HTTP
        private void AsyncHttpCallback(IAsyncResult result)
        {
            try
            {
                HttpListener listener = (HttpListener)result.AsyncState;
                HttpListenerContext context = listener.EndGetContext(result);
                HttpListenerRequest request = context.Request;
                HttpListenerResponse response = context.Response;
                //Build response string
                StringBuilder responseBuilder = new StringBuilder();
                responseBuilder.Append("Version: ");
                responseBuilder.Append(KLFCommon.ProgramVersion);
                responseBuilder.Append('\n');
                responseBuilder.Append("Port: ");
                responseBuilder.Append(Configuration.Port);
                responseBuilder.Append('\n');
                responseBuilder.Append("Num Players: ");
                responseBuilder.Append(NumClients);
                responseBuilder.Append('/');
                responseBuilder.Append(Configuration.MaxClients);
                responseBuilder.Append('\n');
                responseBuilder.Append("Players: ");
                bool first = true;
                for (int i = 0; i < Clients.Length; i++)
                    if (ClientIsReady(i))
                    {
                        if (first)
                            first = false;
                        else
                            responseBuilder.Append(", ");
                        responseBuilder.Append(Clients[i].Username);
                    }
                responseBuilder.Append('\n');
                responseBuilder.Append("Information: ");
                responseBuilder.Append(Configuration.ServerInfo);
                responseBuilder.Append('\n');
                responseBuilder.Append("Updates per Second: ");
                responseBuilder.Append(Configuration.UpdatesPerSecond);
                responseBuilder.Append('\n');
                responseBuilder.Append("Inactive Ship Limit: ");
                responseBuilder.Append(Configuration.TotalInactiveShips);
                responseBuilder.Append('\n');
                responseBuilder.Append("Screenshot Height: ");
                responseBuilder.Append(Configuration.ScreenshotSettings.MaxHeight);
                responseBuilder.Append('\n');
                responseBuilder.Append("Screenshot Save: ");
                responseBuilder.Append(Configuration.SaveScreenshots);
                responseBuilder.Append('\n');
                responseBuilder.Append("Screenshot Backlog: ");
                responseBuilder.Append(Configuration.ScreenshotBacklog);
                responseBuilder.Append('\n');
                //Send response
                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(responseBuilder.ToString());
                response.ContentLength64 = buffer.LongLength;
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Close();
                //Begin listening for the next http request
                listener.BeginGetContext(AsyncHttpCallback, listener);
            }
            catch (ThreadAbortException) {}
            catch (Exception e)
            {
                PassExceptionToMain(e);
            }
        }

        //Messages
        public void QueueClientMessage(int clientIndex, KLFCommon.ClientMessageID id, byte[] data)
        {
            ClientMessage message = new ClientMessage();
            message.ClientIndex = clientIndex;
            message.ID = id;
            message.Data = data;
            ClientMessageQueue.Enqueue(message);
        }

        public void HandleMessage(int clientIndex, KLFCommon.ClientMessageID id, byte[] data)
        {
            if (!ValidClient(clientIndex))
                return;
            DebugConsoleWriteLine("Message id: " + id.ToString() + " data: " + (data != null ? data.Length.ToString() : "0"));
            UnicodeEncoding encoder = new UnicodeEncoding();

            switch (id)
            {
                case KLFCommon.ClientMessageID.Handshake:
                    if (data != null)
                    {
                        StringBuilder sb = new StringBuilder();
                        //Read username
                        Int32 usernameLength = KLFCommon.BytesToInt(data, 0);
                        String username = encoder.GetString(data, 4, usernameLength);
                        int offset = 4 + usernameLength;
                        String version = encoder.GetString(data, offset, data.Length - offset);
                        String usernameLower = username.ToLower();
                        bool accepted = true;
                        //Ensure no other players have the same username
                        for (int i = 0; i < Clients.Length; i++)
                        {
                            if (i != clientIndex && ClientIsReady(i) && Clients[i].Username.ToLower() == usernameLower)
                            {
                                //Disconnect the player
                                DisconnectClient(clientIndex, "Your username is already in use.");
                                StampedConsoleWriteLine("Rejected client due to duplicate username: " + username);
                                accepted = false;
                                break;
                            }
                        }

                        if (!accepted)
                            break;

                        //Send the active user count to the client
                        if (NumClients == 2)
                        {
                            //Get the username of the other user on the server
                            sb.Append("There is currently 1 other user on this server: ");
                            for (int i = 0; i < Clients.Length; i++)
                            {
                                if (i != clientIndex && ClientIsReady(i))
                                {
                                    sb.Append(Clients[i].Username);
                                    break;
                                }
                            }
                        }
                        else
                        {
                            sb.Append("There are currently ");
                            sb.Append(NumClients - 1);
                            sb.Append(" other users on this server.");
                            if (NumClients > 1)
                                sb.Append(" Enter /list to see them.");
                        }
                        Clients[clientIndex].Username = username;
                        Clients[clientIndex].ReceivedHandshake = true;
                        SendServerMessage(clientIndex, sb.ToString());
                        SendServerSettings(clientIndex);
                        StampedConsoleWriteLine(username + " ("+GetClientIP(clientIndex).ToString()+") has joined the server using client version " + version);
                        if (!Clients[clientIndex].MessagesThrottled)
                        {//Build join message
                            sb.Clear();
                            sb.Append("User ");
                            sb.Append(username);
                            sb.Append(" has joined the server.");
                            //Send the join message to all other Clients
                            SendServerMessageToAll(sb.ToString(), clientIndex);
                        }
                        MessageFloodIncrement(clientIndex);
                    }
                    break;

                case KLFCommon.ClientMessageID.PrimaryPluginUpdate:
                case KLFCommon.ClientMessageID.SecondaryPluginUpdate:
                    if (data != null && ClientIsReady(clientIndex))
                    {
#if SEND_UPDATES_TO_SENDER
                        SendPluginUpdateToAll(data, id == KLFCommon.ClientMessageID.SecondaryPluginUpdate);
#else
                        SendPluginUpdateToAll(data, id == KLFCommon.ClientMessageID.SecondaryPluginUpdate, clientIndex);
#endif
                    }
                    break;

                case KLFCommon.ClientMessageID.TextMessage:
                    if (data != null && ClientIsReady(clientIndex))
                        HandleClientTextMessage(clientIndex, encoder.GetString(data, 0, data.Length));
                    break;

                case KLFCommon.ClientMessageID.ScreenWatchPlayer:
                    if(!ClientIsReady(clientIndex)
                    || data == null
                    || data.Length < 9)
                        break;
                    bool sendScreenshot = data[0] != 0;
                    int watchIndex = KLFCommon.BytesToInt(data, 1);
                    int currentIndex = KLFCommon.BytesToInt(data, 5);
                    String watchName = encoder.GetString(data, 9, data.Length - 9);
                    bool watchNameChanged = false;
                    lock (Clients[clientIndex].WatchPlayerNameLock)
                    {
                        if(watchName != Clients[clientIndex].WatchPlayerName
                        || watchIndex != Clients[clientIndex].WatchPlayerIndex)
                        {//Set the watch player name
                            Clients[clientIndex].WatchPlayerIndex = watchIndex;
                            Clients[clientIndex].WatchPlayerName = watchName;
                            watchNameChanged = true;
                        }
                    }

                    if (sendScreenshot && watchNameChanged && watchName.Length > 0)
                    {//Try to find the player the client is watching and send that player's current screenshot
                        int watchedIndex = GetClientIndexByName(watchName);
                        if (ClientIsReady(watchedIndex))
                        {
                            Screenshot screenshot = null;
                            lock (Clients[watchedIndex].ScreenshotLock)
                            {
                                screenshot = Clients[watchedIndex].GetScreenshot(watchIndex);
                                if (screenshot == null && watchIndex == -1)
                                    screenshot = Clients[watchedIndex].LastScreenshot;
                            }
                            if(screenshot != null
                            && screenshot.Index != currentIndex)
                                SendScreenshot(clientIndex, screenshot);
                        }
                    }
                    break;

                case KLFCommon.ClientMessageID.ScreenshotShare:
                    if (data != null && data.Length <= Configuration.ScreenshotSettings.MaxNumBytes && ClientIsReady(clientIndex))
                    {
                        if (!Clients[clientIndex].ScreenshotsThrottled)
                        {
                            StringBuilder sb = new StringBuilder();
                            Screenshot screenshot = new Screenshot();
                            screenshot.SetFromByteArray(data);
                            //Set the screenshot for the player
                            lock (Clients[clientIndex].ScreenshotLock)
                            {
                                Clients[clientIndex].PushScreenshot(screenshot);
                            }
                            sb.Append(Clients[clientIndex].Username);
                            sb.Append(" has shared a screenshot.");
                            SendTextMessageToAll(sb.ToString());
                            StampedConsoleWriteLine(sb.ToString());
                            //Send the screenshot to every client watching the player
                            SendScreenshotToWatchers(clientIndex, screenshot);
                            if (Configuration.SaveScreenshots)
                                saveScreenshot(screenshot, Clients[clientIndex].Username);
                        }
                        bool throttled = Clients[clientIndex].ScreenshotsThrottled;
                        Clients[clientIndex].ScreenshotFloodIncrement();
                        if (!throttled && Clients[clientIndex].ScreenshotsThrottled)
                        {
                            long throttleSeconds = Configuration.ScreenshotFloodThrottleTime / 1000;
                            SendServerMessage(clientIndex, "You have been restricted from sharing screenshots for " + throttleSeconds + " seconds.");
                            StampedConsoleWriteLine(Clients[clientIndex].Username + " has been restricted from sharing screenshots for " + throttleSeconds + " seconds.");
                        }
                        else if (Clients[clientIndex].CurrentThrottle.MessageFloodCounter == Configuration.ScreenshotFloodLimit - 1)
                            SendServerMessage(clientIndex, "Warning: You are sharing too many screenshots.");

                    }

                    break;

                case KLFCommon.ClientMessageID.ConnectionEnd:
                    String message = String.Empty;
                    if (data != null)
                        message = encoder.GetString(data, 0, data.Length); //Decode the message
                    DisconnectClient(clientIndex, message); //Disconnect the client
                    break;

                case KLFCommon.ClientMessageID.ShareCraftFile:
                    if(ClientIsReady(clientIndex)
                    && data != null
                    && data.Length > 5
                    && (data.Length - 5) <= KLFCommon.MaxCraftFileBytes)
                    {
                        if (Clients[clientIndex].MessagesThrottled)
                        {
                            MessageFloodIncrement(clientIndex);
                            break;
                        }
                        MessageFloodIncrement(clientIndex);

                        //Read craft name length
                        byte craftType = data[0];
                        int craftNameLength = KLFCommon.BytesToInt(data, 1);
                        if (craftNameLength < data.Length - 5)
                        {
                            //Read craft name
                            String craftName = encoder.GetString(data, 5, craftNameLength);
                            //Read craft bytes
                            byte[] craftBytes = new byte[data.Length - craftNameLength - 5];
                            Array.Copy(data, 5 + craftNameLength, craftBytes, 0, craftBytes.Length);

                            lock (Clients[clientIndex].SharedCraftLock)
                            {
                                Clients[clientIndex].SharedCraftName = craftName;
                                Clients[clientIndex].SharedCraftFile = craftBytes;
                                Clients[clientIndex].SharedCraftType = craftType;
                            }

                            //Send a message to players informing them that a craft has been shared
                            StringBuilder sb = new StringBuilder();
                            sb.Append(Clients[clientIndex].Username);
                            sb.Append(" shared ");
                            sb.Append(craftName);
                            switch (craftType)
                            {
                                case KLFCommon.CraftTypeVab:
                                    sb.Append(" (VAB)");
                                    break;
                                case KLFCommon.CraftTypeSph:
                                    sb.Append(" (SPH)");
                                    break;
                            }
                            StampedConsoleWriteLine(sb.ToString());
                            sb.Append(" . Enter " + KLFCommon.GetCraftCommand);
                            sb.Append(Clients[clientIndex].Username);
                            sb.Append(" to get it.");
                            SendTextMessageToAll(sb.ToString());
                        }
                    }
                    break;

                case KLFCommon.ClientMessageID.ActivityUpdateInFlight:
                    Clients[clientIndex].UpdateActivity(ServerClient.Activity.InFlight);
                    break;

                case KLFCommon.ClientMessageID.ActivityUpdateInGame:
                    Clients[clientIndex].UpdateActivity(ServerClient.Activity.InGame);
                    break;

                case KLFCommon.ClientMessageID.Ping:
                    Clients[clientIndex].QueueOutgoingMessage(KLFCommon.ServerMessageID.PingReply, null);
                    break;
            }
            DebugConsoleWriteLine("Handled message");
        }

        public void HandleClientTextMessage(int clientIndex, String messageText)
        {
            if (Clients[clientIndex].MessagesThrottled)
            {
                MessageFloodIncrement(clientIndex);
                return;
            }
            MessageFloodIncrement(clientIndex);

            StringBuilder sb = new StringBuilder();
            if (messageText.Length > 0 && messageText.First() == '/')
            {
                string msgLow = messageText.ToLower();
                if (msgLow == "/list")
                {//Compile list of usernames
                    sb.Append("Connected users:\n");
                    for (int i = 0; i < Clients.Length; i++)
                    {
                        if (ClientIsReady(i))
                        {
                            sb.Append(Clients[i].Username);
                            sb.Append('\n');
                        }
                    }
                    SendTextMessage(clientIndex, sb.ToString());
                    return;
                }
                else if (msgLow == "/quit")
                {
                    DisconnectClient(clientIndex, "Requested quit");
                    return;
                }
                else if(msgLow.Length > (KLFCommon.GetCraftCommand.Length + 1)
                     && msgLow.Substring(0, KLFCommon.GetCraftCommand.Length) == KLFCommon.GetCraftCommand)
                {
                    String playerName = msgLow.Substring(KLFCommon.GetCraftCommand.Length + 1);

                    //Find the player with the given name
                    int targetIndex = GetClientIndexByName(playerName);
                    if (ClientIsReady(targetIndex))
                    {
                        //Send the client the craft data
                        lock (Clients[targetIndex].SharedCraftLock)
                        {
                            if(Clients[targetIndex].SharedCraftName.Length > 0
                            && Clients[targetIndex].SharedCraftFile != null
                            && Clients[targetIndex].SharedCraftFile.Length > 0)
                            {
                                SendCraftFile( clientIndex
                                             , Clients[targetIndex].SharedCraftName
                                             , Clients[targetIndex].SharedCraftFile
                                             , Clients[targetIndex].SharedCraftType);
                                StampedConsoleWriteLine("Sent craft " + Clients[targetIndex].SharedCraftName
                                                        + " to client " + Clients[clientIndex].Username);
                            }
                        }
                    }
                    return;
                }
            }

            //Compile full message
            sb.Append('[');
            sb.Append(Clients[clientIndex].Username);
            sb.Append("] ");
            sb.Append(messageText);

            String fullMessage = sb.ToString();
            //Console.SetCursorPosition(0, Console.CursorTop);
            StampedConsoleWriteLine(fullMessage);
            //Send the update to all other Clients
            SendTextMessageToAll(fullMessage, clientIndex);
        }

        public static byte[] BuildMessageArray(KLFCommon.ServerMessageID id, byte[] data)
        {
            //Construct the byte array for the message
            int messageDataLength = 0;
            if (data != null)
                messageDataLength = data.Length;
            byte[] messageBytes = new byte[KLFCommon.MessageHeaderLength + messageDataLength];
            KLFCommon.IntToBytes((int)id).CopyTo(messageBytes, 0);
            KLFCommon.IntToBytes(messageDataLength).CopyTo(messageBytes, 4);
            if (data != null)
                data.CopyTo(messageBytes, KLFCommon.MessageHeaderLength);
            return messageBytes;
        }

        private void SendMessageHeaderDirect(TcpClient client, KLFCommon.ServerMessageID id, int msgLength)
        {
            client.GetStream().Write(KLFCommon.IntToBytes((int)id), 0, 4);
            client.GetStream().Write(KLFCommon.IntToBytes(msgLength), 0, 4);
            DebugConsoleWriteLine("Sending message: " + id.ToString());
        }

        private void SendHandshakeRefusalMessageDirect(TcpClient client, String message)
        {
            try
            {
                UnicodeEncoding encoder = new UnicodeEncoding();
                byte[] messageBytes = encoder.GetBytes(message);
                SendMessageHeaderDirect(client, KLFCommon.ServerMessageID.HandshakeRefusal, messageBytes.Length);
                client.GetStream().Write(messageBytes, 0, messageBytes.Length);
                client.GetStream().Flush();
            }
            catch (System.IO.IOException) {}
            catch (System.ObjectDisposedException) {}
            catch (System.InvalidOperationException) {}
        }

        private void SendConnectionEndMessageDirect(TcpClient client, String message)
        {
            try
            {
                UnicodeEncoding encoder = new UnicodeEncoding();
                byte[] messageBytes = encoder.GetBytes(message);
                SendMessageHeaderDirect(client, KLFCommon.ServerMessageID.ConnectionEnd, messageBytes.Length);
                client.GetStream().Write(messageBytes, 0, messageBytes.Length);
                client.GetStream().Flush();
            }
            catch (System.IO.IOException) {}
            catch (System.ObjectDisposedException) {}
            catch (System.InvalidOperationException) {}
        }

        private void SendHandshakeMessage(int clientIndex)
        {
            //Encode version string
            UnicodeEncoding encoder = new UnicodeEncoding();
            byte[] versionBytes = encoder.GetBytes(KLFCommon.ProgramVersion);
            byte[] dataBytes = new byte[versionBytes.Length + 12];
            //Write net protocol version
            KLFCommon.IntToBytes(KLFCommon.NetProtocolVersion).CopyTo(dataBytes, 0);
            //Write version string length
            KLFCommon.IntToBytes(versionBytes.Length).CopyTo(dataBytes, 4);
            //Write version string
            versionBytes.CopyTo(dataBytes, 8);
            //Write client ID
            KLFCommon.IntToBytes(clientIndex).CopyTo(dataBytes, 8 + versionBytes.Length);
            Clients[clientIndex].QueueOutgoingMessage(KLFCommon.ServerMessageID.Handshake, dataBytes);
        }

        private void SendServerMessageToAll(String message, int excludeIndex = -1)
        {
            UnicodeEncoding encoder = new UnicodeEncoding();
            byte[] messageBytes = BuildMessageArray(KLFCommon.ServerMessageID.ServerMessage, encoder.GetBytes(message));

            for (int i = 0; i < Clients.Length; i++)
                if ((i != excludeIndex) && ClientIsReady(i))
                    Clients[i].QueueOutgoingMessage(messageBytes);
        }

        private void SendServerMessage(int clientIndex, String message)
        {
            UnicodeEncoding encoder = new UnicodeEncoding();
            Clients[clientIndex].QueueOutgoingMessage(KLFCommon.ServerMessageID.ServerMessage, encoder.GetBytes(message));
        }

        private void SendTextMessageToAll(String message, int excludeIndex = -1)
        {
            UnicodeEncoding encoder = new UnicodeEncoding();
            byte[] messageBytes = BuildMessageArray(KLFCommon.ServerMessageID.TextMessage, encoder.GetBytes(message));
            for (int i = 0; i < Clients.Length; i++)
                if ((i != excludeIndex) && ClientIsReady(i))
                    Clients[i].QueueOutgoingMessage(messageBytes);
        }

        private void SendTextMessage(int clientIndex, String message)
        {
            UnicodeEncoding encoder = new UnicodeEncoding();
            Clients[clientIndex].QueueOutgoingMessage(KLFCommon.ServerMessageID.ServerMessage, encoder.GetBytes(message));
        }

        //Send the update to all other Clients
        private void SendPluginUpdateToAll(byte[] data, bool inFlightOnly, int excludeIndex = -1)
        {
            byte[] messageBytes = BuildMessageArray(KLFCommon.ServerMessageID.PluginUpdate, data);
            //Make sure the client is valid and in-game
            for (int i = 0; i < Clients.Length; i++)
                if(i != excludeIndex
                && ClientIsReady(i)
                && Clients[i].CurrentActivity != ServerClient.Activity.Inactive
                &&(Clients[i].CurrentActivity == ServerClient.Activity.InFlight || !inFlightOnly))
                    Clients[i].QueueOutgoingMessage(messageBytes);
        }

        private void SendScreenshot(int clientIndex, Screenshot screenshot)
        {
            Clients[clientIndex].QueueOutgoingMessage(KLFCommon.ServerMessageID.ScreenshotShare, screenshot.ToByteArray());
        }

        private void SendScreenshotToWatchers(int clientIndex, Screenshot screenshot)
        {
            //Create a list of valid watchers
            List<int> watcherIndices = new List<int>();

            for (int i = 0; i < Clients.Length; i++)
                if (ClientIsReady(i) && Clients[i].CurrentActivity != ServerClient.Activity.Inactive)
                {
                    bool match = false;
                    lock (Clients[i].WatchPlayerNameLock)
                    {
                        match = Clients[i].WatchPlayerName == Clients[clientIndex].Username;
                    }
                    if (match)
                        watcherIndices.Add(i);
                }

            if (watcherIndices.Count > 0)
            {
                //Build the message and send it to all watchers
                byte[] messageBytes = BuildMessageArray(KLFCommon.ServerMessageID.ScreenshotShare, screenshot.ToByteArray());
                foreach (int i in watcherIndices)
                    Clients[i].QueueOutgoingMessage(messageBytes);
            }
        }

        private void SendCraftFile(int clientIndex, String craftName, byte[] data, byte type)
        {
            UnicodeEncoding encoder = new UnicodeEncoding();
            byte[] nameBytes = encoder.GetBytes(craftName);
            byte[] bytes = new byte[5 + nameBytes.Length + data.Length];
            //Copy data
            bytes[0] = type;
            KLFCommon.IntToBytes(nameBytes.Length).CopyTo(bytes, 1);
            nameBytes.CopyTo(bytes, 5);
            data.CopyTo(bytes, 5 + nameBytes.Length);
            Clients[clientIndex].QueueOutgoingMessage(KLFCommon.ServerMessageID.CraftFile, bytes);
        }

        private void SendServerSettingsToAll()
        {
            //Build the message array
            byte[] settingBytes = ServerSettingBytes();
            byte[] messageBytes = BuildMessageArray(KLFCommon.ServerMessageID.ServerSettings, settingBytes);
            //Send to Clients
            for (int i = 0; i < Clients.Length; i++)
                if (ValidClient(i))
                    Clients[i].QueueOutgoingMessage(messageBytes);
        }

        private void SendServerSettings(int clientIndex)
        {
            Clients[clientIndex].QueueOutgoingMessage(KLFCommon.ServerMessageID.ServerSettings, ServerSettingBytes());
        }

        private byte[] ServerSettingBytes()
        {
            byte[] bytes = new byte[KLFCommon.ServerSettingsLength];
            KLFCommon.IntToBytes(UpdateInterval).CopyTo(bytes, 0); //Update interval
            KLFCommon.IntToBytes(Configuration.ScreenshotInterval).CopyTo(bytes, 4); //Screenshot interval
            KLFCommon.IntToBytes(Configuration.ScreenshotSettings.MaxHeight).CopyTo(bytes, 8); //Screenshot height
            bytes[12] = InactiveShipsPerClient; //Inactive ships per client

            return bytes;
        }

        //Flood limit

        void MessageFloodIncrement(int index)
        {
            bool throttled = Clients[index].MessagesThrottled;
            Clients[index].MessageFloodIncrement();
            if (ValidClient(index) && !throttled && Clients[index].MessagesThrottled)
            {
                long throttleSeconds = Configuration.MessageFloodThrottleTime / 1000;
                SendServerMessage(index, "You have been restricted from sending messages for " + throttleSeconds + " seconds.");
                StampedConsoleWriteLine(Clients[index].Username + " has been restricted from sending messages for " + throttleSeconds + " seconds.");
            }
        }
    }
}
