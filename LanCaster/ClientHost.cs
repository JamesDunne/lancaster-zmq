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

        public ClientHost(string endpoint, string subscription, DirectoryInfo downloadDirectory)
        {
            this.endpoint = endpoint;
            this.subscription = subscription;
            this.downloadDirectory = downloadDirectory;
            this.doLogging = new BooleanSwitch("doLogging", "Log client events", "0");
        }

        private void trace(string format, params object[] args)
        {
            Trace.WriteLineIf(this.doLogging.Enabled, String.Format(format, args), "client");
        }

        /// <summary>
        /// Main thread to host the client process.
        /// </summary>
        /// <param name="threadContext">A ZMQ.Context instance.</param>
        public void Run(object threadContext)
        {
            Context ctx = (Context)threadContext;
            using (Socket
                data = ctx.Socket(SocketType.SUB),
                ctl = ctx.Socket(SocketType.REQ)
            )
            {
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
                    Console.WriteLine("Setting HWM to {0} messages", hwmValue);
                    data.HWM = UInt64.Parse(hwmValue);
                    ctl.HWM = UInt64.Parse(hwmValue);
                }

                data.RcvBuf = ServerHost.ChunkSize * 20L;
                data.Connect("tcp://" + addr + ":" + port.ToString());
                data.Subscribe(subscription, Encoding.Unicode);

                // Connect to the control request socket:
                ctl.Connect("tcp://" + addr + ":" + (port + 1).ToString());
                ctl.Identity = new byte[1] { (byte)'@' }.Concat(Guid.NewGuid().ToByteArray()).ToArray();

                Thread.Sleep(500);

                // Send a JOIN request:
                ctl.SendMore(ctl.Identity);
                ctl.Send("JOIN", Encoding.Unicode);

                // Process the reply:
                Queue<byte[]> reply = ctl.RecvAll();

                string resp = Encoding.Unicode.GetString(reply.Dequeue());

                Console.WriteLine(resp);
                if (resp != "JOINED")
                {
                    Console.WriteLine("Fail!");
                    return;
                }

                numChunks = BitConverter.ToInt32(reply.Dequeue(), 0);
                int numFiles = BitConverter.ToInt32(reply.Dequeue(), 0);

                List<TarballEntry> tbes = new List<TarballEntry>(numFiles);
                for (int i = 0; i < numFiles; ++i)
                {
                    string fiName = Encoding.Unicode.GetString(reply.Dequeue());
                    long length = BitConverter.ToInt64(reply.Dequeue(), 0);
                    Console.WriteLine("{0,15} {1}", length, fiName);
                    byte[] hash = reply.Dequeue();

                    TarballEntry tbe = new TarballEntry(fiName, length, hash);
                    tbes.Add(tbe);
                }

                // Create the tarball reader that writes the files locally:
                using (var tarball = new TarballStreamReader(downloadDirectory, tbes))
                {
                    // Send a NAK message for all the chunks:
                    ctl.SendMore(ctl.Identity);
                    ctl.SendMore("NAK", Encoding.Unicode);

                    BitArray naks = new BitArray(numChunks, true);
                    Console.WriteLine(String.Format("Chunks: {0}", numChunks), "client");
                    int numBytes = ((numChunks + 7) & ~7) >> 3;
                    byte[] nakBuf = new byte[numBytes];
                    naks.CopyTo(nakBuf, 0);
                    trace("CTL SEND NAK");
                    ctl.Send(nakBuf);
                    nakBuf = null;

                    // Wait for the NAK reply:
                    trace("CTL RECV NAK");
                    ctl.RecvAll();

                    bool done = false;

                    // Create a socket poller for the data socket:
                    do
                    {
                        trace("POLL");
                        Queue<byte[]> packet = data.RecvAll();

                        string sub = Encoding.Unicode.GetString(packet.Dequeue());
                        Debug.Assert(sub == this.subscription);
                        int chunkIdx = BitConverter.ToInt32(packet.Dequeue(), 0);
                        // Already received this chunk?
                        if (!naks[chunkIdx])
                        {
                            trace("ALREADY RECV {0}", chunkIdx);
                            return;
                        }

                        trace("RECV {0}", chunkIdx);

                        byte[] chunk = packet.Dequeue();
                        tarball.Seek((long)chunkIdx * ServerHost.ChunkSize, SeekOrigin.Begin);
                        tarball.Write(chunk, 0, chunk.Length);
                        chunk = null;

                        naks[chunkIdx] = false;

                        // Send a NAK packet to the control socket:
                        ctl.SendMore(ctl.Identity);
                        ctl.SendMore("NAK", Encoding.Unicode);
                        nakBuf = new byte[numBytes];
                        naks.CopyTo(nakBuf, 0);
                        trace("CTL SEND NAK");
                        ctl.Send(nakBuf);
                        nakBuf = null;

                        // Wait for the reply:
                        trace("CTL RECV NAK");
                        ctl.RecvAll();

                        // No more NAKed packets?
                        if (naks.Cast<bool>().All(b => !b))
                        {
                            Console.WriteLine("Completed");
                            done = true;
                        }

                        packet = null;
                    }
                    while (!done);

                    Console.WriteLine("Exiting");
                    ctl.SendMore(ctl.Identity);
                    ctl.Send("LEAVE", Encoding.Unicode);

                    ctl.RecvAll();
                }
            }
        }
    }
}
