using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ZMQ;
using System.Threading;
using System.Diagnostics;
using System.Collections;
using System.IO;
using System.Collections.ObjectModel;

namespace WellDunne.LanCaster
{
    public sealed class ServerHost
    {
        TarballStreamWriter tarball;
        string basePath;

        public const int ChunkSize = 10240;

        public ServerHost(TarballStreamWriter tarball, string basePath)
        {
            this.tarball = tarball;
            this.basePath = basePath;
            this.numChunks = (int)(tarball.Length / ChunkSize) + ((tarball.Length % ChunkSize > 0) ? 1 : 0);
            this.numBitArrayBytes = ((numChunks + 7) & ~8) >> 3;
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
                    // Bind the data publisher socket:
                    //data.Rate = 20000L;
                    // TODO: use epgm:// protocol for multicast efficiency when we build that support into libzmq.dll for Windows.
                    data.Bind("tcp://*:12198");

                    // Bind the control reply socket:
                    ctl.Bind("tcp://*:12199");

                    // Wait for the sockets to bind:
                    Thread.Sleep(500);

                    // Create a poller on the control socket to handle client requests:
                    PollItem[] pollItems = new PollItem[1];
                    pollItems[0] = ctl.CreatePollItem(IOMultiPlex.POLLIN);
                    pollItems[0].PollInHandler += new PollHandler((Socket sock, IOMultiPlex mp) =>
                    {
                        Queue<byte[]> request = sock.RecvAll();

                        Guid clientIdentity = new Guid(request.Dequeue());
                        Trace.WriteLine(clientIdentity.ToString(), "server");

                        string cmd = Encoding.Unicode.GetString(request.Dequeue());

                        // Process the client command:
                        switch (cmd)
                        {
                            case "JOIN":
                                if (clients.ContainsKey(clientIdentity))
                                {
                                    Trace.WriteLine("Client already joined", "server");
                                    sock.Send("ALREADY_JOINED", Encoding.Unicode);
                                    break;
                                }

                                clients.Add(clientIdentity, new ClientState(clientIdentity, new BitArray(numChunks)));

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
                                    sock.SendMore(new byte[16]);
                                }

                                sock.Send("", Encoding.Unicode);
                                Trace.WriteLine("Sent JOINED response", "server");
                                break;

                            case "NAK":
                                // Recieve the client's current NAK:
                                if (!clients.ContainsKey(clientIdentity))
                                {
                                    Trace.WriteLine("Client not joined", "server");
                                    sock.Send("NOTJOINED", Encoding.Unicode);
                                    break;
                                }

                                byte[] tmp = request.Dequeue();
                                if (tmp.Length != numBitArrayBytes)
                                {
                                    Trace.WriteLine("Bad NAKs", "server");
                                    sock.Send("BADNAKS", Encoding.Unicode);
                                    break;
                                }

                                clients[clientIdentity].NAK = new BitArray(tmp);
                                Trace.WriteLine("NAKs received", "server");
                                sock.Send("OK", Encoding.Unicode);
                                break;

                            case "LEAVE":
                                if (!clients.ContainsKey(clientIdentity))
                                {
                                    sock.Send("ALREADY_LEFT", Encoding.Unicode);
                                    break;
                                }

                                // Remove the client's state record:
                                clients.Remove(clientIdentity);
                                sock.Send("LEFT", Encoding.Unicode);

                                // TODO: reset NAKs?!?
                                break;

                            default:
                                // Unknown command.
                                sock.Send("UNKNOWN", Encoding.Unicode);
                                Trace.WriteLine("Unknown request", "server");
                                break;
                        }
                    });

                    int chunkIdx = 0;
                    BitArray currentNAKs = new BitArray(numBitArrayBytes * 8, false);
                    byte[] buf = new byte[ChunkSize];
                    int pollWait = 100;

                    // Begin the main polling and data delivery loop:
                    while (true)
                    {
                        Trace.WriteLine(String.Format("POLL {0}", pollWait), "server");
                        ctx.Poll(pollItems, pollWait);

                        // If any client state is dirty, OR all the NAKs amongst the joined clients:
                        if (clients.Values.Any(cli => cli.IsDirty))
                        {
                            Trace.WriteLine("Rebuilding NAK state", "server");
                            currentNAKs = clients.Values.Aggregate(
                                new BitArray(numBitArrayBytes * 8, false),
                                // Clean the dirty bit and OR the NAK bit array:
                                (acc, cli) => acc.Or(cli.Clean().NAK)
                            );
                            Trace.WriteLine("Completed rebuilding NAKs", "server");
                        }
                        else
                        {
                            pollWait = 100000;
                            continue;
                        }

                        pollWait = 100;

                        // If the current chunk is not NAKed, find the next one that is:
                        if (!currentNAKs[chunkIdx])
                        {
                            // Determine the next NAKed packet amongst all joined clients:
                            int lastIdx = chunkIdx;
                            bool found = true;

                            while (!currentNAKs[chunkIdx])
                            {
                                chunkIdx = (chunkIdx + 1) % numChunks;

                                // Have we circled all the way around?
                                if (lastIdx == chunkIdx)
                                {
                                    // None are NAKed. Nothing to do.
                                    pollWait = 100000;
                                    found = false;
                                    break;
                                }
                            }

                            if (!found) continue;
                        }

                        // Send the current chunk:
                        Trace.WriteLine(String.Format("Current chunk: {0}", chunkIdx), "server");
                        if (currentNAKs[chunkIdx])
                        {
                            // Chunk index first:
                            data.SendMore(BitConverter.GetBytes(chunkIdx));

                            // Chunk size:
                            int currChunkSize = tarball.Read(buf, 0, ChunkSize);
                            data.SendMore(BitConverter.GetBytes(currChunkSize));

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
