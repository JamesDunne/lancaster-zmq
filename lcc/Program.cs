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
                string endpoint = "*";
                string subscription = String.Empty;

                Queue<string> argQueue = new Queue<string>(args);
                while (argQueue.Count > 0)
                {
                    string arg = argQueue.Dequeue();

                    switch (arg)
                    {
                        case "-e":
                            if (argQueue.Count == 0)
                            {
                                Console.Error.WriteLine("-e expects an endpoint argument");
                                return;
                            }
                            endpoint = argQueue.Dequeue();
                            break;
                        case "-d":
                            if (argQueue.Count == 0)
                            {
                                Console.Error.WriteLine("-d expects a path argument");
                                return;
                            }

                            downloadDirectory = new DirectoryInfo(argQueue.Dequeue());
                            break;
                        case "-s":
                            if (argQueue.Count == 0)
                            {
                                Console.Error.WriteLine("-s expects a subscription name argument");
                                return;
                            }
                            subscription = argQueue.Dequeue();
                            break;
                        case "-?":
                            DisplayUsage();
                            return;
                        default:
                            break;
                    }
                }

                if ((endpoint == null) || (downloadDirectory == null))
                {
                    DisplayUsage();
                    return;
                }

                downloadDirectory.Create();

                // Create a local state directory:
                lccDir = downloadDirectory.CreateSubdirectory(".lcc");
                localStateFile = new FileInfo(Path.Combine(lccDir.FullName, ".chunks"));

                // Create the client:
                var client = new LanCaster.ClientHost(endpoint, subscription, downloadDirectory, new ClientHost.GetClientNAKStateDelegate(GetClientNAKState));
                client.ChunkWritten += new Action<int>(ChunkWritten);

                // Start the client thread and wait for it to complete:
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
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("Failed completion.");
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
        private bool wroteLegend = false;

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

            if (!wroteLegend)
            {
                Console.WriteLine();
                Console.WriteLine(" '-' no chunks    '*' some chunks    '#' all chunks    'O' current zone");
                Console.WriteLine();
                wroteLegend = true;
            }

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

        private static void DisplayHeader()
        {
            // Displays the error text wrapped to the console's width:
            Console.Error.WriteLine(
                String.Join(
                    Environment.NewLine,
                    (
                        from line in new string[] {
@"lcc.exe <options> ...",
@"Version " + System.Reflection.Assembly.GetEntryAssembly().GetName().Version.ToString(4),
@"(C)opyright 2011 James S. Dunne <lancaster@bittwiddlers.org>",
                        }
                        // Wrap the lines to the window width:
                        from wrappedLine in line.WordWrap(Console.WindowWidth - 1)
                        select wrappedLine
                    ).ToArray()
                )
            );
        }

        private static void DisplayUsage()
        {
            DisplayHeader();

            string[][] prms = new string[][] {
new[] { @"" },
new[] { @"-e <endpoint>",       @"Connect to a server at the given address. Optionally add a ':' and port number to specify a custom port number, default port is 12198." },
new[] { @"-d <path>",           @"Download files to local directory (will be created if it doesn't exist)." },
new[] { @"-s <subscription>",   @"Set subscription name to filter out transfers from other servers on the same endpoint. Default is empty." },
            };

            // Displays the error text wrapped to the console's width:
            int maxprmLength = prms.Where(prm => prm.Length == 2).Max(prm => prm[0].Length);

            Console.Error.WriteLine(
                String.Join(
                    Environment.NewLine,
                    (
                        from cols in prms
                        let wrap1 = (cols.Length == 1) ? null
                            : cols[1].WordWrap(Console.WindowWidth - maxprmLength - 2)
                        let tmp = (cols.Length == 1) ? cols[0].WordWrap(Console.WindowWidth - 1)
                            : Enumerable.Repeat(cols[0] + new string(' ', maxprmLength - cols[0].Length + 1) + wrap1.First(), 1)
                              .Concat(
                                from line in wrap1.Skip(1)
                                select new string(' ', maxprmLength + 1) + line
                              )
                        from wrappedLine in tmp
                        select wrappedLine
                    ).ToArray()
                )
            );
        }
    }
}
