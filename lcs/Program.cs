using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Threading;
using ZMQ;

namespace WellDunne.LanCaster.Server
{
    class Program
    {
        static void Main(string[] args)
        {
            new Program().Run(args);
        }

        void Run(string[] args)
        {
            string endpoint = "*";
            string subscription = String.Empty;
            DirectoryInfo tmpDir = null;
            List<DirectoryInfo> uploadDirs = new List<DirectoryInfo>();
            DirectoryInfo baseDir = null;
            string basePath = null;
            bool recurseMode = true;
            List<FileInfo> files = new List<FileInfo>();
            HashSet<string> ignoreFiles = new HashSet<string>();
            HashSet<string> ignoreExtensions = new HashSet<string>();
            bool listFiles = false;

            // Set default chunk size to 1MB:
            int chunkSize = 1024 * 1024;
            int queueBacklog = 128;
            int ioThreads = 1;

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

                        tmpDir = new DirectoryInfo(argQueue.Dequeue());
                        if (!tmpDir.Exists)
                        {
                            Console.Error.WriteLine("Directory '{0}' does not exist!", tmpDir.FullName);
                            return;
                        }

                        uploadDirs.Add(tmpDir);
                        // Set the base directory to this directory if the base was not already set:
                        if ((baseDir == null) && (uploadDirs.Count == 1))
                        {
                            baseDir = tmpDir;
                            basePath = baseDir.FullName.EndsWith(Path.DirectorySeparatorChar.ToString()) ? baseDir.FullName : baseDir.FullName + Path.DirectorySeparatorChar;
                        }

                        // Add all files from the given directory, recursively or not, excluding any special '.lcc' download state folders/files:
                        files.AddRange(
                            from fi in (recurseMode ? tmpDir.GetFiles("*.*", SearchOption.AllDirectories) : tmpDir.GetFiles())
                            where fi.Directory.Name != ".lcc"
                            where !ignoreFiles.Contains(fi.FullName.Substring(basePath.Length))
                            where !ignoreFiles.Contains(fi.Name)
                            where !ignoreExtensions.Contains(fi.Extension)
                            select fi
                        );

                        // Now reorder the entire files list:
                        files = (
                            from fi in files
                            orderby fi.FullName
                            select fi
                        ).ToList();
                        break;
                    case "-b":
                        if (argQueue.Count == 0)
                        {
                            Console.Error.WriteLine("-b expects a path argument");
                            return;
                        }
                        string tmpS = argQueue.Dequeue();
                        baseDir = new DirectoryInfo(tmpS);
                        if (!baseDir.Exists)
                        {
                            Console.Error.WriteLine("Directory '{0}' does not exist!", baseDir.FullName);
                            return;
                        }
                        basePath = baseDir.FullName.EndsWith(Path.DirectorySeparatorChar.ToString()) ? baseDir.FullName : baseDir.FullName + Path.DirectorySeparatorChar;
                        break;
                    case "-i":
                        if (argQueue.Count == 0)
                        {
                            Console.Error.WriteLine("-i expects a path argument");
                            return;
                        }

                        string ignorePath = argQueue.Dequeue();
                        if (!File.Exists(ignorePath))
                        {
                            Console.Error.WriteLine("Could not open ignore file '{0}'", ignorePath);
                            return;
                        }

                        // Set the ignoreFiles set to all the unique filenames found in the file:
                        string[] lines = File.ReadAllLines(ignorePath);
                        ignoreExtensions = new HashSet<string>(
                            (
                                from line in lines
                                where line.StartsWith("*.")
                                select line.Substring(1)
                            ).Distinct(StringComparer.OrdinalIgnoreCase),
                            StringComparer.OrdinalIgnoreCase
                        );

                        ignoreFiles = new HashSet<string>(
                            lines.Except(ignoreExtensions).Distinct(StringComparer.OrdinalIgnoreCase),
                            StringComparer.OrdinalIgnoreCase
                        );
                        break;
                    case "-r":
                        recurseMode = true;
                        break;
                    case "-R":
                        recurseMode = false;
                        break;
                    case "-s":
                        if (argQueue.Count == 0)
                        {
                            Console.Error.WriteLine("-s expects a subscription name argument");
                            return;
                        }
                        subscription = argQueue.Dequeue();
                        break;
                    case "-c":
                        if (argQueue.Count == 0)
                        {
                            Console.Error.WriteLine("-c expects a chunk size argument");
                            return;
                        }
                        // Override the config with the commandline arg if it can be parsed:
                        Int32.TryParse(argQueue.Dequeue(), out chunkSize);
                        break;
                    case "-q":
                        if (argQueue.Count == 0)
                        {
                            Console.Error.WriteLine("-q expects a queue length argument");
                            return;
                        }
                        // Override the config with the commandline arg if it can be parsed:
                        Int32.TryParse(argQueue.Dequeue(), out queueBacklog);
                        break;
                    case "-n":
                        if (argQueue.Count == 0)
                        {
                            Console.Error.WriteLine("-s expects a subscription name argument");
                            return;
                        }
                        Int32.TryParse(argQueue.Dequeue(), out ioThreads);
                        break;
                    case "-?":
                        DisplayUsage();
                        return;
                    case "-l":
                        listFiles = true;
                        break;
                    default:
                        break;
                }
            }

            if ((uploadDirs.Count == 0) || String.IsNullOrEmpty(subscription))
            {
                DisplayUsage();
                return;
            }

            foreach (var fi in files)
            {
                if (!fi.FullName.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("Directory '{0}' is not underneath the base directory '{1}'!", fi.Directory.FullName, baseDir.FullName);
                    return;
                }
            }

