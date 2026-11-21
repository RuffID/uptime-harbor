using System.Security.Cryptography;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;
using Xunit;
using static UptimeHarbor.Infrastructure.Installation.Renewal.LocalVoterCredentialFiles;

namespace UptimeHarbor.Tests.Infrastructure.Installation.Renewal;

public class LocalVoterContinuationInstallationTests
{
    [Fact]
    public async Task LostBindResponseRecoversOriginalMaterialAfterReopenAndLeaseExpiry()
    {
        using LocalVoterContinuationInstallationFixture fixture = new();
        await fixture.StageAsync();
        fixture.Scenario.LoseResponse = true;
        await Assert.ThrowsAsync<IOException>(fixture.BindAsync);
        CommandResult bind = fixture.AcceptedBind();
        byte[] material = File.ReadAllBytes(fixture.PathFor("intent"));
        byte[] pin = File.ReadAllBytes(fixture.PathFor(CONTINUATION_BIND_INTENT));
        Assert.False(File.Exists(fixture.PathFor(CONTINUATION_BIND_RESULT)));

        fixture.Reopen();
        fixture.Scenario.Time.Now = fixture.Scenario.Time.Now.AddMinutes(3);
        LocalVoterCredentialInstallation installed = await fixture.Service.RecoverAsync(fixture.Reference, fixture.Token);

        Assert.Equal(installed, await fixture.Service.ReadInstalledAsync(fixture.Reference, fixture.Token));
        Assert.Equal(material, File.ReadAllBytes(fixture.PathFor("intent")));
        Assert.Equal(pin, File.ReadAllBytes(fixture.PathFor(CONTINUATION_BIND_INTENT)));
        fixture.AssertCompleted(installed, bind);
    }

    [Theory]
    [InlineData(CONTINUATION_BIND_RESULT, false)]
    [InlineData(CONTINUATION_BIND_RESULT, true)]
    [InlineData(MANIFEST_STAGE, false)]
    [InlineData(MANIFEST_STAGE, true)]
    [InlineData(REFERENCE_STAGE, false)]
    [InlineData(REFERENCE_STAGE, true)]
    [InlineData(CONFIRMATION_STAGE, false)]
    [InlineData(CONFIRMATION_STAGE, true)]
    public async Task RecoveryCompletesOnlyMissingSuffix(string stage, bool afterWrite)
    {
        using LocalVoterContinuationInstallationFixture fixture = new();
        await fixture.StageAsync();
        CommandResult bind = await fixture.BindAsync();
        fixture.Files.FailStage = stage;
        fixture.Files.AfterWrite = afterWrite;
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.InstallAsync(fixture.Reference, fixture.Receipt(bind), fixture.Token));
        Assert.Equal(afterWrite, File.Exists(fixture.PathFor(stage)));

