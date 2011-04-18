using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ZMQ;

namespace WellDunne.LanCaster
{
    public sealed class ServerHost
    {
        TarballStreamWriter tarball;
        private string basePath;
        private string subscription;
        private string endpoint;
        private readonly object clientLock = new object();
        private static readonly BooleanSwitch doLogging = new BooleanSwitch("doLogging", "Log server events", "0");

        private bool testMode;
        private int chunkSize, lastChunkSize;
        private Transport tsp;
        private uint port;
        private string device;

        public ServerHost(Transport tsp, string endpoint, string subscription, TarballStreamWriter tarball, string basePath, int chunkSize, int hwm, bool testMode)
        {
            if (String.IsNullOrEmpty(endpoint)) throw new ArgumentNullException("endpoint");
            if (String.IsNullOrEmpty(subscription)) throw new ArgumentNullException("subscription");
            if (tarball == null) throw new ArgumentNullException("tarball");
            if (String.IsNullOrEmpty(basePath)) throw new ArgumentNullException("basePath");

            this.tsp = tsp;
            this.endpoint = endpoint;
            this.subscription = subscription;
            this.tarball = tarball;
            this.basePath = basePath;
            this.chunkSize = chunkSize;
            this.testMode = testMode;

            this.lastChunkSize = (int)(tarball.Length % chunkSize);
            this.numChunks = (int)(tarball.Length / chunkSize) + ((lastChunkSize > 0) ? 1 : 0);
            this.numBitArrayBytes = ((numChunks + 7) & ~7) >> 3;
            //this.currentNAKs = new BitArray(numBitArrayBytes * 8, false);

            if (this.numChunks == 0) throw new System.Exception();

            this.port = 12198;
            this.device = endpoint;
            int idx = endpoint.LastIndexOf(':');
            if (idx >= 0)
            {
                device = endpoint.Substring(0, idx);
                UInt32.TryParse(endpoint.Substring(idx + 1), out port);
            }

            this.hwm = hwm;
        }

        public int NumChunks { get { return this.numChunks; } }
        public int NumBitArrayBytes { get { return this.numBitArrayBytes; } }
        public int ChunkSize { get { return this.chunkSize; } }

        public ReadOnlyCollection<ClientState> Clients { get { return new ReadOnlyCollection<ClientState>(this.clients.Values.Where(cli => cli.HasNAKs && !cli.IsTimedOut).ToList()); } }

        public enum ClientLeaveReason
        {
            Left,
            Error,
            TimedOut
        }

        public event Action<ServerHost, int> ChunkSent;
        public event Action<ServerHost> ChunksACKed;
        public event Action<ServerHost, Guid> ClientJoined;
        public event Action<ServerHost, Guid, ClientLeaveReason> ClientLeft;

        public sealed class ClientState
        {
            public Guid Identity { get; private set; }
            public BitArray NAK { get; internal set; }
            public int ACKsPerMinute { get; internal set; }
            public DateTimeOffset LastMessageTime { get; internal set; }

            public bool HasNAKs { get { return NAK != null; } }
            public bool IsTimedOut { get { return (DateTimeOffset.UtcNow.Subtract(LastMessageTime).TotalSeconds >= 2); } }
            public bool IsRemovable { get { return (DateTimeOffset.UtcNow.Subtract(LastMessageTime).TotalSeconds >= 60); } }

            internal int RunningACKCount { get; set; }
            internal long LastElapsedMilliseconds { get; set; }

            public ClientState(Guid identity)
            {
                this.Identity = identity;
                this.NAK = null;
                this.ACKsPerMinute = 0;
                this.LastElapsedMilliseconds = 0L;
                this.RunningACKCount = 0;
                this.LastMessageTime = DateTimeOffset.UtcNow;
            }
        }

        private int numChunks;
        private int numBitArrayBytes;
        private Dictionary<Guid, ClientState> clients = new Dictionary<Guid, ClientState>();
        private int hwm;
        private bool isRunning;

