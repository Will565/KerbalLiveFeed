using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;

using System.IO;

abstract class Client
{
    //Constants
    public const String CraftFileExtension = ".craft";
    public const int MaxTextMessageQueue = 256;
    public const long KeepAliveDelay = 1000;
    public const long UdpProbeDelay = 1000;
    public const long UdpTimeoutDelay = 20000;
    public const int SleepTime = 15;
    public const int ClientDataForceWriteInterval = 10000;
    public const int ReconnectDelay = 1000;
    public const int MaxReconnectAttempts = 3;
    public const long PingTimeoutDelay = 10000;
    public const int InteropWriteInterval = 100;
    public const int InteropMaxQueueSize = 128;
    public const int MaxQueuedChatLines = 8;
    public const int MaxCachedScreenshots = 8;
    public const int DefaultPort = 2075;
    public const String InteropClientFilename = "GameData/KLF/Plugins/PluginData/KerbalLiveFeed/interopclient.txt";
    public const String InteropPluginFilename = "GameData/KLF/Plugins/PluginData/KerbalLiveFeed/interopplugin.txt";
    public const String PluginDirectory = "GameData/KLF/Plugins/PluginData/KerbalLiveFeed/";

    //Static
    public static UnicodeEncoding Encoder = new UnicodeEncoding();

    //Connection
    private ClientSettings ClientConfiguration;
    public int ClientID;
    public bool EndSession;
    public bool IntentionalConnectionEnd;
    public bool HandshakeComplete;
    public TcpClient TcpConnection;
    public long LastTcpMessageSendTime;
    public Socket UdpSocket;
    public bool UdpConnected;
    public long LastUdpMessageSendTime;
    public long LastUdpAckReceiveTime;
    public bool IsConnected
    {
        get
        {
            return !EndSession && TcpConnection != null && !IntentionalConnectionEnd && TcpConnection.Connected;
        }
    }

    //Server Settings
    public int UpdateInterval = 500;
    public int ScreenshotInterval = 1000;
    public byte InactiveShipsPerUpdate = 0;
    public ScreenshotSettings ScreenshotConfiguration = new ScreenshotSettings();
    protected long LastScreenshotShareTime;
    protected byte[] QueuedOutScreenshot;
    protected List<Screenshot> CachedScreenshots;
    protected String CurrentGameTitle;
    protected int WatchPlayerIndex;
    protected String WatchPlayerName;
    protected long LastClientDataWriteTime;
    protected long LastClientDataChangeTime;

    //Messages
    protected byte[] CurrentMessageHeader = new byte[KLFCommon.MessageHeaderLength];
    protected int CurrentMessageHeaderIndex;
    protected byte[] CurrentMessageData;
    protected int CurrentMessageDataIndex;
    protected KLFCommon.ServerMessageID CurrentMessageID;
    protected byte[] ReceiveBuffer = new byte[8192];
    protected int ReceiveIndex = 0;
    protected int ReceiveHandleIndex = 0;

    //Threading
    protected object TcpSendLock = new object();
    protected object ServerSettingsLock = new object();
    protected object ScreenshotOutLock = new object();
    protected object ThreadExceptionLock = new object();
    protected object ClientDataLock = new object();
    protected object UdpTimestampLock = new object();

    protected Stopwatch ClientStopwatch;
    protected Stopwatch PingStopwatch = new Stopwatch();

    //Structs
    public struct InTextMessage
    {
        public bool FromServer;
        public String message;
    }
    public struct InteropMessage
    {
        public int id;
        public byte[] data;
    }
    public struct ServerMessage
    {
        public KLFCommon.ServerMessageID id;
        public byte[] data;
    }

    //Constructor
    public Client()
    {
        ClientStopwatch = new Stopwatch();
        ClientStopwatch.Start();
    }

