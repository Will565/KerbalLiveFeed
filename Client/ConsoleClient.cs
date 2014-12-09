using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.Threading;
using System.Collections.Concurrent;
using System.IO;

class ConsoleClient : Client
{

    public ConcurrentQueue<InteropMessage> InteropInQueue;
    public ConcurrentQueue<InteropMessage> InteropOutQueue;
    private ConcurrentQueue<ServerMessage> ReceivedMessageQueue;
    public long LastInteropWriteTime;
    private ConcurrentQueue<InTextMessage> TextMessageQueue;
    private Thread InteropThread;
    private Thread ChatThread;
    private Thread ConnectionThread;
    protected String ThreadExceptionStackTrace;
    protected Exception ClientThreadException;

    public void Connect(ClientSettings settings)
    {
        bool allowReconnect = false;
        int reconnectAttempts = MaxReconnectAttempts;
        do
        {
            allowReconnect = false;
            try
            {
                if (ConnectionLoop(settings))
                {
                    reconnectAttempts = 0;
                    allowReconnect = settings.Reconnect && !IntentionalConnectionEnd;
                }
                else
                    allowReconnect = settings.Reconnect && !IntentionalConnectionEnd && reconnectAttempts < MaxReconnectAttempts;
            }
            catch (Exception e)
            {
                TextWriter writer = File.CreateText("KLFClientlog.txt");
                writer.WriteLine(e.ToString());
                if (ThreadExceptionStackTrace != null && ThreadExceptionStackTrace.Length > 0)
                    writer.WriteLine("Stacktrace: " + ThreadExceptionStackTrace);
                writer.Close();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine();
                Console.WriteLine(e.ToString());
                if (ThreadExceptionStackTrace != null && ThreadExceptionStackTrace.Length > 0)
                    Console.WriteLine("Stacktrace: " + ThreadExceptionStackTrace);
                Console.WriteLine();
                Console.WriteLine("Unexpected exception encountered! Crash report written to KLFClientlog.txt");
                Console.WriteLine();
                Console.ResetColor();
                ClearConnectionState();
            }
            if (allowReconnect)
            {
                Console.WriteLine("Attempting to reconnect...");
                Thread.Sleep(ReconnectDelay);
                reconnectAttempts++;
            }
        } while (allowReconnect);
    }

    bool ConnectionLoop(ClientSettings settings)
    {
        if (ConnectToServer(settings))
        {
            Console.WriteLine("Connected to server! Handshaking...");
            while (IsConnected)
            {
                //Check for exceptions thrown by threads
                lock (ThreadExceptionLock)
                {
                    if (ClientThreadException != null)
                    {
                        Exception e = ClientThreadException;
                        ThreadExceptionStackTrace = e.StackTrace;
                        throw e;
                    }
                }
                Thread.Sleep(SleepTime);
            }
            ConnectionEnded();
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine();
            if (IntentionalConnectionEnd)
                EnqueuePluginChatMessage("Closed connection with server", true);
            else
                EnqueuePluginChatMessage("Lost connection with server", true);
            Console.ResetColor();
            return true;
        }
        Console.WriteLine("Unable to connect to server");
        ConnectionEnded();
        return false;
    }

    protected override void ConnectionStarted()
    {
        base.ConnectionStarted();
        //pluginUpdateInQueue = new ConcurrentQueue<byte[]>();
        TextMessageQueue = new ConcurrentQueue<InTextMessage>();
        InteropOutQueue = new ConcurrentQueue<InteropMessage>();
        InteropInQueue = new ConcurrentQueue<InteropMessage>();
        ReceivedMessageQueue = new ConcurrentQueue<ServerMessage>();
        LastInteropWriteTime = 0;
        ClientThreadException = null;
        if (!Directory.Exists(PluginDirectory))
            Directory.CreateDirectory(PluginDirectory);
        ChatThread = new Thread(new ThreadStart(HandleChat));
        ChatThread.Start();
        InteropThread = new Thread(new ThreadStart(HandlePluginInterop));
        InteropThread.Start();
        ConnectionThread = new Thread(new ThreadStart(ConnectionThreadRun));
        ConnectionThread.Start();
    }

