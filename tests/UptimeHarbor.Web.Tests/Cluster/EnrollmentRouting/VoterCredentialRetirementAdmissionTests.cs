using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Routing;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

public class VoterCredentialRetirementAdmissionTests
{
    [Theory]
    [InlineData("singleton")]
    [InlineData("local-multi")]
    [InlineData("remote-multi")]
    public async Task ExactRetirementCommitsOnceWithTheRequiredVoterEvidence(string mode)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        var (intent, published) = await ArrangeAsync(fixture, token);
        EnrollmentRoutingFixture cluster = fixture.Cluster;
        EnrollmentRoutingNode source = fixture.Issuer;
        if (mode != "remote-multi")
        {
            cluster.SetLeader(source);
        }

        if (mode == "singleton")
        {
            SetSingleton(fixture);
        }
        else if (mode == "remote-multi")
        {
            KeepFollowerWitness(cluster);
            cluster.LoseResponse = EnrollmentPeerProtocol.ROTATION_RETIRE;

            // Act
            await Assert.ThrowsAsync<HttpRequestException>(() => source.RotationClient.RetireAsync(intent, token));

            // Assert
            CommandResult accepted = cluster.State.Current!.Results[intent.Command.OperationId];
            var handoff = Assert.Single(cluster.Sent, exchange => exchange.Path == EnrollmentPeerProtocol.RETIREMENT_HANDOFF
                && exchange.Target == fixture.Subject.Options.NodeId);
            Assert.NotEqual(cluster.Leader.Options.NodeId, handoff.Target);
            Assert.Contains(cluster.Sent, exchange => exchange.Path == EnrollmentPeerProtocol.ROTATION_RETIRE
                && exchange.Target == cluster.Leader.Options.NodeId);
            cluster.BeforeSend = null;
            cluster.Term++;
            cluster.SetLeader(fixture.Subject);

            // Act
            CommandResult replay = await source.RotationClient.RetireAsync(intent, token);

            // Assert
            Assert.Equal(accepted, replay);
        }

        // Act
        CommandResult decision = await source.RotationClient.RetireAsync(intent, token);
        RevisionImage retired = cluster.State.Current!;
        CommandResult repeated = await source.RotationClient.RetireAsync(intent, token);

