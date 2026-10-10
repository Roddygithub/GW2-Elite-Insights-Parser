using System.Globalization;
using GW2EIEvtcParser;
using GW2EIEvtcParser.EIData;
using GW2EIEvtcParser.ParsedData;
using GW2EIEvtcParser.LogLogic;
using GW2EIJSON;

namespace GW2EIBuilders;

public static class WvwExportBuilder
{
    public static WvwExportDocument Build(ParsedEvtcLog log, Version parserVersion)
    {
        if (!log.ParserSettings.DetailedWvWParse || log.LogData.Logic.ParseMode != LogLogic.ParseModeEnum.WvW)
        {
            throw new InvalidDataException("WvW export requires a detailed-WvW parse.");
        }

        var agents = Enum.GetValues<AgentItem.AgentType>()
            .SelectMany(log.AgentData.GetAgentByType)
            .Distinct()
            .OrderBy(x => x.FirstAware)
            .ThenBy(x => x.LastAware)
            .ThenBy(x => x.Type)
            .ThenBy(x => x.InstID)
            .ThenBy(x => x.ID)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ThenBy(x => x.Agent)
            .ToList();
        var actorIds = agents.Select((agent, index) => (agent, id: $"a{index:D6}"))
            .ToDictionary(x => x.agent, x => x.id);
        string? ActorId(AgentItem agent, long time)
        {
            if (agent.IsUnknown)
            {
                return null;
            }
            if (actorIds.TryGetValue(agent, out string? actorId))
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
                FirstAwareMs = Math.Clamp(agent.FirstAware - log.LogData.LogStart, 0, duration),
                LastAwareMs = Math.Clamp(agent.LastAware - log.LogData.LogStart, 0, duration),
            };
        }).ToList();

        var events = new List<WvwExportEvent>();
        var seenBuffEvents = new HashSet<BuffEvent>(ReferenceEqualityComparer.Instance);
        foreach (AgentItem agent in agents)
        {
            foreach (HealthDamageEvent damage in log.CombatData.GetDamageData(agent))
            {
                events.Add(new WvwExportEvent
                {
                    Kind = "damage",
                    TimeMs = Time(damage.Time, log),
                    SourceActorId = ActorId(damage.From, damage.Time),
                    TargetActorId = ActorId(damage.To, damage.Time),
                    SkillId = damage.SkillID,
                    Damage = Math.Max(0, damage.HealthDamage),
                    ShieldDamage = Math.Max(0, damage.ShieldDamage),
                });
            }

            foreach (BuffEvent buff in log.CombatData.GetBuffDataByDst(agent))
            {
                if (seenBuffEvents.Add(buff))
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
                events.Add(new WvwExportEvent
                {
                    Kind = "health_update",
                    TimeMs = Time(health.Time, log),
                    TargetActorId = ActorId(health.Src, health.Time),
                    HealthPercent = health.HealthPercent,
                });
            }
        }

        events = events.OrderBy(x => x.TimeMs)
            .ThenBy(x => x.Kind, StringComparer.Ordinal)
            .ThenBy(x => x.SourceActorId, StringComparer.Ordinal)
            .ThenBy(x => x.TargetActorId, StringComparer.Ordinal)
            .ThenBy(x => x.SkillId)
            .ThenBy(x => x.Damage)
            .ThenBy(x => x.BuffId)
            .ThenBy(x => x.BuffDurationMs)
            .ThenBy(x => x.RemovedStacks)
            .Select((value, index) => value with { Sequence = index })
            .ToList();

        var intervals = new List<WvwOwnershipInterval>();
        foreach (AgentItem agent in agents)
        {
            if (!actorIds.TryGetValue(agent, out string? agentId))
            {
                continue;
            }
            var observations = agent.OwnershipObservations
                .Select((observation, index) => (observation, index))
                .OrderBy(x => x.observation.Time)
                .ThenBy(x => x.index)
                .GroupBy(x => x.observation.Time)
                .Select(group => group.Last().observation)
                .ToList();
            for (int index = 0; index < observations.Count; index++)
            {
                var observation = observations[index];
                var end = index + 1 < observations.Count ? observations[index + 1].Time : agent.LastAware;
                long startMs = Math.Clamp(observation.Time - log.LogData.LogStart, 0, log.LogData.LogDuration);
                long endMs = Math.Clamp(end - log.LogData.LogStart, 0, log.LogData.LogDuration);
                string? ownerActorId = ActorId(observation.Owner, observation.Time);
                if (endMs > startMs && ownerActorId != null)
                {
                    intervals.Add(new WvwOwnershipInterval
                    {
                        AgentId = agentId,
                        OwnerActorId = ownerActorId,
                        StartMs = startMs,
                        EndMs = endMs,
                        BoundaryBasis = "master_observation",
                    });
                }
            }
        }
        intervals = intervals.OrderBy(x => x.AgentId, StringComparer.Ordinal)
            .ThenBy(x => x.StartMs)
            .ThenBy(x => x.OwnerActorId, StringComparer.Ordinal)
            .ToList();

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
                        positions.Add(new WvwPositionSample
                        {
                            ActorId = actorId,
                            TimeMs = Time(position.Time, log),
                            X = point.X,
                            Y = point.Y,
                            Z = point.Z,
                        });
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
                    PositionSamples = positions,
                },
            ],
        };
    }

    private static int Time(long time, ParsedEvtcLog log) =>
        (int)Math.Clamp(time - log.LogData.LogStart, 0, log.LogData.LogDuration);

    private static WvwExportEvent BuffEventDto(
        BuffEvent buff,
        ParsedEvtcLog log,
        Func<AgentItem, long, string?> actorId)
    {
        var result = new WvwExportEvent
        {
            TimeMs = Time(buff.Time, log),
            SourceActorId = actorId(buff.By, buff.Time),
            TargetActorId = actorId(buff.To, buff.Time),
            SkillId = buff.BuffID,
            BuffId = buff.BuffID,
        };
        switch (buff)
        {
            case BuffApplyEvent apply:
                return result with { Kind = "buff_apply", BuffDurationMs = Math.Max(0, apply.AppliedDuration) };
            case BuffExtensionEvent extension:
                return result with { Kind = "buff_extension", BuffDurationMs = Math.Max(0, extension.ExtendedDuration) };
            case BuffRemoveAllEvent removeAll:
                return result with
                {
                    Kind = "buff_remove_all",
                    BuffDurationMs = Math.Max(0, removeAll.RemovedDuration),
                    RemovedStacks = Math.Max(0, removeAll.RemovedStacks),
                };
            case BuffRemoveSingleEvent removeSingle:
                return result with
                {
                    Kind = "buff_remove_single",
                    BuffDurationMs = Math.Max(0, removeSingle.RemovedDuration),
                    RemovedStacks = 1,
                };
            case BuffRemoveManualEvent removeManual:
                return result with
                {
                    Kind = "buff_remove_manual",
                    BuffDurationMs = Math.Max(0, removeManual.RemovedDuration),
                    RemovedStacks = 1,
                };
            default:
                throw new InvalidDataException($"Unsupported WvW buff event type: {buff.GetType().Name}");
        }
    }

    private static void AddStatus<T>(
        IReadOnlyList<T> statusEvents,
        string kind,
        ParsedEvtcLog log,
        Func<AgentItem, long, string?> actorId,
        List<WvwExportEvent> output) where T : StatusEvent
    {
        foreach (StatusEvent status in statusEvents)
        {
            output.Add(new WvwExportEvent
            {
                Kind = kind,
                TimeMs = Time(status.Time, log),
                TargetActorId = actorId(status.Src, status.Time),
            });
        }
    }
}