    protected override void ConnectionEnded()
    {
        base.ConnectionEnded();
        SafeAbort(ChatThread, true);
        SafeAbort(ConnectionThread, true);
        SafeAbort(InteropThread, true);
    }

    protected override void SendClientInteropMessage(KLFCommon.ClientInteropMessageID id, byte[] data)
    {
        InteropMessage message = new InteropMessage();
        message.id = (int)id;
        message.data = data;
        InteropOutQueue.Enqueue(message);
        //Enforce max queue size
        while (InteropOutQueue.Count > InteropMaxQueueSize)
            if (!InteropOutQueue.TryDequeue(out message))
                break;
    }

    void SafeAbort(Thread thread, bool join = false)
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

    protected override void EnqueueTextMessage(InTextMessage message, bool toPlugin = true)
    {
        //Dequeue an old text message if there are a lot of messages backed up
        if (TextMessageQueue.Count >= MaxTextMessageQueue)
        {
            InTextMessage oldMessage;
            TextMessageQueue.TryDequeue(out oldMessage);
        }
        TextMessageQueue.Enqueue(message);
        base.EnqueueTextMessage(message, toPlugin);
    }

    protected override void MessageReceived(KLFCommon.ServerMessageID id, byte[] data)
    {
        ServerMessage message;
        message.id = id;
        message.data = data;
        ReceivedMessageQueue.Enqueue(message);
    }

    protected void PassExceptionToMain(Exception e)
    {
        lock (ThreadExceptionLock)
        {
            if (ClientThreadException == null)
                ClientThreadException = e;
        }
    }

    //Threads
    bool WritePluginInterop()
    {
        bool success = false;
        if (InteropOutQueue.Count > 0 && !File.Exists(InteropClientFilename))
        {
            FileStream stream = null;
            try
            {
                stream = File.OpenWrite(InteropClientFilename);
                stream.Write(KLFCommon.IntToBytes(KLFCommon.FileFormatVersion), 0, 4);
                success = true;
                while (InteropOutQueue.Count > 0)
                {
                    InteropMessage message;
                    if (InteropOutQueue.TryDequeue(out message))
                    {
                        byte[] bytes = EncodeInteropMessage(message.id, message.data);
                        stream.Write(bytes, 0, bytes.Length);
                    }
                    else
                        break;
                }

            }
            catch (Exception e)
            {
                Console.WriteLine("Unable to write plugin interop: " + e.ToString());
            }
            finally
            {
                if (stream != null)
                    stream.Close();
            }

        }
        return success;
    }

    void ConnectionThreadRun()
    {
        try
        {
            while (true)
            {
                if (PingStopwatch.IsRunning && PingStopwatch.ElapsedMilliseconds > PingTimeoutDelay)
                {
                    EnqueueTextMessage("Ping timed out.", true);
                    PingStopwatch.Stop();
                    PingStopwatch.Reset();
                }
                //Handle received messages
                while (ReceivedMessageQueue.Count > 0)
                {
                    ServerMessage message;
                    if (ReceivedMessageQueue.TryDequeue(out message))
                        HandleMessage(message.id, message.data);
                    else
                        break;
                }
                HandleConnection();
                Thread.Sleep(SleepTime);
            }
        }
        catch (ThreadAbortException)
        {
        }
        catch (Exception e)
        {
            PassExceptionToMain(e);
        }
    }

