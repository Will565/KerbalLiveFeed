using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.Net;
using System.IO;

using UnityEngine;

namespace KLFServer
{
    class ServerConsole
    {
        const int AutoRestartDelay = 1000;
        const string Filename = "Configuration.xml";
        static ServerSettings Configuration;

        public enum ServerStatus
        { Stopped
        , Quit
        , Crashed
        , Restarting
        }

        static void Main(string[] args)
        {
            Console.Title = "KLF Server " + KLFCommon.ProgramVersion;
            Console.WriteLine("KLF Server Copyright (C) 2013 Alfred Lam");
            Console.WriteLine("This program comes with ABSOLUTELY NO WARRANTY; for details type `/show'.");
            Console.WriteLine("This is free software, and you are welcome to redistribute it");
            Console.WriteLine("under certain conditions; type `/show' for details.");
            Console.WriteLine();

            Configuration = ServerSettings.Load(Path.Combine("./", Filename));
            if(Configuration == null)
            {
                Configuration = new ServerSettings();//default cfg
                Configuration.Save(Path.Combine("./", Filename));
            }

            bool goodState = true;
            if(Configuration.AutoHost)
                goodState = ServerLoop();//jump right in
            while (goodState)
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("Port: ");
                Console.ResetColor();
                Console.WriteLine(Configuration.Port);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("HTTP Port: ");
                Console.ResetColor();
                Console.WriteLine(Configuration.HttpPort);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("Max Clients: ");
                Console.ResetColor();
                Console.WriteLine(Configuration.MaxClients);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("Join Message: ");
                Console.ResetColor();
                Console.WriteLine(Configuration.JoinMessage);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("Server Info: ");
                Console.ResetColor();
                Console.WriteLine(Configuration.ServerInfo);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("Updates Per Second: ");
                Console.ResetColor();
                Console.WriteLine(Configuration.UpdatesPerSecond);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("Total Inactive Ships: ");
                Console.ResetColor();
                Console.WriteLine(Configuration.TotalInactiveShips);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("Screenshot Height: ");
                Console.ResetColor();
                Console.WriteLine(Configuration.ScreenshotSettings.MaxHeight);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("Screenshot Interval: ");
                Console.ResetColor();
                Console.WriteLine(Configuration.ScreenshotInterval + "ms");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("Screenshot Backlog: ");
                Console.ResetColor();
                Console.WriteLine(Configuration.ScreenshotBacklog);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("Save Screenshots: ");
                Console.ResetColor();
                Console.WriteLine(Configuration.SaveScreenshots);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("Auto-Restart: ");
                Console.ResetColor();
                Console.WriteLine(Configuration.AutoRestart);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write("Auto-Host: ");
                Console.ResetColor();
                Console.WriteLine(Configuration.AutoHost);
                Console.ResetColor();
                Console.WriteLine();
                Console.WriteLine("/port    change port");
                Console.WriteLine("/http    change http port");
                Console.WriteLine("/max     change max clients");
                Console.WriteLine("/motd    join message");
                Console.WriteLine("/info    server info");
                Console.WriteLine("/rate    updates per second");
                Console.WriteLine("/ships   total inactive ships");
                Console.WriteLine("/size    screenshot height");
                Console.WriteLine("/shutter screenshot interval");
                Console.WriteLine("/album   screenshot save");
                Console.WriteLine("/recent  screenshot backlog");
                Console.WriteLine("/auto    toggle auto-restart");
                Console.WriteLine("/quick   toggle auto-host on startup");
                Console.WriteLine("/host    begin hosting");
                Console.WriteLine("/quit    quit");

                String[] MenuArgs = Console.ReadLine().Split(' ');
                switch(MenuArgs[0].ToLowerInvariant())
                {
                    case "/quit":
                        goodState = false;
                        break;
                    case "/port":
                        Console.Write("Enter the Port: ");
                        int newServerPort;
                        if (int.TryParse(Console.ReadLine(), out newServerPort))
                        {
                            Configuration.Port = newServerPort;
                            Configuration.Save();
                        }
                        else
                            Console.WriteLine("Invalid port ["
                                             + IPEndPoint.MinPort + '-'
                                             + IPEndPoint.MaxPort + ']'
                                             );
                        break;
                    case "/http":
                        Console.Write("Enter the HTTP Port: ");
                        int newHttpPort;
                        if (int.TryParse(Console.ReadLine(), out newHttpPort))
                        {
                            Configuration.HttpPort = newHttpPort;
                            Configuration.Save();
                        }
                        else
                            Console.WriteLine("Invalid port ["
                                             + IPEndPoint.MinPort + '-'
                                             + IPEndPoint.MaxPort + ']'
                                             );
                        break;
                    case "/max":
                        Console.Write("Enter the max number of clients: ");
                        int newMax;
                        if (int.TryParse(Console.ReadLine(), out newMax) && newMax > 0)
                        {
                            Configuration.MaxClients = newMax;
                            Configuration.Save();
                        }
                        else
                            Console.WriteLine("Invalid number of clients");
                        break;
                    case "/motd":
                        Console.Write("Enter the join message: ");
                        Configuration.JoinMessage = Console.ReadLine();
                        Configuration.Save();
                        break;
                    case "/info":
                        Console.Write("Enter the server info message: ");
                        Configuration.ServerInfo = Console.ReadLine();
                        Configuration.Save();
                        break;
                    case "/rate":
                        Console.Write("Enter the number of updates to receive per second: ");
                        float newRate;
                        if (float.TryParse(Console.ReadLine(), out newRate))
                        {
                            Configuration.UpdatesPerSecond = newRate;
                            Configuration.Save();
                        }
                        else
                            Console.WriteLine("Invalid updates per second ("
                                             + ServerSettings.MinUpdatesPerSecond + ".."
                                             + ServerSettings.MaxUpdatesPerSecond + ")"
                                             );
                        break;
                    case "/size":
                        Console.Write("Enter the screenshot height: ");
                        int newHeight;
                        if (int.TryParse(Console.ReadLine(), out newHeight))
                        {
                            Configuration.ScreenshotSettings.MaxHeight = newHeight;
                            Configuration.Save();
                        }
                        else
                            Console.WriteLine("Invalid screenshot height.");
                        break;
                    case "/shutter":
                        Console.Write("Enter the screenshot interval: ");
                        int newShutterSpeed;
                        if (int.TryParse(Console.ReadLine(), out newShutterSpeed))
                        {
                            Configuration.ScreenshotInterval = newShutterSpeed;
                            Configuration.Save();
                        }
                        break;
                    case "/recent":
                        Console.Write("Enter the screenshot backlog: ");
                        int newRecentCount;
                        if (int.TryParse(Console.ReadLine(), out newRecentCount) && newRecentCount >= 1)
                        {
                            Configuration.ScreenshotBacklog = newRecentCount;
                            Configuration.Save();
                        }
                        break;
                    case "/ships":
                        Console.Write("Enter the total number of inactive ships: ");
                        byte newTrash;
                        if (byte.TryParse(Console.ReadLine(), out newTrash))
                        {
                            Configuration.TotalInactiveShips = newTrash;
                            Configuration.Save();
                        }
                        else
                            Console.WriteLine("Invalid total inactive ships ["
                                             + Byte.MinValue + '-'
                                             + Byte.MaxValue + ']'
                                             );
                        break;
                    case "/album":
                        Configuration.SaveScreenshots = !Configuration.SaveScreenshots;
                        Configuration.Save();
                        break;
                    case "/auto":
                        Configuration.AutoRestart = !Configuration.AutoRestart;
                        Configuration.Save();
                        break;
                    case "/quick":
                        Configuration.AutoHost = !Configuration.AutoHost;
                        Configuration.Save();
                        break;
                    case "/host":
                        goodState = ServerLoop();
                        break;
                    case "/show":
                        ShowLicense();
                        break;
                    //TODO cases for Flooding and Throttle settings
                    default:
                        break;
                }
            }
        }

