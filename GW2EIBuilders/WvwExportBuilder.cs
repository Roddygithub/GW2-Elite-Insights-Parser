using System.Globalization;
using GW2EIEvtcParser;
using GW2EIEvtcParser.EIData;
using GW2EIEvtcParser.ParsedData;
using GW2EIEvtcParser.LogLogic;
using GW2EIJSON;

namespace GW2EIBuilders;

public static class WvwExportBuilder
{
    internal readonly record struct ActorOrderKey(
        long FirstAware,
        long LastAware,
        AgentItem.AgentType Type,
        ushort InstanceId,
        int SpeciesId,
        string Name,
        ulong SourceAgent);

    internal readonly record struct ActorIdAssignment<T>(T Agent, string ActorId);

    internal readonly record struct OwnershipObservationForExport(
        string AgentId,
        long AgentFirstAwareMs,
        long AgentLastAwareMs,
        long ObservedAtMs,
        string? OwnerActorId,
        int SourceOrder);

    internal static List<ActorIdAssignment<T>> AssignActorIds<T>(
        IEnumerable<T> agents,
        Func<T, ActorOrderKey> keySelector)
    {
        return agents.Select(agent => (Agent: agent, Key: keySelector(agent)))
            .OrderBy(x => x.Key.FirstAware)
            .ThenBy(x => x.Key.LastAware)
            .ThenBy(x => x.Key.Type)
            .ThenBy(x => x.Key.InstanceId)
            .ThenBy(x => x.Key.SpeciesId)
            .ThenBy(x => x.Key.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Key.SourceAgent)
            .Select((value, index) => new ActorIdAssignment<T>(value.Agent, $"a{index:D6}"))
            .ToList();
    }

    internal static List<WvwExportEvent> SequenceEvents(IEnumerable<WvwExportEvent> events)
    {
        return events.Select(evt => (
                Event: evt,
                Source: evt switch
                {
                    WvwDamageEvent damageSource => damageSource.SourceActorId,
                    WvwBuffEvent buffSource => buffSource.SourceActorId,
                    _ => null,
                },
                Target: evt switch
                {
                    WvwDamageEvent damageTarget => damageTarget.TargetActorId,
                    WvwBuffEvent buffTarget => buffTarget.TargetActorId,
                    WvwLifecycleEvent lifecycle => lifecycle.TargetActorId,
                    _ => null,
                },
                Skill: evt switch
                {
                    WvwDamageEvent damageSkill => damageSkill.SkillId,
                    WvwBuffEvent buffSkill => buffSkill.BuffId,
                    _ => (long?)null,
                },
                Damage: evt is WvwDamageEvent damageValue ? damageValue.Damage : (long?)null,
                ShieldDamage: evt is WvwDamageEvent shieldValue ? shieldValue.ShieldDamage : (long?)null,
                Duration: evt is WvwBuffEvent buff ? buff.DurationMs : (long?)null,
                Stacks: evt is WvwBuffRemovalEvent removal ? removal.RemovedStacks : (int?)null,
                Health: evt is WvwHealthUpdateEvent health ? health.HealthPercent : (double?)null))
            .OrderBy(x => x.Event.TimeMs)
            .ThenBy(x => x.Event.Kind, StringComparer.Ordinal)
            .ThenBy(x => x.Source, StringComparer.Ordinal)
            .ThenBy(x => x.Target, StringComparer.Ordinal)
            .ThenBy(x => x.Skill)
            .ThenBy(x => x.Damage)
            .ThenBy(x => x.ShieldDamage)
            .ThenBy(x => x.Duration)
            .ThenBy(x => x.Stacks)
            .ThenBy(x => x.Health)
            .Select((value, index) => value.Event with { Sequence = index })
            .ToList();
    }

    internal static List<WvwOwnershipInterval> BuildOwnershipIntervals(
        IEnumerable<OwnershipObservationForExport> source)
    {
        return BuildOwnershipProjection(source).Intervals;
    }

