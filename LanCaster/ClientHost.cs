using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ZMQ;
using System.Threading;

namespace WellDunne.LanCaster
{
    public sealed class ClientHost
    {
        public ClientHost()
        {
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
                //data.Rate = 2000L;
                data.Connect("tcp://localhost:12198");
                data.Subscribe("", Encoding.Unicode);

                // Connect to the control request socket:
                ctl.Connect("tcp://localhost:12199");
                ctl.Identity = Guid.NewGuid().ToByteArray();

                Thread.Sleep(500);

                do
                {
                    ctl.SendMore(ctl.Identity);
                    ctl.Send("JOIN", Encoding.Unicode);

                    Queue<byte[]> reply = ctl.RecvAll();

                    byte[] respBody;
                    string resp = Encoding.Unicode.GetString(reply.Dequeue());
                    
                    Console.WriteLine(resp);
                    switch (resp)
                    {
                        case "JOINED":
                            respBody = reply.Dequeue();
                            Console.WriteLine(Convert.ToBase64String(respBody));
                            break;
                        case "OK":
                            break;
                        case "ALREADY_JOINED":
                            Console.WriteLine("Fail!");
                            break;
                        default:
                            break;
                    }

                    //Console.WriteLine(data.Recv(Encoding.Unicode));
                }
                while (false);
            }
        }
    }
}