    void ReadPluginInterop()
    {
        byte[] bytes = null;
        if (File.Exists(InteropPluginFilename))
        {
            try
            {
                bytes = File.ReadAllBytes(InteropPluginFilename);
                File.Delete(InteropPluginFilename);
            }
            catch (System.IO.FileNotFoundException) {}
            catch (System.UnauthorizedAccessException) {}
            catch (System.IO.DirectoryNotFoundException) {}
            catch (System.InvalidOperationException) {}
            catch (System.IO.IOException) {}
        }

        if (bytes != null && bytes.Length > 0)
        {
            int fileVersion = KLFCommon.BytesToInt(bytes, 0);
            if (fileVersion != KLFCommon.FileFormatVersion)
            {
                Console.WriteLine("KLF Client incompatible with plugin");
                return;
            }
            //Parse the messages
            int index = 4;
            while (index < bytes.Length - KLFCommon.InteropMessageHeaderLength)
            {
                int idInt = KLFCommon.BytesToInt(bytes, index);//read id
                KLFCommon.PluginInteropMessageID id = KLFCommon.PluginInteropMessageID.Null;
                if (idInt >= 0 && idInt < Enum.GetValues(typeof(KLFCommon.PluginInteropMessageID)).Length)
                    id = (KLFCommon.PluginInteropMessageID)idInt;
                int dataLength = KLFCommon.BytesToInt(bytes, index + 4);//length of message
                index += KLFCommon.InteropMessageHeaderLength;
                if (dataLength <= 0)
                    HandleInteropMessage(id, null);
                else if (dataLength <= (bytes.Length - index))
                {
                    byte[] data = new byte[dataLength];
                    Array.Copy(bytes, index, data, 0, data.Length);
                    HandleInteropMessage(id, data);
                }
                if (dataLength > 0)
                    index += dataLength;
            }
        }

        while (InteropInQueue.Count > 0)
        {
            InteropMessage message;
            if (InteropInQueue.TryDequeue(out message))
                HandleInteropMessage(message.id, message.data);
            else
                break;
        }
    }

    void HandlePluginInterop()
    {
        try
        {
            while (true)
            {
                WriteClientData();
                ReadPluginInterop();
                if (ClientStopwatch.ElapsedMilliseconds - LastInteropWriteTime >= InteropWriteInterval)
                    if (WritePluginInterop())
                        LastInteropWriteTime = ClientStopwatch.ElapsedMilliseconds;
                ThrottledShareScreenshots();
                Thread.Sleep(SleepTime);
            }
        }
        catch (ThreadAbortException) {}
        catch (Exception e)
        {
            PassExceptionToMain(e);
        }
    }

    void HandlePluginUpdates()
    {
        try
        {
            while (true)
            {
                WriteClientData();
                int sleepTime = 0;
                lock (ServerSettingsLock)
                {
                    sleepTime = UpdateInterval;
                }
                Thread.Sleep(sleepTime);
            }
        }
        catch (ThreadAbortException) {}
        catch (Exception e)
        {
            PassExceptionToMain(e);
        }
    }

    void HandleChat()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            while (true)
            {
                //Handle outgoing messsages
                if (Console.KeyAvailable)
                {
                    ConsoleKeyInfo key = Console.ReadKey();
                    switch (key.Key)
                    {
                        case ConsoleKey.Enter:
                            String line = sb.ToString();
                            HandleChatInput(line);
                            sb.Clear();
                            Console.WriteLine();
                            break;

                        case ConsoleKey.Backspace:
                        case ConsoleKey.Delete:
                            if (sb.Length > 0)
                            {
                                sb.Remove(sb.Length - 1, 1);
                                Console.Write(' ');
                                if (Console.CursorLeft > 0)
                                    Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
                            }
                            break;

                        default:
                            if (key.KeyChar != '\0')
                                sb.Append(key.KeyChar);
                            else if (Console.CursorLeft > 0)
                                Console.SetCursorPosition(Console.CursorLeft - 1, Console.CursorTop);
                            break;
                    }
                }
                //Handle incoming messages
                if (sb.Length == 0)
                {
                    try
                    {
                        while (TextMessageQueue.Count > 0)
                        {
                            InTextMessage message;
                            if (TextMessageQueue.TryDequeue(out message))
                            {
                                if (message.FromServer)
                                {
                                    Console.ForegroundColor = ConsoleColor.Green;
                                    Console.Write("[Server] ");
                                    Console.ResetColor();
                                }
                                Console.WriteLine(message.message);
                            }
                            else
                                break;
                        }
                    }
                    catch (System.IO.IOException) {}
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
}
