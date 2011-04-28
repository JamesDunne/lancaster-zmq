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
        private bool testMode;
        private int ioThreads;

        private int consoleWidth;

        void Run(string[] args)
        {
            try
            {
                consoleWidth = Console.WindowWidth;
            }
            catch (IOException)
            {
                consoleWidth = 80;
            }

            try
            {
                Transport tsp = Transport.TCP;
                string endpointData = null, endpointCtl = null;
                string subscription = String.Empty;
                int tmp;
                int networkHWM = 0, diskHWM = 20;
                int pgmRate = 819200;
                ioThreads = 1;

                Queue<string> argQueue = new Queue<string>(args);
                while (argQueue.Count > 0)
                {
                    string arg = argQueue.Dequeue();

                    switch (arg)
                    {
                        case "-a":
                            if (argQueue.Count == 0)
                            {
                                Console.Error.WriteLine("-a expects a transport name argument");
                                return;
                            }
                            if (endpointData != null)
                            {
                                Console.Error.WriteLine("-a must appear before -e in order");
                                return;
                            }
                            tsp = (Transport)Enum.Parse(typeof(Transport), argQueue.Dequeue(), true);
                            break;
                        case "-e":
                            if (tsp == Transport.TCP)
                            {
                                if (argQueue.Count == 0)
                                {
                                    Console.Error.WriteLine("-e expects an endpoint argument");
                                    return;
                                }
                                endpointCtl = endpointData = argQueue.Dequeue();
                            }
                            else if (tsp == Transport.EPGM)
                            {
                                if (argQueue.Count <= 1)
                                {
                                    Console.Error.WriteLine("-e expects two endpoint arguments");
                                    return;
                                }
                                endpointData = argQueue.Dequeue();
                                endpointCtl = argQueue.Dequeue();
                            }
                            else
                            {
                                Console.Error.WriteLine("Unsupported protocol {0}", tsp.ToString());
                                return;
                            }
                            break;
                        case "-r":
                            if (argQueue.Count == 0)
                            {
                                Console.Error.WriteLine("-r expects a rate limit in kilobits per second argument");
                                return;
                            }

                            Int32.TryParse(argQueue.Dequeue(), out pgmRate);
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
                        case "-t":
                            testMode = true;
                            break;
                        case "-n":
                            if (argQueue.Count == 0)
                            {
                                Console.Error.WriteLine("-n expects a number of I/O threads argument");
                                return;
                            }
                            if (Int32.TryParse(argQueue.Dequeue(), out tmp))
                            {
                                ioThreads = tmp;
                            }
                            break;
                        case "-w":
                            if (argQueue.Count == 0)
                            {
                                Console.Error.WriteLine("-w expects a high water mark argument");
                                return;
                            }
                            Int32.TryParse(argQueue.Dequeue(), out networkHWM);
                            break;
                        case "-k":
                            if (argQueue.Count == 0)
                            {
                                Console.Error.WriteLine("-k expects a high water mark argument");
                                return;
                            }
                            Int32.TryParse(argQueue.Dequeue(), out diskHWM);
                            break;
                        case "-?":
                            DisplayUsage();
                            return;
                        default:
                            break;
                    }
                }

                if ((endpointData == null) || (downloadDirectory == null))
                {
                    DisplayUsage();
                    return;
                }

                if (!testMode)
                {
                    // Create the local download directory:
                    downloadDirectory.Create();

                    // Create a local state directory:
                    lccDir = downloadDirectory.CreateSubdirectory(".lcc");
                    localStateFile = new FileInfo(Path.Combine(lccDir.FullName, ".chunks"));
                }

                // Create the client:
                var client = new LanCaster.ClientHost(tsp, endpointData, endpointCtl, subscription, downloadDirectory, testMode, new ClientHost.GetClientNAKStateDelegate(GetClientNAKState), networkHWM, diskHWM, pgmRate);
                client.ChunkReceived += new Action<ClientHost, int>(ChunkReceived);
                client.ChunkWritten += new Action<ClientHost, int>(ChunkWritten);

                // Start the client thread and wait for it to complete:
                var clientThread = new Thread(client.Run);
                using (Context ctx = new Context(ioThreads))
                {
                    clientThread.Start(ctx);
                    clientThread.Join();
                }

                // Force the last progress report:
                RenderProgress(client, true);

                if (client.Completed)
                {
                    // Clean up our download state on successful completion:
                    Console.WriteLine();
                    Console.WriteLine("Successful completion.");
                    if (!testMode)
                    {
                        if (localStateStream != null) localStateStream.Close();
                        localStateFile.Delete();
                        lccDir.Delete(true);
                    }
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
        //private BitArray acks;
        private byte[] nakBuf;

        private int lastChunkBlock = -1;
        private int lastWrittenChunk = 0;
        private bool wroteLegend = false;
        private DateTimeOffset lastDisplayTime;
        private readonly object lineWriter = new object();

        void WriteRate(int bps)
        {
            string name;
            double rate;

            if (bps >= 1048576)
            {
                rate = bps / 1048576d;
                name = "MB/s";
            }
            else if (bps >= 1024)
            {
                rate = bps / 1024d;
                name = "KB/s";
            }
            else
            {
                rate = bps;
                name = " B/s";
            }

            Console.Write("{0,10} {1}", rate.ToString("#,##0.00"), name);
        }

        void RenderProgress(ClientHost host, bool display)
        {
            lock (lineWriter)
            {
                if (numChunks == 0) return;

                int usableWidth = consoleWidth - 3;

                int blocks = numChunks / usableWidth;
                int blocksRem = numChunks % usableWidth;

                int subblocks = usableWidth / numChunks;
                int subblocksRem = usableWidth % numChunks;

                if (!display)
                {
                    if (blocks > 0) display = (DateTimeOffset.UtcNow.Subtract(lastDisplayTime).TotalMilliseconds >= 500d) || (lastChunkBlock != (lastChunkBlock = lastWrittenChunk / blocks));
                    else display = true;
                }

                if (display)
                {
                    lastDisplayTime = DateTimeOffset.UtcNow;
                    if (!wroteLegend)
                    {
                        Console.WriteLine();
                        Console.WriteLine(" '-' no chunks    '*' some chunks    '#' all chunks    'W' current writer");
                        Console.WriteLine();
                        wroteLegend = true;
                    }

                    string backup = new string('\b', consoleWidth);
                    Console.Write(backup);
#if true
                    string spaces = new string(' ', consoleWidth - 1);
                    Console.Write(spaces);
                    Console.Write(backup);
                    // Write network receive rate:
                    WriteRate(host.NetworkRecvChunksPerMinute * (host.ChunkSize / 60));
                    // Write disk write rate:
                    WriteRate(host.DiskWriteChunksPerMinute * (host.ChunkSize / 60));
                    Console.WriteLine();
#endif
                    Console.Write('[');

                    // We must clone due to thread-safety issues using enumerators:
                    BitArray naks = host.NAKs.Clone() as BitArray;
                    IEnumerator<bool> boolACKs = naks.Cast<bool>().Take(numChunks).GetEnumerator();
                    if (blocks > 0)
                    {
                        int lastc = 0, c = 0, subc = 0;
                        while (c < usableWidth)
                        {
                            int numBlocks = blocks;
                            lastc = c++;
                            if ((blocksRem > 0) && (subc++ >= blocksRem))
                            {
                                ++numBlocks;
                                ++c;
                                subc = 0;
                            }

                            bool allOn = true;
                            bool allOff = false;
                            for (int i = 0; (i < numBlocks) && boolACKs.MoveNext(); ++i)
                            {
                                bool curr = !boolACKs.Current;
                                allOn = allOn & curr;
                                allOff = allOff | curr;
                            }

                            for (int x = lastc; (x < c) && (c < usableWidth); ++x)
                            {
                                if ((lastWrittenChunk >= c * blocks) && (lastWrittenChunk < (c + 1) * blocks)) Console.Write('W');
                                else if (allOn) Console.Write('#');
                                else if (allOff) Console.Write('*');
                                else Console.Write('-');
                            }
                        }
                    }
                    else
                    {
                        int lastc = 0, c = 0, subc = 0;
                        for (int i = 0; i < numChunks; ++i)
                        {
                            lastc = c;
                            c += subblocks;
                            if ((subblocksRem > 0) && (subc++ >= subblocksRem))
                            {
                                ++c;
                                subc = 0;
                            }

                            if (!boolACKs.MoveNext()) break;
                            bool curr = boolACKs.Current;

                            for (int x = lastc; (x < c) && (c < usableWidth); ++x)
                            {
                                if (lastWrittenChunk == i) Console.Write('W');
                                else if (curr) Console.Write('#');
                                else Console.Write('-');
                            }
                        }
                    }

                    Console.Write(']');
                }
            }
        }

        void ChunkReceived(ClientHost host, int chunkIdx)
        {
            RenderProgress(host, false);
        }

        void ChunkWritten(ClientHost host, int chunkIdx)
        {
            // Acknowledge the chunk is received:
            //acks[chunkIdx] = true;

            if (!testMode)
            {
                // Update the state file:
                BitArray naks = host.NAKs.Clone() as BitArray;
                naks.CopyTo(nakBuf, 0);
                localStateStream.Seek(0L, SeekOrigin.Begin);
                localStateStream.Write(nakBuf, 0, nakBuf.Length);
                localStateStream.Flush();
            }

            lastWrittenChunk = chunkIdx;

            RenderProgress(host, false);
        }

        BitArray GetClientNAKState(ClientHost host, TarballStreamReader tarball)
        {
            Console.WriteLine("Receiving {0} files to '{1}':", tarball.Files.Count, downloadDirectory.FullName);
            foreach (var fi in tarball.Files)
            {
                Console.WriteLine("{0,15} {1}", fi.Length.ToString("##,#"), fi.RelativePath);
            }
            Console.WriteLine();
            Console.WriteLine("{0,15} chunks @ {1,13} bytes/chunk", host.NumChunks.ToString("##,0"), host.ChunkSize.ToString("##,0"));

            this.numChunks = host.NumChunks;
            int numChunkBytes = host.NumBytes;
            nakBuf = new byte[numChunkBytes];
            for (int i = 0; i < numChunkBytes; ++i) nakBuf[i] = 0xFF;

            if (!testMode)
            {
                localStateFile.Refresh();
                if (localStateFile.Exists)
                {
                    if (localStateStream != null)
                    {
                        localStateStream.Close();
                    }
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
                        localStateStream.Read(nakBuf, 0, numChunkBytes);
                    }
                }

                localStateFile.Refresh();
                if (!localStateFile.Exists)
                {
                    // Create a new file and allocate enough space for storing a NAK bit array:
                    localStateStream = localStateFile.Open(FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                    localStateStream.SetLength(numChunkBytes);
                    localStateStream.Seek(0L, SeekOrigin.Begin);
                    localStateStream.Write(nakBuf, 0, numChunkBytes);
                }
            }

            return new BitArray(nakBuf).Clone() as BitArray;
        }

        private void DisplayHeader()
        {
            // Displays the error text wrapped to the console's width:
            Console.Error.WriteLine(
                String.Join(
                    Environment.NewLine,
                    (
                        from line in new string[] {
@"lcc.exe <options> ...",
@"",
@"LanCaster Client - A multicast file transfer client",
@"Version " + System.Reflection.Assembly.GetEntryAssembly().GetName().Version.ToString(4),
@"(C)opyright 2011 James S. Dunne <lancaster@bittwiddlers.org>",
                        }
                        // Wrap the lines to the window width:
                        from wrappedLine in line.WordWrap(consoleWidth - 1)
                        select wrappedLine
                    ).ToArray()
                )
            );
        }

        private void DisplayUsage()
        {
            DisplayHeader();

            string[][] prms = new string[][] {
new[] { @"" },
new[] { @"-a <transport>",      @"Use TCP or EPGM (multicast over UDP) transport for data. Default is TCP. Must occur before -e option." },
new[] { @"-e <data endpoint> [control endpoint]",       @"(REQUIRED) Connect to a server at the given address. Optionally add a ':' and port number to specify a custom port number, default port is 12198. The control endpoint is only required for EPGM protocol." },
new[] { @"-d <path>",           @"(REQUIRED) Download files to local directory (will be created if it doesn't exist)." },
new[] { @"-s <subscription>",   @"Set subscription name to filter out transfers from other servers on the same endpoint. Default is empty." },
new[] { @"-t",                  @"Test mode - don't write to filesystem, just act as a dummy client." },
new[] { @"-n <io threads>",     @"Set this value to the number of threads you wish 0MQ to dedicate to network I/O. Default is 1." },
new[] { @"-w <hwm>",            @"Set the network high water mark (HWM) which is the maximum number of chunks 0MQ will queue from the network before dropping them. Default is 0 - no high water mark." },
new[] { @"-k <hwm>",            @"Set the disk high water mark (HWM) which is the maximum number of chunks 0MQ will queue up to write to disk before dropping them. Default is 20; 0 means no high water mark." },
new[] { @"-r <rate>",           @"Set the PGM rate limit which is the kilobits per second rate at which data will be multicast. Only applicable if -a is set to PGM or EPGM. Default is 819200 (100 MB/sec)." },
new[] { @"" },
new[] { @"NOTE ABOUT HWMs (high water mark values):" },
new[] { @"Be careful to not have network and disk HWMs BOTH set to zero because this will exhaust your memory until the program dies." },
            };

            // Displays the error text wrapped to the console's width:
            int maxprmLength = prms.Where(prm => prm.Length == 2).Max(prm => prm[0].Length);

            Console.Error.WriteLine(
                String.Join(
                    Environment.NewLine,
                    (
                        from cols in prms
                        let wrap1 = (cols.Length == 1) ? null
                            : cols[1].WordWrap(consoleWidth - maxprmLength - 2)
                        let tmp = (cols.Length == 1) ? cols[0].WordWrap(consoleWidth - 1)
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
