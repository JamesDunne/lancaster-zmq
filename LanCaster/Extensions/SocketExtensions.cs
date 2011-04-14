using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;

namespace ZMQ
{
    public static class SocketExtensions
    {
        public static Queue<byte[]> RecvAll(this Socket sock, int timeout)
        {
            Queue<byte[]> q = new Queue<byte[]>();
            do
            {
                byte[] data = sock.Recv(timeout);
                if (data == null) return null;
                q.Enqueue(data);
            } while (sock.RcvMore);
            return q;
        }
    }
}
