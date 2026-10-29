using System.Net;
using System.Net.Sockets;

namespace Crawler.Worker.Crawling;

/// <summary>
/// SSRF guard. Used as the <see cref="SocketsHttpHandler.ConnectCallback"/>: the host is resolved here, every
/// address is vetted and the socket connects to a vetted address itself. Because the check runs for every
/// connection (including the ones opened while following redirects) and the vetted address is the one dialled,
/// neither a redirect to an internal host nor DNS rebinding can bypass it.
/// </summary>
public static class SafeAddressPolicy
{
    public static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken);

        var allowed = addresses.Where(IsAllowed).ToArray();
        if (allowed.Length == 0) throw new BlockedAddressException();

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, endpoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>True only for publicly routable unicast addresses.</summary>
    public static bool IsAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicIPv4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicIPv6(address),
            _ => false
        };
    }

    private static bool IsPublicIPv4(byte[] b) => !(
        b[0] == 0                                   // 0.0.0.0/8 "this network" (incl. unspecified)
        || b[0] == 10                               // 10/8 private
        || b[0] == 127                              // loopback
        || (b[0] == 100 && (b[1] & 0xC0) == 64)     // 100.64/10 CGNAT (also Alibaba metadata 100.100.100.200)
        || (b[0] == 169 && b[1] == 254)             // 169.254/16 link-local (incl. 169.254.169.254 metadata)
        || (b[0] == 172 && (b[1] & 0xF0) == 16)     // 172.16/12 private
        || (b[0] == 192 && b[1] == 168)             // 192.168/16 private
        || b[0] >= 224);                            // multicast, reserved, broadcast

    private static bool IsPublicIPv6(IPAddress address)
    {
        if (address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            return false;
        var first = address.GetAddressBytes()[0];
        return (first & 0xFE) != 0xFC;              // fc00::/7 unique local (incl. fd00:ec2::254 metadata)
    }
}

/// <summary>The target resolved only to addresses refused by <see cref="SafeAddressPolicy"/>.</summary>
public sealed class BlockedAddressException() : HttpRequestException("Blocked address");
