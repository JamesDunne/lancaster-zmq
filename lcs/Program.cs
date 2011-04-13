using System;
using System.Linq;
using System.IO;
using System.Threading;
using ZMQ;
using System.Configuration;

namespace WellDunne.LanCaster.Server
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length != 3)
            {
                Console.WriteLine("lcs <server endpoint> <subscription> <local upload folder>");
                return;
            }

            string endpoint = args[0];
            string subscription = args[1];
            string path = args[2];
            
            // Get all files recursively from the current directory, excluding any special '.lcc' download state folders/files:
            var files =
                from fi in new DirectoryInfo(path).GetFiles("*.*", SearchOption.AllDirectories)
                where fi.Directory.Name != ".lcc"
                select fi;

            int chunkSize = 64 * 1024;
            string chunkSizeValue = ConfigurationManager.AppSettings["ChunkSize"];
            if (chunkSizeValue != null)
                Int32.TryParse(chunkSizeValue, out chunkSize);

            using (var serverTarball = new TarballStreamWriter(files))
            {
                var server = new LanCaster.ServerHost(endpoint, subscription, serverTarball, path, chunkSize);
                var serverThread = new Thread(server.Run);

                using (Context ctx = new Context(1))
                {
                    serverThread.Start(ctx);
                    serverThread.Join();
                }
            }
        }
    }
}