            Console.WriteLine("Serving {0} files from '{1}':", files.Count, basePath);
            Console.WriteLine("{0,15} {1}", "Size (bytes)", "Path");
            foreach (var fi in files)
            {
                string fiName = fi.FullName.Substring(basePath.Length);
                Console.WriteLine("{0,15} {1}", fi.Length.ToString("##,#"), fiName);
            }
            if (listFiles) return;

            using (var serverTarball = new TarballStreamWriter(files))
            {
                var server = new LanCaster.ServerHost(endpoint, subscription, serverTarball, basePath, chunkSize, queueBacklog);

                Console.WriteLine();
                Console.WriteLine("{0,15} chunks @ {1,13} bytes/chunk", server.NumChunks.ToString("##,#"), server.ChunkSize.ToString("##,#"));

                server.ChunkSent += new Action<ServerHost, int>(ChunkSent);
                server.ChunksACKed += new Action<ServerHost, int[]>(ChunksACKed);
                server.ClientJoined += new Action<ServerHost, Guid>(ClientJoined);
                server.ClientLeft += new Action<ServerHost, Guid, ServerHost.ClientLeaveReason>(ClientLeft);

                // Begin the server thread:
                var serverThread = new Thread(server.Run);
                using (Context ctx = new Context(ioThreads))
                {
                    serverThread.Start(ctx);
                    serverThread.Join();
                }
            }
        }

        void ChunksACKed(ServerHost host, int[] chunkIdx)
        {
            RenderProgress(host, false);
        }

        void ChunkSent(ServerHost host, int chunkIdx)
        {
            lastWrittenChunk = chunkIdx;
            RenderProgress(host, false);
        }

        void ClientLeft(ServerHost host, Guid id, ServerHost.ClientLeaveReason reason)
        {
            RenderProgress(host, true);
        }

        void ClientJoined(ServerHost host, Guid id)
        {
            RenderProgress(host, true);
        }

        private int lastChunkBlock = -1;
        private bool wroteLegend = false;
        private int lastWrittenChunk = -1;

        void RenderProgress(ServerHost host, bool display)
        {
            int numChunks = host.NumChunks;
            if (numChunks == 0) return;

            int usableWidth = Console.WindowWidth - 3;

            int blocks = numChunks / usableWidth;
            int blocksRem = numChunks % usableWidth;

            int subblocks = usableWidth / numChunks;
            int subblocksRem = usableWidth % numChunks;

            if (!display)
            {
                if (blocks > 0) display = (lastChunkBlock != (lastChunkBlock = lastWrittenChunk / blocks));
                else display = true;
            }

            if (display)
            {
                if (!wroteLegend)
                {
                    Console.WriteLine();
                    Console.WriteLine(" '-' no NAKs      '*' some NAKs      '#' all NAKs      'S' currently sending");
                    Console.WriteLine();
                    wroteLegend = true;
                }

                string backup = new string('\b', Console.WindowWidth);
                Console.Write(backup);
                Console.Write('[');

                BitArray naks = new BitArray(host.NumBitArrayBytes * 8, false);
                foreach (var cli in host.Clients)
                {
                    naks = naks.Or(cli.NAK);
                }

                IEnumerator boolACKs = naks.GetEnumerator();
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
                            bool curr = (bool)boolACKs.Current;
                            allOn = allOn & curr;
                            allOff = allOff | curr;
                        }

                        for (int x = lastc; (x < c) && (c < usableWidth); ++x)
                        {
                            if ((lastWrittenChunk >= c * blocks) && (lastWrittenChunk < (c + 1) * blocks)) Console.Write('S');
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
                        bool curr = (bool)boolACKs.Current;

                        for (int x = lastc; (x < c) && (c < usableWidth); ++x)
                        {
                            if (lastWrittenChunk == i) Console.Write('S');
                            else if (curr) Console.Write('#');
                            else Console.Write('-');
                        }
                    }
                }

                Console.Write(']');
            }
        }

        private static void DisplayHeader()
        {
            // Displays the error text wrapped to the console's width:
            Console.Error.WriteLine(
                String.Join(
                    Environment.NewLine,
                    (
                        from line in new string[] {
@"lcs.exe <options> ...",
@"",
@"LanCaster Server - A multicast file transfer server",
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
new[] { @"-s <identity>",       @"(REQUIRED) Set identity name; helps clients reconnect if the server dies or is aborted and restarted." },
new[] { @"-d <path>",           @"(REQUIRED) Add files from directory to upload. Can specify multiple -d options." },
new[] { @"-e <endpoint>",       @"Listen for clients on the given 0MQ endpoint. '*' is all network interfaces, or provide a specific network interface's primary IPv4 address. Add a ':' and port number to specify a custom port number, default port is 12198. Default value is '*'." },
new[] { @"-b <path>",           @"Set base path of upload (all directories must be beneath this folder)" },
new[] { @"-i <path>",           @"Read the file at <path> for a listing of filenames, paths, and extensions (e.g. '*.txt') to ignore (applies to next -d options)" },
new[] { @"-r",                  @"Set recursive mode (applies to following -d options). Default mode." },
new[] { @"-R",                  @"Set nonrecursive mode (applies to following -d options)." },
new[] { @"-c <chunk size>",     @"Set the chunk size in bytes to use for dividing up the files into chunks. Larger values are better for faster networks. Recommend keeping it under 8388608 (8 MB). Default is 1048576 (1 MB)" },
new[] { @"-q <queue backlog>",  @"Set the server transmission queue backlog length (number of chunks). Default is 128 chunks." },
new[] { @"-n <io threads>",     @"Set this value to the number of threads you wish 0MQ to dedicate to network I/O. Default is 1." },
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