        private static void WriteBuffer(Stream st, byte[] buf)
        {
            st.Write(buf, 0, buf.Length);
        }

        private static void trace(string format, params object[] args)
        {
            Trace.WriteLineIf(doLogging.Enabled, String.Format(format, args), "server");
        }

        private class ControlHandler
        {
            private ServerHost host;

            public ControlHandler(ServerHost host)
            {
                this.host = host;
            }

            public void Run(object threadContext)
            {
                object[] thrContextObjects = (object[])threadContext;
                Context ctx = (Context)thrContextObjects[0];

                using (Socket ctl = ctx.Socket(SocketType.REP))
                {
                    // Bind the control reply socket:
                    ctl.Bind(Transport.TCP, host.device, (host.port + 1));

                    // Wait for the sockets to bind:
                    Thread.Sleep(500);

                    // Create a poller on the control socket to handle client requests:
                    PollItem[] pollItems = new PollItem[1];
                    pollItems[0] = ctl.CreatePollItem(IOMultiPlex.POLLIN);
                    pollItems[0].PollInHandler += new PollHandler((Socket sock, IOMultiPlex mp) =>
                    {
                        Queue<byte[]> request = sock.RecvAll();

                        // Bad message?
                        if (request.Count == 0) return;

                        Guid clientIdentity = new Guid(request.Dequeue().Skip(1).ToArray());
                        ClientState client;
                        bool newClient = false;

                        lock (host.clientLock)
                        {
                            if (!host.clients.ContainsKey(clientIdentity))
                            {
                                client = new ClientState(clientIdentity);
                                host.clients.Add(clientIdentity, client);
                                newClient = true;
                            }
                            else
                            {
                                client = host.clients[clientIdentity];
                            }

                            // Update last message received time to now:
                            client.LastMessageTime = DateTimeOffset.UtcNow;
                        }

                        string cmd = Encoding.Unicode.GetString(request.Dequeue());

                        // Process the client command:
                        trace("{0}: Client sent '{1}'", clientIdentity.ToString(), cmd);
                        switch (cmd)
                        {
                            case "JOIN":
                                sock.SendMore("JOINED", Encoding.Unicode);

                                ReadOnlyCollection<FileInfo> files = host.tarball.Files;

                                sock.SendMore(BitConverter.GetBytes(host.numChunks));
                                sock.SendMore(BitConverter.GetBytes(host.chunkSize));
                                sock.SendMore(BitConverter.GetBytes(host.tarball.Files.Count));

                                foreach (var fi in files)
                                {
                                    string fiName = fi.FullName.Substring(host.basePath.Length);
                                    sock.SendMore(fiName, Encoding.Unicode);
                                    sock.SendMore(BitConverter.GetBytes(fi.Length));
                                    sock.SendMore(new byte[16]);
                                }

                                sock.Send("", Encoding.Unicode);
                                trace("{0}: Sent JOINED response", clientIdentity.ToString());
                                if (host.ClientJoined != null) host.ClientJoined(host, clientIdentity);
                                break;

                            case "NAKS":
                                // Receive the client's current NAK:
                                byte[] tmp = request.Dequeue();
                                if (tmp.Length != host.numBitArrayBytes)
                                {
                                    trace("{0}: Bad NAKs", clientIdentity.ToString());
                                    sock.Send("BADNAKS", Encoding.Unicode);
                                    break;
                                }
                                sock.Send("", Encoding.Unicode);

                                lock (host.clientLock)
                                {
                                    BitArray newNAKs = new BitArray(tmp);
                                    // Add to the running ACK count the number of chunks turned from NAK to ACK in this update:
                                    if (client.HasNAKs)
                                    {
                                        client.RunningACKCount += (
                                            from i in Enumerable.Range(0, host.numChunks)
                                            where (client.NAK[i] == true) && (newNAKs[i] == false)
                                            select i
                                        ).Count();
                                    }
                                    else
                                    {
                                        client.RunningACKCount += newNAKs.Cast<bool>().Take(host.numChunks).Count(b => !b);
                                    }
                                    // Update to the new NAK state:
                                    client.NAK = newNAKs;
                                }

                                trace("{0}: NAKs received", clientIdentity.ToString());
                                if (host.ChunksACKed != null) host.ChunksACKed(host);
                                break;

                            case "LEAVE":
                                sock.Send("LEFT", Encoding.Unicode);

                                lock (host.clientLock)
                                {
                                    // Remove the client's state record:
                                    host.clients.Remove(clientIdentity);
                                }

                                if (host.ClientLeft != null) host.ClientLeft(host, clientIdentity, ClientLeaveReason.Left);

                                trace("{0}: Client left", clientIdentity.ToString());
                                break;

                            case "ALIVE":
                                if (newClient)
                                {
                                    trace("{0}: WHOAREYOU", clientIdentity.ToString());
                                    byte[] sendIdentity = new byte[1] { (byte)'@' }.Concat(clientIdentity.ToByteArray()).ToArray();
                                    sock.Send("WHOAREYOU", Encoding.Unicode);
                                    break;
                                }

                                // Fantastic.
                                sock.Send("", Encoding.Unicode);
                                break;

                            default:
                                // Unknown command.
                                sock.Send("UNKNOWN", Encoding.Unicode);
                                trace("{0}: Unknown request", clientIdentity.ToString());
                                break;
                        }

                        request = null;
                    });

                    while (host.isRunning)
                    {
                        while (ctx.Poll(pollItems, 100000L) == 1)
                        {
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Main thread to host the server process.
        /// </summary>
        /// <param name="threadContext">A ZMQ.Context instance.</param>
        public void Run(object threadContext)
        {
            try
            {
                Context ctx = (Context)threadContext;
                using (Socket data = ctx.Socket(SocketType.PUB))
                {
                    data.SNDHWM = hwm;

                    // Bind the data publisher socket:

                    // TODO: use epgm:// protocol for multicast efficiency when we build that support into libzmq.dll for Windows.
                    //data.Rate = 20000L;
                    data.SndBuf = chunkSize * hwm * 8;
                    data.StringToIdentity(subscription, Encoding.Unicode);
                    data.Bind(tsp.ToString().ToLower() + "://" + device + ":" + port);

                    isRunning = true;

                    var controlHandler = new ControlHandler(this);
                    var controlHandlerThread = new Thread(new ParameterizedThreadStart(controlHandler.Run));
                    controlHandlerThread.Start(new object[] { ctx, this });

                    // Wait for the sockets to bind:
                    Thread.Sleep(1000);

                    //PollItem[] pollItems = new PollItem[1];
                    //pollItems[0] = data.CreatePollItem(IOMultiPlex.POLLOUT);

                    byte[] buf = new byte[chunkSize];

                    DateTimeOffset lastPing = DateTimeOffset.UtcNow;

                    Stopwatch sendTimer = Stopwatch.StartNew();
                    long lastElapsedMilliseconds = sendTimer.ElapsedMilliseconds;
                    int msgsSent = 0;
                    int msgsPerMinute = 0;

                    // Begin the data delivery loop:
                    while (true)
                    {
                        // PING everyone:
                        if (DateTimeOffset.UtcNow.Subtract(lastPing).TotalMilliseconds >= 500d)
                        {
                            lastPing = DateTimeOffset.UtcNow;

                            // Send a PING to all subscribers:
                            trace("PING");
                            //if (ctx.Poll(pollItems) == 1)
                            //{
                            data.SendMore(this.subscription, Encoding.Unicode);
                            data.Send("PING", Encoding.Unicode);
                            //}
                        }

                        lock (clientLock)
                        {
                            // Anyone fully timed out yet?
                            DateTimeOffset rightMeow = DateTimeOffset.UtcNow;

                            foreach (KeyValuePair<Guid, ClientState> timedOutClient in clients.Where(cli => cli.Value.IsRemovable))
                            {
                                // Yes, remove that client:
                                clients.Remove(timedOutClient.Key);

                                if (ClientLeft != null) ClientLeft(this, timedOutClient.Key, ClientLeaveReason.TimedOut);
                            }
                        }

                        // Measure our message send rate per minute:
                        long elapsed = sendTimer.ElapsedMilliseconds - lastElapsedMilliseconds;
                        if (elapsed >= 500L)
                        {
                            msgsPerMinute = (int)((msgsSent * 60000L) / elapsed);
                            lastElapsedMilliseconds = sendTimer.ElapsedMilliseconds;
                            msgsSent = 0;
                        }

                        if (!sendTimer.IsRunning)
                        {
                            sendTimer.Reset();
                            sendTimer.Start();
                            lastElapsedMilliseconds = 0L;
                        }

                        // Find the ACKs/min rates per client:
                        lock (clientLock)
                        {
                            foreach (var cli in clients.Values.Where(cl => cl.HasNAKs && !cl.IsTimedOut))
                            {
                                // Measure the ACK rate in ACKs per minute:
                                elapsed = sendTimer.ElapsedMilliseconds - cli.LastElapsedMilliseconds;
                                if (elapsed >= 500L)
                                {
                                    cli.ACKsPerMinute = (int)((cli.RunningACKCount * 60000L) / elapsed);
                                    cli.LastElapsedMilliseconds = sendTimer.ElapsedMilliseconds;
                                    Console.WriteLine(cli.RunningACKCount);
                                    cli.RunningACKCount = 0;
                                }
                            }
                        }

                        // Find the next set of chunks to send:
                        List<int> chunkIdxs;
                        lock (clientLock)
                        {
                            chunkIdxs = (
                                from cli in
                                    (
                                        // Order the clients by number of chunks left to receive:
                                        from cli in clients.Values
                                        where cli.HasNAKs && !cli.IsTimedOut
                                        let naks = cli.NAK.Cast<bool>().Take(numChunks).Count(b => b)
                                        orderby naks ascending
                                        select cli
                                        )
                                // Find the NAKs for the client with the least amount of NAKs to receive:
                                from x in cli.NAK.Cast<bool>().Take(numChunks).Select((b, i) => new { b, i })
                                where x.b
                                select x.i
                            ).ToList();
                        }

                        // No chunks to send? Sleep.
                        if (chunkIdxs.Count == 0)
                        {
                            Thread.Sleep(10);
                            continue;
                        }

                        // Send the chunks:
                        foreach (int chunkIdx in chunkIdxs)
                        {
                            // Chunk index first:
                            trace("SEND chunk: {0}", chunkIdx);
                            data.SendMore(this.subscription, Encoding.Unicode);
                            data.SendMore("DATA", Encoding.Unicode);
                            data.SendMore(BitConverter.GetBytes(chunkIdx));

                            // Chunk size:
                            int currChunkSize;
                            if (!testMode)
                            {
                                tarball.Seek((long)chunkIdx * chunkSize, SeekOrigin.Begin);
                                currChunkSize = tarball.Read(buf, 0, chunkSize);
                            }
                            else
                            {
                                if (lastChunkSize > 0)
                                    currChunkSize = (chunkIdx < (numChunks - 1)) ? chunkSize : lastChunkSize;
                                else
                                    currChunkSize = chunkSize;
                            }

                            if (currChunkSize < chunkSize)
                            {
                                // Copy to an exact-sized temporary buffer for the last uneven sized chunk:
                                byte[] tmpBuf = new byte[currChunkSize];
                                Array.Copy(buf, tmpBuf, currChunkSize);
                                data.Send(tmpBuf);
                                tmpBuf = null;
                            }
                            else
                            {
                                // Send the exact-sized chunk:
                                data.Send(buf);
                            }

                            if (ChunkSent != null) ChunkSent(this, chunkIdx);
                            trace("COMPLETE chunk: {0}", chunkIdx);

                            ++msgsSent;
                        }
                    }

                    // TODO: break out of the while loop somehow
                    //isRunning = false;
                    //controlHandlerThread.Join();
                }
            }
            catch (System.Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
            }
        }
    }
}
