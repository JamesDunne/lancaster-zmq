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

        public int chunkSize;
        private ushort port;
        private string device;

        public ServerHost(string endpoint, string subscription, TarballStreamWriter tarball, string basePath, int chunkSize, int queueBacklog, int hwm)
        {
            if (String.IsNullOrEmpty(endpoint)) throw new ArgumentNullException("endpoint");
            if (String.IsNullOrEmpty(subscription)) throw new ArgumentNullException("subscription");
            if (tarball == null) throw new ArgumentNullException("tarball");
            if (String.IsNullOrEmpty(basePath)) throw new ArgumentNullException("basePath");
            if (queueBacklog < 1) throw new ArgumentOutOfRangeException("queueBacklog", "queueBacklog must be 1 or greater");

            this.endpoint = endpoint;
            this.subscription = subscription;
            this.tarball = tarball;
            this.basePath = basePath;
            this.chunkSize = chunkSize;
            this.queueBacklog = queueBacklog;
            this.numChunks = (int)(tarball.Length / chunkSize) + ((tarball.Length % chunkSize > 0) ? 1 : 0);
            this.numBitArrayBytes = ((numChunks + 7) & ~7) >> 3;
            //this.currentNAKs = new BitArray(numBitArrayBytes * 8, false);

            if (this.numChunks == 0) throw new System.Exception();

            this.port = 12198;
            this.device = endpoint;
            int idx = endpoint.LastIndexOf(':');
            if (idx >= 0)
            {
                device = endpoint.Substring(0, idx);
                UInt16.TryParse(endpoint.Substring(idx + 1), out port);
            }

            this.hwm = hwm;
        }

        public int NumChunks { get { return this.numChunks; } }
        public int NumBitArrayBytes { get { return this.numBitArrayBytes; } }
        public int ChunkSize { get { return this.chunkSize; } }

        public ReadOnlyCollection<ClientState> Clients { get { return new ReadOnlyCollection<ClientState>(this.clients.Values.ToList()); } }

        public enum ClientLeaveReason
        {
            Left,
            Error,
            TimedOut
        }

        public event Action<ServerHost, int> ChunkSent;
        public event Action<ServerHost, int[]> ChunksACKed;
        public event Action<ServerHost, Guid> ClientJoined;
        public event Action<ServerHost, Guid, ClientLeaveReason> ClientLeft;

        public sealed class ClientState
        {
            public Guid Identity { get; private set; }

            private BitArray _nak;
            public BitArray NAK { get { return _nak; } set { _nak = value; /* _dirty = true; */ } }

            internal int RunningACKCount { get; set; }
            internal long LastElapsedMilliseconds { get; set; }
            public int ACKsPerMinute { get; internal set; }

            public ClientState(Guid identity, BitArray nak)
            {
                this.Identity = identity;
                this._nak = nak;
                this.ACKsPerMinute = 0;
                this.LastElapsedMilliseconds = 0L;
                this.RunningACKCount = 0;
            }
        }

        //private BitArray currentNAKs;
        private int numChunks;
        private int numBitArrayBytes;
        private Dictionary<Guid, ClientState> clients = new Dictionary<Guid, ClientState>();
        private Dictionary<Guid, DateTimeOffset> clientTimeout = new Dictionary<Guid, DateTimeOffset>();
        private Dictionary<int, HashSet<Guid>> awaitingClientACKs = new Dictionary<int, HashSet<Guid>>();
        private int queueBacklog;
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
                    ctl.Bind("tcp://" + host.device + ":" + (host.port + 1).ToString());

                    // Wait for the sockets to bind:
                    Thread.Sleep(500);

                    // Create a poller on the control socket to handle client requests:
                    PollItem[] pollItems = new PollItem[1];
                    pollItems[0] = ctl.CreatePollItem(IOMultiPlex.POLLIN);
                    pollItems[0].PollInHandler += new PollHandler((Socket sock, IOMultiPlex mp) =>
                    {
                        Queue<byte[]> request = sock.RecvAll();

                        Guid clientIdentity = new Guid(request.Dequeue().Skip(1).ToArray());
                        bool clientExists;
                        lock (host.clientLock)
                        {
                            host.clientTimeout[clientIdentity] = DateTimeOffset.UtcNow.AddSeconds(2);
                            clientExists = host.clients.ContainsKey(clientIdentity);
                        }

                        string cmd = Encoding.Unicode.GetString(request.Dequeue());

                        // Process the client command:
                        trace("{0}: Client sent '{1}'", clientIdentity.ToString(), cmd);
                        switch (cmd)
                        {
                            case "JOIN":
                                if (clientExists)
                                {
                                    trace("{0}: Client already joined", clientIdentity.ToString());
                                    sock.Send("ALREADY_JOINED", Encoding.Unicode);
                                    break;
                                }

                                lock (host.clientLock)
                                {
                                    host.clients.Add(clientIdentity, new ClientState(clientIdentity, new BitArray(host.numBitArrayBytes << 3)));
                                }

                                // TODO: send out the tarball descriptor:
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

                            case "ACK":
                                if (!clientExists)
                                {
                                    lock (host.clientLock)
                                    {
                                        host.clientTimeout.Remove(clientIdentity);
                                    }
                                    trace("{0}: Client not joined", clientIdentity.ToString());
                                    sock.Send("NOTJOINED", Encoding.Unicode);
                                    break;
                                }

                                trace("{0}: ACK received", clientIdentity.ToString());

                                int numIdxs = BitConverter.ToInt32(request.Dequeue(), 0);

                                int[] nakChunkIdxs = new int[numIdxs];

                                byte[] idxBytes = request.Dequeue();
                                if (idxBytes.Length != (numIdxs * 4))
                                {
                                    sock.Send("BADACK", Encoding.Unicode);
                                    break;
                                }

                                sock.Send("", Encoding.Unicode);

                                lock (host.clientLock)
                                {
                                    ClientState cli = host.clients[clientIdentity];
                                    cli.RunningACKCount += numIdxs;
                                    
                                    // ACK the packets:
                                    for (int i = 0; i < numIdxs; ++i)
                                    {
                                        int idx = BitConverter.ToInt32(idxBytes, i * 4);
                                        nakChunkIdxs[i] = idx;

                                        cli.NAK[idx] = false;
                                        if (host.awaitingClientACKs.ContainsKey(idx))
                                        {
                                            host.awaitingClientACKs[idx].Remove(clientIdentity);
                                        }
                                    }
                                }
                                if (host.ChunksACKed != null) host.ChunksACKed(host, nakChunkIdxs);
                                break;

                            case "NAKS":
                                // Receive the client's current NAK:
                                if (!clientExists)
                                {
                                    lock (host.clientLock)
                                    {
                                        host.clientTimeout.Remove(clientIdentity);
                                    }

                                    trace("{0}: Client not joined", clientIdentity.ToString());
                                    sock.Send("NOTJOINED", Encoding.Unicode);
                                    break;
                                }

                                byte[] tmp = request.Dequeue();
                                if (tmp.Length != host.numBitArrayBytes)
                                {
                                    lock (host.clientLock)
                                    {
                                        host.clientTimeout.Remove(clientIdentity);
                                    }

                                    trace("{0}: Bad NAKs", clientIdentity.ToString());
                                    sock.Send("BADNAKS", Encoding.Unicode);
                                    break;
                                }
                                sock.Send("", Encoding.Unicode);

                                lock (host.clientLock)
                                {
                                    host.clients[clientIdentity].NAK = new BitArray(tmp);
                                }

                                trace("{0}: NAKs received", clientIdentity.ToString());
                                break;

                            case "LEAVE":
                                if (!clientExists)
                                {
                                    lock (host.clientLock)
                                    {
                                        host.clientTimeout.Remove(clientIdentity);
                                    }

                                    trace("{0}: Client already left!", clientIdentity.ToString());
                                    sock.Send("ALREADY_LEFT", Encoding.Unicode);
                                    break;
                                }
                                sock.Send("LEFT", Encoding.Unicode);

                                lock (host.clientLock)
                                {
                                    // Remove the client's state record:
                                    host.clients.Remove(clientIdentity);

                                    foreach (HashSet<Guid> awaiter in host.awaitingClientACKs.Values)
                                        awaiter.Remove(clientIdentity);
                                    host.clientTimeout.Remove(clientIdentity);
                                }

                                if (host.ClientLeft != null) host.ClientLeft(host, clientIdentity, ClientLeaveReason.Left);

                                trace("{0}: Client left", clientIdentity.ToString());
                                break;

                            case "ALIVE":
                                if (!clientExists)
                                {
                                    trace("{0}: WHOAREYOU", clientIdentity.ToString());
                                    byte[] sendIdentity = new byte[1] { (byte)'@' }.Concat(clientIdentity.ToByteArray()).ToArray();
                                    sock.Send("WHOAREYOU", Encoding.Unicode);
                                    break;
                                }

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
                    data.SndBuf = chunkSize * queueBacklog * 8;
                    data.StringToIdentity(subscription, Encoding.Unicode);
                    data.Bind("tcp://" + device + ":" + port.ToString());

                    isRunning = true;

                    var controlHandler = new ControlHandler(this);
                    var controlHandlerThread = new Thread(new ParameterizedThreadStart(controlHandler.Run));
                    controlHandlerThread.Start(new object[] { ctx, this });

                    // Wait for the sockets to bind:
                    Thread.Sleep(1000);

                    //PollItem[] pollItems = new PollItem[1];
                    //pollItems[0] = data.CreatePollItem(IOMultiPlex.POLLOUT);

                    int? chunkIdx = null;
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
                            // Anyone timed out yet?
                            DateTimeOffset rightMeow = DateTimeOffset.UtcNow;
                            KeyValuePair<Guid, DateTimeOffset> timedOutClient = clientTimeout.FirstOrDefault(dt => dt.Value < rightMeow);
                            if (timedOutClient.Key != Guid.Empty)
                            {
                                // Yes, remove that client:
                                clientTimeout.Remove(timedOutClient.Key);
                                clients.Remove(timedOutClient.Key);

                                foreach (HashSet<Guid> awaiter in awaitingClientACKs.Values)
                                    awaiter.Remove(timedOutClient.Key);

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

                        // Find the ACKs/min rate of our fastest receiver:
                        int maxACKsPerMinute;
                        lock (clientLock)
                        {
                            foreach (var cli in clients.Values)
                            {
                                // Measure the ACK rate in ACKs per minute:
                                elapsed = sendTimer.ElapsedMilliseconds - cli.LastElapsedMilliseconds;
                                if (elapsed >= 500L)
                                {
                                    cli.ACKsPerMinute = (int)((cli.RunningACKCount * 60000L) / elapsed);
                                    cli.LastElapsedMilliseconds = sendTimer.ElapsedMilliseconds;
                                    cli.RunningACKCount = 0;
                                }
                            }

                            if (clients.Count > 0)
                                maxACKsPerMinute = clients.Values.Max(cli => cli.ACKsPerMinute);
                            else
                                maxACKsPerMinute = 0;
                        }

#if true
                        // Don't send faster than our fastest receiver can receive:
                        if ((msgsPerMinute > 0) && (maxACKsPerMinute > 0) && (msgsPerMinute >= (maxACKsPerMinute * 2)))
                        {
                            // Hold off on queueing up more chunks to deliver if we're still awaiting ACKs for at least `queueBacklog` chunks:
                            if (awaitingClientACKs.Values.Count(e => e.Count > 0) >= queueBacklog)
                            {
                                //trace("Still awaiting ACKs on {0} packets", awaitingClientACKs.Count);
                                Thread.Sleep(1);
                                continue;
                            }
                        }
#endif

                        // Find the next best chunk to send:
                        lock (clientLock)
                        {
                            // Order the clients by number of chunks left to receive:
                            var clientOrder = (
                                from cli in clients.Values
                                let naks = cli.NAK.Cast<bool>().Count(b => b)
                                orderby naks ascending
                                select new { client = cli, naks }
                            );

                            chunkIdx = (
                                from cli in clientOrder
                                // Find the first NAK for the client with the least amount of NAKs to receive:
                                let ch = cli.client.NAK
                                    .Cast<bool>()
                                    .Select((b, i) => new { b, i })
                                    .FirstOrDefault(x => 
                                        ((!awaitingClientACKs.ContainsKey(x.i)) || (awaitingClientACKs[x.i].Count == 0)) &&
                                        x.b)
                                where ch != null
                                select (int?)ch.i
                            ).FirstOrDefault();
                        }

                        if (!chunkIdx.HasValue)
                        {
                            // Sleep until we're ready to send more data:
                            Thread.Sleep(1);
                            continue;
                        }

                        // Send the current chunk:

                        // Chunk index first:
                        trace("SEND chunk: {0}", chunkIdx.Value);
                        data.SendMore(this.subscription, Encoding.Unicode);
                        data.SendMore("DATA", Encoding.Unicode);
                        data.SendMore(BitConverter.GetBytes(chunkIdx.Value));

                        // Chunk size:
                        tarball.Seek((long)chunkIdx.Value * chunkSize, SeekOrigin.Begin);
                        int currChunkSize = tarball.Read(buf, 0, chunkSize);
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

                        if (ChunkSent != null) ChunkSent(this, chunkIdx.Value);
                        trace("COMPLETE chunk: {0}", chunkIdx.Value);

                        lock (clientLock)
                        {
                            // Wait for an ACK from all the clients who have this chunk currently NAKed:
                            HashSet<Guid> awaiter;
                            if (!awaitingClientACKs.ContainsKey(chunkIdx.Value))
                            {
                                awaiter = awaitingClientACKs[chunkIdx.Value] = new HashSet<Guid>();
                            }
                            else
                            {
                                (awaiter = awaitingClientACKs[chunkIdx.Value]).Clear();
                            }

                            foreach (var cli in clients.Values.Where(x => x.NAK[chunkIdx.Value]))
                            {
                                //trace("Awaiting ACK from {0} for chunk {1}", cli.Identity.ToString(), chunkIdx.Value);
                                awaiter.Add(cli.Identity);
                            }
                        }

                        ++msgsSent;
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
