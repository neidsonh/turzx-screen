// Soma o trafego das placas fisicas. Adaptadores de VPN e virtuais ficam de fora: o mesmo
// trafego ja passa pela placa real e seria contado duas vezes.
using System;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;

namespace TurzxScreen
{
    static class NetStats
    {
        const int RefreshMs = 30000;
        static readonly Regex VirtualName = new Regex(
            "virtual|hyper-v|vmware|vbox|tap-|tap adapter|wireguard|tailscale|wintun|zerotier|npcap|pseudo|miniport",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        static NetworkInterface[] nics;
        static int nicsAt;

        public static string[] CountedNames()
        {
            Refresh();
            var names = new List<string>();
            foreach (var n in nics) names.Add(n.Description);
            return names.ToArray();
        }

        // changed = a lista de placas mudou e a diferenca para a leitura anterior nao vale
        public static void Read(out long rx, out long tx, out bool changed)
        {
            changed = false;
            if (nics == null || unchecked(Environment.TickCount - nicsAt) >= RefreshMs) changed = Refresh();
            rx = 0; tx = 0;
            try
            {
                foreach (var n in nics) { var st = n.GetIPStatistics(); rx += st.BytesReceived; tx += st.BytesSent; }
            }
            catch { nics = null; throw; }
        }

        static bool Refresh()
        {
            var up = new List<NetworkInterface>(); var physical = new List<NetworkInterface>();
            foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (n.OperationalStatus != OperationalStatus.Up || n.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                up.Add(n);
                if (IsPhysical(n)) physical.Add(n);
            }
            var next = (physical.Count > 0 ? physical : up).ToArray();
            bool changed = nics != null && Ids(nics) != Ids(next);
            nics = next; nicsAt = Environment.TickCount;
            return changed;
        }

        static bool IsPhysical(NetworkInterface n)
        {
            int type = (int)n.NetworkInterfaceType;
            return type != 53 && n.NetworkInterfaceType != NetworkInterfaceType.Tunnel && !VirtualName.IsMatch(n.Description);
        }

        static string Ids(NetworkInterface[] list)
        {
            var ids = new List<string>();
            foreach (var n in list) ids.Add(n.Id);
            ids.Sort(StringComparer.Ordinal);
            return string.Join(",", ids.ToArray());
        }
    }
}
