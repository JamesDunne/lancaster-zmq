using System;
using System.IO;
using System.Threading;
using ZMQ;
using System.Collections;

namespace WellDunne.LanCaster.Client
{
    class Program
    {
        static void Main(string[] args)
        {
            new Program().Run(args);
        }

        private DirectoryInfo lccDir;
        private FileInfo localStateFile;
        private FileStream localStateStream;

        void Run(string[] args)
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

            // Create a local state directory:
            lccDir = clientPath.CreateSubdirectory(".lcc");
            localStateFile = new FileInfo(Path.Combine(lccDir.FullName, ".chunks"));

            var client = new LanCaster.ClientHost(endpoint, subscription, clientPath, new ClientHost.GetClientNAKStateDelegate(GetClientNAKState));
            client.ChunkWritten += new Action<int>(ChunkWritten);
            var clientThread = new Thread(client.Run);

            using (Context ctx = new Context(1))
            {
                clientThread.Start(ctx);
                clientThread.Join();
            }
        }

        private BitArray acks;
        private byte[] ackBuf;

        void ChunkWritten(int chunkIdx)
        {
            // Acknowledge the chunk is received:
            acks[chunkIdx] = true;

            // Update the state file:
            acks.CopyTo(ackBuf, 0);
            localStateStream.Seek(0L, SeekOrigin.Begin);
            localStateStream.Write(ackBuf, 0, ackBuf.Length);
            localStateStream.Flush();
        }

        BitArray GetClientNAKState(int numChunks, TarballStreamReader tarball)
        {
            int numChunkBytes = ((numChunks + 7) & ~7) >> 3;
            ackBuf = new byte[numChunkBytes];

            if (localStateFile.Exists)
            {
                localStateStream = localStateFile.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                if (localStateStream.Length != numChunkBytes)
                {
                    localStateStream.Close();
                    Console.Error.WriteLine("Inconsistent state file. Overwriting with new state.");
                    localStateFile.Delete();
                }
                else
                {
                    // Read the NAK state:
                    localStateStream.Seek(0L, SeekOrigin.Begin);
                    localStateStream.Read(ackBuf, 0, numChunkBytes);
                }
            }

            if (!localStateFile.Exists)
            {
                // Create a new file and allocate enough space for storing a NAK bit array:
                localStateStream = localStateFile.Open(FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                localStateStream.SetLength(numChunkBytes);
                localStateStream.Seek(0L, SeekOrigin.Begin);
                localStateStream.Write(ackBuf, 0, numChunkBytes);
            }

            acks = new BitArray(ackBuf);

            return acks.Not();
        }
    }
}
