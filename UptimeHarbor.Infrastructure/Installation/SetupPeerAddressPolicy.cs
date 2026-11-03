using System.Net;
using System.Net.Sockets;
using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>DNS проверяется при каждом connect, dial закреплён за проверенным IP, TLS сохраняет SNI.</summary>
public class SetupPeerAddressPolicy(InstallationOptions options)
{
    private readonly IPNetwork[] allowed = options.AllowedPeerNetworks.Select(IPNetwork.Parse).ToArray();

    public bool Allows(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        byte[] bytes = address.GetAddressBytes();
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)
            || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.Equals(IPAddress.Parse("fd00:ec2::254"))
            || address.Equals(IPAddress.Parse("100.100.100.200")))
        {
            return false;
        }

        bool privateAddress;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            if (bytes[0] == 0 || bytes[0] == 169 && bytes[1] == 254 || bytes[0] >= 224)
            {
                return false;
            }

            privateAddress = bytes[0] == 10 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 100 && bytes[1] is >= 64 and <= 127;
        }
        else
        {
            privateAddress = (bytes[0] & 0xFE) == 0xFC;
            if (!privateAddress && (bytes[0] & 0xE0) != 0x20)
            {
                return false;
            }
        }

        return !privateAddress || allowed.Any(network => network.Contains(address));
    }

    public void ValidateUri(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) || uri.Scheme != "https"
            || uri.AbsoluteUri != endpoint || uri.AbsolutePath != "/internal/raft" || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.IsLoopback
            || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.IdnHost, out IPAddress? address) && !Allows(address))
        {
            throw new InvalidDataException("PeerAddressPolicyRejected");
        }
    }

    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        RuntimeGenerationContext.Verify();
        IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
        if (addresses.Length == 0 || addresses.Any(address => !Allows(address)))
        {
            throw new HttpRequestException("PeerAddressPolicyRejected");
        }

        Socket socket = new(addresses[0].AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            RuntimeGenerationContext.Verify();
            await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
