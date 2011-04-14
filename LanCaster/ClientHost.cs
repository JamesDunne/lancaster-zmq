using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using ZMQ;
using System.Configuration;

namespace WellDunne.LanCaster
{
    public sealed class ClientHost
    {
        private DirectoryInfo downloadDirectory;
        private int numChunks;
        private string subscription;
        private string endpoint;
        private BooleanSwitch doLogging;
        private int chunkSize;

        public delegate BitArray GetClientNAKStateDelegate(ClientHost host, int numChunks, int chunkSize, TarballStreamReader tarball);

        public ClientHost(string endpoint, string subscription, DirectoryInfo downloadDirectory, GetClientNAKStateDelegate getClientState)
        {
            this.endpoint = endpoint;
            this.subscription = subscription;
            this.downloadDirectory = downloadDirectory;
            this.getClientState = getClientState;
            this.Completed = false;
            this.doLogging = new BooleanSwitch("doLogging", "Log client events", "0");
        }

        private GetClientNAKStateDelegate getClientState;
        public event Action<int> ChunkWritten;

        private void trace(string format, params object[] args)
        {
            Trace.WriteLineIf(this.doLogging.Enabled, String.Format(format, args), "client");
        }

        public bool Completed { get; private set; }

        /// <summary>
        /// Main thread to host the client process.
        /// </summary>
        /// <param name="threadContext">A ZMQ.Context instance.</param>
        public void Run(object threadContext)
        {
            try
            {
                Context ctx = (Context)threadContext;

                // This would be a using statement on data and ctl but we need to close and reopen sockets, which means
                // we have to Dispose() of a socket early and create a new instance. C#, wisely so, prevents you from
                // reassigning a using variable.
                Socket data = null, ctl = null;
                try
                {
                    data = ctx.Socket(SocketType.SUB);
                    ctl = ctx.Socket(SocketType.REQ);

                    ushort port = 12198;
                    string addr = endpoint;
                    int idx = endpoint.LastIndexOf(':');
                    if (idx >= 0)
                    {
                        addr = endpoint.Substring(0, idx);
                        UInt16.TryParse(endpoint.Substring(idx + 1), out port);
                    }

                    string hwmValue = ConfigurationManager.AppSettings["HWM"];
                    if (hwmValue != null)
                    {
                        data.HWM = UInt64.Parse(hwmValue);
                        ctl.HWM = UInt64.Parse(hwmValue);
                    }

                    data.RcvBuf = 256 * 1024 * 20L;
                    data.Connect("tcp://" + addr + ":" + port.ToString());
                    data.Subscribe(subscription, Encoding.Unicode);

                    // Connect to the control request socket:
                    ctl.Connect("tcp://" + addr + ":" + (port + 1).ToString());

                    Guid myIdentity = Guid.NewGuid();
                    ctl.Identity = new byte[1] { (byte)'@' }.Concat(myIdentity.ToByteArray()).ToArray();

                    Thread.Sleep(500);

                    // Send a JOIN request:
                    ctl.SendMore(ctl.Identity);
                    ctl.Send("JOIN", Encoding.Unicode);

                    // Process the reply:
                    trace("ctl.RecvAll for JOIN reply");
                    Queue<byte[]> reply = ctl.RecvAll();

                    string resp = Encoding.Unicode.GetString(reply.Dequeue());

                    if (resp != "JOINED")
                    {
                        Console.WriteLine("Fail!");
                        return;
                    }

                    numChunks = BitConverter.ToInt32(reply.Dequeue(), 0);
                    chunkSize = BitConverter.ToInt32(reply.Dequeue(), 0);
                    int numFiles = BitConverter.ToInt32(reply.Dequeue(), 0);

                    List<TarballEntry> tbes = new List<TarballEntry>(numFiles);
                    for (int i = 0; i < numFiles; ++i)
                    {
                        string fiName = Encoding.Unicode.GetString(reply.Dequeue());
                        long length = BitConverter.ToInt64(reply.Dequeue(), 0);
                        byte[] hash = reply.Dequeue();

                        TarballEntry tbe = new TarballEntry(fiName, length, hash);
                        tbes.Add(tbe);
                    }

                    // Create the tarball reader that writes the files locally:
                    using (var tarball = new TarballStreamReader(downloadDirectory, tbes))
                    {
                        // Send a NAK message for all the chunks:
                        ctl.SendMore(ctl.Identity);
                        ctl.SendMore("NAKS", Encoding.Unicode);

                        BitArray naks = getClientState(this, numChunks, chunkSize, tarball);
                        int numBytes = ((numChunks + 7) & ~7) >> 3;
                        byte[] nakBuf = new byte[numBytes];
                        naks.CopyTo(nakBuf, 0);
                        trace("CTL1 SEND NAKS");
                        ctl.Send(nakBuf);
                        nakBuf = null;

                        // Wait for the NAK reply:
                        trace("CTL1 RECV NAKS");
                        Queue<byte[]> nak1Reply = ctl.RecvAll(10000);
                        if (nak1Reply == null)
                        {
                            trace("Timed out waiting for server reply to NAKS");
                            return;
                        }
                        trace("NAKS1 ok");

                        // Poll for incoming messages on the data SUB socket:
                        PollItem[] pollItems = new PollItem[1];
                        pollItems[0] = data.CreatePollItem(IOMultiPlex.POLLIN);
                        pollItems[0].PollInHandler += new PollHandler((Socket sock, IOMultiPlex mp) =>
                        {
                            // Receive a message on the data socket:
                            trace("data.RecvAll for poll");
                            Queue<byte[]> packet = sock.RecvAll();

                            string sub = Encoding.Unicode.GetString(packet.Dequeue());
                            string cmd = Encoding.Unicode.GetString(packet.Dequeue());
                            trace("Server: '{0}'", cmd);
                            if (cmd == "PING")
                            {
                                ctl.SendMore(ctl.Identity);
                                ctl.Send("ALIVE", Encoding.Unicode);

                                // Wait for the response:
                                trace("ctl.RecvAll for ALIVE reply");
                                Queue<byte[]> pingReply = ctl.RecvAll(10000);
                                if (pingReply == null)
                                {
                                    trace("Timed out waiting for server reply to ALIVE");
                                    ctl.Dispose();
                                    ctl = null;
                                    return;
                                }

                                string pingCmd = Encoding.Unicode.GetString(pingReply.Dequeue());
                                trace("Server: '{0}'", pingCmd);
                                if (pingCmd == "") return;

                                // Is server confused?
                                if (pingCmd == "WHOAREYOU")
                                {
                                    // Server must have restarted and is now confused.

                                    // Send a JOIN request:
                                    ctl.SendMore(ctl.Identity);
                                    ctl.Send("JOIN", Encoding.Unicode);

                                    // Process the reply:
                                    trace("ctl.RecvAll for JOIN reply");
                                    Queue<byte[]> reply2 = ctl.RecvAll();

                                    string resp2 = Encoding.Unicode.GetString(reply2.Dequeue());

                                    if (resp2 != "JOINED")
                                    {
                                        Console.WriteLine("Fail!");
                                        return;
                                    }

                                    // TODO: verify the JOINED response is the same as what we originally got?

                                    // Send our NAKs:
                                    ctl.SendMore(ctl.Identity);
                                    ctl.SendMore("NAKS", Encoding.Unicode);
                                    nakBuf = new byte[numBytes];
                                    naks.CopyTo(nakBuf, 0);
                                    trace("CTL2 SEND NAK");
                                    ctl.Send(nakBuf);
                                    nakBuf = null;

                                    // Wait for the NAK reply:
                                    trace("CTL2 RECV NAK");
                                    Queue<byte[]> nak2Reply = ctl.RecvAll(10000);
                                    if (nak2Reply == null)
                                    {
                                        trace("Timed out waiting for server reply to NAK");
                                        ctl.Dispose();
                                        ctl = null;
                                        return;
                                    }
                                    trace("NAK2 ok");

                                    return;
                                }
                            }

                            Debug.Assert(cmd == "DATA");

                            int chunkIdx = BitConverter.ToInt32(packet.Dequeue(), 0);
                            // Already received this chunk?
                            if (!naks[chunkIdx])
                            {
                                trace("ALREADY RECV {0}", chunkIdx);
                                return;
                            }

                            trace("RECV {0}", chunkIdx);

                            byte[] chunk = packet.Dequeue();
                            tarball.Seek((long)chunkIdx * chunkSize, SeekOrigin.Begin);
                            tarball.Write(chunk, 0, chunk.Length);
                            chunk = null;

                            naks[chunkIdx] = false;
                            // Notify the host that a chunk was written:
                            if (ChunkWritten != null) ChunkWritten(chunkIdx);

#if true
                            // Send a ACK packet to the control socket:
                            ctl.SendMore(ctl.Identity);
                            ctl.SendMore("ACK", Encoding.Unicode);
                            ctl.Send(BitConverter.GetBytes(chunkIdx));
                            if (ctl.RecvAll(10000) == null)
                            {
                                trace("Timed out waiting for server reply to ACK");
                                ctl.Dispose();
                                ctl = null;
                            }
#else
                            // Send a NAK packet to the control socket:
                            ctl.SendMore(ctl.Identity);
                            ctl.SendMore("NAK", Encoding.Unicode);
                            nakBuf = new byte[numBytes];
                            naks.CopyTo(nakBuf, 0);
                            trace("CTL3 SEND NAK");
                            ctl.Send(nakBuf);
                            nakBuf = null;

                            // Wait for the reply:
                            trace("CTL3 RECV NAK");
                            Queue<byte[]> nak3Reply = ctl.RecvAll(10000);
                            if (nak3Reply == null)
                            {
                                trace("Timed out waiting for server reply to ALIVE");
                                ctl.Dispose();
                                ctl = null;
                            }
                            else
                            {
                                trace("NAK3 ok");
                            }
#endif

                            packet = null;
                        });

                        DateTimeOffset lastRecv = DateTimeOffset.UtcNow;

                        // Create a socket poller for the data socket:
                        while (true)
                        {
                            // If we disposed of the previous control socket, create a new one:
                            if (ctl == null)
                            {
                                Console.WriteLine("Creating new socket");
                                // Set up new socket:
                                ctl = ctx.Socket(SocketType.REQ);

                                // Connect to the control request socket:
                                ctl.Connect("tcp://" + addr + ":" + (port + 1).ToString());
                                ctl.Identity = new byte[1] { (byte)'@' }.Concat(myIdentity.ToByteArray()).ToArray();
                            }

                            // No more NAKed packets?
                            if (naks.Cast<bool>().Take(numChunks).All(b => !b))
                            {
                                Completed = true;
                                break;
                            }

                            // Process incoming DATA subscription messages while they're available:
                            bool gotData = false;
                            trace("POLL");
                            while ((ctx.Poll(pollItems, 100000) == 1) && (ctl != null))
                            {
                                gotData = true;
                                lastRecv = DateTimeOffset.UtcNow;
                            }

                            if (!gotData && (DateTimeOffset.UtcNow.Subtract(lastRecv).TotalSeconds >= 10))
                            {
#if false
                                // TODO: Hack the clrzmq2 Socket to allow closing and reopening the same Socket instance using
                                // a new 0MQ socket.
                                throw new System.Exception("BUG: No data received from server in 10 seconds. Need to close and reopen socket but CLRZMQ2 API doesn't allow this.");
#endif
                            }
                        }

                        ctl.SendMore(ctl.Identity);
                        ctl.Send("LEAVE", Encoding.Unicode);

                        ctl.RecvAll(10000);
                    }
                }
                finally
                {
                    if (data != null) data.Dispose();
                    if (ctl != null) ctl.Dispose();
                }
            }
            catch (System.Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
            }
        }
    }
}
