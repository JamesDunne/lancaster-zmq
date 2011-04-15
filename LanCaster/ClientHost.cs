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

        public delegate BitArray GetClientNAKStateDelegate(ClientHost host, int numChunks, int chunkSize, TarballStreamReader tarball);

        public ClientHost(string endpoint, string subscription, DirectoryInfo downloadDirectory, bool testMode, GetClientNAKStateDelegate getClientState)
        {
            this.endpoint = endpoint;
            this.subscription = subscription;
            this.downloadDirectory = downloadDirectory;
            this.testMode = testMode;
            this.getClientState = getClientState;
            this.Completed = false;
            this.doLogging = new BooleanSwitch("doLogging", "Log client events", "0");
        }

        private GetClientNAKStateDelegate getClientState;
        public event Action<ClientHost, int> ChunkWritten;

        private void trace(string format, params object[] args)
        {
            Trace.WriteLineIf(this.doLogging.Enabled, String.Format(format, args), "client");
        }

        public bool Completed { get; private set; }

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

                    // Begin client logic:

                    TarballStreamReader tarball = null;
                    BitArray naks = null;
                    int numBytes = 0;
                    int ackCount = -1;
                    byte[] nakBuf = null;

                    {
                        // Poll for incoming messages on the data SUB socket:
                        PollItem[] pollItems = new PollItem[2];
                        pollItems[0] = data.CreatePollItem(IOMultiPlex.POLLIN);
                        pollItems[1] = ctl.CreatePollItem(IOMultiPlex.POLLIN | IOMultiPlex.POLLOUT);

                        ZMQStateMasheen<DataSUBState> dataFSM;
                        ZMQStateMasheen<ControlREQState> controlFSM;
                        Queue<ControlREQState> controlStateQueue = new Queue<ControlREQState>();

                        int chunkIdx = -1;

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
                                    chunkIdx = BitConverter.ToInt32(packet.Dequeue(), 0);

                                    // Already received this chunk?
                                    if (!naks[chunkIdx])
                                    {
                                        trace("ALREADY RECV {0}", chunkIdx);
                                        return DataSUBState.Recv;
                                    }

                                    trace("RECV {0}", chunkIdx);

                                    byte[] chunk = packet.Dequeue();
                                    if (!testMode)
                                    {
                                        tarball.Seek((long)chunkIdx * chunkSize, SeekOrigin.Begin);
                                        tarball.Write(chunk, 0, chunk.Length);
                                    }
                                    chunk = null;

                                    naks[chunkIdx] = false;
                                    ++ackCount;
                                    // Notify the host that a chunk was written:
                                    if (ChunkWritten != null) ChunkWritten(this, chunkIdx);

                                    controlStateQueue.Enqueue(ControlREQState.SendACK);
                                    return DataSUBState.Recv;
                                }
                                else if (cmd == "PING")
                                {
                                    controlStateQueue.Enqueue(ControlREQState.SendALIVE);
                                    return DataSUBState.Recv;
                                }
                                else if (cmd == "WHOAREYOU")
                                {
                                    controlStateQueue.Enqueue(ControlREQState.SendJOIN);
                                    return DataSUBState.Recv;
                                }
                                else
                                {
                                    return DataSUBState.Recv;
                                }
                            })
                        );

                        // We create a state machine to handle our send/recv state:
                        controlFSM = new ZMQStateMasheen<ControlREQState>(
                            ControlREQState.SendJOIN,
                            new ZMQStateMasheen<ControlREQState>.State(ControlREQState.Nothing, (sock, revents) =>
                            {
                                if (controlStateQueue.Count > 0) return controlStateQueue.Dequeue();
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

                                // Send a ACK packet to the control socket:
                                ctl.SendMore(ctl.Identity);
                                ctl.SendMore("ACK", Encoding.Unicode);
                                ctl.Send(BitConverter.GetBytes(chunkIdx));

                                return ControlREQState.RecvACK;
                            }),
                            new ZMQStateMasheen<ControlREQState>.State(ControlREQState.RecvACK, (sock, revents) =>
                            {
                                if ((revents & IOMultiPlex.POLLIN) != IOMultiPlex.POLLIN) return ControlREQState.Nothing;

                                ctl.RecvAll();
                                return ControlREQState.Nothing;
                            })
                        );

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
                                ctl.Connect("tcp://" + addr + ":" + (port + 1).ToString());
                                ctl.Identity = new byte[1] { (byte)'@' }.Concat(myIdentity.ToByteArray()).ToArray();
                            }

                            trace("POLL");
                            while ((ctx.Poll(pollItems, 100000) == 1) && (ctl != null))
                            {
                            }

                            if (ackCount >= numChunks)
                            {
                                Completed = true;
                                break;
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
