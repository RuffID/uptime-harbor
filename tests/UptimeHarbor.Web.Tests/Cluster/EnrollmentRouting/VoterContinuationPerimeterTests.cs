using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using UptimeHarbor.Web.Cluster;
using UptimeHarbor.Web.Cluster.EnrollmentRouting;
using UptimeHarbor.Web.Controllers;
using UptimeHarbor.Web.Security;
using Xunit;
using Wire = UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal.VoterContinuationWire;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

public class VoterContinuationPerimeterTests
{
    private const string BROWSER = "browser.invalid";
    private const string ROOT = "/api/credential-continuation/v1";

    [Fact]
    public async Task MachineClientWaitsForAuthorizationAndReplaysLostResponsesWithoutNewAcceptedWrites()
    {
        using VoterContinuationHttpAuthority fixture = new(administrator: true, signing: true);
        InstallationState installation = new();
        installation.Publish(fixture.Profile);
        installation.ConfirmCommonOpen(fixture.Profile);
        RuntimeWriteGate writes = new();
        writes.Open(fixture.Profile.RuntimeGeneration);
        await using ServiceProvider services = Provider(installation, fixture);
        InstallationOptions configured = Setup();
        configured.CredentialContinuationOrigin = configured.BrowserOrigin;
        PeerBoundaryMiddleware middleware = new(context => Controller(context).ExchangeAsync(),
            Options.Create(fixture.Options), Options.Create(configured), installation);
        ContinuationHandler handler = new(services, middleware, writes) { LoseResponse = "request" };
        EnrollmentEndpoints endpoints = new(new SetupPeerAddressPolicy(configured));
        using EnrollmentTransport transport = new(handler, endpoints);
        VoterContinuationClient client = new(transport, endpoints, Options.Create(configured), fixture);
        VoterContinuationClient.Intent intent = fixture.ClientIntent();
        using ECDsa key = fixture.OpenPreviousKey();
        CancellationToken token = TestContext.Current.CancellationToken;
        using LocalReplay local = await LocalReplay.OpenAsync(fixture, intent, token);
        Wire.AcceptedSlice initial = await client.ReadAcceptedSliceAsync(local.Evidence, intent, key, token);
        Assert.Null(initial.Requested);
        Assert.Contains("\"Subject\"", handler.SliceJson);
        Assert.DoesNotContain("PasswordHash", handler.SliceJson);
        Assert.DoesNotContain("Operations", handler.SliceJson);
        Assert.DoesNotContain("test-hash", handler.SliceJson);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.RequestAsync(intent, key, token));
        Assert.Equal(1, fixture.Writes);
        CommandResult request = await client.RequestAsync(intent, key, token);
        Wire.AcceptedSlice requested = await client.ReadAcceptedSliceAsync(local.Evidence, intent, key, token);
        Assert.Equal(request, requested.Requested!.Decision);
        Assert.Null(requested.Authorized);
        VoterContinuationStatus pending = await client.ReadStatusAsync(intent, key, token);
        Assert.Equal(request, pending.Request);
        Assert.Null(pending.AuthorizationDigest);
        Assert.Null(pending.Registration);
        Assert.Equal(1, fixture.Writes);

        fixture.Majority = false;
        HttpRequestException unavailable = await Assert.ThrowsAsync<HttpRequestException>(() => client.ReadStatusAsync(intent, key, token));
        Assert.Equal(HttpStatusCode.Forbidden, unavailable.StatusCode);
        Assert.Equal(1, fixture.Writes);
        fixture.Majority = true;