    internal static (List<WvwOwnershipInterval> Intervals, int ObservationCount, int SameTimeCollisionCount)
        BuildOwnershipProjection(IEnumerable<OwnershipObservationForExport> source)
    {
        var sourceList = source.ToList();
        var intervals = new List<WvwOwnershipInterval>();
        int sameTimeCollisions = sourceList.GroupBy(x => (x.AgentId, x.ObservedAtMs))
            .Sum(group => group.Count() - 1);
        foreach (var observations in sourceList.GroupBy(x => x.AgentId, StringComparer.Ordinal))
        {
            var ordered = observations.OrderBy(x => x.ObservedAtMs)
                .ThenBy(x => x.SourceOrder)
                .GroupBy(x => x.ObservedAtMs)
                .Select(group => group.Last())
                .ToList();
            for (int index = 0; index < ordered.Count; index++)
            {
                var observation = ordered[index];
                long endMs = index + 1 < ordered.Count
                    ? ordered[index + 1].ObservedAtMs
                    : observation.AgentLastAwareMs;
                if (endMs <= observation.ObservedAtMs)
                {
                    continue;
                }
                intervals.Add(new WvwOwnershipInterval
                {
                    AgentId = observation.AgentId,
                    OwnerActorId = observation.OwnerActorId,
                    OwnerResolution = observation.OwnerActorId is null ? "unresolved" : "resolved",
                    StartMs = observation.ObservedAtMs,
                    EndMs = endMs,
                    StartBasis = "master_observation",
                    EndBasis = index + 1 < ordered.Count ? "master_observation" : "agent_awareness",
                });
            }
        }
        return (intervals.OrderBy(x => x.AgentId, StringComparer.Ordinal)
            .ThenBy(x => x.StartMs)
            .ThenBy(x => x.OwnerActorId, StringComparer.Ordinal)
            .ToList(), sourceList.Count, sameTimeCollisions);
    }

