using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.Net.Sockets;
using System.Threading;
using System.Net;
using System.Collections.Concurrent;

namespace KLFServer
{
    class ServerClient
    {
        public struct ThrottleState
        {
            public long MessageFloodCounterTime;
            public int MessageFloodCounter;
            public long MessageFloodThrottleUntilTime;
            public long ScreenshotFloodCounterTime;
            public int ScreenshotFloodCounter;
            public long ScreenshotThrottleUntilTime;

            public void Reset()
            {
                ScreenshotFloodCounter = 0;
                ScreenshotFloodCounterTime = 0;
                ScreenshotThrottleUntilTime = 0;

                MessageFloodCounterTime = 0;
                MessageFloodCounter = 0;
                MessageFloodThrottleUntilTime = 0;
            }
        }

        public enum Activity
        {
            Inactive,
            InGame,
            InFlight
        }

        public const int SendBufferSize = 8192;
        public const long ScreenshotThrottleInterval = 60 * 1000;
        public const long MessageThrottleInterval = 60 * 1000;

        //Handles
        public Server Parent
        {
            private set;
            get;
        }
        public int ClientIndex
        {
            private set;
            get;
        }
        public String Username;

        public bool ReceivedHandshake;
        public bool CanBeReplaced;

        public Screenshot[] Screenshots;
        public int WatchPlayerIndex;
        public String WatchPlayerName;

        public byte[] SharedCraftFile;
        public String SharedCraftName;
        public byte SharedCraftType;

        //timing
        public long LastInGameActivityTime;
        public long LastInFlightActivityTime;
        public Activity CurrentActivity;
        public ThrottleState CurrentThrottle;

        //connection tracking
        public long ConnectionStartTime;
        public long LastReceiveTime;
        public long LastUdpAckTime;

        public TcpClient TcpConnection;
        public IPAddress IP;

        //Message buffers
        private byte[] ReceiveBuffer = new byte[8192];
        private int ReceiveIndex = 0;
        private int ReceiveHandleIndex = 0;

        private byte[] CurrentMessageHeader = new byte[KLFCommon.MessageHeaderLength];
        private int CurrentMessageHeaderIndex;
        private byte[] CurrentMessageData;
        private int CurrentMessageDataIndex;

        public KLFCommon.ClientMessageID CurrentMessageID;
        public ConcurrentQueue<byte[]> QueuedOutMessages;

        //Locks
        public object TcpClientLock = new object();
        public object TimestampLock = new object();
        public object ActivityLock = new object();
        public object ScreenshotLock = new object();
        public object WatchPlayerNameLock = new object();
        public object SharedCraftLock = new object();

        public int LastScreenshotIndex
        {
            get
            {
                if (Screenshots[0] != null)
                    return Screenshots[0].Index;
                else
                    return 0;
            }
        }
        public int FirstScreenshotIndex
        {
            get
            {
                for (int i = Screenshots.Length - 1; i >= 0; i--)
                    if (Screenshots[i] != null)
                        return Screenshots[i].Index;
                return -1;
            }
        }

        //Constructor
        public ServerClient(Server parent, int index)
        {
            this.Parent = parent;
            this.ClientIndex = index;
            CanBeReplaced = true;
            QueuedOutMessages = new ConcurrentQueue<byte[]>();
        }

        public void ResetProperties()
        {
            Username = "new user";
            Screenshots = new Screenshot[Parent.Configuration.ScreenshotBacklog];
            WatchPlayerName = String.Empty;
            WatchPlayerIndex = 0;
            CanBeReplaced = false;
            ReceivedHandshake = false;
            SharedCraftFile = null;
            SharedCraftName = String.Empty;
            SharedCraftType = 0;
            CurrentThrottle.Reset();
            LastUdpAckTime = 0;
            QueuedOutMessages = new ConcurrentQueue<byte[]>();
            lock (ActivityLock)
            {
                CurrentActivity = ServerClient.Activity.Inactive;
                LastInGameActivityTime = Parent.CurrentMillisecond;
                LastInFlightActivityTime = Parent.CurrentMillisecond;
            }
            lock (TimestampLock)
            {
                LastReceiveTime = Parent.CurrentMillisecond;
                ConnectionStartTime = Parent.CurrentMillisecond;
            }
        }

