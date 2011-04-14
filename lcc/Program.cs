using System;
using System.Linq;
using System.IO;
using System.Threading;
using ZMQ;
using System.Collections;
using System.Collections.Generic;

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
        private DirectoryInfo downloadDirectory;

        void Run(string[] args)
        {
            try
            {
                if (args.Length != 3)
                {
                    Console.WriteLine("lcc <server endpoint> <subscription> <local download folder>");
                    return;
                }

                string endpoint = args[0];
                string subscription = args[1];
                downloadDirectory = new DirectoryInfo(args[2]);
                downloadDirectory.Create();

                // Create a local state directory:
                lccDir = downloadDirectory.CreateSubdirectory(".lcc");
                localStateFile = new FileInfo(Path.Combine(lccDir.FullName, ".chunks"));

                var client = new LanCaster.ClientHost(endpoint, subscription, downloadDirectory, new ClientHost.GetClientNAKStateDelegate(GetClientNAKState));
                client.ChunkWritten += new Action<int>(ChunkWritten);
                var clientThread = new Thread(client.Run);

                using (Context ctx = new Context(1))
                {
                    clientThread.Start(ctx);
                    clientThread.Join();
                }

                if (client.Completed)
                {
                    // Clean up our download state on successful completion:
                    Console.WriteLine();
                    Console.WriteLine("Successful completion.");
                    localStateStream.Close();
                    localStateFile.Delete();
                    lccDir.Delete(true);
                }
            }
            catch (System.Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
            }
        }

        private int numChunks;
        private BitArray acks;
        private byte[] ackBuf;

        private int chunksWritten = 0;

        void ChunkWritten(int chunkIdx)
        {
            // Acknowledge the chunk is received:
            acks[chunkIdx] = true;

            // Update the state file:
            acks.CopyTo(ackBuf, 0);
            localStateStream.Seek(0L, SeekOrigin.Begin);
            localStateStream.Write(ackBuf, 0, ackBuf.Length);
            localStateStream.Flush();

#if false
            Console.WriteLine("Wrote chunk {0,13} of {1,13}", (chunkIdx + 1).ToString("##,#"), numChunks.ToString("##,#"));
#else
            int blocks = (numChunks / (Console.WindowWidth - 3));

            if (chunksWritten != 0)
            {
                chunksWritten = (chunksWritten + 1) % blocks;
                return;
            }
            chunksWritten = (chunksWritten + 1) % blocks;

            string backup = new string('\b', Console.WindowWidth);
            Console.Write(backup);
            Console.Write('[');

            IEnumerator boolACKs = acks.GetEnumerator();
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
            Console.Write(']');
#endif
        }

        BitArray GetClientNAKState(ClientHost host, int numChunks, int chunkSize, TarballStreamReader tarball)
        {
            Console.WriteLine("Receiving {0} files to '{1}':", tarball.Files.Count, downloadDirectory.FullName);
            foreach (var fi in tarball.Files)
            {
                Console.WriteLine("{0,15} {1}", fi.Length.ToString("##,#"), fi.RelativePath);
            }
            Console.WriteLine();
            Console.WriteLine("{0,15} chunks @ {1,13} bytes/chunk", numChunks.ToString("##,#"), chunkSize.ToString("##,#"));

            this.numChunks = numChunks;
            int numChunkBytes = ((numChunks + 7) & ~7) >> 3;
            ackBuf = new byte[numChunkBytes];

            if (localStateFile.Exists)
            {
                localStateStream = localStateFile.Open(FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
                if (localStateStream.Length != numChunkBytes)
                {
                    localStateStream.Close();
                    Console.Error.WriteLine("Inconsistent state file. Overwriting with new state.");
                    localStateFile.Delete();
                    localStateFile.Refresh();
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
                localStateStream = localStateFile.Open(FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                localStateStream.SetLength(numChunkBytes);
                localStateStream.Seek(0L, SeekOrigin.Begin);
                localStateStream.Write(ackBuf, 0, numChunkBytes);
            }

            acks = new BitArray(ackBuf);

            return (acks.Clone() as BitArray).Not();
        }
    }
}
