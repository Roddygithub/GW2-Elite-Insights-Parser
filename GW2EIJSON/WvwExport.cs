namespace GW2EIJSON;

public sealed record WvwExportDocument
{
    public required int SchemaVersion { get; init; }
    public required string ParserVersion { get; init; }
    public string? SourceCommit { get; init; }
    public string? ConfigSha256 { get; init; }
    public string? SessionId { get; init; }
    public required int SourceLogCount { get; init; }
    public required IReadOnlyList<WvwExportFight> Fights { get; init; }
}

public sealed record WvwExportFight
{
    public required string FightId { get; init; }
    public required int SourceLogIndex { get; init; }
    public required int SegmentIndex { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public required long DurationMs { get; init; }
    public ulong? Build { get; init; }
    public required string Outcome { get; init; }
    public required IReadOnlyList<WvwExportActor> Actors { get; init; }
    public required IReadOnlyList<WvwExportEvent> Events { get; init; }
    public required IReadOnlyList<WvwOwnershipInterval> OwnershipIntervals { get; init; }
    public required IReadOnlyList<WvwPositionSample> PositionSamples { get; init; }
}

public sealed record WvwExportActor
{
    public required string ActorId { get; init; }
    public required string Kind { get; init; }
    public string? Name { get; init; }
    public string? Account { get; init; }
    public int? Subgroup { get; init; }
    public string? Profession { get; init; }
    public string? EliteSpec { get; init; }
    public int? InstanceId { get; init; }
    public int? SpeciesId { get; init; }
    public required long FirstAwareMs { get; init; }
    public required long LastAwareMs { get; init; }
}

public sealed record WvwExportEvent
{
    public int Sequence { get; init; }
    public string Kind { get; init; } = "unknown";
    public int TimeMs { get; init; }
    public string? SourceActorId { get; init; }
    public string? TargetActorId { get; init; }
    public long? SkillId { get; init; }
    public long? Damage { get; init; }
    public long? ShieldDamage { get; init; }
    public long? BuffId { get; init; }
    public long? BuffDurationMs { get; init; }
    public int? RemovedStacks { get; init; }
    public double? HealthPercent { get; init; }
}

public sealed record WvwOwnershipInterval
{
    public required string AgentId { get; init; }
    public string? OwnerActorId { get; init; }
    public required long StartMs { get; init; }
    public required long EndMs { get; init; }
    public required string BoundaryBasis { get; init; }
}

public sealed record WvwPositionSample
{
    public required string ActorId { get; init; }
    public required int TimeMs { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
}
