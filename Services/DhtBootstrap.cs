using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace SneakyFlow.Services;

internal static class DhtBootstrap
{
    // Independent public bootstrap operators, also used by Transmission/libtorrent.
    private static readonly (string Host, int Port)[] Routers =
        [("router.bittorrent.com", 6881), ("dht.transmissionbt.com", 6881), ("dht.libtorrent.org", 25401)];

    internal static async Task<ReadOnlyMemory<byte>[]> ResolveAsync(CancellationToken token)
    {
        var results = await Task.WhenAll(Routers.Select(async router =>
        {
            try
            {
                var addresses = await Dns.GetHostAddressesAsync(router.Host, token);
                return addresses.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).Take(2)
                    .Select(ip => Compact(new IPEndPoint(ip, router.Port))).ToArray();
            }
            catch (SocketException) { return Array.Empty<ReadOnlyMemory<byte>>(); }
            catch (OperationCanceledException) { return Array.Empty<ReadOnlyMemory<byte>>(); }
        }));
        return results.SelectMany(nodes => nodes).ToArray();
    }

    internal static ReadOnlyMemory<byte> Compact(IPEndPoint endpoint)
    {
        var node = new byte[26];
        RandomNumberGenerator.Fill(node.AsSpan(0, 20));
        endpoint.Address.GetAddressBytes().CopyTo(node, 20);
        node[24] = (byte)(endpoint.Port >> 8);
        node[25] = (byte)endpoint.Port;
        return node;
    }
}
