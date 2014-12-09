using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Diagnostics;

using System.IO;
using System.Collections.Concurrent;

class ClientMain
{
    const string Filename = "KLFClientConfig.xml";
    static ClientSettings Configuration;

    static void Main(string[] args)
    {
        Console.Title = "KLF Client " + KLFCommon.ProgramVersion;
        Console.WriteLine("KLF Client Copyright (C) 2013 Alfred Lam");
        Console.WriteLine("This program comes with ABSOLUTELY NO WARRANTY; for details type `/show'.");
        Console.WriteLine("This is free software, and you are welcome to redistribute it");
        Console.WriteLine("under certain conditions; type `/show' for details.");
        Console.WriteLine();

        ConsoleClient client = new ConsoleClient();
        Configuration = ClientSettings.Load(Path.Combine("./", Filename));
        while(true)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("Username: ");
            Console.ResetColor();
            Console.WriteLine(Configuration.Username);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("Server Address: ");
            Console.ResetColor();
            Console.WriteLine(((Uri)Configuration.GetDefaultServer()).ToString());

            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write("Auto-Reconnect: ");
            Console.ResetColor();
            Console.WriteLine(Configuration.Reconnect);

            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("/name     change username");
            //Console.WriteLine("/auth     change token");
            Console.WriteLine("/add      add server");
            Console.WriteLine("/select   select default server");
            Console.WriteLine("/connect  connect to default server");
            Console.WriteLine("/auto     toggle auto-reconnect");
            Console.WriteLine("/quit     quit KLFClient");

            String[] MenuArgs = Console.ReadLine().Split(' ');
            switch(MenuArgs[0].ToLowerInvariant())
            {
                case "/quit":
                case "/q":
                    Console.Write("Closing.\n");
                    return;
                case "/name":
                case "/n":
                case "/user":
                case "/u":
                    if(MenuArgs.Length > 1)
                        Configuration.Username = MenuArgs[1];
                    else
                    {
                        Console.Write("Enter your new username: ");
                        Configuration.Username = Console.ReadLine();
                    }
                    Configuration.Save(Filename);
                    break;
                case "/auth":
                    //TODO auth token feature
                    break;
                case "/add":
                case "/server":
                    String hn;
                    Int32 pn;
                    Console.Write("Host name or IP Address: ");
                    hn = Console.ReadLine();
                    Console.Write("Port number: ");
                    Int32.TryParse(Console.ReadLine(), out pn);
                    if(Configuration.AddServer(hn, pn))
                        Console.WriteLine("Server Added.");
                    else
                        Console.WriteLine("Bad input.");
                    Configuration.Save(Filename);
                    break;
                case "/auto":
                case "/a":
                    Configuration.Reconnect = !Configuration.Reconnect;
                    Configuration.Save(Filename);
                    break;
                case "/select":
                case "/sel":
                case "/list":
                case "/default":
                case "/def":
                    Int32 choice = 0;
                    foreach(ServerProfile server in Configuration.Servers)
                        Console.WriteLine("  {0}{1} {2}", choice++, server.Default?"*":" ", server.Uri.ToString());
                    Console.Write("Choose default server: ");
                    if(!Int32.TryParse(Console.ReadLine(), out choice))
                            choice = -1;//invalid
                    if(0 <= choice && choice < Configuration.Servers.Count)
                    {
                        Configuration.SetDefaultServer(choice);
                        Configuration.Save(Filename);
                        Console.WriteLine("Default saved.");
                    }
                    else
                        Console.WriteLine("Invalid index. No changes.");
                    break;
                case "/connect":
                case "/co":
                    //TODO validate
                    if(Configuration.ValidServer(Configuration.GetDefaultServer().Host, Configuration.GetDefaultServer().Port))
                        client.Connect(Configuration);
                    else
                        Console.WriteLine("/add a server then use /def to select default.");
                    break;
                case "/show":
                    ShowLicense();
                    break;
                default:
                    break;
            }
        }//end while
    }

    private static void ShowLicense()
    {
        Console.WriteLine("KLFClient relays messages between KerbalLiveFeed and server.");
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
