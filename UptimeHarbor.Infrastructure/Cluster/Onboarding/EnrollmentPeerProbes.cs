using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Две настоящие TLS-пробы. Ни один результат не является activation/enable или snapshot grant.</summary>
public class EnrollmentPeerProbes(EnrollmentPeerProbeTransport transport, EnrollmentProbeSession session,
    DynamicPeerTls tls, EnrollmentDescriptors descriptors,
    EnrollmentProbeReplay replay, IOptions<InstallationOptions> local, TimeProvider time)
{
    public async Task ReverseAsync(EnrollmentDescriptor descriptor, CancellationToken token)
    {
        RevisionImage image = descriptors.RequireImage();
        OperationalNode from = image.Operations.Nodes.Values.Single(node => node.Address == local.Value.PeerEndpoint && !node.Removed);
        X509Certificate2 certificate = tls.Certificate ?? throw new InvalidDataException("EnrollmentProbeCertificateMissing");
        EnrollmentPeerProbe request = Create(descriptor, from.NodeId, descriptor.Request.NodeId);
        await transport.ProbeAsync(new(descriptor.Request.PeerUri), certificate, image, request, token);
        // Во время probe delegation/credential могли отозвать: повторить accepted checks после await.
        descriptors.Bound(descriptor.Request, descriptor.ResponseProof!);
    }

    public async Task ForwardAsync(LocalOnboardingAttempt localAttempt, EnrollmentTrustInstallation trust, CancellationToken token)
    {
        X509Certificate2 certificate = trust.Read(localAttempt);
        session.Publish(localAttempt, certificate);
        EnrollmentDescriptor descriptor = session.Descriptor ?? throw new InvalidDataException("EnrollmentProbeSessionExpired");
        RevisionImage image = EnrollmentProbeSession.Registry(descriptor);
        OperationalNode to = descriptor.Nodes.Single(node => node.Address == descriptor.Discovery.PeerAddress && !node.Removed);
        EnrollmentPeerProbe probe = Create(descriptor, descriptor.Request.NodeId, to.NodeId);
        await transport.ProbeAsync(new(to.Address), certificate, image, probe, token);
        if (!session.Matches(probe))
        {
            throw new InvalidDataException("EnrollmentProbeSessionExpired");
        }
    }

    public EnrollmentPeerProbeReply Reply(EnrollmentPeerProbe probe, X509Certificate2 certificate)
    {
        if (probe.Version != 1 || probe.RequestId == Guid.Empty || probe.FromNodeId == probe.ToNodeId
            || !EnrollmentProofs.IsChallenge(probe.Challenge))
        {
            throw new InvalidDataException("EnrollmentPeerProbeInvalid");
        }

        RevisionImage image;
        if (session.Descriptor is EnrollmentDescriptor descriptor)
        {
            if (!session.Matches(probe) || probe.ToNodeId != descriptor.Request.NodeId)
            {
                throw new UnauthorizedAccessException("EnrollmentProbeProcessChanged");
            }

            image = EnrollmentProbeSession.Registry(descriptor);
            OperationalNode source = image.Operations.Nodes[probe.FromNodeId];
            if (source.Address != descriptor.Discovery.PeerAddress)
            {
                throw new UnauthorizedAccessException("EnrollmentProbeSourceChanged");
            }
        }
        else
        {
            image = descriptors.RequireImage();
            OnboardingAttempt attempt = image.Onboarding!.Invitations[probe.Request.InvitationId].Attempt
                ?? throw new UnauthorizedAccessException("EnrollmentAttemptMissing");
            descriptors.RequireCurrent(image, attempt, probe.Request);
            if (attempt.BootId != probe.BootId || attempt.BootChallenge != probe.BootChallenge
                || attempt.ProcessEpoch != probe.ProcessEpoch || attempt.ProcessLeaseUntilUtc <= time.GetUtcNow()
                || attempt.AdmissionUntilUtc <= time.GetUtcNow() || probe.FromNodeId != probe.Request.NodeId)
            {
                throw new UnauthorizedAccessException("EnrollmentProbeProcessChanged");
            }
        }

        OperationalNode target = image.Operations.Nodes[probe.ToNodeId];
        string fingerprint = EnrollmentCertificates.Hash(certificate.RawData);
        if (target.Address != local.Value.PeerEndpoint || target.Removed || !EnrollmentLiveTrust.Validate(image, certificate, time.GetUtcNow())
            || !image.Operations.Credentials.Values.Any(item => item.NodeId == probe.FromNodeId && item.Fingerprint == fingerprint)
            || !replay.Accept(probe.RequestId))
        {
            throw new UnauthorizedAccessException("EnrollmentProbePeerRejected");
        }

        return new(1, probe, probe.ToNodeId);
    }

    private static EnrollmentPeerProbe Create(EnrollmentDescriptor descriptor, Guid from, Guid to)
    {
        return new(1, descriptor.Request, Guid.NewGuid(), from, to, descriptor.BootId, descriptor.BootChallenge,
            descriptor.ProcessEpoch, EnrollmentPeerProbeTransport.Challenge());
    }
}