        using (IServiceScope scope = services.CreateScope())
        {
            ClaimsPrincipal principal = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, fixture.ActorId.ToString()),
                new Claim(ClaimTypes.Role, ApplicationRoles.ADMINISTRATOR)], IdentityConstants.ApplicationScheme));
            DefaultHttpContext administrator = new() { User = principal };
            administrator.Features.Set<IAuthenticateResultFeature>(new AuthenticationResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, IdentityConstants.ApplicationScheme))));
            IHttpContextAccessor accessor = services.GetRequiredService<IHttpContextAccessor>();
            accessor.HttpContext = administrator;
            try
            {
                await scope.ServiceProvider.GetRequiredService<VoterContinuationAuthority>().AuthorizeAsync(
                    request.OperationId, request.Digest, VoterContinuationHttpAuthority.PASSWORD, token);
            }
            finally
            {
                accessor.HttpContext = null;
            }
        }

        VoterContinuationStatus authorized = await client.ReadStatusAsync(intent, key, token);
        Assert.NotNull(authorized.AuthorizationDigest);
        handler.LoseResponse = "register";
        await Assert.ThrowsAsync<HttpRequestException>(() => client.RegisterAsync(intent, key, authorized.AuthorizationDigest!, token));
        CommandResult registration = Assert.IsType<CommandResult>((await client.ReadStatusAsync(intent, key, token)).Registration);
        Wire.AcceptedSlice registered = await client.ReadAcceptedSliceAsync(local.Evidence, intent, key, token);
        Assert.Equal(registration, registered.Registered!.Decision);
        Assert.Equal(authorized.AuthorizationDigest, registered.Authorized!.Decision.Digest);
        Assert.Null(registered.Bound);
        Assert.Equal(3, fixture.Writes);

        handler.LoseResponse = "issue";
        await Assert.ThrowsAsync<HttpRequestException>(() => client.IssueAsync(intent, key, registration.Digest, token));
        var leaf = await client.IssueAsync(intent, key, registration.Digest, token);
        Assert.Equal(Assert.Single(fixture.SigningJournal.Issues).Value.CertificateDer, leaf.CertificateDer);
        Assert.Equal(1, fixture.SigningJournal.Saves);

        handler.LoseResponse = "bind";
        await Assert.ThrowsAsync<HttpRequestException>(() => client.BindAsync(intent, key, registration.Digest, leaf.CertificateDigest, token));
        var bound = await client.BindAsync(intent, key, registration.Digest, leaf.CertificateDigest, token);
        Wire.AcceptedSlice confirmed = await client.ReadAcceptedSliceAsync(local.Evidence, intent, key, token);
        Assert.Equal(bound.Decision, confirmed.Bound!.Decision);
        Assert.Equal(leaf.CertificateDer, Convert.FromBase64String(confirmed.Bound.Command.VoterContinuation!.Credential!.PublicCertificate));
        Assert.False(local.Installation.CommonOpen);
        Assert.False(local.State.Available);
        Assert.False(local.Recovery.Accepted.CaughtUp);
        Assert.Equal(0, local.Installation.Transition.CurrentCount);
        Assert.Equal(bound, await client.BindAsync(intent, key, registration.Digest, leaf.CertificateDigest, token));
        VoterContinuationClient.Intent restarted = intent with
        {
            Process = new(Guid.NewGuid(), VoterContinuationHttpAuthority.RandomChallenge(), intent.Process.Epoch + 1,
                intent.Process.RecoveryVersion)
        };
        HttpRequestException otherBoot = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.BindAsync(restarted, key, registration.Digest, leaf.CertificateDigest, token));
        Assert.Equal(HttpStatusCode.Forbidden, otherBoot.StatusCode);
        Assert.Equal(4, fixture.Writes);
        Assert.Equal(1, fixture.SigningJournal.Saves);
    }

    [Theory]
    [InlineData("rollback")]
    [InlineData("wrong-replay-record")]
    [InlineData("wrong-identity")]
    [InlineData("reply-replay")]
    [InlineData("reply-term")]
    [InlineData("reply-recovery")]
    [InlineData("reply-decision")]
    [InlineData("reply-identity")]
    [InlineData("reply-expired")]
    [InlineData("local-recovery")]
    [InlineData("remote-recovery")]
    [InlineData("term")]
    [InlineData("majority")]
    [InlineData("membership")]
    [InlineData("revocation")]
    [InlineData("issuer-revocation")]
    [InlineData("storage")]
    public async Task AcceptedSliceRefusesChangesWithoutOpeningRecovery(string change)
    {
        using VoterContinuationHttpAuthority fixture = new();
        InstallationState installation = new();
        installation.Publish(fixture.Profile);
        installation.ConfirmCommonOpen(fixture.Profile);
        RuntimeWriteGate writes = new();
        writes.Open(fixture.Profile.RuntimeGeneration);
        await using ServiceProvider services = Provider(installation, fixture);
        InstallationOptions configured = Setup();
        configured.CredentialContinuationOrigin = configured.BrowserOrigin;
        PeerBoundaryMiddleware middleware = new(context => Controller(context).ExchangeAsync(),
            Options.Create(fixture.Options), Options.Create(configured), installation);
        ContinuationHandler handler = new(services, middleware, writes);
        EnrollmentEndpoints endpoints = new(new SetupPeerAddressPolicy(configured));
        using EnrollmentTransport transport = new(handler, endpoints);
        VoterContinuationClient client = new(transport, endpoints, Options.Create(configured), fixture);
        VoterContinuationClient.Intent intent = fixture.ClientIntent();
        using ECDsa key = fixture.OpenPreviousKey();
        CancellationToken token = TestContext.Current.CancellationToken;
        using LocalReplay local = await LocalReplay.OpenAsync(fixture, intent, token);
        await client.RequestAsync(intent, key, token);
        Wire.AcceptedSlice accepted = await client.ReadAcceptedSliceAsync(local.Evidence, intent, key, token);
        Guid voterId = fixture.VoterId;
        Guid previousId = intent.Request.Renewal.PreviousCredentialId;
        Guid issuerId = intent.Request.Renewal.Issuance.IssuerEpoch;
        switch (change)
        {
            case "rollback":
                // Remote cut still matches the original replay but is behind the verified request.
                fixture.ChangeImage(_ => local.State.Current!);
                break;
            case "wrong-replay-record":
                fixture.ChangeImage(image => image with { Results = image.Results.Remove(
                    image.Results.Values.Single(result => result.Index == local.State.Current!.Index).OperationId) });
                break;
            case "wrong-identity":
                intent = intent with { AudiencePeerAddress = intent.Request.Renewal.Issuance.PeerUri };
                break;
            case "reply-replay":
                handler.TransformSlice = _ => accepted;
                break;
            case "reply-term":
                handler.TransformSlice = slice => slice with { Term = slice.Term + 1 };
                break;
            case "reply-recovery":
                handler.TransformSlice = slice => slice with { RecoveryVersion = slice.RecoveryVersion + 1 };
                break;
            case "reply-decision":
                handler.TransformSlice = slice => slice with { Requested = slice.Requested! with
                {
                    Decision = slice.Requested.Decision with { Digest = new string('F', 64) }
                } };
                break;
            case "reply-identity":
                handler.TransformSlice = slice => slice with { Subject = slice.Subject with { StorageGeneration = Guid.NewGuid() } };
                break;
            case "reply-expired":
                handler.TransformSlice = slice =>
                {
                    fixture.TimestampOffset = fixture.TimestampFrequency * 10;
                    return slice;
                };
                break;
            case "membership":
                fixture.ChangeImage(image => image with { Operations = image.Operations with
                {
                    Nodes = image.Operations.Nodes.SetItem(voterId, image.Operations.Nodes[voterId] with { RaftMemberId = "other-voter" })
                } });
                break;
            case "local-recovery":
                handler.TransformSlice = slice =>
                {
                    local.State.Invalidate();
                    return slice;
                };
                break;
            default:
                int barriers = 0;
                fixture.BeforeBarrier = () =>
                {
                    if (++barriers == 2)
                    {
                        switch (change)
                        {
                            case "majority": fixture.Majority = false; break;
                            case "term": fixture.Term++; break;
                            case "remote-recovery": fixture.RecoverProjection(); break;
                            case "revocation":
                                fixture.ChangeImage(image => image with { Operations = image.Operations with
                                {
                                    Credentials = image.Operations.Credentials.SetItem(previousId,
                                        image.Operations.Credentials[previousId] with { RevokedAtUtc = fixture.GetUtcNow() })
                                } });
                                break;
                            case "issuer-revocation":
                                fixture.ChangeImage(image => image with { OnboardingContracts = image.OnboardingContracts! with
                                {
                                    Issuers = image.OnboardingContracts.Issuers!.SetItem(issuerId,
                                        image.OnboardingContracts.Issuers[issuerId] with { RevokedAtUtc = fixture.GetUtcNow() })
                                } });
                                break;
                            case "storage":
                                fixture.ChangeImage(image => image with { Operations = image.Operations with
                                {
                                    Nodes = image.Operations.Nodes.SetItem(voterId,
                                        image.Operations.Nodes[voterId] with { StorageGeneration = Guid.NewGuid() })
                                } });
                                break;
                            default: throw new InvalidOperationException(change);
                        }
                    }
                };
                break;
        }

        if (change.StartsWith("reply-", StringComparison.Ordinal) || change is "local-recovery" or "rollback")
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => client.ReadAcceptedSliceAsync(local.Evidence, intent, key, token));
        }
        else
        {
            HttpRequestException error = await Assert.ThrowsAsync<HttpRequestException>(() =>
                client.ReadAcceptedSliceAsync(local.Evidence, intent, key, token));
            Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        }

        Assert.Equal(1, fixture.Writes);
        Assert.False(local.Installation.CommonOpen);
        Assert.False(local.State.Available);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    [InlineData(-1, true)]
    public async Task MachineClientCanSubmitNewBootOnlyAtOrAfterPreviousLeaseExpiry(int secondsUntilExpiry, bool allowed)
    {
        using VoterContinuationHttpAuthority fixture = new();
        VoterContinuationClient.Intent intent = fixture.WithPreviousLease(fixture.GetUtcNow().AddSeconds(secondsUntilExpiry));
        InstallationState installation = new();
        installation.Publish(fixture.Profile);
        installation.ConfirmCommonOpen(fixture.Profile);
        RuntimeWriteGate writes = new();
        writes.Open(fixture.Profile.RuntimeGeneration);
        await using ServiceProvider services = Provider(installation, fixture);
        InstallationOptions configured = Setup();
        configured.CredentialContinuationOrigin = configured.BrowserOrigin;
        PeerBoundaryMiddleware middleware = new(context => Controller(context).ExchangeAsync(),
            Options.Create(fixture.Options), Options.Create(configured), installation);
        EnrollmentEndpoints endpoints = new(new SetupPeerAddressPolicy(configured));
        using EnrollmentTransport transport = new(new ContinuationHandler(services, middleware, writes), endpoints);
        VoterContinuationClient client = new(transport, endpoints, Options.Create(configured), fixture);
        using ECDsa key = fixture.OpenPreviousKey();
        CancellationToken token = TestContext.Current.CancellationToken;

        if (allowed)
        {
            CommandResult request = await client.RequestAsync(intent, key, token);
            VoterContinuationStatus status = await client.ReadStatusAsync(intent, key, token);
            Assert.Equal(request, status.Request);
            Assert.Null(status.Registration);
            Assert.Null(status.AuthorizationDigest);
            Assert.Equal(1, fixture.Writes);
            return;
        }

        HttpRequestException refusal = await Assert.ThrowsAsync<HttpRequestException>(() => client.RequestAsync(intent, key, token));
        Assert.Equal(HttpStatusCode.Forbidden, refusal.StatusCode);
        Assert.Equal(0, fixture.Writes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://browser.invalid")]
    [InlineData("https://browser.invalid/other")]
    [InlineData("https://browser.invalid?query")]
    [InlineData("https://localhost")]
    public async Task MachineClientRejectsMissingOrUnsafeConfiguredOriginBeforeSending(string origin)
    {
        using VoterContinuationHttpAuthority fixture = new();
        InstallationOptions configured = new() { CredentialContinuationOrigin = origin };
        EnrollmentEndpoints endpoints = new(new SetupPeerAddressPolicy(configured));
        using EnrollmentTransport transport = new(new UnusedHandler(), endpoints);
        VoterContinuationClient client = new(transport, endpoints, Options.Create(configured), fixture);
        using ECDsa key = fixture.OpenPreviousKey();
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.RequestAsync(fixture.ClientIntent(), key, TestContext.Current.CancellationToken));
        Assert.Equal(0, fixture.Writes);
    }

    [Fact]
    public async Task ExpiredLeafCanSubmitAndReadItsAcceptedRequestThroughSeparateRoute()
    {
        // Arrange
        using VoterContinuationHttpAuthority fixture = new();
        InstallationState installation = new();
        installation.Publish(fixture.Profile);
        installation.ConfirmCommonOpen(fixture.Profile);
        RuntimeWriteGate writes = new();
        writes.Open(fixture.Profile.RuntimeGeneration);
        await using ServiceProvider services = Provider(installation, fixture);
        PeerBoundaryMiddleware middleware = new(context => Controller(context).ExchangeAsync(),
            Options.Create(fixture.Options), Options.Create(Setup()), installation);

        // Act
        string nonce = await NonceAsync(VoterContinuationAuthority.REQUEST);
        VoterCredentialProofRequest signed = fixture.Sign(nonce);
        DefaultHttpContext decisionContext = await SendAsync("request", signed, StatusCodes.Status200OK);

        // Assert
        CommandResult decision = Decode<CommandResult>(decisionContext);
        Assert.Equal(OperationOutcome.Committed, decision.Outcome);
        await SendAsync("request", signed, StatusCodes.Status403Forbidden);

        // Act
        string statusNonce = await NonceAsync(VoterContinuationAuthority.STATUS);
        DefaultHttpContext statusContext = await SendAsync("status", fixture.Sign(statusNonce), StatusCodes.Status200OK);

        // Assert
        VoterContinuationStatus status = Decode<VoterContinuationStatus>(statusContext);
        Assert.Equal(decision, status.Request);
        Assert.Null(status.AuthorizationDigest);
        Assert.Null(status.Registration);
        Assert.Equal(1, fixture.Writes);

        async Task<string> NonceAsync(string purpose)
        {
            VoterCredentialProofRequest challenge = fixture.Sign(VoterContinuationHttpAuthority.RandomChallenge(), purpose);
            DefaultHttpContext context = await SendAsync("nonce", new { Call = challenge, Purpose = purpose, AuthorizationDigest = (string?)null },
                StatusCodes.Status200OK);
            return Decode<Nonce>(context).Value;
        }

        async Task<DefaultHttpContext> SendAsync<T>(string operation, T input, int expectedStatus)
        {
            using IServiceScope scope = services.CreateScope();
            DefaultHttpContext context = Request(scope.ServiceProvider, ROOT + "/" + operation, StrictOnboardingJson.Encode(input));
            await Invoke(middleware, context, writes);
            Assert.Equal(expectedStatus, context.Response.StatusCode);
            Assert.Empty(context.Items);
            return context;
        }
    }

    [Fact]
    public async Task ExactHttpsIssuanceAndBindRecoverTheSameAcceptedResultsWithFreshProof()
    {
        // Arrange
        using VoterContinuationHttpAuthority fixture = new(administrator: true, signing: true);
        InstallationState installation = new();
        installation.Publish(fixture.Profile);
        installation.ConfirmCommonOpen(fixture.Profile);
        RuntimeWriteGate writes = new();
        writes.Open(fixture.Profile.RuntimeGeneration);
        await using ServiceProvider services = Provider(installation, fixture);
        PeerBoundaryMiddleware middleware = new(context => Controller(context).ExchangeAsync(),
            Options.Create(fixture.Options), Options.Create(Setup()), installation);

        VoterCredentialProofRequest submitted = fixture.Sign(await NonceAsync(VoterContinuationAuthority.REQUEST));
        CommandResult request = Decode<CommandResult>(await SendAsync("request", submitted));
        await SendAsync("issue", new { request.OperationId }, StatusCodes.Status403Forbidden);
        CommandResult approval;
        using (IServiceScope scope = services.CreateScope())
        {
            ClaimsPrincipal principal = new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, fixture.ActorId.ToString()),
                new Claim(ClaimTypes.Role, ApplicationRoles.ADMINISTRATOR)], IdentityConstants.ApplicationScheme));
            DefaultHttpContext administrator = new() { User = principal };
            administrator.Features.Set<IAuthenticateResultFeature>(new AuthenticationResult(AuthenticateResult.Success(
                new AuthenticationTicket(principal, IdentityConstants.ApplicationScheme))));
            IHttpContextAccessor accessor = services.GetRequiredService<IHttpContextAccessor>();
            accessor.HttpContext = administrator;
            try
            {
                VoterContinuationAuthority authority = scope.ServiceProvider.GetRequiredService<VoterContinuationAuthority>();
                approval = await authority.AuthorizeAsync(request.OperationId, request.Digest, VoterContinuationHttpAuthority.PASSWORD,
                    TestContext.Current.CancellationToken);
            }
            finally
            {
                accessor.HttpContext = null;
            }
        }

        VoterCredentialProofRequest register = fixture.Sign(await NonceAsync(VoterContinuationAuthority.REGISTER, approval.Digest));
        CommandResult registration = Decode<CommandResult>(await SendAsync("register", new { Call = register, AuthorizationDigest = approval.Digest }));
        Assert.Equal(OperationOutcome.Committed, registration.Outcome);
        string digest = registration.Digest;

        // Act
        VoterCredentialProofRequest first = fixture.Sign(await NonceAsync(VoterContinuationAuthority.ISSUE, registration: digest));
        var input = new { Call = first, RegistrationDigest = digest };
        IssuedLeaf result = Decode<IssuedLeaf>(await SendAsync("issue", input));
        await SendAsync("issue", input, StatusCodes.Status403Forbidden);
        VoterCredentialProofRequest retry = fixture.Sign(await NonceAsync(VoterContinuationAuthority.ISSUE, registration: digest));
        IssuedLeaf replay = Decode<IssuedLeaf>(await SendAsync("issue", new { Call = retry, RegistrationDigest = digest }));

        // Assert
        Assert.Equal(request.OperationId, result.OperationId);
        Assert.Equal(first.Request.Renewal.Issuance.IssueId, result.IssueId);
        Assert.Equal(result with { CertificateDer = replay.CertificateDer }, replay);
        Assert.Equal(result.CertificateDer, replay.CertificateDer);
        Assert.Equal(EnrollmentCertificates.Hash(result.CertificateDer), result.CertificateDigest);
        Assert.Equal(3, fixture.Writes);
        Assert.Equal(1, fixture.SigningJournal.Saves);
        Assert.Single(fixture.SigningJournal.Issues);

        // Act
        VoterCredentialProofRequest wrongPurpose = fixture.Sign(await NonceAsync(VoterContinuationAuthority.ISSUE, registration: digest));
        await SendAsync("bind", new { Call = wrongPurpose, RegistrationDigest = digest, result.CertificateDigest }, StatusCodes.Status403Forbidden);
        VoterCredentialProofRequest firstBind = fixture.Sign(await NonceAsync(VoterContinuationAuthority.BIND,
            registration: digest, certificate: result.CertificateDigest));
        var bindRequest = new { Call = firstBind, RegistrationDigest = digest, result.CertificateDigest };
        BoundLeaf bound = Decode<BoundLeaf>(await SendAsync("bind", bindRequest));
        await SendAsync("bind", bindRequest, StatusCodes.Status403Forbidden);
        VoterCredentialProofRequest retryBind = fixture.Sign(await NonceAsync(VoterContinuationAuthority.BIND,
            registration: digest, certificate: result.CertificateDigest));
        BoundLeaf recovered = Decode<BoundLeaf>(await SendAsync("bind", bindRequest with { Call = retryBind }));

        // Assert
        Assert.Equal(OperationOutcome.Committed, bound.Decision.Outcome);
        Assert.Equal(result.OperationId, bound.OperationId);
        Assert.Equal(result.IssueId, bound.IssueId);
        Assert.Equal(bound.Decision.Index, bound.CredentialRevision);
        Assert.Equal(bound, recovered);
        Assert.Equal(4, fixture.Writes);
        Assert.Equal(1, fixture.SigningJournal.Saves);

        async Task<string> NonceAsync(string purpose, string? authorization = null, string? registration = null, string? certificate = null)
        {
            string? decisionDigest = authorization ?? registration;
            if (purpose == VoterContinuationAuthority.BIND)
            {
                decisionDigest = VoterContinuationAuthority.BindChallengeDigest(registration, certificate);
            }

            VoterCredentialProofRequest proof = fixture.Sign(VoterContinuationHttpAuthority.RandomChallenge(), purpose,
                decisionDigest: decisionDigest);
            var challenge = new { Call = proof, Purpose = purpose, AuthorizationDigest = authorization, RegistrationDigest = registration,
                CertificateDigest = certificate };
            return Decode<Nonce>(await SendAsync("nonce", challenge)).Value;
        }

        async Task<DefaultHttpContext> SendAsync<T>(string operation, T input, int expected = StatusCodes.Status200OK)
        {
            using IServiceScope scope = services.CreateScope();
            DefaultHttpContext context = Request(scope.ServiceProvider, ROOT + "/" + operation, StrictOnboardingJson.Encode(input));
            await Invoke(middleware, context, writes);
            Assert.Equal(expected, context.Response.StatusCode);
            Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
            if (expected != StatusCodes.Status200OK)
            {
                Assert.Equal(0, context.Response.Body.Length);
            }

            return context;
        }
    }

    [Theory]
    [InlineData("nonce")]
    [InlineData("request")]
    [InlineData("status")]
    [InlineData("register")]
    [InlineData("issue")]
    [InlineData("bind")]
    [InlineData("accepted-slice")]
    public async Task FixedPostGoesThroughExistingPerimeterToController(string operation)
    {
        // Arrange
        var (installation, writes) = Open();
        await using ServiceProvider services = Provider(installation);
        using IServiceScope scope = services.CreateScope();
        DefaultHttpContext context = Request(scope.ServiceProvider, ROOT + "/" + operation);
        int reached = 0;
        PeerBoundaryMiddleware middleware = Perimeter(async current =>
        {
            reached++;
            await Controller(current).ExchangeAsync();
        }, installation);
        Assert.Contains(typeof(VoterContinuationController).GetMethod(nameof(VoterContinuationController.ExchangeAsync))!
            .GetCustomAttributes(typeof(HttpPostAttribute), inherit: false).Cast<HttpPostAttribute>(),
            route => route.Template == context.Request.Path.Value);

        // Act
        await Invoke(middleware, context, writes);

        // Assert
        Assert.Equal(1, reached);
        // Идентификатор сам по себе не является proof; в графе отсутствуют SQL и внешний transport.
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal(0, context.Response.Body.Length);
        Assert.Empty(context.Items);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("wrong-host")]
    [InlineData("peer-port")]
    [InlineData("setup")]
    [InlineData("cookie")]
    [InlineData("authorization")]
    [InlineData("origin")]
    [InlineData("peer-envelope")]
    [InlineData("get")]
    [InlineData("query")]
    [InlineData("legacy")]
    [InlineData("alias")]
    [InlineData("join")]
    public async Task WrongPerimeterCannotReachAnyDownstreamEndpoint(string reason)
    {
        // Arrange
        var (installation, writes) = Open(reason != "setup");
        DefaultHttpContext context = Request(null, ROOT + "/status");
        switch (reason)
        {
            case "plain":
                context.Request.Scheme = "http";
                context.Request.Headers["X-Forwarded-Proto"] = "https";
                break;
            case "wrong-host":
                context.Request.Host = new("different.invalid");
                break;
            case "peer-port":
                context.Connection.LocalPort = 8443;
                break;
            case "cookie":
                context.Request.Headers.Cookie = SetupAuthentication.COOKIE_NAME + "=invalid";
                break;
            case "authorization":
                context.Request.Headers.Authorization = "Bearer invalid";
                break;
            case "origin":
                context.Request.Headers.Origin = "https://" + BROWSER;
                break;
            case "peer-envelope":
                context.Request.Headers[PeerHeaders.ENVELOPE] = "invalid";
                break;
            case "get":
                context.Request.Method = "GET";
                break;
            case "query":
                context.Request.QueryString = new("?id=1");
                break;
            case "alias":
                context.Request.Path = ROOT + "/Status";
                break;
            case "join":
                context.Request.Path = ROOT + "/join";
                break;
        }

        PeerBoundaryMiddleware middleware = Perimeter(_ => throw new InvalidOperationException("Unexpected route"),
            installation, reason != "legacy");
        int expected = StatusCodes.Status403Forbidden;
        if (reason is "alias" or "join")
        {
            expected = StatusCodes.Status404NotFound;
        }

        // Act
        await Invoke(middleware, context, writes);

        // Assert
        Assert.Equal(expected, context.Response.StatusCode);
        Assert.Equal(0, context.Response.Body.Length);
    }

    [Fact]
    public async Task ExpiredPeerStillCannotReachOrdinaryBoundary()
    {
        // Arrange
        var (installation, writes) = Open();
        Guid cluster = Guid.NewGuid();
        Guid audience = Guid.NewGuid();
        Guid peer = Guid.NewGuid();
        Guid storage = Guid.NewGuid();
        Guid credential = Guid.NewGuid();
        TimeProvider time = new FrozenClock();
        ClusterOptions options = new()
        {
            Mode = "Bootstrap", ClusterId = cluster, Generation = 1, NodeId = audience,
            PublicUri = "https://peer.invalid:8443/internal/raft"
        };
        PeerBoundaryMiddleware middleware = new(_ => throw new InvalidOperationException("Unexpected peer route"),
            Options.Create(options), Options.Create(Setup()), installation);
        DefaultHttpContext context = Request(null, "/internal/cluster/v1/commands");
        context.Connection.LocalPort = 8443;
        context.Request.Host = new("peer.invalid", 8443);
        context.Request.ContentLength = 0;
        PeerEnvelope envelope = new(cluster, 1, peer, audience, storage, credential,
            Guid.NewGuid(), time.GetUtcNow(), options.Compatibility, Guid.NewGuid());
        context.Request.Headers[PeerHeaders.ENVELOPE] = PeerHeaders.Encode(envelope);
        NodeReadiness readiness = new(time);
        readiness.Report("Clock", true, "Ready", TimeSpan.FromMinutes(1));
        IPeerTrustSource trust = new ExpiredTrust(new(credential, peer, storage, new("https://origin.invalid/internal/raft"),
            new string('A', 64), true, false, time.GetUtcNow()));

        // Act
        await middleware.InvokeAsync(context, trust, readiness, time, null!, writes: writes);

        // Assert
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Empty(context.Items);
        Assert.False(context.Response.Headers.ContainsKey(PeerHeaders.ENVELOPE));
    }

    [Fact]
    public async Task ControllerWithoutTrustedIngressAndSetupTicketCannotAuthorize()
    {
        // Arrange
        var (installation, _) = Open();
        await using ServiceProvider services = Provider(installation);
        DefaultHttpContext context = Request(services, ROOT + "/status");
        // Act
        await Controller(context).ExchangeAsync();

        // Assert
        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);

        // Arrange
        IHttpContextAccessor actorContext = services.GetRequiredService<IHttpContextAccessor>();
        actorContext.HttpContext = context;
        using IServiceScope scope = services.CreateScope();
        IVoterContinuationAdministrator administrator = scope.ServiceProvider.GetRequiredService<IVoterContinuationAdministrator>();
        Guid actor = Guid.NewGuid();
        ClaimsPrincipal principal = new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, actor.ToString()), new Claim(ClaimTypes.Role, ApplicationRoles.ADMINISTRATOR)],
            IdentityConstants.ApplicationScheme));
        // Act
        context.User = principal;

        // Assert
        Assert.Null(administrator.CommonActorId);

        // Act
        context.Features.Set<IAuthenticateResultFeature>(new AuthenticationResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, SetupAuthentication.SCHEME))));

        // Assert
        Assert.Null(administrator.CommonActorId);

        // Act
        context.Features.Set<IAuthenticateResultFeature>(new AuthenticationResult(AuthenticateResult.Success(
            new AuthenticationTicket(principal, IdentityConstants.ApplicationScheme))));

        // Assert
        Assert.Equal(actor, administrator.CommonActorId);
        actorContext.HttpContext = null;
    }

    internal static ServiceProvider Provider(InstallationState installation, VoterContinuationHttpAuthority? fixture = null)
    {
        ServiceCollection services = new();
        services.AddSingleton(installation);
        services.AddSingleton(TimeProvider.System);
        services.AddHttpContextAccessor();
        services.AddSingleton<EnrollmentSigner>(_ => throw new InvalidOperationException("Signing forbidden"));
        services.AddSingleton<LocalVoterCredentialPublication>(_ => throw new InvalidOperationException("Publication forbidden"));
        fixture?.AddServices(services);
        services.AddEnrollmentPeerRouting();
        services.AddSingleton<IEnrollmentPeerClient>(_ => throw new InvalidOperationException("Peer transport forbidden"));
        services.AddSingleton<IVoterCredentialSignerClient>(_ => throw new InvalidOperationException("Peer signing forbidden"));
        services.AddSingleton<IHttpClientFactory>(_ => throw new InvalidOperationException("Network forbidden"));
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task Invoke(PeerBoundaryMiddleware middleware, HttpContext context, RuntimeWriteGate writes)
    {
        await middleware.InvokeAsync(context, null!, null!, TimeProvider.System, null!, writes: writes);
    }

    private static PeerBoundaryMiddleware Perimeter(RequestDelegate next, InstallationState installation, bool setup = true)
    {
        InstallationOptions configured = new();
        if (setup)
        {
            configured = Setup();
        }

        return new(next, Options.Create(new ClusterOptions()), Options.Create(configured), installation);
    }

    internal static InstallationOptions Setup()
    {
        return new() { InstallationIntent = "Setup", BrowserOrigin = "https://" + BROWSER };
    }

    private static (InstallationState Installation, RuntimeWriteGate Writes) Open(bool commonOpen = true)
    {
        InstallationState installation = new();
        LocalInstallationProfile profile = new(Guid.NewGuid(), "Bootstrap", InstallationPhase.ClusterReady,
            1, 1, 1, Guid.NewGuid(), DateTimeOffset.UnixEpoch);
        installation.Publish(profile);
        if (commonOpen)
        {
            installation.ConfirmCommonOpen(profile);
        }

        RuntimeWriteGate writes = new();
        writes.Open(profile.RuntimeGeneration);
        return (installation, writes);
    }

    private static DefaultHttpContext Request(IServiceProvider? services, string path, byte[]? body = null)
    {
        DefaultHttpContext context = new();
        if (services is not null)
        {
            context.RequestServices = services;
        }

        body ??= Encoding.UTF8.GetBytes("""{"id":"00000000-0000-0000-0000-000000000001"}""");
        context.Request.Method = "POST";
        context.Request.Path = path;
        context.Request.Scheme = "https";
        context.Request.Host = new(BROWSER);
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        context.RequestAborted = TestContext.Current.CancellationToken;
        context.Connection.LocalPort = 443;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static T Decode<T>(HttpContext context)
    {
        return StrictOnboardingJson.Decode<T>(((MemoryStream)context.Response.Body).ToArray());
    }

    private sealed record Nonce(string Value);
    private sealed record IssuedLeaf(Guid OperationId, Guid IssueId, byte[] CertificateDer, string CertificateDigest);
    private sealed record BoundLeaf(Guid OperationId, Guid IssueId, CommandResult Decision, long CredentialRevision);

    internal sealed class ContinuationHandler(ServiceProvider services, PeerBoundaryMiddleware middleware,
        RuntimeWriteGate writes) : HttpMessageHandler
    {
        internal string? LoseResponse { get; set; }
        internal Func<Wire.AcceptedSlice, Wire.AcceptedSlice>? TransformSlice { get; set; }
        internal string? SliceJson { get; private set; }
        internal Action<string>? BeforeSend { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
        {
            Uri uri = Assert.IsType<Uri>(message.RequestUri);
            Assert.Equal("https://" + BROWSER, uri.GetLeftPart(UriPartial.Authority));
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.False(message.Headers.Contains("Cookie"));
            Assert.Null(message.Headers.Authorization);
            Assert.False(message.Headers.Contains(PeerHeaders.ENVELOPE));
            Assert.Equal("application/json", message.Content!.Headers.ContentType!.ToString());
            BeforeSend?.Invoke(uri.AbsolutePath);
            using IServiceScope scope = services.CreateScope();
            DefaultHttpContext context = Request(scope.ServiceProvider, uri.AbsolutePath,
                await message.Content.ReadAsByteArrayAsync(token));
            await Invoke(middleware, context, writes);
            if (LoseResponse is { } operation && uri.AbsolutePath == ROOT + "/" + operation)
            {
                Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
                LoseResponse = null;
                throw new HttpRequestException("ResponseLost");
            }

            byte[] body = ((MemoryStream)context.Response.Body).ToArray();
            if (uri.AbsolutePath == ROOT + "/" + VoterContinuationAuthority.SLICE && context.Response.StatusCode == StatusCodes.Status200OK)
            {
                SliceJson = Encoding.UTF8.GetString(body);
                if (TransformSlice is { } transform)
                {
                    body = StrictOnboardingJson.Encode(transform(StrictOnboardingJson.Decode<Wire.AcceptedSlice>(body)));
                }
            }

            HttpResponseMessage response = new((HttpStatusCode)context.Response.StatusCode)
            {
                Content = new ByteArrayContent(body)
            };
            if (context.Response.ContentType is { } contentType)
            {
                response.Content.Headers.ContentType = new(contentType);
            }

            return response;
        }
    }

    private sealed class UnusedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Unexpected HTTP request");
        }
    }

    internal static VoterContinuationController Controller(HttpContext context)
    {
        return new() { ControllerContext = new ControllerContext { HttpContext = context } };
    }

    internal sealed class AuthenticationResult(AuthenticateResult result) : IAuthenticateResultFeature
    {
        public AuthenticateResult? AuthenticateResult { get; set; } = result;
    }

    private sealed class FrozenClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return DateTimeOffset.UnixEpoch;
        }
    }

    private sealed class ExpiredTrust(PeerCredential credential) : IPeerTrustSource
    {
        public ValueTask<PeerCredential?> FindAsync(Guid credentialId, CancellationToken cancellationToken)
        {
            Assert.Equal(credential.CredentialId, credentialId);
            return ValueTask.FromResult<PeerCredential?>(credential);
        }

        public ValueTask<IReadOnlyList<PeerCredential>> ListAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("Unexpected trust listing");
        }
    }
}
