using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.IO;
using System.Net;

using System.Xml;
using System.Xml.Serialization;
using System.Xml.Schema;

/* An XML serializable URI class. http://stackoverflow.com/a/14388429 */
public class XmlUri : IXmlSerializable
{
    private Uri _Value;
    /* constructors */
    public XmlUri() { }
    public XmlUri(Uri source) { _Value = source; }

    public static implicit operator Uri(XmlUri o)
    {
        return o == null ? null : o._Value;
    }

    public static implicit operator XmlUri(Uri o)
    {
        return o == null ? null : new XmlUri(o);
    }

    public XmlSchema GetSchema()
    {
        return null;
    }

    public void ReadXml(XmlReader reader)
    {
        _Value = new Uri(reader.ReadElementContentAsString());
    }

    public void WriteXml(XmlWriter writer)
    {
        writer.WriteValue(_Value.ToString());
    }
    public override String ToString()
    {
        return _Value.ToString();
    }
}

/* Information for a server connection. */
public class ServerProfile
{ 
    public XmlUri Uri { get; set; }
    [XmlAttribute] public bool Default { get; set; }
    public bool ShouldSerializeDefault() { return Default; }
    /* constructors */
    protected ServerProfile() {}
    public ServerProfile(Uri u) { Uri = u; }
    public ServerProfile(Uri u, bool f) { Uri = u; Default = f; }
}

/* Basic client configuration.
 * reference http://wiki.unity3d.com/index.php?title=Saving_and_Loading_Data:_XmlSerializer */
[XmlRoot] public class ClientSettings
{
    [XmlIgnoreAttribute] private string filename;
    public string Username { get; set; }
    public string Token { get; set; }
    public bool Reconnect { get; set; }
    [XmlArray,XmlArrayItem] public List<ServerProfile> Servers;

    /* constructors */
    public ClientSettings(string u, string t)
    {
        Username = u;
        Token = t;
        Servers = new List<ServerProfile>();
        this.filename = "KLFClientConfig.xml";
    }
    public ClientSettings(string u)
    {
        new ClientSettings(u, "");
    }
    public ClientSettings()
    {
        new ClientSettings("username");
    }


    /* return the first server flagged as default */
    public Uri GetDefaultServer()
    {
        ServerProfile def = Servers.Find(item => item.Default == true);
        if(def != null)
            return def.Uri;
        else
            return null;
    }

    /* set default server by index */
    public bool SetDefaultServer(int i)
    {
        if(i > -1)
        {
            foreach(ServerProfile srv in Servers.FindAll(item => item.Default == true))
                srv.Default = false;//clear all default flags
            ServerProfile tmp = Servers.ElementAtOrDefault(i);
            if(tmp != null)
               tmp.Default = true;//flag only one as default
            else
                return false;
        }
        else
            return false;
        return true;
    }

    /* TODO integrate validation so we don't check after Load
     * hostname is known format;  dns, IPv4 or IPv6, or basic name
     * port is within valid range */
    public bool ValidServer(String host, Int32 port)
    {
        bool validHost = Uri.CheckHostName(host) != UriHostNameType.Unknown;
        bool validPort = IPEndPoint.MinPort <= port && port <= IPEndPoint.MaxPort;
        return validHost && validPort;
    }

    /* Add new server, set favourite flag.
     * If already in list, set favourite flag.  */
    public bool AddServer(String host, Int32 port)
    {
        if(ValidServer(host, port))
        {
            Uri u = new Uri("net.tcp://" + host + ":" + port);
            if(!Servers.Exists(item => item.Uri == u))
                Servers.Add(new ServerProfile(u));
        }
        else
            return false;
        return true;
    }

    //Save
    public void Save(string path)
    {
        this.filename = path;
        Save();
    }
    public void Save()
    {
        XmlSerializer serializer =
            new XmlSerializer(typeof(ClientSettings));
        try
        {
            using(FileStream stream = new FileStream(this.filename, FileMode.Create))
            {
                serializer.Serialize(stream, this);
            }
        }
        catch(Exception e)
        {
            Console.WriteLine("Failed to save: " + e.ToString());
        }
    }

    //Load
    public static ClientSettings Load(string path)
    {
        XmlSerializer serializer =
            new XmlSerializer(typeof(ClientSettings));
        try
        {
            using(FileStream stream = new FileStream(path, FileMode.Open))
            {
                return serializer.Deserialize(stream)
                    as ClientSettings;
            }
        }
        catch(Exception e)
        {
            Console.WriteLine("Failed to load: " + e.ToString());
            return null;
        }
    }

    //STATIC load from string for testing
    public static ClientSettings LoadFromText(string text)
    {
        XmlSerializer serializer =
            new XmlSerializer(typeof(ClientSettings));
        return serializer.Deserialize(new StringReader(text))
            as ClientSettings;
    }
}

