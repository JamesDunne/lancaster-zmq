using System;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading;
using ZMQ;
using System.Collections;

namespace WellDunne.LanCaster.Server
{
    class Program
    {
        static void Main(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("lcs <server endpoint> <subscription> <local upload folder> [chunk size in bytes]");
                return;
            }

            string endpoint = args[0];
            string subscription = args[1];
            DirectoryInfo uploadDir = new DirectoryInfo(args[2]);
            string basePath = uploadDir.FullName + Path.DirectorySeparatorChar;
            
            // Get all files recursively from the current directory, excluding any special '.lcc' download state folders/files:
            var files = (
                from fi in uploadDir.GetFiles("*.*", SearchOption.AllDirectories)
                where fi.Directory.Name != ".lcc"
                select fi
            ).ToList();

            // Set default chunk size:
            int chunkSize = 128 * 1024;

            // Override with config value if exists:
            string chunkSizeValue = ConfigurationManager.AppSettings["ChunkSize"];
            if (chunkSizeValue != null)
                Int32.TryParse(chunkSizeValue, out chunkSize);

            // Override the config with the commandline arg if exists:
            if (args.Length >= 4)
                Int32.TryParse(args[3], out chunkSize);

            using (var serverTarball = new TarballStreamWriter(files))
            {
                Console.WriteLine("Serving {0} files from '{1}':", files.Count, basePath);
                var server = new LanCaster.ServerHost(endpoint, subscription, serverTarball, basePath, chunkSize);

                foreach (var fi in serverTarball.Files)
                {
                    string fiName = fi.FullName.Substring(basePath.Length);
                    Console.WriteLine("{0,15} {1}", fi.Length.ToString("##,#"), fiName);
                }
                Console.WriteLine();
                Console.WriteLine("{0,15} chunks @ {1,13} bytes/chunk", server.NumChunks.ToString("##,#"), server.ChunkSize.ToString("##,#"));

                server.ChunkSent += new Action<ServerHost, int>(ChunkSent);

                // Begin the server thread:
                var serverThread = new Thread(server.Run);
                using (Context ctx = new Context(1))
                {
                    serverThread.Start(ctx);
                    serverThread.Join();
                }
            }
        }

        static int lastChunkBlock = -1;

        static void ChunkSent(ServerHost host, int chunkIdx)
        {
#if false
            Console.WriteLine("Broadcast chunk {0,13} of {1,13}", (chunkIdx + 1).ToString("##,#"), host.NumChunks.ToString("##,#"));
#else
            int blocks = host.NumChunks / (Console.WindowWidth - 3);
            int blocksRem = host.NumChunks % (Console.WindowWidth - 3);
            int currChunkBlock = chunkIdx / blocks;

            if (currChunkBlock != lastChunkBlock)
            {
                lastChunkBlock = currChunkBlock;
                string backup = new string('\b', Console.WindowWidth);
                Console.Write(backup);
                Console.Write('[');

#if false
                for (int i = 0; i < currChunkBlock; ++i) Console.Write('-');
                Console.Write('O');
                for (int i = currChunkBlock + 1; i < (Console.WindowWidth - 3); ++i) Console.Write('-');
#else
                IEnumerator boolACKs = host.NAKs.GetEnumerator();
                for (int c = 0; c < Console.WindowWidth - 3; ++c)
                {
                    bool allOn = true;
                    bool allOff = false;
                    for (int i = 0; boolACKs.MoveNext() && (i < blocks); ++i)
                    {
                        allOn = allOn & ((bool)boolACKs.Current);
                        allOff = allOff | ((bool)boolACKs.Current);
                    }

                    if ((chunkIdx >= c * blocks) && (chunkIdx < (c + 1) * blocks)) Console.Write('O');
                    else if (allOn) Console.Write('#');
                    else if (allOff) Console.Write('*');
                    else Console.Write('-');
                }
#endif

                Console.Write(']');
            }
#endif
        }
    }
}
