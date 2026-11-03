using System.Collections.Immutable;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Предоставляет только mTLS certificate/allowlist на время E4. Общий runtime/peer RPC остаётся закрыт.</summary>
public class EnrollmentProbeSession(InstallationState state, InstallationLease lease, TimeProvider time)
{
    private Session? current;

    public X509Certificate2? Certificate => Current()?.Certificate;
    public EnrollmentDescriptor? Descriptor => Current()?.Descriptor;

    public void Publish(LocalOnboardingAttempt local, X509Certificate2 certificate)
    {
        LocalInstallationProfile profile = state.Current ?? throw new InvalidDataException("InstallationProfileMissing");
        EnrollmentDescriptor descriptor = local.Descriptor ?? throw new InvalidDataException("EnrollmentDescriptorMissing");
        if (local.TrustReference is null || profile.AttemptId != local.AttemptId || profile.Mode != "Join"
            || profile.Phase != Application.Installation.InstallationPhase.LocalPrepared || profile.BootId != lease.BootId
            || descriptor.BootId != lease.BootId || descriptor.BootChallenge != lease.BootChallenge
            || descriptor.ProcessLeaseUntilUtc <= time.GetUtcNow() || descriptor.AdmissionUntilUtc <= time.GetUtcNow()
            || !certificate.HasPrivateKey || Convert.ToBase64String(certificate.RawData) != descriptor.CertificateDer)
        {
            throw new InvalidDataException("EnrollmentProbeSessionInvalid");
        }

        Volatile.Write(ref current, new(profile.RuntimeGeneration, descriptor, certificate, Registry(descriptor),
            time.GetTimestamp()));
    }

    public static RevisionImage Registry(EnrollmentDescriptor descriptor)
    {
        return new(descriptor.Request.ClusterId, descriptor.Request.Generation)
        {
            Operations = new()
            {
                Nodes = descriptor.Nodes.ToImmutableDictionary(node => node.NodeId),
                Credentials = descriptor.Credentials.ToImmutableDictionary(item => item.Id)
            },
            OnboardingContracts = new() { Issuers = descriptor.Issuers.ToImmutableDictionary(item => item.Epoch) }
        };
    }

    public bool Validate(X509Certificate2 certificate)
    {
        Session? session = Current();
        return session is not null && EnrollmentLiveTrust.Validate(session.Image, certificate, time.GetUtcNow());
    }

    public bool Matches(EnrollmentPeerProbe probe)
    {
        EnrollmentDescriptor? descriptor = Descriptor;
        return descriptor is not null && probe.Request == descriptor.Request && probe.BootId == descriptor.BootId
            && probe.BootChallenge == descriptor.BootChallenge && probe.ProcessEpoch == descriptor.ProcessEpoch;
    }

    private Session? Current()
    {
        Session? session = Volatile.Read(ref current);
        LocalInstallationProfile? profile = state.Current;
        return session is not null && profile?.RuntimeGeneration == session.Generation
            && profile.Phase == Application.Installation.InstallationPhase.LocalPrepared && profile.BootId == lease.BootId
            && time.GetElapsedTime(session.Started) < TimeSpan.FromMinutes(2)
            && time.GetUtcNow() < session.Descriptor.ProcessLeaseUntilUtc
            && time.GetUtcNow() < session.Descriptor.AdmissionUntilUtc ? session : null;
    }

    private record Session(long Generation, EnrollmentDescriptor Descriptor, X509Certificate2 Certificate,
        RevisionImage Image, long Started);
}
