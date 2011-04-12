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
            var server = new LanCaster.ServerHost(new TarballStreamWriter(Enumerable.Empty<FileInfo>()));
            var serverThread = new Thread(server.Run);

            var client = new LanCaster.ClientHost();
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
