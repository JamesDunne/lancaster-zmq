﻿using System;
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

        public ServerHost(string endpoint, string subscription, TarballStreamWriter tarball, string basePath, int chunkSize, int queueBacklog)
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

            string hwmValue = ConfigurationManager.AppSettings["HWM"];
            ulong tmphwm = 128;
            if (hwmValue != null)
            {
                UInt64.TryParse(hwmValue, out tmphwm);
            }
            this.hwm = tmphwm;
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

            //private bool _dirty;
            //public bool IsDirty { get { return _dirty; } }
            //public ClientState Clean() { _dirty = false; return this; }

            public ClientState(Guid identity, BitArray nak)
            {
                this.Identity = identity;

                //this._dirty = false;
                this._nak = nak;
            }
        }

        //private BitArray currentNAKs;
        private int numChunks;
        private int numBitArrayBytes;
        private Dictionary<Guid, ClientState> clients = new Dictionary<Guid, ClientState>();
        private Dictionary<Guid, DateTimeOffset> clientTimeout = new Dictionary<Guid, DateTimeOffset>();
        private Dictionary<int, HashSet<Guid>> awaitingClientACKs = new Dictionary<int, HashSet<Guid>>();
        private int queueBacklog;
        private ulong hwm;
        private bool isRunning;
        private int lastAcks, currentAcks;

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
                    ctl.HWM = host.hwm;

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
                            host.clientTimeout[clientIdentity] = DateTimeOffset.UtcNow.AddSeconds(10);
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

                                lock (host.clientLock)
                                {
                                    for (int i = 0; i < numIdxs; ++i)
                                    {
                                        int idx = BitConverter.ToInt32(idxBytes, i * 4);
                                        nakChunkIdxs[i] = idx;

                                        host.clients[clientIdentity].NAK[idx] = false;
                                        if (host.awaitingClientACKs.ContainsKey(idx))
                                        {
                                            host.awaitingClientACKs[idx].Remove(clientIdentity);
                                        }
                                    }

                                    // Keep a running count of ACKs received so we know when to keep
                                    // the server sending data. If no one is listening or is too slow,
                                    // don't bother pegging the CPU in the while loop.
                                    Interlocked.Increment(ref host.currentAcks);
                                }
                                if (host.ChunksACKed != null) host.ChunksACKed(host, nakChunkIdxs);

                                sock.Send("", Encoding.Unicode);
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

                                lock (host.clientLock)
                                {
                                    host.clients[clientIdentity].NAK = new BitArray(tmp);
                                }

                                trace("{0}: NAKs received", clientIdentity.ToString());
                                sock.Send("", Encoding.Unicode);
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

                                lock (host.clientLock)
                                {
                                    // Remove the client's state record:
                                    host.clients.Remove(clientIdentity);

                                    foreach (HashSet<Guid> awaiter in host.awaitingClientACKs.Values)
                                        awaiter.Remove(clientIdentity);
                                    host.clientTimeout.Remove(clientIdentity);
                                }

                                if (host.ClientLeft != null) host.ClientLeft(host, clientIdentity, ClientLeaveReason.Left);

                                sock.Send("LEFT", Encoding.Unicode);
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
                    data.HWM = hwm;

                    // Bind the data publisher socket:

                    // TODO: use epgm:// protocol for multicast efficiency when we build that support into libzmq.dll for Windows.
                    //data.Rate = 20000L;
                    data.SndBuf = (ulong)chunkSize * (ulong)queueBacklog * 2UL;
                    data.StringToIdentity(subscription, Encoding.Unicode);
                    data.Bind("tcp://" + device + ":" + port.ToString());

                    isRunning = true;

                    var controlHandler = new ControlHandler(this);
                    var controlHandlerThread = new Thread(new ParameterizedThreadStart(controlHandler.Run));
                    controlHandlerThread.Start(new object[] { ctx, this });

                    // Wait for the sockets to bind:
                    Thread.Sleep(500);

                    PollItem[] pollItems = new PollItem[1];
                    pollItems[0] = data.CreatePollItem(IOMultiPlex.POLLOUT);

                    int? chunkIdx = null;
                    byte[] buf = new byte[chunkSize];

                    DateTimeOffset lastPing = DateTimeOffset.UtcNow;

                    // Begin the data delivery loop:
                    while (true)
                    {
#if false
                        // Wait until the data socket is ready to send on:
                        while (ctx.Poll(pollItems) == 0)
                        {
                        }
#endif

                        // PING everyone:
                        if (DateTimeOffset.UtcNow.Subtract(lastPing).TotalMilliseconds >= 500d)
                        {
                            lastPing = DateTimeOffset.UtcNow;

                            // Send a PING to all subscribers:
                            trace("PING");
                            data.SendMore(this.subscription, Encoding.Unicode);
                            data.Send("PING", Encoding.Unicode);
                        }

                        int currAcksValue;
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

                            currAcksValue = Thread.VolatileRead(ref currentAcks);
                        }

