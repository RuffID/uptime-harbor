using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

public class RemoteGrantSecurityTests
{
    [Theory]
    [InlineData("version")]
    [InlineData("proof")]
    [InlineData("origin")]
    [InlineData("scope")]
    [InlineData("snapshot")]
    [InlineData("epoch")]
    [InlineData("process")]
    [InlineData("operations")]
    [InlineData("actor")]
    [InlineData("expiry")]
    public async Task SignerRejectsAlteredRequestEvenWithAuthenticatedVoter(string mutation)
    {
        // Arrange
        await using RemoteGrantFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.InitializeAsync(token);
        OnlineGrantSigningRequest request = await fixture.Caller.GrantRequests.PrepareAsync(fixture.Grant(), token);
        OnlineGrant grant = request.Grant;
        if (mutation == "version")
        {
            request = request with { Version = 2 };
        }
        else if (mutation == "proof")
        {
            request = request with { ProcessProof = Convert.ToBase64String(new byte[70]) };
        }
        else
        {
            grant = Mutate(grant, mutation);
            // Даже корректная подпись peer key не заменяет accepted authority/scope.
            if (mutation is not ("scope" or "snapshot"))
            {
                using ECDsa key = fixture.Caller.Certificate.GetECDsaPrivateKey()!;
                byte[] signature = key.SignData(OnlineGrantRequestSignatures.Canonical(grant), HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence);
                request = request with { ProcessProof = Convert.ToBase64String(signature) };
            }

            request = request with { Grant = grant };
        }

        RevisionImage before = fixture.Cluster.State.Current!;

        // Act
        Exception? rejected = await Record.ExceptionAsync(() => fixture.Caller.Client.SignGrantAsync(
            fixture.Cluster.Issuer.Options.NodeId, request, token));

        // Assert
        Assert.NotNull(rejected);
        Assert.Same(before, fixture.Cluster.State.Current);
        Assert.Single(fixture.Cluster.First.Store.Issues);
        Assert.Contains(fixture.Cluster.Issuer.Log.Entries, entry => entry.Any(pair => pair.Key == "{OriginalFormat}"
            && pair.Value?.ToString()?.Contains("rejected", StringComparison.Ordinal) == true));
    }

    [Theory]
    [InlineData("majority")]
    [InlineData("revoked-issuer")]
    [InlineData("retired-issuer")]
    [InlineData("revoked-leaf")]
    [InlineData("nonvoter")]
    [InlineData("maintenance")]
    public async Task RetryRequiresFreshAuthorityDespitePreviousSuccessfulSignature(string mutation)
    {
        // Arrange
        await using RemoteGrantFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.InitializeAsync(token);
        OnlineGrant grant = fixture.Grant();
        await fixture.Caller.Router.SignAsync(grant, token);
        RevisionImage image = fixture.Cluster.State.Current!;
        AcceptedEnrollmentIssuer issuer = image.OnboardingContracts!.Issuers![grant.IssuerEpoch];
        if (mutation == "majority")
        {
            fixture.Cluster.Issuer.Native.Majority = false;
        }
        else if (mutation is "revoked-issuer" or "retired-issuer")
        {
            if (mutation == "revoked-issuer")
            {
                issuer = issuer with { RevokedAtUtc = fixture.Cluster.First.Time.Now };
            }
            else
            {
                issuer = issuer with { RetiredAtUtc = fixture.Cluster.First.Time.Now };
            }

            image = image with { OnboardingContracts = image.OnboardingContracts with
            {
                Issuers = image.OnboardingContracts.Issuers.SetItem(issuer.Epoch, issuer)
            } };
        }
        else if (mutation == "revoked-leaf")
        {
            OperationalCredential leaf = image.Operations.Credentials[grant.CredentialId];
            image = image with { Operations = image.Operations with
            {
                Credentials = image.Operations.Credentials.SetItem(leaf.Id, leaf with { RevokedAtUtc = fixture.Cluster.First.Time.Now })
            } };
        }
        else if (mutation == "nonvoter")
        {
            OperationalNode node = image.Operations.Nodes[grant.NodeId];
            image = image with { Operations = image.Operations with
            {
                Nodes = image.Operations.Nodes.SetItem(node.NodeId, node with { Voting = false })
            } };
        }
        else
        {
            image = image with { Operations = image.Operations with { MaintenanceId = Guid.NewGuid() } };
        }

        fixture.Cluster.State.Publish(image);

        // Act
        Exception? rejected = await Record.ExceptionAsync(() => fixture.Caller.Router.SignAsync(grant, token));

        // Assert
        Assert.NotNull(rejected);
        Assert.False(fixture.Cluster.State.Current!.OnboardingContracts!.OnlineGrants!.ContainsKey(grant.GrantId));
        Assert.Single(fixture.Caller.Store.Steps);
        Assert.Single(fixture.Cluster.First.Store.Issues);
    }

    [Fact]
    public async Task CallerRejectsChangedSignerGrantBeforeAcceptedEffects()
    {
        // Arrange
        await using RemoteGrantFixture fixture = new();
        CancellationToken token = TestContext.Current.CancellationToken;
        await fixture.InitializeAsync(token);
        OnlineGrant grant = fixture.Grant();
        fixture.Cluster.EditResponse = (_, bytes) =>
        {
            OnlineGrant signed = StrictOnboardingJson.Decode<OnlineGrant>(bytes);
            return StrictOnboardingJson.Encode(signed with { GrantId = Guid.NewGuid() });
        };

        // Act
        Exception? rejected = await Record.ExceptionAsync(() => fixture.Caller.Router.SignAsync(grant, token));

        // Assert
        Assert.IsType<UnauthorizedAccessException>(rejected);
        Assert.False(fixture.Cluster.State.Current!.OnboardingContracts!.OnlineGrants!.ContainsKey(grant.GrantId));
    }

    private static OnlineGrant Mutate(OnlineGrant grant, string mutation)
    {
        if (mutation == "origin")
        {
            return grant with { NodeId = Guid.NewGuid() };
        }

        if (mutation == "scope")
        {
            return grant with { Purpose = OnlineGrantPurpose.CatchUp };
        }

        if (mutation == "snapshot")
        {
            return grant with { SnapshotDigest = new string('C', 64) };
        }

        if (mutation == "epoch")
        {
            return grant with { IssuerEpoch = Guid.NewGuid() };
        }

        if (mutation == "process")
        {
            return grant with { ProcessEpoch = grant.ProcessEpoch + 1 };
        }

        if (mutation == "operations")
        {
            return grant with { OperationsRevision = grant.OperationsRevision + 1 };
        }

        if (mutation == "actor")
        {
            return grant with { ActorRevision = grant.ActorRevision + 1 };
        }

        return grant with { IssuedAtUtc = grant.IssuedAtUtc.AddMinutes(-2), ExpiresAtUtc = grant.IssuedAtUtc };
    }
}