        // Assert
        Assert.Equal(OperationOutcome.Committed, decision.Outcome);
        Assert.Equal(intent.Command.OperationId, decision.OperationId);
        Assert.Equal(intent.ReadDecision(retired, cluster.First.Time.Now), decision);
        Assert.Equal(decision, repeated);
        Assert.Same(retired, cluster.State.Current);
        Assert.Same(published, source.Tls.Capture());
        Assert.False(EnrollmentLiveTrust.Validate(retired, source.Certificate, cluster.First.Time.Now));
        Assert.True(EnrollmentLiveTrust.Validate(retired, published.Certificate, cluster.First.Time.Now));
        Assert.Equal(mode != "singleton", cluster.Sent.Any(exchange => exchange.Path == EnrollmentPeerProtocol.RETIREMENT_HANDOFF));
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            cluster.Leader.Dispatcher.AcceptAsync(intent.Command, source.Options.NodeId, token));
        Assert.Equal("VoterCredentialRenewalAuthorityUnavailable", refusal.Message);
    }

    [Theory]
    [InlineData("no-evidence")]
    [InlineData("signature")]
    [InlineData("revision")]
    [InlineData("other-operation")]
    [InlineData("caller-stamp")]
    [InlineData("old-witness-term")]
    [InlineData("old-witness-time")]
    [InlineData("changed-membership")]
    [InlineData("subject-as-witness")]
    [InlineData("revoked-witness")]
    [InlineData("expired-witness")]
    public async Task RemoteLeaderRejectsInvalidEvidenceBeforeRecordingAnOperation(string fault)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        var (intent, published) = await ArrangeAsync(fixture, token);
        EnrollmentRoutingFixture cluster = fixture.Cluster;
        KeepFollowerWitness(cluster);
        Action<EnrollmentRoutingNode, string>? skipLeader = cluster.BeforeSend;
        cluster.EditBody = (path, bytes) =>
        {
            if (path != EnrollmentPeerProtocol.ROTATION_RETIRE)
            {
                return bytes;
            }

            var call = StrictOnboardingJson.Decode<VoterCredentialRetirementCall>(bytes);
            VoterCredentialRetirementEvidence evidence = Assert.IsType<VoterCredentialRetirementEvidence>(call.Evidence);
            call = fault switch
            {
                "no-evidence" => call with { Evidence = null },
                "signature" => call with { Evidence = evidence with { Signature = "AA==" } },
                "revision" => call with { Intent = intent with { Handoff = intent.Handoff with { CredentialRevision = 1 } } },
                "other-operation" => call with { Intent = intent with { Command = intent.Command with { OperationId = Guid.NewGuid() } } },
                "caller-stamp" => call with { Intent = intent with { Command = intent.Command with
                {
                    EnrollmentTrust = intent.Command.EnrollmentTrust! with { CoordinatorAtUtc = cluster.First.Time.Now, CoordinatorTerm = cluster.Term }
                } } },
                "old-witness-term" => call with
                {
                    Evidence = (evidence with { Term = evidence.Term - 1 }).Sign(intent, fixture.Subject.Certificate)
                },
                "old-witness-time" => call with
                {
                    Evidence = (evidence with { ConfirmedAtUtc = evidence.ConfirmedAtUtc.AddSeconds(-31) })
                        .Sign(intent, fixture.Subject.Certificate)
                },
                "changed-membership" => call with
                {
                    Evidence = (evidence with { MembershipVersion = evidence.MembershipVersion - 1 })
                        .Sign(intent, fixture.Subject.Certificate)
                },
                "subject-as-witness" => call with { Evidence = (evidence with
                {
                    NodeId = fixture.Issuer.Options.NodeId, StorageGeneration = fixture.Issuer.Options.StorageGeneration,
                    CredentialId = intent.Handoff.IssueId, Fingerprint = intent.Handoff.Fingerprint
                }).Sign(intent, published.Certificate) },
                _ => call
            };
            return StrictOnboardingJson.Encode(call);
        };
        cluster.BeforeSend = (target, path) =>
        {
            skipLeader!(target, path);
            if (path != EnrollmentPeerProtocol.ROTATION_RETIRE)
            {
                return;
            }

            if (fault is "revoked-witness" or "expired-witness")
            {
                RevisionImage image = cluster.State.Current!;
                var credentials = image.Operations.Credentials;
                OperationalCredential witness = credentials[fixture.Subject.Options.CredentialId];
                witness = fault == "revoked-witness" ? witness with { RevokedAtUtc = cluster.First.Time.Now }
                    : witness with { ExpiresAtUtc = cluster.First.Time.Now };
                fixture.Publish(image with { Operations = image.Operations with { Credentials = credentials.SetItem(witness.Id, witness) } });
            }
        };

        // Act
        var refusal = await Assert.ThrowsAsync<HttpRequestException>(() => fixture.Issuer.RotationClient.RetireAsync(intent, token));

        // Assert
        Assert.Equal("EnrollmentPeerUnavailable", refusal.Message);
        Assert.Contains(cluster.Sent, exchange => exchange.Path == EnrollmentPeerProtocol.ROTATION_RETIRE);
        Assert.Null(VoterCredentialPolicy.Latest(cluster.State.Current!, fixture.Issuer.Options.NodeId)!.RetiredIndex);
        Assert.DoesNotContain(intent.Command.OperationId, cluster.State.Current!.Results.Keys);
    }

    [Theory]
    [InlineData("overlap")]
    [InlineData("old-source")]
    [InlineData("revoked-source")]
    [InlineData("expired-source")]
    [InlineData("other-retirement")]
    public async Task RetirementRequiresCurrentSubjectAndElapsedOverlap(string fault)
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        var (intent, _) = await ArrangeAsync(fixture, token);
        EnrollmentRoutingFixture cluster = fixture.Cluster;
        if (fault == "overlap")
        {
            cluster.First.Time.Now = cluster.First.Time.Now.AddTicks(-1);
        }
        else if (fault == "old-source")
        {
            cluster.RenewalIngressLeaf = fixture.Issuer.Certificate;
        }
        else if (fault == "other-retirement")
        {
            await fixture.Issuer.RotationClient.RetireAsync(intent, token);
            intent = intent with { Command = intent.Command with { OperationId = Guid.NewGuid() } };
        }
        else
        {
            RevisionImage image = cluster.State.Current!;
            var credentials = image.Operations.Credentials;
            OperationalCredential subject = credentials[intent.Handoff.IssueId];
            subject = fault == "revoked-source" ? subject with { RevokedAtUtc = cluster.First.Time.Now }
                : subject with { ExpiresAtUtc = cluster.First.Time.Now };
            fixture.Publish(image with { Operations = image.Operations with { Credentials = credentials.SetItem(subject.Id, subject) } });
        }

        RevisionImage before = cluster.State.Current!;

        // Act
        Exception? failure = await Record.ExceptionAsync(() => fixture.Issuer.RotationClient.RetireAsync(intent, token));

        // Assert
        Assert.True(failure is UnauthorizedAccessException or HttpRequestException or InvalidDataException, failure?.ToString());
        Assert.Same(before, cluster.State.Current);
        Assert.DoesNotContain(intent.Command.OperationId, before.Results.Keys);
    }

    [Fact]
    public async Task RetirementUsesTheNextWitnessAfterAConnectionDeadline()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        var (intent, _) = await ArrangeAsync(fixture, token);
        EnrollmentRoutingFixture cluster = fixture.Cluster;
        EnrollmentRoutingNode[] witnesses = cluster.Nodes.Where(node => node != fixture.Issuer).OrderBy(node => node.Options.NodeId).ToArray();
        cluster.SetLeader(witnesses[0]);
        cluster.BeforeSend = (target, path) =>
        {
            if (target == witnesses[0] && path == EnrollmentPeerProtocol.RETIREMENT_HANDOFF)
            {
                throw new OperationCanceledException("TestWitnessConnectionDeadline");
            }
        };

        // Act
        CommandResult result = await fixture.Issuer.RotationClient.RetireAsync(intent, token);

        // Assert
        Assert.Equal(OperationOutcome.Committed, result.Outcome);
        Assert.Equal(witnesses.Select(node => node.Options.NodeId),
            cluster.Sent.Where(exchange => exchange.Path == EnrollmentPeerProtocol.RETIREMENT_HANDOFF).Select(exchange => exchange.Target));
    }

    [Fact]
    public async Task OldExpiryBeforeTwentyFourHoursClosesAccessAndAllowsExactRetirement()
    {
        // Arrange
        CancellationToken token = TestContext.Current.CancellationToken;
        await using VoterCredentialPeerFixture fixture = new();
        var (intent, _) = await ArrangeAsync(fixture, token, expiresWithinOverlap: true);
        SetSingleton(fixture);
        EnrollmentRoutingFixture cluster = fixture.Cluster;
        RevisionImage bound = cluster.State.Current!;
        DateTimeOffset expiry = bound.Operations.Credentials[intent.Command.EnrollmentTrust!.Rotation!.Renewal.PreviousCredentialId]
            .ExpiresAtUtc;
        VoterCredentialRotation rotation = VoterCredentialPolicy.Latest(bound, fixture.Issuer.Options.NodeId)!;
        Assert.Equal(expiry, cluster.First.Time.Now);
        Assert.Equal(expiry, rotation.OverlapUntilUtc);
        Assert.True(expiry < rotation.BoundAtUtc!.Value.AddHours(24));

        // Act
        CommandResult result = await fixture.Issuer.RotationClient.RetireAsync(intent, token);

        // Assert
        Assert.False(EnrollmentLiveTrust.Validate(bound, fixture.Issuer.Certificate, expiry));
        Assert.Equal(OperationOutcome.Committed, result.Outcome);
        Assert.Equal(expiry, VoterCredentialPolicy.Latest(cluster.State.Current!, fixture.Issuer.Options.NodeId)!.RetiredAtUtc);
    }

    private static async Task<(VoterCredentialRetirementIntent, DynamicPeerTls.Snapshot)> ArrangeAsync(
        VoterCredentialPeerFixture fixture, CancellationToken token, bool expiresWithinOverlap = false)
    {
        EnrollmentRoutingFixture cluster = fixture.Cluster;
        cluster.UseManagedPools = true;
        cluster.RotationAdmissionEnabled = true;
        await fixture.InitializeAsync(token);
        DateTimeOffset? theoreticalOverlapEnd = null;
        if (expiresWithinOverlap)
        {
            DateTimeOffset expiry = cluster.State.Current!.Operations.Credentials[fixture.Issuer.Options.CredentialId].ExpiresAtUtc;
            theoreticalOverlapEnd = expiry.AddHours(12);
            cluster.First.Time.Now = expiry.AddHours(-12);
        }

        X509Certificate2 replacement = fixture.BindIssuerReplacement(theoreticalOverlapEnd);
        DynamicPeerTls.Snapshot published = fixture.PublishIssuerReplacement(replacement);
        RevisionImage image = cluster.State.Current!;
        VoterCredentialRotation rotation = VoterCredentialPolicy.Latest(image, fixture.Issuer.Options.NodeId)!;
        cluster.First.Time.Now = rotation.OverlapUntilUtc!.Value;
        foreach (EnrollmentRoutingNode node in cluster.Nodes)
        {
            var profile = node.Installation.Current!;
            if (profile.Mode != "Bootstrap")
            {
                node.Installation.Publish(profile with { Mode = "Bootstrap", ProfileRevision = profile.ProfileRevision + 1 });
            }

            foreach (string component in new[] { "Database", "Projection", "Storage", "Clock" })
            {
                node.Readiness.Report(component, true, "Ready", TimeSpan.FromDays(1));
            }
        }

        cluster.RenewalClientLeaf = replacement;
        cluster.RenewalIngressLeaf = replacement;
        VoterCredentialHandoff handoff = new(rotation.Renewal.Issuance.IssueId, rotation.CredentialRevision,
            rotation.BindDigest!, published.Fingerprint);
        return (VoterCredentialRetirementIntent.Create(image, rotation, handoff, Guid.NewGuid()), published);
    }

    private static void KeepFollowerWitness(EnrollmentRoutingFixture cluster)
    {
        cluster.BeforeSend = (target, path) =>
        {
            if (path == EnrollmentPeerProtocol.RETIREMENT_HANDOFF && target == cluster.Leader)
            {
                throw new HttpRequestException("TestWitnessUnavailable");
            }
        };
    }

    private static void SetSingleton(VoterCredentialPeerFixture fixture)
    {
        EnrollmentRoutingFixture cluster = fixture.Cluster;
        cluster.SetLeader(fixture.Issuer);
        RevisionImage image = cluster.State.Current!;
        var nodes = image.Operations.Nodes;
        foreach (OperationalNode node in nodes.Values.Where(node => node.NodeId != fixture.Issuer.Options.NodeId))
        {
            nodes = nodes.SetItem(node.NodeId, node with { Voting = false });
        }

        fixture.Publish(image with { Operations = image.Operations with { Nodes = nodes } });
    }
}
