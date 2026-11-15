namespace UptimeHarbor.Application.Cluster.Models;

/// <summary>Purpose-bound запрос с отдельной подписью peer key текущего локального процесса.</summary>
public record OnlineGrantSigningRequest(int Version, OnlineGrant Grant, string ProcessProof);
