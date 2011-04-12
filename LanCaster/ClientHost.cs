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

        public void Run(object threadContext)
        {
            Context ctx = (Context)threadContext;
            using (Socket mcast = ctx.Socket(SocketType.SUB))
            {
                mcast.Rate = 2000L;
                mcast.Connect("epgm://239.12.19.82:6642");

                Thread.Sleep(500);
                while (true)
                {
                    Console.WriteLine(mcast.Recv(Encoding.Unicode));
                }
            }
        }
    }
}
