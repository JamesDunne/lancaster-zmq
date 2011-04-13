using System;
using System.IO;
using System.Threading;
using ZMQ;

namespace WellDunne.LanCaster.Client
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length != 3)
            {
                Console.WriteLine("lcc <server endpoint> <subscription> <local download folder>");
                return;
            }

            string endpoint = args[0];
            string subscription = args[1];
            var clientPath = new DirectoryInfo(args[2]);
            clientPath.Create();

            var client = new LanCaster.ClientHost(endpoint, subscription, clientPath);
            var clientThread = new Thread(client.Run);

            using (Context ctx = new Context(1))
            {
                clientThread.Start(ctx);
                clientThread.Join();
            }
        }
    }
}
