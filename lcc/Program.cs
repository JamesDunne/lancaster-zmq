using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using ZMQ;
using System.IO;

namespace WellDunne.LanCaster.Client
{
    class Program
    {
        static void Main(string[] args)
        {
            // Get the current directory:
            string path = Environment.CurrentDirectory;
            // Get all files recursively from the current directory:
            var files = new DirectoryInfo(path).GetFiles("*.*", SearchOption.AllDirectories);
            var clientPath = new DirectoryInfo(Path.Combine(Path.GetTempPath(), "lcc"));
            clientPath.Create();
            
            using (var serverTarball = new TarballStreamWriter(files))
            {
                var server = new LanCaster.ServerHost(serverTarball, path);
                var serverThread = new Thread(server.Run);

                var client = new LanCaster.ClientHost(clientPath);
                var clientThread = new Thread(client.Run);

                using (Context ctx = new Context(1))
                {
                    serverThread.Start(ctx);
                    clientThread.Start(ctx);

                    serverThread.Join();
                    clientThread.Join();
                }
            }
        }
    }
}