    /* Attempt to connect */
    public bool ConnectToServer(ClientSettings settings)
    {
        if (IsConnected)
            return false;
        ClientConfiguration = settings;
        TcpConnection = new TcpClient();
        String host = ClientConfiguration.GetDefaultServer().Host;
        Int32 port = ClientConfiguration.GetDefaultServer().Port;

        //attempt to resolve dns
        IPHostEntry hostEntry = new IPHostEntry();
        try { hostEntry = Dns.GetHostEntry(host); }
        catch (SocketException) { hostEntry = null; }
        catch (ArgumentException) { hostEntry = null; }

        //get an IP address
        IPAddress address = null;
        if (hostEntry != null && hostEntry.AddressList.Length == 1)//too strict?
            address = hostEntry.AddressList.First();
        else
            IPAddress.TryParse(host, out address);
        if (address == null)
        {
            Console.WriteLine("Invalid server address.");
            return false;
        }

        //make the connection
        IPEndPoint endPoint = new IPEndPoint(address, port);
        Console.WriteLine("Connecting to server...");
        try
        {
            TcpConnection.Connect(endPoint);
            if (TcpConnection.Connected)
            {
                try
                {
                    UdpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    UdpSocket.Connect(endPoint);
                }
                catch
                {
                    if (UdpSocket != null)
                        UdpSocket.Close();
                    UdpSocket = null;
                }
                UdpConnected = false;
                LastUdpAckReceiveTime = 0;
                LastUdpMessageSendTime = ClientStopwatch.ElapsedMilliseconds;
                ConnectionStarted();
                return true;
            }
        }
        catch (SocketException e)
        {
            Console.WriteLine("Exception: " + e.ToString());
        }
        catch (ObjectDisposedException e)
        {
            Console.WriteLine("Exception: " + e.ToString());
        }
        return false;
    }

    /* initialize */
    protected virtual void ConnectionStarted()
    {
        ClientID = -1;
        EndSession = false;
        IntentionalConnectionEnd = false;
        HandshakeComplete = false;
        CachedScreenshots = new List<Screenshot>();
        CurrentGameTitle = String.Empty;
        WatchPlayerName = String.Empty;
        LastScreenshotShareTime = 0;
        LastTcpMessageSendTime = 0;
        LastClientDataWriteTime = 0;
        LastClientDataChangeTime = ClientStopwatch.ElapsedMilliseconds;
        BeginAsyncRead();
    }

    /* overridable */
    protected virtual void ConnectionEnded()
    {
        ClearConnectionState();
    }

