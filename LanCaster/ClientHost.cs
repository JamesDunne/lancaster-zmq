using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ZMQ;
using System.Threading;
using System.IO;
using System.Collections;
using System.Diagnostics;

namespace WellDunne.LanCaster
{
    public sealed class ClientHost
    {
        DirectoryInfo downloadDirectory;
        int numChunks;
        TarballStreamReader tarball;

        public ClientHost(DirectoryInfo downloadDirectory)
        {
            this.downloadDirectory = downloadDirectory;
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
                data.Connect("tcp://localhost:12198");
                data.Subscribe("", Encoding.Unicode);

                // Connect to the control request socket:
                ctl.Connect("tcp://localhost:12199");
                ctl.Identity = Guid.NewGuid().ToByteArray();

                Thread.Sleep(500);

                // Send a JOIN request:
                ctl.SendMore(ctl.Identity);
                ctl.Send("JOIN", Encoding.Unicode);

                // Process the reply:
                Queue<byte[]> reply = ctl.RecvAll();

                string resp = Encoding.Unicode.GetString(reply.Dequeue());

                Console.WriteLine(resp);
                switch (resp)
                {
                    case "JOINED":
                        numChunks = BitConverter.ToInt32(reply.Dequeue(), 0);
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
                        tarball = new TarballStreamReader(downloadDirectory, tbes);
                        break;
                    case "ALREADY_JOINED":
                        Console.WriteLine("Fail!");
                        break;
                    default:
                        break;
                }

                // Send a NAK message for all the chunks:
                ctl.SendMore(ctl.Identity);
                ctl.SendMore("NAK", Encoding.Unicode);

                BitArray naks = new BitArray(numChunks, true);
                byte[] nakBuf = new byte[((numChunks + 7) & ~8) >> 3];
                naks.CopyTo(nakBuf, 0);
                Trace.WriteLine("CTL SEND NAK", "client");
                ctl.Send(nakBuf);

                // Wait for the NAK reply:
                Trace.WriteLine("CTL RECV NAK", "client");
                ctl.RecvAll();

                // Create a socket poller for the data socket:
                PollItem[] pollItems = new PollItem[1];
                pollItems[0] = data.CreatePollItem(IOMultiPlex.POLLIN);
                pollItems[0].PollInHandler += new PollHandler((Socket sock, IOMultiPlex mp) =>
                {
                    Queue<byte[]> packet = sock.RecvAll();
                    
                    int chunkIdx = BitConverter.ToInt32(packet.Dequeue(), 0);
                    naks[chunkIdx] = false;
                    Trace.WriteLine(String.Format("RECV {0}", chunkIdx), "client");

                    // Send a NAK packet to the control socket:
                    ctl.SendMore(ctl.Identity);
                    ctl.SendMore("NAK", Encoding.Unicode);
                    naks.CopyTo(nakBuf, 0);
                    Trace.WriteLine("CTL SEND NAK", "client");
                    ctl.Send(nakBuf);
                    
                    // Wait for the reply:
                    Trace.WriteLine("CTL RECV NAK", "client");
                    ctl.RecvAll();
                });

                // Begin the polling loop:
                do
                {
                    Trace.WriteLine("POLL", "client");
                    ctx.Poll(pollItems, 100000);
                }
                while (true);
            }
        }
    }
}
