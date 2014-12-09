using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;

namespace KLF
{
    class KLFChatDisplay
    {
        public struct ChatLine
        {
            public String Message;
            public Color Color;
            public ChatLine(String mes)
            {
                this.Message = mes;
                this.Color = Color.white;
            }
        }

        public const float WindowWidthNormal = 320;
        public const float WindowWidthWide = 440;
        public const float WindowHeight = 360;
        public const int MaxChatOutQueue = 4;
        public const int MaxChatLines = 16;
        public const int MaxChatLineLength = 220;
        public const float NameColorSaturationFactor = 0.35f;
        public static GUILayoutOption[] LayoutOptions;

        public static bool DisplayCommands = false;
        public static Rect WindowPos =
            new Rect( Screen.width - WindowWidthNormal - 8
                    , Screen.height / 2 - WindowHeight / 2
                    , WindowWidthNormal
                    , WindowHeight);
        public static Vector2 ScrollPos = Vector2.zero;
        public static float WindowWidth
        {
            get
            {
                if (KLFGlobalSettings.Instance.ChatWindowWide)
                    return WindowWidthWide;
                else
                    return WindowWidthNormal;
            }
        }
        public static Queue<ChatLine> ChatLineQueue = new Queue<ChatLine>();
        public static String ChatEntryString = String.Empty;

        public static void Line(String line)
        {
            ChatLine chatLine = new ChatLine(line);
            if (line.Length > 3 && line.First() == '[')
            {//Check if the message starts with name
                int nameLength = line.IndexOf(']');
                if (nameLength > 0)
                {//render name colour
                    nameLength = nameLength - 1;
                    String name = line.Substring(1, nameLength);
                    if (name == "Server")
                        chatLine.Color = new Color(0.65f, 1.0f, 1.0f);
                    else
                        chatLine.Color =
                            KLFVessel.GenerateActiveColor(name) * NameColorSaturationFactor + Color.white * (1.0f - NameColorSaturationFactor);
                }
            }
            ChatLineQueue.Enqueue(chatLine);
            while (ChatLineQueue.Count > MaxChatLines)
                ChatLineQueue.Dequeue();
            ScrollPos.y += 100;
        }
    }
}
