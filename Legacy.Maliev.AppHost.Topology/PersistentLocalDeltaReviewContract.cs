namespace Legacy.Maliev.AppHost.Topology;

/// <summary>
/// Freezes the persistent PostgreSQL boundary used for exact-23 local Aspire review.
/// </summary>
public static class PersistentLocalDeltaReviewContract
{
    /// <summary>Gets the stable, local-only Docker volume used across Aspire review runs.</summary>
    public const string PostgresVolumeName = "legacy-maliev-exact23-postgres-data";

    /// <summary>Gets the exact migrated inventory accepted by the local delta boundary.</summary>
    public static IReadOnlyList<string> Databases => LegacySnapshotReviewContract.MigratedDatabases;

    /// <summary>Gets the only guarded command AppHost is permitted to invoke.</summary>
    public const string ApplyCommand = "apply-delta-local";
}