    protected void HandleMessage(KLFCommon.ServerMessageID id, byte[] data)
    {
        switch (id)
        {
        case KLFCommon.ServerMessageID.Handshake:
            Int32 protocolVersion = KLFCommon.BytesToInt(data);
            if (data.Length >= 8)
            {
                Int32 server_version_length = KLFCommon.BytesToInt(data, 4);
                if (data.Length >= 12 + server_version_length)
                {
                    String server_version = Encoder.GetString(data, 8, server_version_length);
                    ClientID = KLFCommon.BytesToInt(data, 8 + server_version_length);
                    Console.WriteLine("Handshake received. Server is running version: " + server_version);
                }
            }
            //End the session if the protocol versions don't match
            if (protocolVersion != KLFCommon.NetProtocolVersion)
            {
                Console.WriteLine("Server version is incompatible with client version.");
                EndSession = true;
                IntentionalConnectionEnd = true;
            }
            else
            {
                SendHandshakeMessage(); //Reply to the handshake
                lock (UdpTimestampLock)
                {
                    LastUdpMessageSendTime = ClientStopwatch.ElapsedMilliseconds;
                }
                HandshakeComplete = true;
            }
            break;

        case KLFCommon.ServerMessageID.HandshakeRefusal:
            String refusal_message = Encoder.GetString(data, 0, data.Length);
            EndSession = true;
            IntentionalConnectionEnd = true;
            EnqueuePluginChatMessage("Server refused connection. Reason: " + refusal_message, true);
            break;

        case KLFCommon.ServerMessageID.ServerMessage:
        case KLFCommon.ServerMessageID.TextMessage:
            if (data != null)
            {
                InTextMessage inMessage = new InTextMessage();
                inMessage.FromServer = (id == KLFCommon.ServerMessageID.ServerMessage);
                inMessage.message = Encoder.GetString(data, 0, data.Length);
                //Queue the message
                EnqueueTextMessage(inMessage);
            }
            break;

        case KLFCommon.ServerMessageID.PluginUpdate:
            if (data != null)
                SendClientInteropMessage(KLFCommon.ClientInteropMessageID.PluginUpdate, data);
            break;

        case KLFCommon.ServerMessageID.ServerSettings:
            lock (ServerSettingsLock)
            {
                if (data != null && data.Length >= KLFCommon.ServerSettingsLength && HandshakeComplete)
                {
                    UpdateInterval = KLFCommon.BytesToInt(data, 0);
                    ScreenshotInterval = KLFCommon.BytesToInt(data, 4);
                    lock (ClientDataLock)
                    {
                        int new_screenshot_height = KLFCommon.BytesToInt(data, 8);
                        if (ScreenshotConfiguration.MaxHeight != new_screenshot_height)
                        {
                            ScreenshotConfiguration.MaxHeight = new_screenshot_height;
                            LastClientDataChangeTime = ClientStopwatch.ElapsedMilliseconds;
                            EnqueueTextMessage("Screenshot Height has been set to " + ScreenshotConfiguration.MaxHeight);
                        }
                        if (InactiveShipsPerUpdate != data[12])
                        {
                            InactiveShipsPerUpdate = data[12];
                            LastClientDataChangeTime = ClientStopwatch.ElapsedMilliseconds;
                        }
                    }
                }
            }
            break;

        case KLFCommon.ServerMessageID.ScreenshotShare:
            if (data != null
            && data.Length > 0
            && data.Length < ScreenshotConfiguration.MaxNumBytes
            && WatchPlayerName.Length > 0)
            {
                //Cache the screenshot
                Screenshot screenshot = new Screenshot();
                screenshot.SetFromByteArray(data);
                CacheScreenshot(screenshot);
                //Send the screenshot to the client
                SendClientInteropMessage(KLFCommon.ClientInteropMessageID.ScreenshotReceive, data);
            }
            break;

        case KLFCommon.ServerMessageID.ConnectionEnd:
            if (data != null)
            {
                String message = Encoder.GetString(data, 0, data.Length);
                EndSession = true;
                //If the reason is not a timeout, connection end is intentional
                IntentionalConnectionEnd = message.ToLower() != "timeout";
                EnqueuePluginChatMessage("Server closed the connection: " + message, true);
            }

            break;

        case KLFCommon.ServerMessageID.UdpAcknowledge:
            lock (UdpTimestampLock)
            {
                LastUdpAckReceiveTime = ClientStopwatch.ElapsedMilliseconds;
            }
            break;

        case KLFCommon.ServerMessageID.CraftFile:
            if (data != null && data.Length > 4)
            {
                //Read craft name length
                byte craftType = data[0];
                int craftName_length = KLFCommon.BytesToInt(data, 1);
                if (craftName_length < data.Length - 5)
                {
                    //Read craft name
                    String craftName = Encoder.GetString(data, 5, craftName_length);
                    //Read craft bytes
                    byte[] craft_bytes = new byte[data.Length - craftName_length - 5];
                    Array.Copy(data, 5 + craftName_length, craft_bytes, 0, craft_bytes.Length);
                    //Write the craft to a file
                    String filename = GetCraftFilename(craftName, craftType);
                    if (filename != null)
                    {
                        try
                        {
                            File.WriteAllBytes(filename, craft_bytes);
                            EnqueueTextMessage("Received craft file: " + craftName);
                        }
                        catch
                        {
                            EnqueueTextMessage("Error saving received craft file: " + craftName);
                        }
                    }
                    else
                        EnqueueTextMessage("Unable to save received craft file.");
                }
            }
            break;

        case KLFCommon.ServerMessageID.PingReply:
            if (PingStopwatch.IsRunning)
            {
                EnqueueTextMessage("Ping Reply: " + PingStopwatch.ElapsedMilliseconds + "ms");
                PingStopwatch.Stop();
                PingStopwatch.Reset();
            }
            break;
        }
    }

    protected void ClearConnectionState()
    {
        if (TcpConnection != null)
            TcpConnection.Close();
        if (UdpSocket != null)
            UdpSocket.Close();
        UdpSocket = null;
    }

