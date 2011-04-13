using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

        public const int ChunkSize = 10240;

        public ServerHost(string endpoint, string subscription, TarballStreamWriter tarball, string basePath)
        {
            this.endpoint = endpoint;
            this.subscription = subscription;
            this.tarball = tarball;
            this.basePath = basePath;
            this.numChunks = (int)(tarball.Length / ChunkSize) + ((tarball.Length % ChunkSize > 0) ? 1 : 0);
            this.numBitArrayBytes = ((numChunks + 7) & ~7) >> 3;

            if (this.numChunks == 0) throw new System.Exception();
        }

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

        private int numChunks;
        private int numBitArrayBytes;
        private Dictionary<Guid, ClientState> clients = new Dictionary<Guid, ClientState>();

        private static void WriteBuffer(Stream st, byte[] buf)
        {
            st.Write(buf, 0, buf.Length);
        }

        private static void trace(string format, params object[] args)
        {
            Trace.WriteLine(String.Format(format, args), "server");
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

                    // Bind the data publisher socket:
                    //data.Rate = 20000L;
                    // TODO: use epgm:// protocol for multicast efficiency when we build that support into libzmq.dll for Windows.
                    data.Bind("tcp://" + device + ":" + port.ToString());

                    // Bind the control reply socket:
                    ctl.Bind("tcp://" + device + ":" + (port + 1).ToString());

                    // Wait for the sockets to bind:
                    Thread.Sleep(500);

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

                            string cmd = Encoding.Unicode.GetString(request.Dequeue());

                            // Process the client command:
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
                                    sock.SendMore(BitConverter.GetBytes(tarball.Files.Count));

                                    foreach (var fi in files)
                                    {
                                        string fiName = fi.FullName.Substring(basePath.Length);
                                        sock.SendMore(fiName, Encoding.Unicode);
                                        sock.SendMore(BitConverter.GetBytes(fi.Length));
                                        Console.WriteLine("{0,15} {1}", fi.Length, fiName);
                                        sock.SendMore(new byte[16]);
                                    }

                                    sock.Send("", Encoding.Unicode);
                                    trace("{0}: Sent JOINED response", clientIdentity.ToString());
                                    break;

                                case "NAK":
                                    // Recieve the client's current NAK:
                                    if (!clients.ContainsKey(clientIdentity))
                                    {
                                        trace("{0}: Client not joined", clientIdentity.ToString());
                                        sock.Send("NOTJOINED", Encoding.Unicode);
                                        break;
                                    }

                                    byte[] tmp = request.Dequeue();
                                    if (tmp.Length != numBitArrayBytes)
                                    {
                                        trace("{0}: Bad NAKs", clientIdentity.ToString());
                                        sock.Send("BADNAKS", Encoding.Unicode);
                                        break;
                                    }

                                    clients[clientIdentity].NAK = new BitArray(tmp);
                                    trace("{0}: NAKs received", clientIdentity.ToString());
                                    sock.Send("OK", Encoding.Unicode);
                                    break;

                                case "LEAVE":
                                    if (!clients.ContainsKey(clientIdentity))
                                    {
                                        trace("{0}: Client already left!", clientIdentity.ToString());
                                        sock.Send("ALREADY_LEFT", Encoding.Unicode);
                                        break;
                                    }

                                    // Remove the client's state record:
                                    clients.Remove(clientIdentity);
                                    sock.Send("LEFT", Encoding.Unicode);
                                    trace("{0}: Client left", clientIdentity.ToString());

                                    // TODO: reset NAKs?!?
                                    break;

                                default:
                                    // Unknown command.
                                    sock.Send("UNKNOWN", Encoding.Unicode);
                                    trace("{0}: Unknown request", clientIdentity.ToString());
                                    break;
                            }
                            // TODO: detect more messages to receive
                        } while (false);
                    });

                    int chunkIdx = 0;
                    BitArray currentNAKs = new BitArray(numBitArrayBytes * 8, false);
                    byte[] buf = new byte[ChunkSize];
                    int pollWait = 100;
                    int previousClientCount = 0;

                    // Begin the main polling and data delivery loop:
                    while (true)
                    {
                        Trace.WriteLine(String.Format("POLL {0}", pollWait), "server");
                        ctx.Poll(pollItems, pollWait);

                        // If any client state is dirty, OR all the NAKs amongst the joined clients:
                        if (clients.Values.Any(cli => cli.IsDirty) || (clients.Count != previousClientCount))
                        {
                            previousClientCount = clients.Count;

                            Trace.WriteLine("Rebuilding NAK state", "server");
                            currentNAKs = new BitArray(numBitArrayBytes * 8, false);
                            foreach (var cli in clients.Values)
                            {
                                currentNAKs = currentNAKs.Or(cli.Clean().NAK);
                            }
                        }
                        else
                        {
                            previousClientCount = clients.Count;

                            pollWait = 100000;
                            continue;
                        }

                        pollWait = 100;

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
                                pollWait = 100000;
                                continue;
                            }
                        }

                        // Send the current chunk:
                        if (currentNAKs[chunkIdx])
                        {
                            // Chunk index first:
                            Trace.WriteLine(String.Format("SEND chunk: {0}", chunkIdx), "server");
                            data.SendMore(this.subscription, Encoding.Unicode);
                            data.SendMore(BitConverter.GetBytes(chunkIdx));

                            // Chunk size:
                            tarball.Seek((long)chunkIdx * ChunkSize, SeekOrigin.Begin);
                            int currChunkSize = tarball.Read(buf, 0, ChunkSize);
                            if (currChunkSize < ChunkSize)
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
                            Trace.WriteLine(String.Format("COMPLETE chunk: {0}", chunkIdx), "server");
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                Trace.WriteLine(ex.ToString(), "server");
            }
        }
    }
}
