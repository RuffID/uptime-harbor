namespace UptimeHarbor.Domain.Cluster;

public class TargetObservationState : ProjectionRecord
{
    private TargetObservationState()
    {
    }

    public TargetObservationState(Guid targetId, JournalPosition position, Guid checkGeneration,
        AvailabilityState availability, CoverageState coverage, DateTimeOffset evaluationTimeUtc,
        string frontierDigest, string problemDirectionsJson) : base(targetId, position)
    {
        StorageGuards.Id(checkGeneration, nameof(checkGeneration));
        if (!Enum.IsDefined(availability) || !Enum.IsDefined(coverage))
        {
            throw new ArgumentException("Invalid observation state.");
        }

        StorageGuards.Utc(evaluationTimeUtc);
        CheckGeneration = checkGeneration;
        Availability = availability;
        Coverage = coverage;
        EvaluationTimeUtc = evaluationTimeUtc;
        FrontierDigest = StorageGuards.Digest(frontierDigest);
        ProblemDirectionsJson = StorageGuards.Payload(problemDirectionsJson);
    }

    public Guid CheckGeneration { get; private set; }
    public AvailabilityState Availability { get; private set; }
    public CoverageState Coverage { get; private set; }
    public DateTimeOffset EvaluationTimeUtc { get; private set; }
    public string FrontierDigest { get; private set; } = string.Empty;
    public string ProblemDirectionsJson { get; private set; } = string.Empty;
}