        static bool ServerLoop()
        {//auto-restart loop
            ServerStatus status = HostServer(Configuration);
            while (status == ServerStatus.Restarting)
            {
                System.Threading.Thread.Sleep(AutoRestartDelay);
                status = HostServer(Configuration);
            }
            if (status == ServerStatus.Quit)
            {
                Console.WriteLine("Press any key to quit");
                Console.ReadKey();
                return false;
            }
            else
                Console.WriteLine("Server "+Enum.GetName(typeof(ServerStatus), status).ToLower());
            return true;
        }

        static ServerStatus HostServer(ServerSettings s)
        {//main server instance
            Server server = new Server(s);
            try
            {
                server.HostingLoop();
            }
            catch (Exception e)
            {
                //Write an error log
                TextWriter writer = File.CreateText("KLFServerlog.txt");
                writer.WriteLine(e.ToString());
                if(server.CurrentThreadExceptionStackTrace != null
                && server.CurrentThreadExceptionStackTrace.Length > 0)
                {
                    writer.Write("Stacktrace: ");
                    writer.WriteLine(server.CurrentThreadExceptionStackTrace);
                }
                writer.Close();
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Red;
                Server.StampedConsoleWriteLine("Unexpected exception encountered! Crash report written to KLFServerlog.txt");
                Console.WriteLine(e.ToString());
                if(server.CurrentThreadExceptionStackTrace != null
                && server.CurrentThreadExceptionStackTrace.Length > 0)
                {
                    Console.Write("Stacktrace: ");
                    Console.WriteLine(server.CurrentThreadExceptionStackTrace);
                }
                Console.WriteLine();
                Console.ResetColor();
                //server.clearState();
                //return ServerStatus.Crashed;
            }
            server.ClearState();
            if (server.Stop)
                return ServerStatus.Stopped;
            if (!s.AutoRestart || server.Quit)
                return ServerStatus.Quit;
            return ServerStatus.Restarting;
        }

        private static void ShowLicense()
        {
            Console.WriteLine("KLFServer relays messages between KerbalLiveFeed clients.");
            Console.WriteLine("Copyright (C) 2013  Alfred Lam");
            Console.WriteLine();
            Console.WriteLine("This program is free software: you can redistribute it and/or modify");
            Console.WriteLine("it under the terms of the GNU General Public License as published by");
            Console.WriteLine("the Free Software Foundation, either version 3 of the License, or");
            Console.WriteLine("(at your option) any later version.");
            Console.WriteLine();
            Console.WriteLine("This program is distributed in the hope that it will be useful,");
            Console.WriteLine("but WITHOUT ANY WARRANTY; without even the implied warranty of");
            Console.WriteLine("MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the");
            Console.WriteLine("GNU General Public License for more details.");
            Console.WriteLine();
            Console.WriteLine("You should have received a copy of the GNU General Public License");
            Console.WriteLine("along with this program.  If not, see <http://www.gnu.org/licenses/>.");
        }
    }
}
