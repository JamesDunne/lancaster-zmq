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
        }

        private sealed class ClientState
        {
            public Guid Identity { get; private set; }
            public BitArray NAK { get; set; }

            public ClientState(Guid identity, BitArray nak)
            {
                this.Identity = identity;
                this.NAK = nak;
            }
        }

        private int numChunks;
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

                    string resp = "OK";
                    byte[] respBody = null;

                    string cmd = Encoding.Unicode.GetString(request.Dequeue());

                    // Process the client command:
                    switch (cmd)
                    {
                        case "JOIN":
                            if (clients.ContainsKey(clientIdentity))
                            {
                                resp = "ALREADY_JOINED";
                                break;
                            }
                            clients.Add(clientIdentity, new ClientState(clientIdentity, new BitArray(numChunks)));

                            // TODO: send out the tarball descriptor:
                            resp = "JOINED";

                            ReadOnlyCollection<FileInfo> files = tarball.Files;

                            using (MemoryStream ms = new MemoryStream(4 + files.Sum(fi => 4 + (fi.FullName.Length - basePath.Length) * 2)))
                            {
                                WriteBuffer(ms, BitConverter.GetBytes(tarball.Files.Count));

                                foreach (var fi in files)
                                {
                                    byte[] nameBuf = Encoding.Unicode.GetBytes(fi.FullName.Substring(basePath.Length));
                                    WriteBuffer(ms, BitConverter.GetBytes(nameBuf.Length));
                                    WriteBuffer(ms, nameBuf);
                                }
                                respBody = ms.ToArray();
                            }
                            break;

                        case "NAK":
                            // Recieve the client's current NAK:
                            if (!clients.ContainsKey(clientIdentity))
                            {
                                resp = "NOTJOINED";
                                break;
                            }

                            // TODO: aligned to 8-bit boundaries
                            clients[clientIdentity].NAK = new BitArray(request.Dequeue());
                            break;

                        default:
                            // Unknown command.
                            resp = "UNKNOWN";
                            break;
                    }

                    // Always send a reply:
                    if (respBody != null)
                    {
                        // Send the reply message with the body, non-blocking:
                        sock.SendMore(resp, Encoding.Unicode);
                        sock.Send(respBody);
                    }
                    else
                    {
                        // Just send the reply without a body, non-blocking:
                        sock.Send(resp, Encoding.Unicode);
                    }
                });

                // Begin the main polling and data delivery loop:
                while (true)
                {
                    ctx.Poll(pollItems, 10);
                }
            }
        }
    }
}
