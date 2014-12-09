using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

class Screenshot
{
    public int Index;
    public string Player;
    public string Description;
    public byte[] Image;

    public Screenshot()
    {
        Index = 0;
        Player = "";
        Description = "";
    }

    public void Clear()
    {
        Index = 0;
        Player = "";
        Description = "";
        Image = null;
    }

    public void SetFromByteArray(byte[] bytes, bool metaOnly = false)
    {
        UnicodeEncoding encoding = new UnicodeEncoding();
        int arIndex = 0;
        Index = KLFCommon.BytesToInt(bytes, arIndex);
        arIndex += 4;
        int StringSize = KLFCommon.BytesToInt(bytes, arIndex);
        arIndex += 4;
        Player = encoding.GetString(bytes, arIndex, StringSize);
        arIndex += StringSize;
        StringSize = KLFCommon.BytesToInt(bytes, arIndex);
        arIndex += 4;
        Description = encoding.GetString(bytes, arIndex, StringSize);
        arIndex += StringSize;
        Image = new byte[bytes.Length-arIndex];
        Array.Copy(bytes, arIndex, Image, 0, Image.Length);
    }

    public byte[] ToByteArray()
    {
        UnicodeEncoding encoding = new UnicodeEncoding();
        byte[] playerBytes = encoding.GetBytes(Player);
        byte[] descBytes = encoding.GetBytes(Description);
        byte[] bytes = new byte[12 + playerBytes.Length + descBytes.Length + Image.Length];
        int arIndex = 0;
        KLFCommon.IntToBytes(Index).CopyTo(bytes, arIndex);
        arIndex += 4;
        KLFCommon.IntToBytes(playerBytes.Length).CopyTo(bytes, arIndex);
        arIndex += 4;
        playerBytes.CopyTo(bytes, arIndex);
        arIndex += playerBytes.Length;
        KLFCommon.IntToBytes(descBytes.Length).CopyTo(bytes, arIndex);
        arIndex += 4;
        descBytes.CopyTo(bytes, arIndex);
        arIndex += descBytes.Length;
        Image.CopyTo(bytes, arIndex);
        return bytes;
    }
}
