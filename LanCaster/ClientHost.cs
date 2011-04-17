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
        private bool testMode;
        private ushort port;
        private string device;
        private int hwm;
        private TarballStreamReader tarball = null;
        private readonly object tarballLock = new object();

        public delegate BitArray GetClientNAKStateDelegate(ClientHost host, int numChunks, int chunkSize, TarballStreamReader tarball);

        public ClientHost(string endpoint, string subscription, DirectoryInfo downloadDirectory, bool testMode, GetClientNAKStateDelegate getClientState, int hwm)
        {
            this.endpoint = endpoint;
            this.subscription = subscription;
            this.downloadDirectory = downloadDirectory;
            this.testMode = testMode;
            this.getClientState = getClientState;
            this.Completed = false;
            this.doLogging = new BooleanSwitch("doLogging", "Log client events", "0");

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

        private GetClientNAKStateDelegate getClientState;
        public event Action<ClientHost, int> ChunkWritten;

        private void trace(string format, params object[] args)
        {
            Trace.WriteLineIf(this.doLogging.Enabled, String.Format(format, args), "client");
        }

        public bool Completed { get; private set; }
        public int ChunksPerMinute { get; private set; }
        public int ChunkSize { get { return this.chunkSize; } }

        private enum DataSUBState
        {
            Recv
        }

        private enum ControlREQState
        {
            Nothing,
            SendACK,
            SendALIVE,
            RecvALIVE,
            SendUNKNOWN,
            SendJOIN,
            RecvACK,
            RecvJOIN,
            RecvNAKS
        }

        private struct QueuedControlMessage
        {
            public readonly ControlREQState NewState;
            public readonly object Object;

            public QueuedControlMessage(ControlREQState newState, object obj)
            {
                NewState = newState;
                Object = obj;
            }
        }

        private static readonly byte[] cmdWrite = new byte[1] { (byte)'W' };
        private static readonly byte[] cmdExit = new byte[1] { (byte)'X' };

        private void DiskWriterThread(object threadContext)
        {
            try
            {
                Context ctx = (Context)threadContext;

                using (Socket
                    disk = new Socket(SocketType.PULL),
                    diskACK = new Socket(SocketType.PUSH)
                )
                {
                    disk.RCVHWM = 10;
                    disk.RcvBuf = 1048576 * disk.RCVHWM;
                    disk.Connect("inproc://disk");
                    diskACK.Connect("inproc://diskack");

                    Thread.Sleep(500);

                    bool done = false;

                    PollItem[] pollItems = new PollItem[1];
                    pollItems[0] = disk.CreatePollItem(IOMultiPlex.POLLIN);
                    pollItems[0].PollInHandler += new PollHandler((Socket sock, IOMultiPlex revents) =>
                    {
                        Queue<byte[]> packet = sock.RecvAll();

                        // Determine the action to take:
                        byte[] cmd = packet.Dequeue();

                        int chunkIdx;
                        byte[] chunkIdxPkt;
                        byte[] chunk;

                        switch (cmd[0])
                        {
                            // Write to disk
                            case (byte)'W':
                                chunkIdx = BitConverter.ToInt32(chunkIdxPkt = packet.Dequeue(), 0);
                                chunk = packet.Dequeue();
                                if (!testMode)
                                {
                                    lock (tarballLock)
                                    {
                                        tarball.Seek((long)chunkIdx * chunkSize, SeekOrigin.Begin);
                                        tarball.Write(chunk, 0, chunk.Length);
                                    }
                                }

                                // Send the acknowledgement that this chunk was written to disk:
                                diskACK.Send(chunkIdxPkt);
                                break;

                            // Exit thread
                            case (byte)'X':
                                done = true;
                                break;

                            default:
                                break;
                        }
                    });

                    while (!done)
                    {
                        if (ctx.Poll(pollItems) == 0)
                        {
                            Thread.Sleep(1);
                        }
                    }
                }
            }
            catch (System.Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
            }
        }

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
                Socket data = null, ctl = null, disk = null, diskACK = null;
                try
                {
                    data = ctx.Socket(SocketType.SUB);
                    ctl = ctx.Socket(SocketType.REQ);
                    disk = new Socket(SocketType.PUSH);
                    diskACK = new Socket(SocketType.PULL);

                    // Set the HWM for the disk PUSH so that the PUSH blocks if the PULL can't keep up writing:
                    disk.SNDHWM = 1;
                    disk.RCVHWM = 1;
                    disk.SndBuf = 1048576 * 10;
                    disk.Bind("inproc://disk");

                    diskACK.Bind("inproc://diskack");

                    Thread.Sleep(500);

                    // Create the disk PULL thread:
                    Thread diskWriterThread = new Thread(new ParameterizedThreadStart(DiskWriterThread));
                    diskWriterThread.Start(ctx);

                    data.RCVHWM = hwm;

                    data.RcvBuf = 1048576 * 128 * 2;
                    data.Connect("tcp://" + device + ":" + port.ToString());
                    data.Subscribe(subscription, Encoding.Unicode);

                    // Connect to the control request socket:
                    ctl.Connect("tcp://" + device + ":" + (port + 1).ToString());

                    Guid myIdentity = Guid.NewGuid();
                    ctl.Identity = new byte[1] { (byte)'@' }.Concat(myIdentity.ToByteArray()).ToArray();

                    Thread.Sleep(500);

                    // Begin client logic:

                    BitArray naks = null;
                    int numBytes = 0;
                    int ackCount = -1;
                    byte[] nakBuf = null;

                    Stopwatch recvTimer = Stopwatch.StartNew();
                    long lastElapsedMilliseconds = recvTimer.ElapsedMilliseconds;
                    int msgsRecv = 0;

                    try
                    {
                        // Poll for incoming messages on the data SUB socket:
                        PollItem[] pollItems = new PollItem[3];
                        pollItems[0] = data.CreatePollItem(IOMultiPlex.POLLIN);
                        pollItems[1] = ctl.CreatePollItem(IOMultiPlex.POLLIN | IOMultiPlex.POLLOUT);
                        pollItems[2] = diskACK.CreatePollItem(IOMultiPlex.POLLIN);

                        ZMQStateMasheen<DataSUBState> dataFSM;
                        ZMQStateMasheen<ControlREQState> controlFSM;
                        Queue<QueuedControlMessage> controlStateQueue = new Queue<QueuedControlMessage>();

                        List<int> runningACKs = new List<int>(128);
                        DateTimeOffset lastSentACKs = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromSeconds(60));

                        // We create a state machine to handle our send/recv state:
                        dataFSM = new ZMQStateMasheen<DataSUBState>(
                            // Set the initial state:
                            DataSUBState.Recv,
                            // Initial state handler:
                            new ZMQStateMasheen<DataSUBState>.State(DataSUBState.Recv, (sock, revents) =>
                            {
                                Queue<byte[]> packet = sock.RecvAll();

                                string sub = Encoding.Unicode.GetString(packet.Dequeue());
                                string cmd = Encoding.Unicode.GetString(packet.Dequeue());

                                if ((naks != null) && (cmd == "DATA"))
                                {
                                    ++msgsRecv;

                                    int chunkIdx = BitConverter.ToInt32(packet.Dequeue(), 0);

                                    // Already received this chunk?
                                    if (!naks[chunkIdx])
                                    {
                                        trace("ALREADY RECV {0}", chunkIdx);
                                        return DataSUBState.Recv;
                                    }

                                    trace("RECV {0}", chunkIdx);

                                    // Queue up the disk writes with PUSH/PULL and an HWM on a separate thread to maintain as
                                    // constant disk write throughput as we can get... The HWM will enforce the PUSHer to block
                                    // when the PULLer cannot receive yet.
                                    byte[] chunk = packet.Dequeue();
                                    disk.SendMore(cmdWrite);
                                    disk.SendMore(BitConverter.GetBytes(chunkIdx));
                                    disk.Send(chunk);
                                    chunk = null;

#if false
                                    naks[chunkIdx] = false;
                                    ++ackCount;

                                    // Notify the host that a chunk was written:
                                    if (ChunkWritten != null) ChunkWritten(this, chunkIdx);

                                    runningACKs.Add(chunkIdx);

                                    // If we ran up the timer, send more ACKs:
                                    if (DateTimeOffset.UtcNow.Subtract(lastSentACKs).TotalMilliseconds >= 100d)
                                    {
                                        lastSentACKs = DateTimeOffset.UtcNow;
                                        controlStateQueue.Enqueue(new QueuedControlMessage(ControlREQState.SendACK, new List<int>(runningACKs)));
                                        runningACKs.Clear();
                                    }
#endif
                                    return DataSUBState.Recv;
                                }
                                else if (cmd == "PING")
                                {
                                    controlStateQueue.Enqueue(new QueuedControlMessage(ControlREQState.SendALIVE, null));
                                    return DataSUBState.Recv;
                                }
                                else if (cmd == "WHOAREYOU")
                                {
                                    controlStateQueue.Enqueue(new QueuedControlMessage(ControlREQState.SendJOIN, null));
                                    return DataSUBState.Recv;
                                }
                                else
                                {
                                    return DataSUBState.Recv;
                                }
                            })
                        );

                        // We create a state machine to handle our send/recv state:
                        object tmpControlState = null;
                        controlFSM = new ZMQStateMasheen<ControlREQState>(
                            ControlREQState.SendJOIN,
                            new ZMQStateMasheen<ControlREQState>.State(ControlREQState.Nothing, (sock, revents) =>
                            {
                                // If we ran up the timer, send more ACKs:
                                if (DateTimeOffset.UtcNow.Subtract(lastSentACKs).TotalMilliseconds >= 100d)
                                {
                                    lastSentACKs = DateTimeOffset.UtcNow;
                                    controlStateQueue.Enqueue(new QueuedControlMessage(ControlREQState.SendACK, new List<int>(runningACKs)));
                                    runningACKs.Clear();
                                }

                                if (controlStateQueue.Count > 0)
                                {
                                    var msg = controlStateQueue.Dequeue();
                                    tmpControlState = msg.Object;
                                    return msg.NewState;
                                }

                                // A dummy OUT event? We don't have anything to send, so just sleep:
                                if ((revents & IOMultiPlex.POLLOUT) == IOMultiPlex.POLLOUT) Thread.Sleep(1);
                                return ControlREQState.Nothing;
                            }),
                            new ZMQStateMasheen<ControlREQState>.State(ControlREQState.SendALIVE, (sock, revents) =>
                            {
                                if ((revents & IOMultiPlex.POLLOUT) != IOMultiPlex.POLLOUT) return ControlREQState.Nothing;

                                ctl.SendMore(ctl.Identity);
                                ctl.Send("ALIVE", Encoding.Unicode);
                                return ControlREQState.RecvALIVE;
                            }),
                            new ZMQStateMasheen<ControlREQState>.State(ControlREQState.RecvALIVE, (sock, revents) =>
                            {
                                if ((revents & IOMultiPlex.POLLIN) != IOMultiPlex.POLLIN) return ControlREQState.Nothing;

                                Queue<byte[]> packet = ctl.RecvAll();

                                string cmd = Encoding.Unicode.GetString(packet.Dequeue());
                                trace("Server: '{0}'", cmd);

                                if (cmd == "") return ControlREQState.Nothing;
                                else if (cmd == "WHOAREYOU") return ControlREQState.SendJOIN;

                                return ControlREQState.Nothing;
                            }),
                            new ZMQStateMasheen<ControlREQState>.State(ControlREQState.SendJOIN, (sock, revents) =>
                            {
                                if ((revents & IOMultiPlex.POLLOUT) != IOMultiPlex.POLLOUT) return ControlREQState.Nothing;

                                // Send a JOIN request:
                                ctl.SendMore(ctl.Identity);
                                ctl.Send("JOIN", Encoding.Unicode);

                                return ControlREQState.RecvJOIN;
                            }),
                            new ZMQStateMasheen<ControlREQState>.State(ControlREQState.RecvJOIN, (sock, revents) =>
                            {
                                if ((revents & IOMultiPlex.POLLIN) != IOMultiPlex.POLLIN) return ControlREQState.Nothing;

                                Queue<byte[]> reply = ctl.RecvAll();
                                string resp = Encoding.Unicode.GetString(reply.Dequeue());
                                if (resp != "JOINED")
                                {
                                    return ControlREQState.Nothing;
                                }

                                numChunks = BitConverter.ToInt32(reply.Dequeue(), 0);
                                numBytes = ((numChunks + 7) & ~7) >> 3;
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

                                lock (tarballLock)
                                {
                                    if (tarball != null)
                                    {
                                        tarball.Close();
                                        tarball.Dispose();
                                        tarball = null;
                                    }

                                    // Create the tarball reader that writes the files locally:
                                    tarball = new TarballStreamReader(downloadDirectory, tbes);

                                    // Get our local download state:
                                    naks = getClientState(this, numChunks, chunkSize, tarball);
                                }
                                ackCount = naks.Cast<bool>().Count(b => !b);

                                // Send our NAKs:
                                ctl.SendMore(ctl.Identity);
                                ctl.SendMore("NAKS", Encoding.Unicode);
                                nakBuf = new byte[numBytes];
                                naks.CopyTo(nakBuf, 0);
                                trace("SEND NAK");
                                ctl.Send(nakBuf);
                                nakBuf = null;

                                return ControlREQState.RecvNAKS;
                            }),
                            new ZMQStateMasheen<ControlREQState>.State(ControlREQState.RecvNAKS, (sock, revents) =>
                            {
                                if ((revents & IOMultiPlex.POLLIN) != IOMultiPlex.POLLIN) return ControlREQState.Nothing;

                                Queue<byte[]> packet = ctl.RecvAll();
                                // Don't care what the response is for now.

                                return ControlREQState.Nothing;
                            }),
                            new ZMQStateMasheen<ControlREQState>.State(ControlREQState.SendACK, (sock, revents) =>
                            {
                                if ((revents & IOMultiPlex.POLLOUT) != IOMultiPlex.POLLOUT) return ControlREQState.Nothing;

                                List<int> chunksACKed = (List<int>)tmpControlState;

                                // Send an ACK packet to the control socket:
                                ctl.SendMore(ctl.Identity);
                                ctl.SendMore("ACK", Encoding.Unicode);
                                ctl.SendMore(BitConverter.GetBytes(chunksACKed.Count));
                                byte[] ackBuf = new byte[chunksACKed.Count * 4];
                                for (int i = 0; i < chunksACKed.Count; ++i)
                                {
                                    Array.Copy(BitConverter.GetBytes(chunksACKed[i]), 0, ackBuf, i * 4, 4);
                                }
                                ctl.Send(ackBuf);

                                return ControlREQState.RecvACK;
                            }),
                            new ZMQStateMasheen<ControlREQState>.State(ControlREQState.RecvACK, (sock, revents) =>
                            {
                                if ((revents & IOMultiPlex.POLLIN) != IOMultiPlex.POLLIN) return ControlREQState.Nothing;

                                ctl.RecvAll();
                                return ControlREQState.Nothing;
                            })
                        );

                        // Disk-write acknowledgement poll input handler:
                        pollItems[2].PollInHandler += new PollHandler((Socket sock, IOMultiPlex revents) =>
                        {
                            byte[] idxPkt = sock.RecvAll().Dequeue();

                            int chunkIdx = BitConverter.ToInt32(idxPkt, 0);
                            //Console.WriteLine("Disk write {0} complete", chunkIdx);
                            naks[chunkIdx] = false;
                            ++ackCount;

                            // Notify the host that a chunk was written:
                            if (ChunkWritten != null) ChunkWritten(this, chunkIdx);

                            runningACKs.Add(chunkIdx);

                            // If we ran up the timer, send more ACKs:
                            if (DateTimeOffset.UtcNow.Subtract(lastSentACKs).TotalMilliseconds >= 100d)
                            {
                                lastSentACKs = DateTimeOffset.UtcNow;
                                controlStateQueue.Enqueue(new QueuedControlMessage(ControlREQState.SendACK, new List<int>(runningACKs)));
                                runningACKs.Clear();
                            }

                        });

                        pollItems[0].PollInHandler += new PollHandler(dataFSM.StateMasheen);
                        pollItems[1].PollInHandler += new PollHandler(controlFSM.StateMasheen);
                        pollItems[1].PollOutHandler += new PollHandler(controlFSM.StateMasheen);

                        DateTimeOffset lastRecv = DateTimeOffset.UtcNow;

                        // Create a socket poller for the data socket:
                        while (true)
                        {
                            // If we disposed of the previous control socket, create a new one:
                            if (ctl == null)
                            {
                                trace("Creating new CONTROL socket");
                                // Set up new socket:
                                ctl = ctx.Socket(SocketType.REQ);
                                // Connect to the control request socket:
                                ctl.Connect("tcp://" + device + ":" + (port + 1).ToString());
                                ctl.Identity = new byte[1] { (byte)'@' }.Concat(myIdentity.ToByteArray()).ToArray();
                            }

                            trace("POLL");
                            while ((ctx.Poll(pollItems, 10000) == 1) && (ctl != null))
                            {
                            }

                            // Measure our message send rate per minute:
                            long elapsed = recvTimer.ElapsedMilliseconds - lastElapsedMilliseconds;
                            if (elapsed >= 500L)
                            {
                                ChunksPerMinute = (int)((msgsRecv * 60000L) / elapsed);
                                lastElapsedMilliseconds = recvTimer.ElapsedMilliseconds;
                                msgsRecv = 0;
                            }

                            if (!recvTimer.IsRunning)
                            {
                                recvTimer.Reset();
                                recvTimer.Start();
                                lastElapsedMilliseconds = 0L;
                            }

                            if (ackCount >= numChunks)
                            {
                                Completed = true;
                                disk.Send(cmdExit);
                                break;
                            }
                        }

                        diskWriterThread.Join();

                        // FIXME: this can be the wrong time to attempt Send.
                        ctl.SendMore(ctl.Identity);
                        ctl.Send("LEAVE", Encoding.Unicode);

                        ctl.RecvAll(10000);
                    }
                    finally
                    {
                        if (tarball != null) tarball.Dispose();
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
