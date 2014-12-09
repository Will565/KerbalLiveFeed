using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

public class KLFCommon
{

    public const String ProgramVersion = "0.8.0";
    public const Int32 FileFormatVersion = 8;
    public const Int32 NetProtocolVersion = 9;
    public const int MessageHeaderLength = 8;
    public const int InteropMessageHeaderLength = 8;
    public const int ServerSettingsLength = 13;
    public const int MaxCraftFileBytes = 1024 * 1024;
    public const String ShareCraftCommand = "/sharecraft";
    public const String GetCraftCommand = "/getcraft";
    public const byte CraftTypeVab = 0;
    public const byte CraftTypeSph = 1;

    /* filter illegal characters out of string */
    internal static string FilteredFileName(string filename)
    {
        const String illegal = "\\/:*?\"<>|";
        StringBuilder sb = new StringBuilder();
        foreach (char c in filename)
            if(!illegal.Contains(c))
                sb.Append(c);
        return sb.ToString();
    }

    public static byte[] IntToBytes(Int32 val)
    {
        byte[] bytes = new byte[4];
        bytes[0] = (byte)(val & byte.MaxValue);
        bytes[1] = (byte)((val >> 8) & byte.MaxValue);
        bytes[2] = (byte)((val >> 16) & byte.MaxValue);
        bytes[3] = (byte)((val >> 24) & byte.MaxValue);
        return bytes;
    }

    public static Int32 BytesToInt(byte[] bytes, int offset = 0)
    {
        Int32 val = 0;
        val |= bytes[offset];
        val |= ((Int32)bytes[offset + 1]) << 8;
        val |= ((Int32)bytes[offset + 2]) << 16;
        val |= ((Int32)bytes[offset + 3]) << 24;
        return val;
    }

    public enum ClientMessageID
    { Handshake /*Username Length : Username : Version*/
    , PrimaryPluginUpdate /*data*/
    , SecondaryPluginUpdate /*data*/
    , TextMessage /*Message text*/
    , ScreenWatchPlayer /*Byte - Send Screenshot : Int32 Index : Int32 Current Index :  Player name*/
    , ScreenshotShare /*Screenshot*/
    , KeepAlive
    , ConnectionEnd /*Message*/
    , UdpProbe
    , Null
    , ShareCraftFile /*Craft Type Byte : Craft name length : Craft Name : File bytes*/
    , ActivityUpdateInGame
    , ActivityUpdateInFlight
    , Ping
    }

    public enum ServerMessageID
    { Handshake /*Protocol Version : Version String Length : Version String : ClientID*/
    , HandshakeRefusal /*Refusal message*/
    , ServerMessage /*Message text*/
    , TextMessage /*Message text*/
    , PluginUpdate /*data*/
    , ServerSettings /*UpdateInterval (4) : Screenshot Interval (4) : Screenshot Height (4): InactiveShips (1)*/
    , ScreenshotShare /*Screenshot*/
    , Keepalive
    , ConnectionEnd /*Message*/
    , UdpAcknowledge
    , Null
    , CraftFile /*Craft Type Byte : Craft name length : Craft Name : File bytes*/
    , PingReply
    }

    public enum ClientInteropMessageID
    { Null
    , ClientData /*Byte - Inactive Vessels Per Update : Screenshot Height : UpdateInterval : Player Name*/
    , ScreenshotReceive /*Screenshot*/
    , ChatReceive /*Message*/
    , PluginUpdate /*data*/
    }

    public enum PluginInteropMessageID
    { Null
    , PluginData /*Byte - In-Flight : Int32 - Current Game Title length : Current Game Title*/
    , ScreenshotShare /*Screenshot*/
    , ChatSend /*Message*/
    , PrimaryPluginUpdate /*data*/
    , SecondaryPluginUpdate /*data*/
    , ScreenshotWatchUpdate /*Int32 index : Int32 current index : Watch Name*/
    }
}