    protected void HandleChatInput(String line)
    {
        if(line.Length > 0 && line.ElementAt(0) == '/')
        {
            String[] InputArgs = line.Split(' ');
            switch(InputArgs[0].ToLowerInvariant())
            {
                case "/quit":
                    IntentionalConnectionEnd = true;
                    EndSession = true;
                    SendConnectionEndMessage("Quit");
                    break;
                case "/crash":
                    Object o = null;
                    o.ToString();
                    break;
                case "/ping":
                    if (!PingStopwatch.IsRunning)
                    {
                        SendMessageTcp(KLFCommon.ClientMessageID.Ping, null);
                        PingStopwatch.Start();
                    }
                    break;
                case KLFCommon.ShareCraftCommand:
                    if(InputArgs.Length > 1)
                    {
                        String craftName = String.Join(" ", InputArgs, 1, InputArgs.Length - 2);
                        byte craftType = 0;
                        String filename = FindCraftFilename(craftName, ref craftType);
                        if (filename != null && filename.Length > 0)
                        {
                            try
                            {
                                byte[] craftBytes = File.ReadAllBytes(filename);
                                SendShareCraftMessage(craftName, craftBytes, craftType);
                            }
                            catch
                            {
                                EnqueueTextMessage("Error reading craft file: " + filename);
                            }
                        }
                        else
                            EnqueueTextMessage("Craft file not found: " + craftName);
                    }
                    break;
                default:
                    EnqueueTextMessage("Unrecognized command: " + InputArgs[0]);
                    break;
            }
        }
        else
            SendTextMessage(line);
    }

    /* HandleConnection method needs a rewrite, unreliable */
    protected void HandleConnection()
    {
        //Send a keep-alive message to prevent timeout
        if (ClientStopwatch.ElapsedMilliseconds - LastTcpMessageSendTime >= KeepAliveDelay)
            SendMessageTcp(KLFCommon.ClientMessageID.KeepAlive, null);
        if (UdpSocket != null && HandshakeComplete)
        {
            //Update the status of the udp connection
            long lastUdpAck = 0;
            long lastUdpSend = 0;
            lock (UdpTimestampLock)
            {
                lastUdpAck = LastUdpAckReceiveTime;
                lastUdpSend = LastUdpMessageSendTime;
            }
            bool udpShouldBeConnected = lastUdpAck > 0
                && (ClientStopwatch.ElapsedMilliseconds - lastUdpAck) < UdpTimeoutDelay;
            if (UdpConnected != udpShouldBeConnected)
            {
                if (udpShouldBeConnected)
                    EnqueueTextMessage("Udp connection established.", false, true);
                else
                    EnqueueTextMessage("Udp connection lost.", false, true);
                UdpConnected = udpShouldBeConnected;
            }
            //Send a probe message to try to establish a udp connection
            if ((ClientStopwatch.ElapsedMilliseconds - lastUdpSend) > UdpProbeDelay)
                SendUdpProbeMessage();
        }
    }

    //Plugin Interop
    public void ThrottledShareScreenshots()
    {
        //Throttle the rate at which you can share screenshots
        if (ClientStopwatch.ElapsedMilliseconds - LastScreenshotShareTime > ScreenshotInterval)
        {
            lock (ScreenshotOutLock)
            {
                if (QueuedOutScreenshot != null)
                {
                    //Share the screenshot
                    SendShareScreenshotMessage(QueuedOutScreenshot);
                    QueuedOutScreenshot = null;
                    LastScreenshotShareTime = ClientStopwatch.ElapsedMilliseconds;
                }
            }
        }
    }

    protected void HandleInteropMessage(int id, byte[] data)
    {
        HandleInteropMessage((KLFCommon.PluginInteropMessageID)id, data);
    }

