using System.Security.Cryptography;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class LocalSecretTests
{
    [Fact]
    public void CiphertextUsesExactInstallAttemptPurposeAndDigestWithoutAdoptingOrphans()
    {
        // Arrange
        using InstallationFixture fixture = new();
        Guid attempt = Guid.NewGuid();
        byte[] secret = "private material is never stored in plaintext"u8.ToArray();

        // Act
        string reference = fixture.Secrets.Write(attempt, "peer", secret);
        string orphan = fixture.Secrets.Write(attempt, "peer", "orphan"u8);
        byte[] restored = fixture.Secrets.Read(attempt, "peer", reference);
        Exception? wrongAttempt = Record.Exception(() => fixture.Secrets.Read(Guid.NewGuid(), "peer", reference));
        Exception? wrongPurpose = Record.Exception(() => fixture.Secrets.Read(attempt, "bootstrap", reference));
        string path = Path.Combine(fixture.Options.KeyDirectory, reference.Split(':')[0] + ".key");
        byte[] stored = File.ReadAllBytes(path);

        // Assert
        Assert.Equal(secret, restored);
        Assert.False(stored.AsSpan().IndexOf(secret) >= 0);
        Assert.IsType<InvalidDataException>(wrongAttempt);
        Assert.IsType<InvalidDataException>(wrongPurpose);
        Assert.NotEqual(reference, orphan);
        Assert.Throws<InvalidDataException>(() => fixture.Secrets.ReconcileAll(null));
        Assert.Null(fixture.State.Current);
        Assert.Equal(32, File.ReadAllBytes(Path.Combine(fixture.Options.SecretDirectory, "wrapping.key")).Length);
        ProtectedInstallationFiles.VerifyPermissions(path);

        // Act
        stored[^1] ^= 1;
        File.WriteAllBytes(path, stored);
        Exception? corrupt = Record.Exception(() => fixture.Secrets.Reconcile(attempt, reference));
        Exception? traversal = Record.Exception(() => fixture.Secrets.Read(attempt, "peer", "../private.key:00"));

        // Assert
        Assert.IsType<InvalidDataException>(corrupt);
        Assert.IsType<InvalidDataException>(traversal);
    }

    [Fact]
    public void MissingWrappingKeyIsNeverRegeneratedOverExistingCredentials()
    {
        // Arrange
        using InstallationFixture fixture = new();
        Guid attempt = Guid.NewGuid();
        string reference = fixture.Secrets.Write(attempt, "peer", "key"u8);
        string wrapping = Path.Combine(fixture.Options.SecretDirectory, "wrapping.key");
        File.Delete(wrapping);

        // Act
        Exception? missing = Record.Exception(() => fixture.Secrets.Read(attempt, "peer", reference));
        Exception? overwrite = Record.Exception(() => fixture.Secrets.Write(attempt, "peer", "new"u8));

        // Assert
        Assert.IsType<InvalidDataException>(missing);
        Assert.IsType<InvalidDataException>(overwrite);
        Assert.False(File.Exists(wrapping));
    }
}
