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
    public required int OwnershipObservationCount { get; init; }
    public required int OwnershipSameTimeCollisionCount { get; init; }
    public required IReadOnlyList<WvwPositionSample> PositionSamples { get; init; }
}

public sealed record WvwExportV2Document
{
    public required int SchemaVersion { get; init; }
    public required string ParserVersion { get; init; }
    public string? SourceCommit { get; init; }
    public string? ConfigSha256 { get; init; }
    public string? SessionId { get; init; }
    public required int SourceLogCount { get; init; }
    public required IReadOnlyList<WvwExportV2Fight> Fights { get; init; }
}

public sealed record WvwExportV2Fight
{
    public required string FightId { get; init; }
    public required int SourceLogIndex { get; init; }
    public required int SegmentIndex { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public required long DurationMs { get; init; }
    /// <summary>Raw EVTC recording end relative to the same origin as event timestamps.</summary>
    public required long RecordingEndMs { get; init; }
    public required long EventDomainEndMs { get; init; }
    public required bool HealingAvailable { get; init; }
    public ulong? Build { get; init; }
    public required string Outcome { get; init; }
    public required IReadOnlyList<WvwExportV2Actor> Actors { get; init; }
    public required IReadOnlyList<WvwExportEvent> Events { get; init; }
    public required IReadOnlyList<WvwOwnershipInterval> OwnershipIntervals { get; init; }
    public required int OwnershipObservationCount { get; init; }
    public required int OwnershipSameTimeCollisionCount { get; init; }
    public required IReadOnlyList<WvwPositionSample> PositionSamples { get; init; }
}

public sealed record WvwExportV2Actor
{
    public required string ActorId { get; init; }
    public required string Kind { get; init; }
    public string? Name { get; init; }
    public string? Account { get; init; }
    public int? Subgroup { get; init; }
    public string? Profession { get; init; }
    public string? EliteSpec { get; init; }
    public int? SpeciesId { get; init; }
    public required long FirstAwareMs { get; init; }
    public required long LastAwareMs { get; init; }
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

[System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WvwDamageEvent), "damage")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WvwBuffApplyEvent), "buff_apply")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WvwBuffExtensionEvent), "buff_extension")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WvwBuffRemoveAllEvent), "buff_remove_all")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WvwBuffRemoveSingleEvent), "buff_remove_single")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WvwBuffRemoveManualEvent), "buff_remove_manual")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WvwDownEvent), "down")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WvwDeathEvent), "death")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WvwAliveEvent), "alive")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WvwSpawnEvent), "spawn")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WvwDespawnEvent), "despawn")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WvwHealthUpdateEvent), "health_update")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WvwHealingEvent), "healing")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WvwUpEvent), "up")]
[System.Text.Json.Serialization.JsonDerivedType(typeof(WvwCCEvent), "cc")]
public abstract record WvwExportEvent
{
    public int Sequence { get; init; }
    public int TimeMs { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public abstract string Kind { get; }
}

public sealed record WvwDamageEvent : WvwExportEvent
{
    [System.Text.Json.Serialization.JsonIgnore]
    public override string Kind => "damage";
    public string? SourceActorId { get; init; }
    public string? TargetActorId { get; init; }
    public long SkillId { get; init; }
    public long Damage { get; init; }
    public long ShieldDamage { get; init; }
}

public abstract record WvwBuffEvent : WvwExportEvent
{
    public string? SourceActorId { get; init; }
    public string? TargetActorId { get; init; }
    public long BuffId { get; init; }
    public long DurationMs { get; init; }
}

public sealed record WvwBuffApplyEvent : WvwBuffEvent
{
    [System.Text.Json.Serialization.JsonIgnore]
    public override string Kind => "buff_apply";
}

public sealed record WvwBuffExtensionEvent : WvwBuffEvent
{
    [System.Text.Json.Serialization.JsonIgnore]
    public override string Kind => "buff_extension";
}

public abstract record WvwBuffRemovalEvent : WvwBuffEvent
{
    public int RemovedStacks { get; init; }
}

public sealed record WvwBuffRemoveAllEvent : WvwBuffRemovalEvent
{
    [System.Text.Json.Serialization.JsonIgnore]
    public override string Kind => "buff_remove_all";
}

public sealed record WvwBuffRemoveSingleEvent : WvwBuffRemovalEvent
{
    [System.Text.Json.Serialization.JsonIgnore]
    public override string Kind => "buff_remove_single";
}

public sealed record WvwBuffRemoveManualEvent : WvwBuffRemovalEvent
{
    [System.Text.Json.Serialization.JsonIgnore]
    public override string Kind => "buff_remove_manual";
}

public abstract record WvwLifecycleEvent : WvwExportEvent
{
    public string? TargetActorId { get; init; }
}

public sealed record WvwDownEvent : WvwLifecycleEvent
{
    [System.Text.Json.Serialization.JsonIgnore]
    public override string Kind => "down";
}

public sealed record WvwDeathEvent : WvwLifecycleEvent
{
    [System.Text.Json.Serialization.JsonIgnore]
    public override string Kind => "death";
}

public sealed record WvwAliveEvent : WvwLifecycleEvent
{
    [System.Text.Json.Serialization.JsonIgnore]
    public override string Kind => "alive";
}

public sealed record WvwSpawnEvent : WvwLifecycleEvent
{
    [System.Text.Json.Serialization.JsonIgnore]
    public override string Kind => "spawn";
}

public sealed record WvwDespawnEvent : WvwLifecycleEvent
{
    [System.Text.Json.Serialization.JsonIgnore]
    public override string Kind => "despawn";
}

public sealed record WvwHealthUpdateEvent : WvwLifecycleEvent
{
    [System.Text.Json.Serialization.JsonIgnore]
    public override string Kind => "health_update";
    public double HealthPercent { get; init; }
}

public sealed record WvwHealingEvent : WvwExportEvent
{
    [System.Text.Json.Serialization.JsonIgnore]
    public override string Kind => "healing";
    public string? SourceActorId { get; init; }
    public string? TargetActorId { get; init; }
    public long SkillId { get; init; }
    public int Amount { get; init; }
}

public sealed record WvwUpEvent : WvwExportEvent
{
    [System.Text.Json.Serialization.JsonIgnore]
    public override string Kind => "up";
    public string? ActorId { get; init; }
}

public sealed record WvwCCEvent : WvwExportEvent
{
    [System.Text.Json.Serialization.JsonIgnore]
    public override string Kind => "cc";
    public string? SourceActorId { get; init; }
    public string? TargetActorId { get; init; }
    public long SkillId { get; init; }
    public int DurationMs { get; init; }
}

public sealed record WvwOwnershipInterval
{
    public required string AgentId { get; init; }
    public string? OwnerActorId { get; init; }
    public required string OwnerResolution { get; init; }
    public required long StartMs { get; init; }
    public required long EndMs { get; init; }
    public required string StartBasis { get; init; }
    public required string EndBasis { get; init; }
}

public sealed record WvwPositionSample
{
    public required string ActorId { get; init; }
    public required int TimeMs { get; init; }
    public float X { get; init; }
    public float Y { get; init; }
    public float Z { get; init; }
}
