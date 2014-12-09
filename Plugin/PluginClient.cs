using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;


namespace KLF
{
    class PluginClient : Client
    {
        public delegate void HandleInteropCallback(KLFCommon.ClientInteropMessageID id, byte[] data);

        Queue<InteropMessage> InteropInQueue;
        Queue<InteropMessage> InteropOutQueue;
        Queue<ServerMessage> ServerMessageQueue;

        object ServerMessageQueueLock = new object();

        protected override void ConnectionStarted()
        {
            InteropInQueue = new Queue<InteropMessage>();
            InteropOutQueue = new Queue<InteropMessage>();
            ServerMessageQueue = new Queue<ServerMessage>();
        }

        protected override void MessageReceived(KLFCommon.ServerMessageID id, byte[] data)
        {
            ServerMessage message;
            message.id = id;
            message.data = data;

            lock (ServerMessageQueueLock)
            {
                ServerMessageQueue.Enqueue(message);
            }
        }

        protected override void SendClientInteropMessage(KLFCommon.ClientInteropMessageID id, byte[] data)
        {
            InteropMessage message = new InteropMessage();
            message.id = (int)id;
            message.data = data;
            InteropOutQueue.Enqueue(message);
        }

        public void UpdateStep(HandleInteropCallback Interop_callback)
        {
            if (!IsConnected)
                return;

            while (InteropInQueue.Count > 0)
            {
                InteropMessage message = InteropInQueue.Dequeue();
                HandleInteropMessage(message.id, message.data);
            }

            lock (ServerMessageQueueLock)
            {
                while (ServerMessageQueue.Count > 0)
                {//Handle received messages
                    ServerMessage message = ServerMessageQueue.Dequeue();
                    HandleMessage(message.id, message.data);
                }
            }

            ThrottledShareScreenshots();
            WriteClientData();
            HandleConnection();

            while (InteropOutQueue.Count > 0)
            {
                InteropMessage message = InteropOutQueue.Dequeue();
                Interop_callback((KLFCommon.ClientInteropMessageID)message.id, message.data);
            }
        }

        public void enqueuePluginInteropMessage(KLFCommon.PluginInteropMessageID id, byte[] data)
        {
            InteropMessage message = new InteropMessage();
            message.id = (int)id;
            message.data = data;
            InteropInQueue.Enqueue(message);
        }
    }
}
