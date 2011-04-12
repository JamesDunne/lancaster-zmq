using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using ZMQ;
using System.Threading;

namespace WellDunne.LanCaster
{
    public sealed class ServerHost
    {
        public ServerHost()
        {
        }

        /// <summary>
        /// Main thread to host the server process.
        /// </summary>
        public void Run(object threadContext)
        {
            Context ctx = (Context)threadContext;
            using (Socket mcast = ctx.Socket(SocketType.PUB))
            {
                mcast.Rate = 2000L;
                mcast.Bind("epgm://239.12.19.82:6642");

                Thread.Sleep(500);
                while (true)
                {
                    mcast.Send("START", Encoding.Unicode);
                }
            }
        }
    }
}