    public static WvwExportDocument Build(ParsedEvtcLog log, Version parserVersion)
    {
        if (!log.ParserSettings.DetailedWvWParse || log.LogData.Logic.ParseMode != LogLogic.ParseModeEnum.WvW)
        {
            throw new InvalidDataException("WvW export requires a detailed-WvW parse.");
        }

        var actorAssignments = AssignActorIds(
            Enum.GetValues<AgentItem.AgentType>().SelectMany(log.AgentData.GetAgentByType).Distinct(),
            agent => new ActorOrderKey(
                agent.FirstAware,
                agent.LastAware,
                agent.Type,
                agent.InstID,
                agent.ID,
                agent.Name,
                agent.Agent));
        var agents = actorAssignments.Select(x => x.Agent).ToList();
        var actorIds = actorAssignments.ToDictionary(x => x.Agent, x => x.ActorId);
        var agentByActorId = actorAssignments.ToDictionary(x => x.ActorId, x => x.Agent, StringComparer.Ordinal);
        string? ActorId(AgentItem agent, long time)
        {
            if (agent.IsUnknown)
            {
                return null;
            }
            if (actorIds.TryGetValue(agent, out string? actorId) &&
                agent.FirstAware <= time && time <= agent.LastAware)
            {
                return actorId;
            }
            AgentItem? matchingSlice = agents
                .Where(candidate => candidate.Agent == agent.Agent && candidate.InstID == agent.InstID)
                .Where(candidate => candidate.FirstAware <= time && candidate.LastAware >= time)
                .OrderBy(candidate => candidate.LastAware - candidate.FirstAware)
                .ThenBy(candidate => candidate.FirstAware)
                .FirstOrDefault();
            return matchingSlice == null ? null : actorIds[matchingSlice];
        }

        var players = log.PlayerList.ToDictionary(x => x.AgentItem, x => x);
        var actorDtos = agents.Select(agent =>
        {
            players.TryGetValue(agent, out var player);
            var splitName = agent.Name.Split('\0');
            string? name = player?.Character ?? (splitName.Length > 1 ? splitName[0] : agent.Name);
            string? account = player?.Account;
            if (account is null && splitName.Length > 1 && splitName[1].Length > 0)
            {
                account = splitName[1].TrimStart(':');
            }
            int? subgroup = player?.Group;
            if (subgroup is null && splitName.Length > 2 && int.TryParse(splitName[2], out int group))
            {
                subgroup = group;
            }
            var duration = log.LogData.LogDuration;
            return new WvwExportActor
            {
                ActorId = actorIds[agent],
                Kind = agent.IsPlayer ? "player" : agent.IsNPC ? "npc" : "gadget",
                Name = string.IsNullOrEmpty(name) ? null : name,
                Account = string.IsNullOrEmpty(account) ? null : account,
                Subgroup = subgroup,
                Profession = agent.IsPlayer && agent.BaseSpec != ParserHelper.Spec.Unknown
                    ? agent.BaseSpec.ToString()
                    : null,
                EliteSpec = agent.IsPlayer && agent.Spec != ParserHelper.Spec.Unknown && agent.Spec != agent.BaseSpec
                    ? agent.Spec.ToString()
                    : null,
                InstanceId = agent.InstID == 0 ? null : agent.InstID,
                SpeciesId = agent.ID < 0 ? null : agent.ID,
                FirstAwareMs = RelativeTime(agent.FirstAware, log),
                LastAwareMs = RelativeTime(agent.LastAware, log),
            };
        }).ToList();

        var events = new List<WvwExportEvent>();
        var seenBuffEvents = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (AgentItem agent in agents)
        {
            foreach (HealthDamageEvent damage in log.CombatData.GetDamageData(agent))
            {
                events.Add(CreateDamageEvent(
                    Time(damage.Time, log),
                    ActorId(damage.From, damage.Time),
                    ActorId(damage.To, damage.Time),
                    damage.SkillID,
                    Math.Max(0, damage.HealthDamage),
                    Math.Max(0, damage.ShieldDamage)));
            }

            foreach (BuffEvent buff in log.CombatData.GetBuffDataByDst(agent))
            {
                if (IsExportableBuffEvent(buff) && TryAddBuffEventOnce(seenBuffEvents, buff))
                {
                    events.Add(BuffEventDto(buff, log, ActorId));
                }
            }

            AddStatus(log.CombatData.GetDownEvents(agent), "down", log, ActorId, events);
            AddStatus(log.CombatData.GetDeadEvents(agent), "death", log, ActorId, events);
            AddStatus(log.CombatData.GetAliveEvents(agent), "alive", log, ActorId, events);
            AddStatus(log.CombatData.GetSpawnEvents(agent), "spawn", log, ActorId, events);
            AddStatus(log.CombatData.GetDespawnEvents(agent), "despawn", log, ActorId, events);
            foreach (HealthUpdateEvent health in log.CombatData.GetHealthUpdateEvents(agent))
            {
                events.Add(CreateLifecycleEvent(
                    "health_update",
                    Time(health.Time, log),
                    ActorId(health.Src, health.Time),
                    health.HealthPercent));
            }
        }

        events = SequenceEvents(events);

        var ownershipObservations = new List<OwnershipObservationForExport>();
        foreach (AgentItem agent in agents)
        {
            if (!actorIds.TryGetValue(agent, out string? agentId))
            {
                continue;
            }
            for (int index = 0; index < agent.OwnershipObservations.Count; index++)
            {
                var observation = agent.OwnershipObservations[index];
                ownershipObservations.Add(new OwnershipObservationForExport(
                    agentId,
                    RelativeTime(agent.FirstAware, log),
                    RelativeTime(agent.LastAware, log),
                    RelativeTime(observation.Time, log),
                    ActorId(observation.Owner, observation.Time),
                    index));
            }
        }
        var ownershipProjection = BuildOwnershipProjection(ownershipObservations);
        var intervals = ownershipProjection.Intervals;

        var positions = new List<WvwPositionSample>();
        if (log.CanCombatReplay)
        {
            foreach (AgentItem agent in agents)
            {
                foreach (PositionEvent position in log.CombatData.GetMovementData(agent).OfType<PositionEvent>())
                {
                    var point = position.GetPoint3D();
                    var actorId = ActorId(position.Src, position.Time);
                    if (actorId != null)
                    {
                        positions.Add(CreatePositionSample(
                            actorId, Time(position.Time, log), point.X, point.Y, point.Z));
                    }
                }
            }
        }
        positions = positions.OrderBy(x => x.TimeMs)
            .ThenBy(x => x.ActorId, StringComparer.Ordinal)
            .ThenBy(x => x.X)
            .ThenBy(x => x.Y)
            .ThenBy(x => x.Z)
            .ToList();

        var referencedActorIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (WvwExportEvent evt in events)
        {
            string? sourceId = evt switch
            {
                WvwDamageEvent damage => damage.SourceActorId,
                WvwBuffEvent buff => buff.SourceActorId,
                _ => null,
            };
            string? targetId = evt switch
            {
                WvwDamageEvent damage => damage.TargetActorId,
                WvwBuffEvent buff => buff.TargetActorId,
                WvwLifecycleEvent lifecycle => lifecycle.TargetActorId,
                _ => null,
            };
            if (sourceId != null) referencedActorIds.Add(sourceId);
            if (targetId != null) referencedActorIds.Add(targetId);
        }
        referencedActorIds.UnionWith(positions.Select(x => x.ActorId));
        foreach (WvwOwnershipInterval interval in intervals)
        {
            referencedActorIds.Add(interval.AgentId);
            if (interval.OwnerActorId != null) referencedActorIds.Add(interval.OwnerActorId);
        }
        actorDtos = actorDtos.Where(actor =>
            !agentByActorId[actor.ActorId].IsFake || referencedActorIds.Contains(actor.ActorId)).ToList();

        DateTimeOffset? startedAt = DateTimeOffset.TryParse(
            log.LogMetadata.DateStartStd,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var parsedStart) ? parsedStart : null;
        return new WvwExportDocument
        {
            SchemaVersion = 1,
            ParserVersion = parserVersion.ToString(),
            SourceCommit = null,
            ConfigSha256 = null,
            SessionId = null,
            SourceLogCount = 1,
            Fights =
            [
                new WvwExportFight
                {
                    FightId = "log-0000/fight-0000",
                    SourceLogIndex = 0,
                    SegmentIndex = 0,
                    StartedAt = startedAt,
                    DurationMs = log.LogData.LogDuration,
                    Build = log.LogMetadata.GW2Build,
                    Outcome = "unknown",
                    Actors = actorDtos,
                    Events = events,
                    OwnershipIntervals = intervals,
                    OwnershipObservationCount = ownershipProjection.ObservationCount,
                    OwnershipSameTimeCollisionCount = ownershipProjection.SameTimeCollisionCount,
                    PositionSamples = positions,
                },
            ],
        };
    }

