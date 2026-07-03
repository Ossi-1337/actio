namespace Actio.Engine.Runs;

public sealed record WorkflowRunArtifact(
    string JobName,
    string Name,
    string SourcePath,
    string StoredPath,
    int? RetentionDays = null,
    WorkflowRunArtifactAttestation? Attestation = null);

public sealed record WorkflowRunArtifactAttestation(
    string Format,
    string TrustModel,
    string DigestAlgorithm,
    string Digest,
    long TotalBytes,
    int FileCount,
    DateTimeOffset GeneratedAt);