    protected void HandleInteropMessage(KLFCommon.PluginInteropMessageID id, byte[] data)
    {
        switch (id)
        {
            case KLFCommon.PluginInteropMessageID.ChatSend:
                if (data != null)
                {
                    String line = Encoder.GetString(data);
                    InTextMessage message = new InTextMessage();
                    message.FromServer = false;
                    message.message = "[" + ClientConfiguration.Username + "] " + line;
                    EnqueueTextMessage(message, false);
                    HandleChatInput(line);
                }
                break;

            case KLFCommon.PluginInteropMessageID.PluginData:
                //String new_watch_player_name = String.Empty;
                if (data != null && data.Length >= 9)
                {
                    int index = 0;
                    //Read current activity status
                    bool inFlight = data[index] != 0;
                    index++;
                    //Read current game title
                    int CurrentGameTitleLength = KLFCommon.BytesToInt(data, index);
                    index += 4;
                    CurrentGameTitle = Encoder.GetString(data, index, CurrentGameTitleLength);
                    index += CurrentGameTitleLength;
                    //Send the activity status to the server
                    if (inFlight)
                        SendMessageTcp(KLFCommon.ClientMessageID.ActivityUpdateInFlight, null);
                    else
                        SendMessageTcp(KLFCommon.ClientMessageID.ActivityUpdateInFlight, null);
                }
                break;

            case KLFCommon.PluginInteropMessageID.PrimaryPluginUpdate:
                SendPluginUpdate(data, true);
                break;

            case KLFCommon.PluginInteropMessageID.SecondaryPluginUpdate:
                SendPluginUpdate(data, false);
                break;

            case KLFCommon.PluginInteropMessageID.ScreenshotShare:
                if (data != null)
                {
                    lock (ScreenshotOutLock)
                    {
                        QueuedOutScreenshot = data;
                    }
                }
                break;

            case KLFCommon.PluginInteropMessageID.ScreenshotWatchUpdate:
                if (data != null && data.Length >= 8)
                {
                    int index = KLFCommon.BytesToInt(data, 0);
                    int currentIndex = KLFCommon.BytesToInt(data, 4);
                    String name = Encoder.GetString(data, 8, data.Length - 8);
                    if (WatchPlayerName != name || WatchPlayerIndex != index)
                    {
                        WatchPlayerName = name;
                        WatchPlayerIndex = index;
                        //Look in the screenshot cache for the requested screenshot
                        Screenshot cached = getCachedScreenshot(WatchPlayerIndex, WatchPlayerName);
                        if (cached != null)
                            SendClientInteropMessage(KLFCommon.ClientInteropMessageID.ScreenshotReceive, cached.ToByteArray());
                        SendScreenshotWatchPlayerMessage((cached == null), currentIndex, WatchPlayerIndex, WatchPlayerName);
                    }
                }
                break;
        }
    }

    protected abstract void SendClientInteropMessage(KLFCommon.ClientInteropMessageID id, byte[] data);

    protected byte[] EncodeInteropMessage(int id, byte[] data)
    {
        int msgDataLength = 0;
        if (data != null)
            msgDataLength = data.Length;
        byte[] messageBytes = new byte[KLFCommon.InteropMessageHeaderLength + msgDataLength];
        KLFCommon.IntToBytes((int)id).CopyTo(messageBytes, 0);
        KLFCommon.IntToBytes(msgDataLength).CopyTo(messageBytes, 4);
        if (data != null)
            data.CopyTo(messageBytes, KLFCommon.InteropMessageHeaderLength);
        return messageBytes;
    }

    protected void WriteClientData()
    {
        lock (ClientDataLock)
        {
            if (LastClientDataChangeTime > LastClientDataWriteTime
            || (ClientStopwatch.ElapsedMilliseconds - LastClientDataWriteTime) > ClientDataForceWriteInterval)
            {
                byte[] usernameBytes = Encoder.GetBytes(ClientConfiguration.Username);
                //Build client data array
                byte[] bytes = new byte[9 + usernameBytes.Length];
                bytes[0] = InactiveShipsPerUpdate;
                KLFCommon.IntToBytes(ScreenshotConfiguration.MaxHeight).CopyTo(bytes, 1);
                KLFCommon.IntToBytes(UpdateInterval).CopyTo(bytes, 5);
                usernameBytes.CopyTo(bytes, 9);
                SendClientInteropMessage(KLFCommon.ClientInteropMessageID.ClientData, bytes);
                LastClientDataWriteTime = ClientStopwatch.ElapsedMilliseconds;
            }
        }
    }

    protected void EnqueueTextMessage(String message, bool FromServer = false, bool toPlugin = true)
    {
        InTextMessage textMessage = new InTextMessage();
        textMessage.message = message;
        textMessage.FromServer = FromServer;
        EnqueueTextMessage(textMessage, toPlugin);
    }

    protected virtual void EnqueueTextMessage(InTextMessage message, bool toPlugin = true)
    {
        if (toPlugin)
            if (message.FromServer)
                EnqueuePluginChatMessage("[Server] " + message.message, false);
            else
                EnqueuePluginChatMessage(message.message);
    }