        public void UpdateReceiveTimestamp()
        {
            lock (TimestampLock)
            {
                LastReceiveTime = Parent.CurrentMillisecond;
            }
        }

        public void Disconnected()
        {
            CanBeReplaced = true;
            Screenshots = null;
            WatchPlayerName = String.Empty;
            SharedCraftFile = null;
            SharedCraftName = String.Empty;
        }

        //Async read
        private void BeginAsyncRead()
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
            catch (InvalidOperationException) {}
            catch (System.IO.IOException) {}
            catch (Exception e)
            {
                Parent.PassExceptionToMain(e);
            }
        }

        private void AsyncReceive(IAsyncResult result)
        {
            try
            {
                int read = TcpConnection.GetStream().EndRead(result);
                if (read > 0)
                {
                    ReceiveIndex += read;
                    UpdateReceiveTimestamp();
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
            catch (InvalidOperationException) {}
            catch (System.IO.IOException) {}
            catch (ThreadAbortException) {}
            catch (Exception e)
            {
                Parent.PassExceptionToMain(e);
            }

        }

        private void HandleReceive()
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
                        if (idInt >= 0 && idInt < Enum.GetValues(typeof(KLFCommon.ClientMessageID)).Length)
                            CurrentMessageID = (KLFCommon.ClientMessageID)idInt;
                        else
                            CurrentMessageID = KLFCommon.ClientMessageID.Null;

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
                            //Handle received message
                            MessageReceived(CurrentMessageID, null);
                            //Prepare for the next header read
                            CurrentMessageHeaderIndex = 0;
                        }
                    }
                }

                if (CurrentMessageData != null)//Read data bytes
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

            //Once all receive bytes have been handled, reset buffer indices to use the whole buffer again
            ReceiveHandleIndex = 0;
            ReceiveIndex = 0;
        }

        //Asyc send
        private void AsyncSend(IAsyncResult result)
        {
            try
            {
                TcpConnection.GetStream().EndWrite(result);
            }
            catch (InvalidOperationException) {}
            catch (System.IO.IOException) {}
            catch (ThreadAbortException) {}
            catch (Exception e)
            {
                Parent.PassExceptionToMain(e);
            }
        }

        //Messages

        private void MessageReceived(KLFCommon.ClientMessageID id, byte[] data)
        {
            Parent.QueueClientMessage(ClientIndex, id, data);
        }

        public void SendOutgoingMessages()
        {
            try
            {
                if (QueuedOutMessages.Count > 0)
                {
                    //Check the size of the next message
                    byte[] nextMessage = null;
                    int sendBufferIndex = 0;
                    byte[] sendBuffer = new byte[SendBufferSize];
                    while (QueuedOutMessages.TryPeek(out nextMessage))
                    {
                        if (sendBufferIndex == 0 && nextMessage.Length >= sendBuffer.Length)
                        {//next message is too large for the send buffer, send it
                            QueuedOutMessages.TryDequeue(out nextMessage);
                            TcpConnection.GetStream().BeginWrite
                                ( nextMessage
                                , 0
                                , nextMessage.Length
                                , AsyncSend
                                , nextMessage
                                );
                        }
                        else if (nextMessage.Length <= (sendBuffer.Length - sendBufferIndex))
                        {//next message is small enough, copy to the send buffer
                            QueuedOutMessages.TryDequeue(out nextMessage);
                            nextMessage.CopyTo(sendBuffer, sendBufferIndex);
                            sendBufferIndex += nextMessage.Length;
                        }
                        else
                        {//next message is too big, send the send buffer
                            TcpConnection.GetStream().BeginWrite
                                ( sendBuffer
                                , 0
                                , sendBufferIndex
                                , AsyncSend
                                , nextMessage
                                );
                            sendBufferIndex = 0;
                            sendBuffer = new byte[SendBufferSize];
                        }
                    }

                    //Send the send buffer
                    if (sendBufferIndex > 0)
                        TcpConnection.GetStream().BeginWrite
                            ( sendBuffer
                            , 0
                            , sendBufferIndex
                            , AsyncSend
                            , nextMessage
                            );
                }
            }
            catch (System.InvalidOperationException) { }
            catch (System.IO.IOException) { }
        }

        public void QueueOutgoingMessage(KLFCommon.ServerMessageID id, byte[] data)
        {
            QueueOutgoingMessage(Server.BuildMessageArray(id, data));
        }

        public void QueueOutgoingMessage(byte[] messageBytes)
        {
            QueuedOutMessages.Enqueue(messageBytes);
        }

        internal void StartReceivingMessages()
        {
            BeginAsyncRead();
        }

        internal void EndReceivingMessages()
        {
        }

        //Activity Level
        public void UpdateActivity(Activity activity)
        {
            bool changed = false;
            lock (ActivityLock)
            {
                switch (activity)
                {
                    case Activity.InGame:
                        LastInGameActivityTime = Parent.CurrentMillisecond;
                        break;

                    case Activity.InFlight:
                        LastInFlightActivityTime = Parent.CurrentMillisecond;
                        LastInGameActivityTime = Parent.CurrentMillisecond;
                        break;
                }

                if (activity > CurrentActivity)
                {
                    CurrentActivity = activity;
                    changed = true;
                }
            }
            if (changed)
                Parent.ClientActivityChanged(ClientIndex);
        }

        //Flood limit
        public bool ScreenshotsThrottled
        {
            get
            {
                return Parent.CurrentMillisecond < CurrentThrottle.ScreenshotThrottleUntilTime;
            }
        }
        public bool MessagesThrottled
        {
            get
            {
                return Parent.CurrentMillisecond < CurrentThrottle.MessageFloodThrottleUntilTime;
            }
        }
        public void ScreenshotFloodIncrement()
        {
            //Reset the counter if enough time has passed
            if (Parent.CurrentMillisecond - CurrentThrottle.ScreenshotFloodCounterTime > ScreenshotThrottleInterval)
            {
                CurrentThrottle.ScreenshotFloodCounter = 0;
                CurrentThrottle.ScreenshotFloodCounterTime = Parent.CurrentMillisecond;
            }
            CurrentThrottle.ScreenshotFloodCounter++;
            //If the client has shared too many Screenshots in the last interval, throttle them
            if (CurrentThrottle.ScreenshotFloodCounter >= Parent.Configuration.ScreenshotFloodLimit)
                CurrentThrottle.ScreenshotThrottleUntilTime = Parent.CurrentMillisecond + Parent.Configuration.ScreenshotFloodThrottleTime;
        }
        public void MessageFloodIncrement()
        {
            //Reset the counter if enough time has passed
            if (Parent.CurrentMillisecond - CurrentThrottle.MessageFloodCounterTime > MessageThrottleInterval)
            {
                CurrentThrottle.MessageFloodCounter = 0;
                CurrentThrottle.MessageFloodCounterTime = Parent.CurrentMillisecond;
            }
            CurrentThrottle.MessageFloodCounter++;
            if (CurrentThrottle.MessageFloodCounter >= Parent.Configuration.MessageFloodLimit)
                CurrentThrottle.MessageFloodThrottleUntilTime = Parent.CurrentMillisecond + Parent.Configuration.MessageFloodThrottleTime;
        }

        //Screenshots
        public Screenshot GetScreenshot(int index)
        {
            foreach (Screenshot shot in Screenshots)
                if (shot != null && shot.Index == index)
                    return shot;
            return null;
        }
        public Screenshot LastScreenshot
        {
            get
            {
                return Screenshots[0];
            }
        }
        public void PushScreenshot(Screenshot shot)
        {
            int lastIndex = LastScreenshotIndex;
            for (int i = 0; i < Screenshots.Length - 1; i++)
                Screenshots[i + 1] = Screenshots[i];
            Screenshots[0] = shot;
            Screenshots[0].Index = lastIndex + 1;
        }
    }
}