    private static int Time(long time, ParsedEvtcLog log) => checked((int)RelativeTime(time, log));

    private static long RelativeTime(long time, ParsedEvtcLog log) => time - log.LogData.LogStart;

    internal static WvwDamageEvent CreateDamageEvent(
        int timeMs,
        string? sourceActorId,
        string? targetActorId,
        long skillId,
        long damage,
        long shieldDamage) => new()
    {
        TimeMs = timeMs,
        SourceActorId = sourceActorId,
        TargetActorId = targetActorId,
        SkillId = skillId,
        Damage = damage,
        ShieldDamage = shieldDamage,
    };

    internal static bool TryAddBuffEventOnce(HashSet<object> seen, object buff) => seen.Add(buff);

    internal static bool IsExportableBuffEvent(BuffEvent buff) =>
        IsExportableBuffEventType(buff.GetType(), buff.BuffID);

    internal static bool IsExportableBuffEventType(Type type, long buffId) =>
        !typeof(BuffStackEvent).IsAssignableFrom(type) && buffId != GW2EIEvtcParser.SkillIDs.NoBuff;

    internal static WvwExportEvent CreateLifecycleEvent(
        string kind,
        int timeMs,
        string? targetActorId,
        double healthPercent = 0) => kind switch
    {
        "down" => new WvwDownEvent { TimeMs = timeMs, TargetActorId = targetActorId },
        "death" => new WvwDeathEvent { TimeMs = timeMs, TargetActorId = targetActorId },
        "alive" => new WvwAliveEvent { TimeMs = timeMs, TargetActorId = targetActorId },
        "spawn" => new WvwSpawnEvent { TimeMs = timeMs, TargetActorId = targetActorId },
        "despawn" => new WvwDespawnEvent { TimeMs = timeMs, TargetActorId = targetActorId },
        "health_update" => new WvwHealthUpdateEvent
        {
            TimeMs = timeMs,
            TargetActorId = targetActorId,
            HealthPercent = healthPercent,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported lifecycle kind"),
    };

    internal static WvwPositionSample CreatePositionSample(
        string actorId, int timeMs, float x, float y, float z) => new()
    {
        ActorId = actorId,
        TimeMs = timeMs,
        X = x,
        Y = y,
        Z = z,
    };

    private static WvwExportEvent BuffEventDto(
        BuffEvent buff,
        ParsedEvtcLog log,
        Func<AgentItem, long, string?> actorId)
    {
        int timeMs = Time(buff.Time, log);
        string? sourceActorId = actorId(buff.By, buff.Time);
        string? targetActorId = actorId(buff.To, buff.Time);
        switch (buff)
        {
            case BuffApplyEvent apply:
                return CreateBuffEvent("buff_apply", timeMs, sourceActorId, targetActorId,
                    buff.BuffID, Math.Max(0, apply.AppliedDuration));
            case BuffExtensionEvent extension:
                return CreateBuffEvent("buff_extension", timeMs, sourceActorId, targetActorId,
                    buff.BuffID, Math.Max(0, extension.ExtendedDuration));
            case BuffRemoveAllEvent removeAll:
                return CreateBuffEvent("buff_remove_all", timeMs, sourceActorId, targetActorId,
                    buff.BuffID, Math.Max(0, removeAll.RemovedDuration),
                    Math.Max(0, removeAll.RemovedStacks));
            case BuffRemoveSingleEvent removeSingle:
                return CreateBuffEvent("buff_remove_single", timeMs, sourceActorId, targetActorId,
                    buff.BuffID, Math.Max(0, removeSingle.RemovedDuration), 1);
            case BuffRemoveManualEvent removeManual:
                return CreateBuffEvent("buff_remove_manual", timeMs, sourceActorId, targetActorId,
                    buff.BuffID, Math.Max(0, removeManual.RemovedDuration), 1);
            default:
                throw new InvalidDataException($"Unsupported WvW buff event type: {buff.GetType().Name}");
        }
    }

    internal static WvwExportEvent CreateBuffEvent(
        string kind,
        int timeMs,
        string? sourceActorId,
        string? targetActorId,
        long buffId,
        long durationMs,
        int? removedStacks = null) => kind switch
    {
        "buff_apply" => new WvwBuffApplyEvent
        {
            TimeMs = timeMs,
            SourceActorId = sourceActorId,
            TargetActorId = targetActorId,
            BuffId = buffId,
            DurationMs = durationMs,
        },
        "buff_extension" => new WvwBuffExtensionEvent
        {
            TimeMs = timeMs,
            SourceActorId = sourceActorId,
            TargetActorId = targetActorId,
            BuffId = buffId,
            DurationMs = durationMs,
        },
        "buff_remove_all" => new WvwBuffRemoveAllEvent
        {
            TimeMs = timeMs,
            SourceActorId = sourceActorId,
            TargetActorId = targetActorId,
            BuffId = buffId,
            DurationMs = durationMs,
            RemovedStacks = removedStacks ?? 0,
        },
        "buff_remove_single" => new WvwBuffRemoveSingleEvent
        {
            TimeMs = timeMs,
            SourceActorId = sourceActorId,
            TargetActorId = targetActorId,
            BuffId = buffId,
            DurationMs = durationMs,
            RemovedStacks = removedStacks ?? 0,
        },
        "buff_remove_manual" => new WvwBuffRemoveManualEvent
        {
            TimeMs = timeMs,
            SourceActorId = sourceActorId,
            TargetActorId = targetActorId,
            BuffId = buffId,
            DurationMs = durationMs,
            RemovedStacks = removedStacks ?? 0,
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported buff kind"),
    };

    private static void AddStatus<T>(
        IReadOnlyList<T> statusEvents,
        string kind,
        ParsedEvtcLog log,
        Func<AgentItem, long, string?> actorId,
        List<WvwExportEvent> output) where T : StatusEvent
    {
        foreach (StatusEvent status in statusEvents)
        {
            output.Add(CreateLifecycleEvent(
                kind,
                Time(status.Time, log),
                actorId(status.Src, status.Time)));
        }
    }
}
