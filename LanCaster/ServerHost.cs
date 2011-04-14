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
        private BooleanSwitch doLogging;

        public int chunkSize;

        public ServerHost(string endpoint, string subscription, TarballStreamWriter tarball, string basePath, int chunkSize)
        {
            if (String.IsNullOrEmpty(endpoint)) throw new ArgumentNullException("endpoint");
            if (String.IsNullOrEmpty(subscription)) throw new ArgumentNullException("subscription");
            if (tarball == null) throw new ArgumentNullException("tarball");
            if (String.IsNullOrEmpty(basePath)) throw new ArgumentNullException("basePath");

            this.endpoint = endpoint;
            this.subscription = subscription;
            this.tarball = tarball;
            this.basePath = basePath;
            this.chunkSize = chunkSize;
            this.numChunks = (int)(tarball.Length / chunkSize) + ((tarball.Length % chunkSize > 0) ? 1 : 0);
            this.numBitArrayBytes = ((numChunks + 7) & ~7) >> 3;
            this.currentNAKs = new BitArray(numBitArrayBytes * 8, false);

            if (this.numChunks == 0) throw new System.Exception();

            this.doLogging = new BooleanSwitch("doLogging", "Log server events", "0");
        }

        public int NumChunks { get { return this.numChunks; } }
        public int ChunkSize { get { return this.chunkSize; } }

        public BitArray NAKs { get { return this.currentNAKs; } }

        public event Action<ServerHost, int> ChunkSent;

        private sealed class ClientState
        {
            private bool _dirty;

            public Guid Identity { get; private set; }

            private BitArray _nak;
            public BitArray NAK { get { return _nak; } set { _nak = value; _dirty = true; } }

            public bool IsDirty { get { return _dirty; } }
            public ClientState Clean() { _dirty = false; return this; }

            public ClientState(Guid identity, BitArray nak)
            {
                this.Identity = identity;
                this._dirty = false;
                this._nak = nak;
            }
        }

        private BitArray currentNAKs;
        private int numChunks;
        private int numBitArrayBytes;
        private Dictionary<Guid, ClientState> clients = new Dictionary<Guid, ClientState>();
        private Dictionary<Guid, DateTimeOffset> clientTimeout = new Dictionary<Guid, DateTimeOffset>();

        private static void WriteBuffer(Stream st, byte[] buf)
        {
            st.Write(buf, 0, buf.Length);
        }

        private void trace(string format, params object[] args)
        {
            Trace.WriteLineIf(doLogging.Enabled, String.Format(format, args), "server");
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
                using (Socket
                    data = ctx.Socket(SocketType.PUB),
                    ctl = ctx.Socket(SocketType.REP)
                )
                {
                    ushort port = 12198;
                    string device = endpoint;
                    int idx = endpoint.LastIndexOf(':');
                    if (idx >= 0)
                    {
                        device = endpoint.Substring(0, idx);
                        UInt16.TryParse(endpoint.Substring(idx + 1), out port);
                    }

                    string hwmValue = ConfigurationManager.AppSettings["HWM"];
                    if (hwmValue != null)
                    {
                        data.HWM = UInt64.Parse(hwmValue);
                        ctl.HWM = UInt64.Parse(hwmValue);
                    }

                    // Bind the data publisher socket:

                    // TODO: use epgm:// protocol for multicast efficiency when we build that support into libzmq.dll for Windows.
                    //data.Rate = 20000L;
                    data.SndBuf = (uint)chunkSize * 80UL;
                    data.StringToIdentity(subscription, Encoding.Unicode);
                    data.Bind("tcp://" + device + ":" + port.ToString());

                    // Bind the control reply socket:
                    ctl.Bind("tcp://" + device + ":" + (port + 1).ToString());

                    // Wait for the sockets to bind:
                    Thread.Sleep(500);

                    HashSet<Guid> clientsResponded = new HashSet<Guid>();

                    // Create a poller on the control socket to handle client requests:
                    PollItem[] pollItems = new PollItem[1];
                    pollItems[0] = ctl.CreatePollItem(IOMultiPlex.POLLIN);
                    pollItems[0].PollInHandler += new PollHandler((Socket sock, IOMultiPlex mp) =>
                    {
                        //uint ev = 0;
                        do
                        {
                            Queue<byte[]> request = sock.RecvAll();

                            Guid clientIdentity = new Guid(request.Dequeue().Skip(1).ToArray());
                            clientTimeout[clientIdentity] = DateTimeOffset.UtcNow.AddSeconds(3);

                            string cmd = Encoding.Unicode.GetString(request.Dequeue());

                            // Process the client command:
                            trace("{0}: Client sent '{1}'", clientIdentity.ToString(), cmd);
                            switch (cmd)
                            {
                                case "JOIN":
                                    if (clients.ContainsKey(clientIdentity))
                                    {
                                        trace("{0}: Client already joined", clientIdentity.ToString());
                                        sock.Send("ALREADY_JOINED", Encoding.Unicode);
                                        break;
                                    }

                                    clients.Add(clientIdentity, new ClientState(clientIdentity, new BitArray(numBitArrayBytes << 3)));

                                    // TODO: send out the tarball descriptor:
                                    sock.SendMore("JOINED", Encoding.Unicode);

                                    ReadOnlyCollection<FileInfo> files = tarball.Files;

                                    sock.SendMore(BitConverter.GetBytes(numChunks));
                                    sock.SendMore(BitConverter.GetBytes(chunkSize));
                                    sock.SendMore(BitConverter.GetBytes(tarball.Files.Count));

                                    foreach (var fi in files)
                                    {
                                        string fiName = fi.FullName.Substring(basePath.Length);
                                        sock.SendMore(fiName, Encoding.Unicode);
                                        sock.SendMore(BitConverter.GetBytes(fi.Length));
                                        sock.SendMore(new byte[16]);
                                    }

                                    sock.Send("", Encoding.Unicode);
                                    trace("{0}: Sent JOINED response", clientIdentity.ToString());
                                    break;

                                case "NAK":
                                    // Receive the client's current NAK:
                                    if (!clients.ContainsKey(clientIdentity))
                                    {
                                        trace("{0}: Client not joined", clientIdentity.ToString());
                                        sock.Send("NOTJOINED", Encoding.Unicode);
                                        clientsResponded.Remove(clientIdentity);
                                        clientTimeout.Remove(clientIdentity);
                                        break;
                                    }

                                    byte[] tmp = request.Dequeue();
                                    if (tmp.Length != numBitArrayBytes)
                                    {
                                        trace("{0}: Bad NAKs", clientIdentity.ToString());
                                        sock.Send("BADNAKS", Encoding.Unicode);
                                        clientsResponded.Remove(clientIdentity);
                                        clientTimeout.Remove(clientIdentity);
                                        break;
                                    }

                                    clients[clientIdentity].NAK = new BitArray(tmp);
                                    trace("{0}: NAKs received", clientIdentity.ToString());
                                    sock.Send("OK", Encoding.Unicode);

                                    if (!clientsResponded.Contains(clientIdentity))
                                        clientsResponded.Add(clientIdentity);
                                    break;

                                case "LEAVE":
                                    if (!clients.ContainsKey(clientIdentity))
                                    {
                                        trace("{0}: Client already left!", clientIdentity.ToString());
                                        sock.Send("ALREADY_LEFT", Encoding.Unicode);
                                        clientsResponded.Remove(clientIdentity);
                                        clientTimeout.Remove(clientIdentity);
                                        break;
                                    }

                                    // Remove the client's state record:
                                    clients.Remove(clientIdentity);
                                    sock.Send("LEFT", Encoding.Unicode);
                                    trace("{0}: Client left", clientIdentity.ToString());

                                    // TODO: reset NAKs?!?
                                    clientsResponded.Remove(clientIdentity);
                                    clientTimeout.Remove(clientIdentity);
                                    break;

                                case "ALIVE":
                                    if (!clients.ContainsKey(clientIdentity))
                                    {
                                        trace("{0}: WHOAREYOU", clientIdentity.ToString());
                                        byte[] sendIdentity = new byte[1] { (byte)'@' }.Concat(clientIdentity.ToByteArray()).ToArray();
                                        sock.Send("WHOAREYOU", Encoding.Unicode);
                                        break;
                                    }

                                    sock.Send("", Encoding.Unicode);

                                    if (!clientsResponded.Contains(clientIdentity))
                                        clientsResponded.Add(clientIdentity);
                                    break;

                                default:
                                    // Unknown command.
                                    sock.Send("UNKNOWN", Encoding.Unicode);
                                    trace("{0}: Unknown request", clientIdentity.ToString());
                                    break;
                            }

                            request = null;
                            // TODO: detect more messages to receive
                        } while (false);
                    });

                    int chunkIdx = 0;
                    byte[] buf = new byte[chunkSize];
                    int pollWait = 1000;
                    int previousClientCount = 0;

                    DateTimeOffset lastPing = DateTimeOffset.UtcNow;

                    // Begin the main polling and data delivery loop:
                    while (true)
                    {
                        //trace("POLL {0}", pollWait);
                        while (ctx.Poll(pollItems, pollWait) == 1);

                        if (DateTimeOffset.UtcNow.Subtract(lastPing).TotalMilliseconds >= 500d)
                        {
                            lastPing = DateTimeOffset.UtcNow;

                            // Send a PING to all subscribers:
                            trace("PING");
                            data.SendMore(this.subscription, Encoding.Unicode);
                            data.Send("PING", Encoding.Unicode);
                        }

                        // Wait for all clients to send back a response:
                        if (clientsResponded.Count < clients.Count)
                        {
                            DateTimeOffset rightMeow = DateTimeOffset.UtcNow;
                            
                            // Anyone timed out yet?
                            KeyValuePair<Guid, DateTimeOffset> timedOutClient = clientTimeout.FirstOrDefault(dt => dt.Value < rightMeow);
                            if (timedOutClient.Key == Guid.Empty)
                            {
                                continue;
                            }
                            
                            // Yes, remove that client:
                            Console.WriteLine("{0}: Timeout expired.", timedOutClient.Key);
                            clientTimeout.Remove(timedOutClient.Key);
                            clients.Remove(timedOutClient.Key);
                        }
                        clientsResponded.Clear();

                        // If any client state is dirty, OR all the NAKs amongst the joined clients:
                        if (clients.Values.Any(cli => cli.IsDirty) || (clients.Count != previousClientCount))
                        {
                            previousClientCount = clients.Count;

                            trace("Rebuilding NAK state");
                            BitArray tmpNAKs = new BitArray(numBitArrayBytes * 8, false);
                            foreach (var cli in clients.Values)
                            {
                                tmpNAKs = tmpNAKs.Or(cli.Clean().NAK);
                            }
                            currentNAKs = tmpNAKs;
                        }
                        else
                        {
                            previousClientCount = clients.Count;

                            //pollWait = 10000;
                            continue;
                        }

                        //pollWait = 10000;

                        // If the current chunk is not NAKed, find the next one that is:
                        //if (!currentNAKs[chunkIdx])
                        {
                            // Determine the next NAKed packet amongst all joined clients:
                            int lastIdx = chunkIdx;
                            bool found = true;

                            do
                            {
                                chunkIdx = (chunkIdx + 1) % numChunks;

                                // Have we circled all the way around?
                                if (lastIdx == chunkIdx)
                                {
                                    // None are NAKed. Nothing to do.
                                    found = false;
                                    break;
                                }
                            } while (!currentNAKs[chunkIdx]);

                            if (!found)
                            {
                                //pollWait = 100000;
                                continue;
                            }
                        }

                        // Send the current chunk:
                        if (currentNAKs[chunkIdx])
                        {
                            // Chunk index first:
                            trace("SEND chunk: {0}", chunkIdx);
                            data.SendMore(this.subscription, Encoding.Unicode);
                            data.SendMore("DATA", Encoding.Unicode);
                            data.SendMore(BitConverter.GetBytes(chunkIdx));

                            // Chunk size:
                            tarball.Seek((long)chunkIdx * chunkSize, SeekOrigin.Begin);
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

                            if (ChunkSent != null) ChunkSent(this, chunkIdx);
                            trace("COMPLETE chunk: {0}", chunkIdx);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
            }
        }
    }
}
