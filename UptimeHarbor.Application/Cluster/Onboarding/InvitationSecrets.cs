using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UptimeHarbor.Application.Cluster.Onboarding;

public static class InvitationSecrets
{
    public static Guid CreateCode()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    public static string Verifier(Guid clusterId, long generation, Guid invitationId, Guid code)
    {
        if (code.Version != 4 || (code.ToByteArray(bigEndian: true)[8] & 0xc0) != 0x80)
        {
            throw new ArgumentException("InvalidInvitationCode");
        }

        string source = "UptimeHarbor.Invitation.v1/" + clusterId.ToString("D") + "/"
            + generation.ToString(CultureInfo.InvariantCulture) + "/" + invitationId.ToString("D") + "/" + code.ToString("D");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }

    public static bool Matches(string expected, string actual)
    {
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual));
    }
}