        string[] stages = [CONTINUATION_BIND_RESULT, MANIFEST_STAGE, REFERENCE_STAGE, CONFIRMATION_STAGE];
        var saved = stages.Where(stage => File.Exists(fixture.PathFor(stage)))
            .ToDictionary(stage => stage, stage => File.ReadAllBytes(fixture.PathFor(stage)));
        fixture.Reopen();
        if (!saved.ContainsKey(CONFIRMATION_STAGE))
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.ReadInstalledAsync(fixture.Reference, fixture.Token));
            Assert.Empty(fixture.Files.Writes);
        }

        LocalVoterCredentialInstallation installed = await fixture.Service.RecoverAsync(fixture.Reference, fixture.Token);

        Assert.Equal(stages.Except(saved.Keys), fixture.Files.Writes);
        foreach ((string name, byte[] original) in saved)
        {
            Assert.Equal(original, File.ReadAllBytes(fixture.PathFor(name)));
        }

        fixture.AssertCompleted(installed, bind);
    }

    [Fact]
    public async Task BindIntentCannotBeRecreatedAfterBind()
    {
        using LocalVoterContinuationInstallationFixture fixture = new();
        await fixture.StageAsync();
        await fixture.BindAsync();
        File.Delete(fixture.PathFor(CONTINUATION_BIND_INTENT));
        fixture.Reopen();

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.PinBindIntentAsync(fixture.Reference,
            fixture.Registration, fixture.Artifact.CertificateDer, fixture.Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.RecoverAsync(fixture.Reference, fixture.Token));
        Assert.Empty(fixture.Files.Writes);
    }

    [Theory]
    [InlineData("intent")]
    [InlineData(CONTINUATION_BIND_INTENT)]
    [InlineData(CONTINUATION_BIND_RESULT)]
    [InlineData(MANIFEST_STAGE)]
    [InlineData(REFERENCE_STAGE)]
    public async Task HoleBelowConfirmedTailFailsClosed(string stage)
    {
        using LocalVoterContinuationInstallationFixture fixture = new();
        await fixture.StageAsync();
        CommandResult bind = await fixture.BindAsync();
        await fixture.Service.InstallAsync(fixture.Reference, fixture.Receipt(bind), fixture.Token);
        File.Delete(fixture.PathFor(stage));
        fixture.Reopen();

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.RecoverAsync(fixture.Reference, fixture.Token));
        Assert.Empty(fixture.Files.Writes);
        Assert.False(File.Exists(fixture.PathFor(stage)));
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("issue")]
    [InlineData("revision")]
    [InlineData("decision")]
    public async Task ReceiptMustEqualTheAcceptedResult(string field)
    {
        using LocalVoterContinuationInstallationFixture fixture = new();
        await fixture.StageAsync();
        var receipt = fixture.Receipt(await fixture.BindAsync());
        receipt = field switch
        {
            "operation" => receipt with { OperationId = Guid.NewGuid() },
            "issue" => receipt with { IssueId = Guid.NewGuid() },
            "revision" => receipt with { CredentialRevision = receipt.CredentialRevision + 1 },
            _ => receipt with { Decision = receipt.Decision with { Digest = new string('A', 64) } }
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.InstallAsync(fixture.Reference, receipt, fixture.Token));
        Assert.False(File.Exists(fixture.PathFor(CONTINUATION_BIND_RESULT)));
    }

    [Theory]
    [InlineData("material")]
    [InlineData("registration")]
    [InlineData("der")]
    [InlineData("key")]
    [InlineData("csr")]
    [InlineData("previous-ref")]
    [InlineData("boot")]
    [InlineData("installed-ref")]
    [InlineData("bind-result")]
    public async Task AlteredProtectedMaterialOrEvidenceCannotConfirm(string field)
    {
        using LocalVoterContinuationInstallationFixture fixture = new();
        await fixture.StageAsync();
        CommandResult bind = await fixture.BindAsync();
        await fixture.Service.InstallAsync(fixture.Reference, fixture.Receipt(bind), fixture.Token);

        if (field is "material" or "registration" or "der")
        {
            fixture.Rewrite<LocalVoterContinuationInstallation.BindIntent>(CONTINUATION_BIND_INTENT, pin => field switch
            {
                "material" => pin with { MaterialDigest = new string('A', 64) },
                "registration" => pin with { Registration = pin.Registration with { Index = pin.Registration.Index - 1 } },
                _ => pin with { Artifact = pin.Artifact with { CertificateDer = [1, 2, 3] } }
            });
        }
        else if (field == "installed-ref")
        {
            fixture.Rewrite<LocalVoterCredentialInstalledReference>(REFERENCE_STAGE, pin => pin with { IssueId = Guid.NewGuid() });
        }
        else if (field == "bind-result")
        {
            fixture.Rewrite<LocalVoterContinuationInstallation.BindResult>(CONTINUATION_BIND_RESULT,
                saved => saved with { Binding = saved.Binding with { Result = saved.Binding.Result with { Index = bind.Index - 1 } } });
        }
        else
        {
            using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            LocalVoterCredentialIntent edited = field switch
            {
                "csr" => fixture.Intent with { CsrDer = Convert.ToBase64String([1, 2, 3]) },
                "previous-ref" => fixture.Intent with { PreviousKeyReference = new string('B', 64) },
                "boot" => fixture.Intent with { Process = fixture.Intent.Process! with { BootId = Guid.NewGuid() } },
                _ => fixture.Intent
            };
            byte[] bundle = LocalVoterCredentialMaterial.Encode(edited, field == "key" ? other : fixture.Scenario.Crypto.NewKey);
            try
            {
                File.Delete(fixture.PathFor("intent"));
                fixture.Files.WriteOnce(fixture.Reference, "intent", bundle);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bundle);
            }
        }

        fixture.Reopen();
        Assert.NotNull(await Record.ExceptionAsync(() => fixture.Service.ReadInstalledAsync(fixture.Reference, fixture.Token)));
        Assert.Empty(fixture.Files.Writes);
        Assert.False(fixture.State.CommonOpen);
    }

    [Theory]
    [InlineData("new-leaf")]
    [InlineData("old-leaf")]
    [InlineData("issuer")]
    [InlineData("process")]
    [InlineData("accepted-revision")]
    [InlineData("storage")]
    [InlineData("majority")]
    public async Task ChangedAcceptedEvidenceCannotRecover(string field)
    {
        using LocalVoterContinuationInstallationFixture fixture = new();
        await fixture.StageAsync();
        CommandResult bind = await fixture.BindAsync();
        RevisionImage image = fixture.Scenario.State;
        OnboardingContractImage contracts = image.OnboardingContracts!;
        EnrollmentIssuance leaf = fixture.Intent.Request.Renewal.Issuance;
        DateTimeOffset now = fixture.Scenario.Time.Now;
        if (field is "new-leaf" or "old-leaf")
        {
            Guid id = field == "new-leaf" ? leaf.IssueId : fixture.Intent.Request.Renewal.PreviousCredentialId;
            var credentials = image.Operations.Credentials;
            image = image with { Operations = image.Operations with { Credentials = credentials.SetItem(id, credentials[id] with { RevokedAtUtc = now }) } };
        }
        else if (field == "issuer")
        {
            var issuers = contracts.Issuers!;
            image = image with { OnboardingContracts = contracts with { Issuers = issuers.SetItem(leaf.IssuerEpoch, issuers[leaf.IssuerEpoch] with { RevokedAtUtc = now }) } };
        }
        else if (field == "process")
        {
            var leases = contracts.OnlineProcesses!;
            image = image with { OnboardingContracts = contracts with { OnlineProcesses = leases.SetItem(leaf.NodeId, leases[leaf.NodeId] with { BootId = Guid.NewGuid() }) } };
        }
        else if (field == "storage")
        {
            var nodes = image.Operations.Nodes;
            image = image with { Operations = image.Operations with { Nodes = nodes.SetItem(leaf.NodeId, nodes[leaf.NodeId] with { StorageGeneration = Guid.NewGuid() }) } };
        }
        else if (field == "accepted-revision")
        {
            image = image with { Results = image.Results.SetItem(bind.OperationId, bind with { AppliedRevision = bind.Index - 1 }) };
        }
        else
        {
            fixture.Scenario.Barrier.Available = false;
        }

        fixture.Scenario.Store.Publish(image);
        Assert.NotNull(await Record.ExceptionAsync(() => fixture.Service.RecoverAsync(fixture.Reference, fixture.Token)));
        Assert.False(File.Exists(fixture.PathFor(CONTINUATION_BIND_RESULT)));
    }

    [Fact]
    public async Task UnboundAndRolledBackCutsCannotConfirm()
    {
        using LocalVoterContinuationInstallationFixture fixture = new();
        await fixture.StageAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.RecoverAsync(fixture.Reference, fixture.Token));
        CommandResult bind = await fixture.BindAsync();
        RevisionImage bound = fixture.Scenario.State;
        fixture.Scenario.Store.Publish(bound with { Index = bound.Index + 1, Digest = new string('B', 64) });
        await fixture.Service.InstallAsync(fixture.Reference, fixture.Receipt(bind), fixture.Token);

        foreach (RevisionImage rolledBack in new[] { fixture.PreBind!, bound, bound with { Index = bound.Index + 1, Digest = new string('C', 64) } })
        {
            fixture.Scenario.Store.Publish(rolledBack);
            await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.ReadInstalledAsync(fixture.Reference, fixture.Token));
        }
    }

    [Fact]
    public async Task RollbackDuringFirstResultWriteRequiresOriginalAcceptedCut()
    {
        using LocalVoterContinuationInstallationFixture fixture = new();
        await fixture.StageAsync();
        CommandResult bind = await fixture.BindAsync();
        RevisionImage bound = fixture.Scenario.State;
        RevisionImage later = bound with { Index = bound.Index + 1, Digest = new string('B', 64) };
        fixture.Scenario.Store.Publish(later);
        fixture.Files.BeforeWrite = stage =>
        {
            if (stage == CONTINUATION_BIND_RESULT)
            {
                fixture.Scenario.Store.Publish(bound);
            }
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.InstallAsync(fixture.Reference, fixture.Receipt(bind), fixture.Token));
        Assert.False(File.Exists(fixture.PathFor(MANIFEST_STAGE)));
        fixture.Reopen();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.RecoverAsync(fixture.Reference, fixture.Token));
        fixture.Scenario.Store.Publish(later);
        fixture.AssertCompleted(await fixture.Service.RecoverAsync(fixture.Reference, fixture.Token), bind);
    }

    [Fact]
    public async Task RevocationDuringConfirmationPreventsSuccess()
    {
        using LocalVoterContinuationInstallationFixture fixture = new();
        await fixture.StageAsync();
        CommandResult bind = await fixture.BindAsync();
        fixture.Files.BeforeWrite = stage =>
        {
            if (stage == CONFIRMATION_STAGE)
            {
                RevisionImage image = fixture.Scenario.State;
                var credentials = image.Operations.Credentials;
                var credential = credentials[fixture.Artifact.IssueId] with { RevokedAtUtc = fixture.Scenario.Time.Now };
                fixture.Scenario.Store.Publish(image with
                {
                    Index = image.Index + 1,
                    Operations = image.Operations with { Credentials = credentials.SetItem(credential.Id, credential) }
                });
            }
        };

        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.InstallAsync(fixture.Reference, fixture.Receipt(bind), fixture.Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Service.ReadInstalledAsync(fixture.Reference, fixture.Token));
        Assert.False(fixture.State.CommonOpen);
    }

    [Fact]
    public async Task ConcurrentRecoveryUsesOnePrefixAndRejectsCompetingIntent()
    {
        using LocalVoterContinuationInstallationFixture fixture = new();
        await fixture.StageAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.PinAsync(fixture.Intent.Request with { OperationId = Guid.NewGuid() }));
        CommandResult bind = await fixture.BindAsync();
        fixture.Reopen();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Scenario.Barrier.Wait = async _ =>
        {
            if (entered.TrySetResult())
            {
                await release.Task.WaitAsync(fixture.Token);
            }
        };

        Task<LocalVoterCredentialInstallation> first = fixture.Service.RecoverAsync(fixture.Reference, fixture.Token);
        await entered.Task.WaitAsync(fixture.Token);
        Task<LocalVoterCredentialInstallation> second = fixture.Service.RecoverAsync(fixture.Reference, fixture.Token);
        Assert.False(second.IsCompleted);
        release.SetResult();
        LocalVoterCredentialInstallation[] results = await Task.WhenAll(first, second);

        Assert.Equal(results[0], results[1]);
        Assert.Equal(new[] { CONTINUATION_BIND_RESULT, MANIFEST_STAGE, REFERENCE_STAGE, CONFIRMATION_STAGE }, fixture.Files.Writes);
        fixture.AssertCompleted(results[0], bind);
    }
}