#if false
                        // If we didn't ACK anything at all this time around, wait until we do before we continue sending:
                        if (lastAcks == currAcksValue)
                        {
                            Thread.Sleep(20);
                            continue;
                        }
                        lastAcks = currAcksValue;
#endif

                        // Hold off on queueing up more chunks to deliver if we're still awaiting ACKs for at least `queueBacklog` chunks:
                        if (awaitingClientACKs.Values.Count(e => e.Count > 0) >= queueBacklog)
                        {
                            //trace("Still awaiting ACKs on {0} packets", awaitingClientACKs.Count);
                            Thread.Sleep(20);
                            continue;
                        }

                        // Find the next best chunk to send:
#if true
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
                                    .FirstOrDefault(x => x.b && ((!awaitingClientACKs.ContainsKey(x.i)) || (awaitingClientACKs[x.i].Count == 0)))
                                where ch != null
                                select (int?)ch.i
                            ).FirstOrDefault();
                        }
#else
                        var chunkQuery = (
                            from index in Enumerable.Range(0, numChunks)
                            // Count up how many clients have NAKs for this chunk:
                            let count = clients.Values.Count(cli => cli.NAK[index])
                            // We don't want any fully ACKed chunks:
                            where count > 0
                            select new { index, count }
                        );

                        // Find the first chunk with the least number of clients waiting for it:
                        chunkIdx = null;
                        for (int i = 1; (i <= clients.Count) && !chunkIdx.HasValue; ++i)
                        {
                            var tmp = chunkQuery.FirstOrDefault((ch) => (ch.count == i));
                            if (tmp != null)
                            {
                                chunkIdx = tmp.index;
                                break;
                            }
                        }
#endif

                        if (!chunkIdx.HasValue)
                        {
                            // Sleep until we're ready to send more data:
                            Thread.Sleep(20);
                            continue;
                        }

                        // Send the current chunk:
                        // FIXME: timeouts on await!
                        bool notAwaiting = true;
                        lock (clientLock)
                        {
                            notAwaiting = (!awaitingClientACKs.ContainsKey(chunkIdx.Value) || (awaitingClientACKs[chunkIdx.Value].Count == 0));
                        }

                        if (!notAwaiting)
                        {
                            Thread.Sleep(20);
                            continue;
                        }

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
                            if (!awaitingClientACKs.ContainsKey(chunkIdx.Value))
                            {
                                awaitingClientACKs[chunkIdx.Value] = new HashSet<Guid>();
                            }
                            else
                            {
                                awaitingClientACKs[chunkIdx.Value].Clear();
                            }

                            foreach (var cli in clients.Values.Where(x => x.NAK[chunkIdx.Value]))
                            {
                                //trace("Awaiting ACK from {0} for chunk {1}", cli.Identity.ToString(), chunkIdx.Value);
                                awaitingClientACKs[chunkIdx.Value].Add(cli.Identity);
                            }
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