    protected void EnqueuePluginChatMessage(String message, bool print = false)
    {
        SendClientInteropMessage
            ( KLFCommon.ClientInteropMessageID.ChatReceive
            , Encoder.GetBytes(message)
            );
        if(print)
            Console.WriteLine(message);
    }

    protected void SafeDelete(String filename)
    {
        if (File.Exists(filename))
        {
            try
            {
                File.Delete(filename);
            }
            catch (System.UnauthorizedAccessException) {}
            catch (System.IO.IOException) {}
        }
    }

    protected String FindCraftFilename(String craftName, ref byte craftType)
    {
        String vabFilename = GetCraftFilename(craftName, KLFCommon.CraftTypeVab);
        if (vabFilename != null && File.Exists(vabFilename))
        {
            craftType = KLFCommon.CraftTypeVab;
            return vabFilename;
        }
        String sphFilename = GetCraftFilename(craftName, KLFCommon.CraftTypeSph);
        if (sphFilename != null && File.Exists(sphFilename))
        {
            craftType = KLFCommon.CraftTypeSph;
            return sphFilename;
        }
        return null;
    }

    /* TODO improve filename filtering */
    protected String GetCraftFilename(String craftName, byte craftType)
    {
        //Filter the craft name for illegal characters
        String filteredCraftName = KLFCommon.FilteredFileName(craftName.Replace('.', '_'));
        String result="";
        if (CurrentGameTitle.Length <= 0 || filteredCraftName.Length <= 0)
            return null;
        switch (craftType)
        {
            case KLFCommon.CraftTypeVab:
                result= "saves/" + CurrentGameTitle + "/Ships/VAB/" + filteredCraftName + CraftFileExtension;
                return result;

            case KLFCommon.CraftTypeSph:
                result= "saves/" + CurrentGameTitle + "/Ships/SPH/" + filteredCraftName + CraftFileExtension;
                return result;
        }
        return null;
    }

    protected void CacheScreenshot(Screenshot screenshot)
    {
        foreach (Screenshot cachedScreenshot in CachedScreenshots)
            if (cachedScreenshot.Index == screenshot.Index && cachedScreenshot.Player == screenshot.Player)
                return;
        CachedScreenshots.Add(screenshot);
        while (CachedScreenshots.Count > MaxCachedScreenshots)
            CachedScreenshots.RemoveAt(0);
    }

    protected Screenshot getCachedScreenshot(int index, string player)
    {
        foreach (Screenshot cachedScreenshot in CachedScreenshots)
            if (cachedScreenshot.Index == index && cachedScreenshot.Player == player)
            {
                //Put the screenshot at the end of the list to keep it from being uncached a little longer
                CachedScreenshots.Remove(cachedScreenshot);
                CachedScreenshots.Add(cachedScreenshot);
                return cachedScreenshot;
            }
        return null;
    }

