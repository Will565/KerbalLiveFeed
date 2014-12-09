using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.IO;
using System.Net;

using System.Xml;
using System.Xml.Serialization;
using System.Xml.Schema;

namespace KLFServer
{
    [XmlRoot] public class ServerSettings
    {
        [XmlIgnoreAttribute] public const int MinUpdateInterval = 200;
        [XmlIgnoreAttribute] public const int MaxUpdateInterval = 5000;
        [XmlIgnoreAttribute] public const float MinUpdatesPerSecond = 0.5f;
        [XmlIgnoreAttribute] public const float MaxUpdatesPerSecond = 1000.0f;
        [XmlIgnoreAttribute] public const int DefaultPort = 2075;
        [XmlIgnoreAttribute] public const int DefaultHttpPort = 80;
        [XmlIgnoreAttribute] public const int DefaultUpdatesPerSecond = 10;

        [XmlIgnoreAttribute] private string filename;
        //public IPAddress LocalAddress;
        public int MaxClients;
        public int ScreenshotBacklog;
        public int ScreenshotInterval;
        public int ScreenshotFloodLimit;
        public int ScreenshotFloodThrottleTime;
        public int MessageFloodLimit;
        public int MessageFloodThrottleTime;
        public bool AutoRestart;
        public bool AutoHost;
        public bool SaveScreenshots;
        public String JoinMessage;
        public String ServerInfo;
        public byte TotalInactiveShips;
        public ScreenshotSettings ScreenshotSettings;
        private int port;
        public int Port
        {
            get { return port; }
            set
            {
                port = Math.Max( IPEndPoint.MinPort
                               , Math.Min(IPEndPoint.MaxPort, value)
                               );
            }
        }
        private int httpPort;
        public int HttpPort
        {
            get { return httpPort; }
            set
            {
                httpPort = Math.Max( IPEndPoint.MinPort
                                   , Math.Min(IPEndPoint.MaxPort, value)
                                   );
            }
        }
        private float updatesPerSecond;
        public float UpdatesPerSecond
        {
            get { return updatesPerSecond; }
            set
            {
                updatesPerSecond = Math.Max( MinUpdatesPerSecond
                                           , Math.Min(MaxUpdatesPerSecond, value)
                                           );
            }
        }

        public ServerSettings()
        {//constructor
            Port = DefaultPort;
            HttpPort = DefaultHttpPort;
            UpdatesPerSecond = DefaultUpdatesPerSecond;
            //LocalAddress = IPAddress.Any;
            MaxClients = 32;
            ScreenshotBacklog = 4;
            ScreenshotInterval = 3000;
            ScreenshotFloodLimit = 10;
            ScreenshotFloodThrottleTime = 300000;
            MessageFloodLimit = 15;
            MessageFloodThrottleTime = 120000;
            AutoRestart = false;
            AutoHost = false;
            SaveScreenshots = false;
            JoinMessage = String.Empty;
            ServerInfo = String.Empty;
            TotalInactiveShips = 20;
            ScreenshotSettings = new ScreenshotSettings();
            filename = "Configuration.xml";
        }


        public void Save(string path)
        {
            this.filename = path;
            Save();
        }
        public void Save()
        {
            XmlSerializer serializer = 
                new XmlSerializer(typeof(ServerSettings));
            try
            {
                using(FileStream stream = new FileStream(this.filename, FileMode.Create))
                {
                    serializer.Serialize(stream, this);
                }
            }
            catch(Exception e)
            {
                Console.WriteLine("Failed to save: {0}", this.filename);
                Console.WriteLine(e.ToString());
            }
        }

        public static ServerSettings Load(string path)
        {
            XmlSerializer serializer =
                new XmlSerializer(typeof(ServerSettings));
            try
            {
                using(FileStream stream = new FileStream(path, FileMode.Open))
                {
                    return serializer.Deserialize(stream)
                        as ServerSettings;
                }
            }
        //    catch(FileNotFoundException) {}
            catch(Exception e)
            {
                Console.WriteLine("Failed to load: {0}", path);
                Console.WriteLine(e.ToString());
            }
            return null;
        }
    }
}
