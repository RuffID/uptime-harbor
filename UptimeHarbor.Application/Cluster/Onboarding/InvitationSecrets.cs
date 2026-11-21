using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace UptimeHarbor.Application.Cluster.Onboarding;

public static class InvitationSecrets
{
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