    //Messages
    protected void BeginAsyncRead()
    {
        try
        {
            if (TcpConnection != null)
            {
                CurrentMessageHeaderIndex = 0;
                CurrentMessageDataIndex = 0;
                ReceiveIndex = 0;
                ReceiveHandleIndex = 0;
                TcpConnection.GetStream().BeginRead
                    ( ReceiveBuffer
                    , ReceiveIndex
                    , ReceiveBuffer.Length - ReceiveIndex
                    , AsyncReceive
                    , ReceiveBuffer
                    );
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.IO.IOException)
        {
        }
    }

    protected void AsyncReceive(IAsyncResult result)
    {
        try
        {
            int read = TcpConnection.GetStream().EndRead(result);
            if (read > 0)
            {
                ReceiveIndex += read;
                HandleReceive();
            }
            TcpConnection.GetStream().BeginRead
                ( ReceiveBuffer
                , ReceiveIndex
                , ReceiveBuffer.Length - ReceiveIndex
                , AsyncReceive
                , ReceiveBuffer
                );
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.IO.IOException)
        {
        }
        catch (ThreadAbortException)
        {
        }
    }

    protected void HandleReceive()
    {
        while (ReceiveHandleIndex < ReceiveIndex)
        {
            //Read header bytes
            if (CurrentMessageHeaderIndex < KLFCommon.MessageHeaderLength)
            {
                //Determine how many header bytes can be read
                int bytesToRead = Math.Min(ReceiveIndex - ReceiveHandleIndex, KLFCommon.MessageHeaderLength - CurrentMessageHeaderIndex);
                //Read header bytes
                Array.Copy(ReceiveBuffer, ReceiveHandleIndex, CurrentMessageHeader, CurrentMessageHeaderIndex, bytesToRead);
                //Advance buffer indices
                CurrentMessageHeaderIndex += bytesToRead;
                ReceiveHandleIndex += bytesToRead;
                //Handle header
                if (CurrentMessageHeaderIndex >= KLFCommon.MessageHeaderLength)
                {
                    int idInt = KLFCommon.BytesToInt(CurrentMessageHeader, 0);
                    //Make sure the message id section of the header is a valid value
                    if(idInt >= 0
                    && idInt < Enum.GetValues(typeof(KLFCommon.ServerMessageID)).Length)
                        CurrentMessageID = (KLFCommon.ServerMessageID)idInt;
                    else
                        CurrentMessageID = KLFCommon.ServerMessageID.Null;
                    int dataLength = KLFCommon.BytesToInt(CurrentMessageHeader, 4);
                    if (dataLength > 0)
                    {
                        //Init message data buffer
                        CurrentMessageData = new byte[dataLength];
                        CurrentMessageDataIndex = 0;
                    }
                    else
                    {
                        CurrentMessageData = null;
                        MessageReceived(CurrentMessageID, null);
                        CurrentMessageHeaderIndex = 0;//Prepare for the next header read
                    }
                }
            }
            if (CurrentMessageData != null)
            {
                //Read data bytes
                if (CurrentMessageDataIndex < CurrentMessageData.Length)
                {
                    //Determine how many data bytes can be read
                    int bytesToRead = Math.Min(ReceiveIndex - ReceiveHandleIndex, CurrentMessageData.Length - CurrentMessageDataIndex);
                    //Read data bytes
                    Array.Copy(ReceiveBuffer, ReceiveHandleIndex, CurrentMessageData, CurrentMessageDataIndex, bytesToRead);
                    //Advance buffer indices
                    CurrentMessageDataIndex += bytesToRead;
                    ReceiveHandleIndex += bytesToRead;
                    //Handle data
                    if (CurrentMessageDataIndex >= CurrentMessageData.Length)
                    {
                        //Handle received message
                        MessageReceived(CurrentMessageID, CurrentMessageData);
                        CurrentMessageData = null;
                        //Prepare for the next header read
                        CurrentMessageHeaderIndex = 0;
                    }
                }
            }
        }
        //Once all receive bytes have been handled, reset buffer indices to use the whole buffer again
        ReceiveHandleIndex = 0;
        ReceiveIndex = 0;
    }

    protected abstract void MessageReceived(KLFCommon.ServerMessageID id, byte[] data);

    /* FormPacket -- Handshake */
    protected void SendHandshakeMessage()
    {
        byte[] usernameBytes = Encoder.GetBytes(ClientConfiguration.Username);
        byte[] versionBytes = Encoder.GetBytes(KLFCommon.ProgramVersion);
        byte[] messageData = new byte[4 + usernameBytes.Length + versionBytes.Length];
        KLFCommon.IntToBytes(usernameBytes.Length).CopyTo(messageData, 0);//zero offset to length of username
        usernameBytes.CopyTo(messageData, 4);//offset to username
        versionBytes.CopyTo(messageData, 4 + usernameBytes.Length);//dynamic offset to version
        SendMessageTcp(KLFCommon.ClientMessageID.Handshake, messageData);
    }

    /* FormPacket -- TextMessage */
    protected void SendTextMessage(String message)
    {
        //Encode message
        byte[] messageBytes = Encoder.GetBytes(message);
        SendMessageTcp(KLFCommon.ClientMessageID.TextMessage, messageBytes);
    }

    /* FormPacket -- PluginUpdate */
    protected void SendPluginUpdate(byte[] data, bool primary)
    {
        if (data != null && data.Length > 0)
        {
            KLFCommon.ClientMessageID id
                = primary ? KLFCommon.ClientMessageID.PrimaryPluginUpdate : KLFCommon.ClientMessageID.SecondaryPluginUpdate;
            if (UdpConnected)
                SendMessageUdp(id, data);
            else
                SendMessageTcp(id, data);
        }
    }

    /* FormPacket -- ShareScreenShotMessage */
    protected void SendShareScreenshotMessage(byte[] data)
    {
        if (data != null && data.Length > 0)
            SendMessageTcp(KLFCommon.ClientMessageID.ScreenshotShare, data);
    }

    /* FormPacket -- ScreenshotWatchPlayerMessage */
    protected void SendScreenshotWatchPlayerMessage(bool sendScreenshot, int currentIndex, int index, String name)
    {
        byte[] nameBytes = Encoder.GetBytes(name);
        byte[] bytes = new byte[9 + nameBytes.Length];
        bytes[0] = sendScreenshot ? (byte)1 : (byte)0;//TODO sendScreenshot?
        KLFCommon.IntToBytes(index).CopyTo(bytes, 1);//TODO why index and currentIndex?
        KLFCommon.IntToBytes(currentIndex).CopyTo(bytes, 5);
        nameBytes.CopyTo(bytes, 9);//which name
        SendMessageTcp(KLFCommon.ClientMessageID.ScreenWatchPlayer, bytes);
    }

    protected void SendConnectionEndMessage(String message)
    {
        byte[] messageBytes = Encoder.GetBytes(message);
        SendMessageTcp(KLFCommon.ClientMessageID.ConnectionEnd, messageBytes);
    }

    protected void SendShareCraftMessage(String craftName, byte[] data, byte type)
    {
        byte[] nameBytes = Encoder.GetBytes(craftName);
        byte[] bytes = new byte[5 + nameBytes.Length + data.Length];
        //Check size of data to make sure it's not too large
        if ((nameBytes.Length + data.Length) <= KLFCommon.MaxCraftFileBytes)
        {
            bytes[0] = type;//first bytes toggles vab/sph
            KLFCommon.IntToBytes(nameBytes.Length).CopyTo(bytes, 1);//length of craft name
            nameBytes.CopyTo(bytes, 5);//craft name
            data.CopyTo(bytes, 5 + nameBytes.Length);//dynamic offset to vessel data
            SendMessageTcp(KLFCommon.ClientMessageID.ShareCraftFile, bytes);
        }
        else
            EnqueueTextMessage("Craft file is too large to send.", false, true);
    }

    protected void SendMessageTcp(KLFCommon.ClientMessageID id, byte[] data)
    {
        byte[] messageBytes = BuildMessageByteArray(id, data);
        lock (TcpSendLock)
        {
            try
            {
                TcpConnection.GetStream().Write(messageBytes, 0, messageBytes.Length);
            }
            catch (System.InvalidOperationException) {}
            catch (System.IO.IOException) {}
        }
        LastTcpMessageSendTime = ClientStopwatch.ElapsedMilliseconds;
    }

    protected void SendUdpProbeMessage()
    {
        SendMessageUdp(KLFCommon.ClientMessageID.UdpProbe, null);
    }

    protected void SendMessageUdp(KLFCommon.ClientMessageID id, byte[] data)
    {
        if (UdpSocket != null)
        {
            try
            {
                UdpSocket.Send(BuildMessageByteArray(id, data, KLFCommon.IntToBytes(ClientID)));
            }
            catch { }
            lock (UdpTimestampLock)
            {
                LastUdpMessageSendTime = ClientStopwatch.ElapsedMilliseconds;
            }
        }
    }

    /* TODO what's this prefix business? */
    protected byte[] BuildMessageByteArray(KLFCommon.ClientMessageID id, byte[] data, byte[] prefix = null)
    {
        int prefixLength = 0;
        if(prefix != null)
            prefixLength = prefix.Length;
        int msgDataLength = 0;
        if(data != null)
            msgDataLength = data.Length;
        byte[] messageBytes =
            new byte[KLFCommon.MessageHeaderLength + msgDataLength + prefixLength];
        int index = 0;
        if(prefix != null)
        {
            prefix.CopyTo(messageBytes, index);
            index += 4;
        }
        KLFCommon.IntToBytes((int)id).CopyTo(messageBytes, index);
        index += 4;
        KLFCommon.IntToBytes(msgDataLength).CopyTo(messageBytes, index);
        index += 4;
        if (data != null)
        {
            data.CopyTo(messageBytes, index);
            index += data.Length;
        }
        return messageBytes;
    }
}
