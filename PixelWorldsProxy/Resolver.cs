using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PixelWorldsProxy
{
    class Resolver
    {
        public static string GetIPFromDNS(string pwserverDNS)
        {
            IPHostEntry hostEntry = Dns.GetHostEntry(pwserverDNS);

            if (hostEntry.AddressList.Length > 0)
            {
                var ip = hostEntry.AddressList[0];
                return ip.ToString();
            }
            return string.Empty;
        }
    }
}
