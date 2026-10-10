using System.Text.Json;
using GW2EIBuilders;
using GW2EIJSON;
using GW2EIParserCommons;
using GW2EIEvtcParser;
using GW2EIEvtcParser.ParsedData;
using NUnit.Framework;
using static GW2EIEvtcParser.ArcDPSEnums;

namespace GW2EIEvtcParser.tst;

public sealed class WvwExportTests
{
    private static readonly int[] ExpectedSequences = [0, 1, 2];
    private static readonly int[] ExpectedTimes = [10, 20, 20];
    private static readonly string[] ExpectedKinds = ["damage", "death", "down"];
    private static readonly string[] ExpectedActorIds = ["a000000", "a000001"];

    private sealed record TestActor(string Name, WvwExportBuilder.ActorOrderKey Key);

    [Test]
    public void ExportIsOptIn()
    {
        var settings = new ProgramSettings
        {
            SaveOutCSV = false,
            SaveOutHTML = false,
            SaveOutJSON = false,
            SaveOutWvwExport = false,
        };

        Assert.That(settings.SaveOutWvwExport, Is.False);
        Assert.That(settings.HasFormat(), Is.False);
        Assert.That(new EvtcParserSettings(0, 0).CaptureOwnershipObservations, Is.False);
        Assert.That(new EvtcParserSettings(0, 0) { CaptureOwnershipObservations = true }
            .CaptureOwnershipObservations, Is.True);
    }

    [Test]
    public void ActorIdsAreDeterministicForInputPermutations()
    {
        var early = Actor("early", 10, 22);
        var late = Actor("late", 20, 11);

        var first = WvwExportBuilder.AssignActorIds(new[] { late, early }, x => x.Key);
        var second = WvwExportBuilder.AssignActorIds(new[] { early, late }, x => x.Key);

        Assert.That(first, Is.EqualTo(second));
        Assert.That(first[0], Is.EqualTo(new WvwExportBuilder.ActorIdAssignment<TestActor>(early, "a000000")));
        Assert.That(first[1].ActorId, Is.EqualTo("a000001"));
    }

    [Test]
    public void EventsAreSortedAndSequencedContiguously()
    {
        WvwExportEvent[] input =
        [
            WvwExportBuilder.CreateLifecycleEvent("down", 20, "actor"),
            WvwExportBuilder.CreateDamageEvent(10, "actor", "target", 3, 4, 0),
            WvwExportBuilder.CreateLifecycleEvent("death", 20, "actor"),
        ];

        var output = WvwExportBuilder.SequenceEvents(input);

        Assert.That(output.Select(x => x.Sequence), Is.EqualTo(ExpectedSequences));
        Assert.That(output.Select(x => x.TimeMs), Is.EqualTo(ExpectedTimes));
        Assert.That(output.Select(x => x.Kind), Is.EqualTo(ExpectedKinds));
    }

    [Test]
    public void DamageAndLifecycleProjectionsHaveOnlyTheirOwnFields()
    {
        WvwExportEvent damage = WvwExportBuilder.CreateDamageEvent(3, "s", "t", 9, 8, 2);
        WvwExportEvent down = WvwExportBuilder.CreateLifecycleEvent("down", 4, "t");

        JsonElement damageJson = JsonDocument.Parse(JsonSerializer.Serialize(damage, JsonOptions)).RootElement;
        JsonElement downJson = JsonDocument.Parse(JsonSerializer.Serialize(down, JsonOptions)).RootElement;
        Assert.That(damageJson.GetProperty("kind").GetString(), Is.EqualTo("damage"));
        Assert.That(damageJson.GetProperty("damage").GetInt64(), Is.EqualTo(8));
        Assert.That(damageJson.TryGetProperty("buff_id", out _), Is.False);
        Assert.That(downJson.GetProperty("kind").GetString(), Is.EqualTo("down"));
        Assert.That(downJson.TryGetProperty("damage", out _), Is.False);
        Assert.That(downJson.TryGetProperty("health_percent", out _), Is.False);
    }

    [TestCase("buff_apply", typeof(WvwBuffApplyEvent))]
    [TestCase("buff_extension", typeof(WvwBuffExtensionEvent))]
    [TestCase("buff_remove_all", typeof(WvwBuffRemoveAllEvent))]
    [TestCase("buff_remove_single", typeof(WvwBuffRemoveSingleEvent))]
    [TestCase("buff_remove_manual", typeof(WvwBuffRemoveManualEvent))]
    public void BuffKindsProjectToDistinctVariants(string kind, Type expectedType)
    {
        var evt = WvwExportBuilder.CreateBuffEvent(kind, 10, "s", "t", 42, 30, 2);

        Assert.That(evt, Is.TypeOf(expectedType));
        JsonElement json = JsonDocument.Parse(JsonSerializer.Serialize(evt, JsonOptions)).RootElement;
        Assert.That(json.GetProperty("kind").GetString(), Is.EqualTo(kind));
        Assert.That(json.GetProperty("buff_id").GetInt64(), Is.EqualTo(42));
        Assert.That(json.TryGetProperty("damage", out _), Is.False);
    }

    [Test]
    public void BuffCollectionDeduplicatesSameObjectButKeepsDistinctObjects()
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        object first = new();

        Assert.That(WvwExportBuilder.TryAddBuffEventOnce(seen, first), Is.True);
        Assert.That(WvwExportBuilder.TryAddBuffEventOnce(seen, first), Is.False);
        Assert.That(WvwExportBuilder.TryAddBuffEventOnce(seen, new object()), Is.True);
    }

    [Test]
    public void BuffStackBookkeepingEventsAreNotExportedAsBuffChanges()
    {
        Assert.That(WvwExportBuilder.IsExportableBuffEventType(typeof(BuffStackActiveEvent), 1), Is.False);
        Assert.That(WvwExportBuilder.IsExportableBuffEventType(typeof(BuffStackDeactiveEvent), 1), Is.False);
        Assert.That(WvwExportBuilder.IsExportableBuffEventType(typeof(BuffExtensionEvent), 1), Is.True);
        Assert.That(WvwExportBuilder.IsExportableBuffEventType(typeof(BuffApplyEvent), long.MinValue), Is.False);
    }

    [Test]
    public void OwnershipObservationsFormIntervalsWithOwnerChangesAndFinalAwareness()
    {
        WvwExportBuilder.OwnershipObservationForExport[] source =
        [
            Ownership("minion", "owner-a", 100, 0),
            Ownership("minion", "owner-b", 100, 1),
            Ownership("minion", "owner-b", 200, 2),
            Ownership("minion", "owner-c", 300, 3),
        ];

        var projection = WvwExportBuilder.BuildOwnershipProjection(source);
        var intervals = projection.Intervals;

        Assert.That(intervals.Select(x => (x.StartMs, x.EndMs, x.OwnerActorId, x.StartBasis, x.EndBasis)),
            Is.EqualTo(new[]
            {
                (100L, 200L, "owner-b", "master_observation", "master_observation"),
                (200L, 300L, "owner-b", "master_observation", "master_observation"),
                (300L, 400L, "owner-c", "master_observation", "agent_awareness"),
            }));
        Assert.That(projection.ObservationCount, Is.EqualTo(4));
        Assert.That(projection.SameTimeCollisionCount, Is.EqualTo(1));
    }

    [Test]
    public void UnresolvedOwnerObservationIsPreserved()
    {
        var intervals = WvwExportBuilder.BuildOwnershipIntervals(
            new[] { Ownership("minion", null, 100, 0) });

        Assert.That(intervals, Has.Count.EqualTo(1));
        Assert.That(intervals[0].OwnerActorId, Is.Null);
        Assert.That(intervals[0].OwnerResolution, Is.EqualTo("unresolved"));
    }

    [Test]
    public void PositionProjectionPreservesCoordinateValues()
    {
        var position = WvwExportBuilder.CreatePositionSample("actor", 12, 1.25f, -2.5f, 3.75f);

        Assert.That((position.ActorId, position.TimeMs, position.X, position.Y, position.Z),
            Is.EqualTo(("actor", 12, 1.25f, -2.5f, 3.75f)));
    }

    [Test]
    public void V2ActorOrderingIgnoresUnstableInstanceMetadata()
    {
        var firstParse = new[]
        {
            Actor("late", 20, 0x5678, 123),
            Actor("early", 10, 0x1234, 42),
        };
        var repeatedParse = new[]
        {
            Actor("early", 10, 0x1234, 999),
            Actor("late", 20, 0x5678, 777),
        };

        var first = WvwExportBuilder.AssignActorIds(firstParse, actor => actor.Key);
        var repeated = WvwExportBuilder.AssignActorIds(repeatedParse, actor => actor.Key);

        Assert.That(first.Select(item => (item.Agent.Name, item.ActorId)),
            Is.EqualTo(repeated.Select(item => (item.Agent.Name, item.ActorId))));
        Assert.That(first.Select(item => item.ActorId), Is.EqualTo(ExpectedActorIds));
    }

    [Test]
    public void V2HealingUpAndCcKeepCanonicalFieldsSeparate()
    {
        WvwExportEvent healing = WvwExportBuilder.CreateHealingEvent(10, "source", "target", -5, 125);
        WvwExportEvent up = WvwExportBuilder.CreateLifecycleEvent("up", 20, "target");
        WvwExportEvent cc = WvwExportBuilder.CreateCCEvent(30, "source", "target", 7, 400);

        Assert.That(healing, Is.TypeOf<WvwHealingEvent>());
        Assert.That(((WvwHealingEvent)healing).Amount, Is.EqualTo(125));
        Assert.That(((WvwHealingEvent)healing).SkillId, Is.EqualTo(-5));
        Assert.That(up, Is.TypeOf<WvwUpEvent>());
        Assert.That(((WvwUpEvent)up).ActorId, Is.EqualTo("target"));
        Assert.That(cc, Is.TypeOf<WvwCCEvent>());
        Assert.That(((WvwCCEvent)cc).DurationMs, Is.EqualTo(400));

        JsonElement healingJson = JsonDocument.Parse(JsonSerializer.Serialize(healing, JsonOptions)).RootElement;
        JsonElement upJson = JsonDocument.Parse(JsonSerializer.Serialize(up, JsonOptions)).RootElement;
        JsonElement ccJson = JsonDocument.Parse(JsonSerializer.Serialize(cc, JsonOptions)).RootElement;
        Assert.That(healingJson.GetProperty("kind").GetString(), Is.EqualTo("healing"));
        Assert.That(healingJson.TryGetProperty("duration_ms", out _), Is.False);
        Assert.That(upJson.GetProperty("kind").GetString(), Is.EqualTo("up"));
        Assert.That(upJson.TryGetProperty("source_actor_id", out _), Is.False);
        Assert.That(ccJson.GetProperty("kind").GetString(), Is.EqualTo("cc"));
        Assert.That(ccJson.TryGetProperty("amount", out _), Is.False);
    }

    [Test]
    public void V2EventDomainUsesLaterRawRecordingEndWhenItCoversEvents()
    {
        WvwExportEvent derivedTail = WvwExportBuilder.CreateBuffEvent(
            "buff_remove_single", 56_974, "actor", "actor", -51, 0, 1);
        long eventDomainEnd = WvwExportBuilder.GetEventDomainEndMs(56_789, 60_000, [derivedTail]);

        Assert.That(eventDomainEnd, Is.EqualTo(60_000));
    }

    [Test]
    public void V2EventDomainIncludesCanonicalEventAfterRawRecordingEnd()
    {
        WvwExportEvent derivedTail = WvwExportBuilder.CreateBuffEvent(
            "buff_remove_single", 56_974, "actor", "actor", -51, 0, 1);
        long eventDomainEnd = WvwExportBuilder.GetEventDomainEndMs(56_789, 56_789, [derivedTail]);

        Assert.That(eventDomainEnd, Is.EqualTo(56_974));
        Assert.Throws<InvalidDataException>(() => WvwExportBuilder.GetEventDomainEndMs(100, 99, []));
    }

    [Test]
    public void V2HealingDeduplicatesSharedSourceAndRecipientIndexes()
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        object eventRecord = new();

        Assert.That(WvwExportBuilder.TryAddHealingEventOnce(seen, eventRecord), Is.True);
        Assert.That(WvwExportBuilder.TryAddHealingEventOnce(seen, eventRecord), Is.False);
    }

    [Test]
    public void ChangeUpAliveEventBecomesExactlyOneV2Up()
    {
        AliveEvent changeUp = AliveEventFor(StateChange.ChangeUp);

        Assert.That(changeUp.StateChange, Is.EqualTo(StateChange.ChangeUp));
        Assert.That(changeUp.IsCustom, Is.False);
        Assert.DoesNotThrow(() => WvwExportBuilder.ValidateV2UpSemantics([changeUp]));
        WvwExportEvent up = WvwExportBuilder.CreateLifecycleEvent(
            WvwExportBuilder.AliveEventKind(v2: true), 100, "a000000");
        Assert.That(up, Is.TypeOf<WvwUpEvent>());
        Assert.That(((WvwUpEvent)up).ActorId, Is.EqualTo("a000000"));
    }

    [Test]
    public void OrdinaryNonChangeUpAliveEventIsNotAV2Up()
    {
        AliveEvent spawn = AliveEventFor(StateChange.Spawn);
        AliveEvent changeUp = AliveEventFor(StateChange.ChangeUp);

        Assert.That(spawn.StateChange, Is.EqualTo(StateChange.Spawn));
        Assert.Throws<InvalidDataException>(() => WvwExportBuilder.ValidateV2UpSemantics([spawn]));
        Assert.Throws<InvalidDataException>(() =>
            WvwExportBuilder.ValidateV2UpSemantics([changeUp, spawn]));
    }

    [Test]
    public void V1AliveProjectionRemainsUnchanged()
    {
        Assert.That(WvwExportBuilder.AliveEventKind(v2: false), Is.EqualTo("alive"));
        Assert.That(WvwExportBuilder.AliveEventKind(v2: true), Is.EqualTo("up"));

        WvwExportEvent v1Alive = WvwExportBuilder.CreateLifecycleEvent(
            WvwExportBuilder.AliveEventKind(v2: false), 20, "actor");
        Assert.That(v1Alive, Is.TypeOf<WvwAliveEvent>());
        JsonElement json = JsonDocument.Parse(JsonSerializer.Serialize(v1Alive, JsonOptions)).RootElement;
        Assert.That(json.GetProperty("kind").GetString(), Is.EqualTo("alive"));
        Assert.That(json.GetProperty("target_actor_id").GetString(), Is.EqualTo("actor"));
        Assert.That(json.TryGetProperty("actor_id", out _), Is.False);
    }

    [Test]
    public void ActorFallbackResolvesTheUniqueAwareSlice()
    {
        var early = Slice(0xAA, 0, 100);
        var late = Slice(0xAA, 200, 300);

        Assert.That(
            WvwExportBuilder.ResolveAwareSlice(early, 250, [early, late], v2: true),
            Is.SameAs(late));
        Assert.That(
            WvwExportBuilder.ResolveAwareSlice(early, 50, [early, late], v2: true),
            Is.SameAs(early));
    }

    [Test]
    public void ActorFallbackWithoutCandidateKeepsNullBehavior()
    {
        var agent = Slice(0xAA, 0, 100);
        var otherAddress = Slice(0xBB, 0, 400);
        var sameAddressNotAware = Slice(0xAA, 0, 200);

        Assert.That(
            WvwExportBuilder.ResolveAwareSlice(agent, 250, [otherAddress], v2: true),
            Is.Null);
        Assert.That(
            WvwExportBuilder.ResolveAwareSlice(agent, 250, [sameAddressNotAware], v2: true),
            Is.Null);
    }

    [Test]
    public void V2ActorFallbackRejectsAmbiguousSlices()
    {
        var agent = Slice(0xAA, 0, 100);
        var first = Slice(0xAA, 0, 300);
        var second = Slice(0xAA, 200, 400);

        Assert.Throws<InvalidDataException>(() =>
            WvwExportBuilder.ResolveAwareSlice(agent, 250, [first, second], v2: true));
    }

    [Test]
    public void V1ActorFallbackKeepsInstanceAndShortestWindowSelection()
    {
        var agent = Slice(0xAA, 0, 100, instid: 7);
        var shortest = Slice(0xAA, 200, 300, instid: 7);
        var longest = Slice(0xAA, 100, 400, instid: 7);
        var otherInst = Slice(0xAA, 200, 300, instid: 9);

        Assert.That(
            WvwExportBuilder.ResolveAwareSlice(agent, 250, [longest, otherInst, shortest], v2: false),
            Is.SameAs(shortest));
        Assert.That(
            WvwExportBuilder.ResolveAwareSlice(agent, 250, [otherInst], v2: false),
            Is.Null);
    }

    [Test]
    public void V2TemporalCaseADurationBeforeRecordingEndEqualToDomain()
    {
        long eventDomainEnd = WvwExportBuilder.GetEventDomainEndMs(2_000, 2_100, []);

        Assert.That(eventDomainEnd, Is.EqualTo(2_100));
        Assert.DoesNotThrow(() => WvwExportBuilder.ValidateTemporalBounds(2_000, 2_100, 2_100));
    }

    [Test]
    public void V2TemporalCaseBDurationEqualsRecordingBeforeDomainFromDerivedEvent()
    {
        WvwExportEvent derivedTail = WvwExportBuilder.CreateBuffEvent(
            "buff_remove_single", 56_974, "actor", "actor", -51, 0, 1);
        long eventDomainEnd = WvwExportBuilder.GetEventDomainEndMs(56_789, 56_789, [derivedTail]);

        Assert.That(eventDomainEnd, Is.EqualTo(56_974));
        Assert.DoesNotThrow(() => WvwExportBuilder.ValidateTemporalBounds(56_789, 56_789, 56_974));
    }

    [Test]
    public void V2TemporalCaseCDurationBeforeRecordingBeforeDomain()
    {
        WvwExportEvent derivedTail = WvwExportBuilder.CreateBuffEvent(
            "buff_remove_single", 2_200, "actor", "actor", -51, 0, 1);
        long eventDomainEnd = WvwExportBuilder.GetEventDomainEndMs(2_000, 2_100, [derivedTail]);

        Assert.That(eventDomainEnd, Is.EqualTo(2_200));
        Assert.DoesNotThrow(() => WvwExportBuilder.ValidateTemporalBounds(2_000, 2_100, 2_200));
    }

    [Test]
    public void V2TemporalCaseDRecordingEndBeforeDurationIsRejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            WvwExportBuilder.ValidateTemporalBounds(2_000, 1_999, 1_999));
        Assert.Throws<InvalidDataException>(() => WvwExportBuilder.GetEventDomainEndMs(100, 99, []));
    }

    [Test]
    public void V2TemporalCaseEDomainBeforeRecordingEndIsRejected()
    {
        Assert.Throws<InvalidDataException>(() =>
            WvwExportBuilder.ValidateTemporalBounds(2_000, 2_100, 2_099));
    }

    private static CombatItem StateChangeItem(
        StateChange stateChange, long time = 100, ulong srcAgent = 0x1234) => new(
        time, srcAgent, 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        (byte)stateChange, 0, 0, 0, 0, new EvtcVersionEvent(0));

    private static AgentItem Slice(
        ulong address, long firstAware, long lastAware, ushort instid = 1, string name = "slice") => new(
        address, name, ParserHelper.Spec.NPC, -1, AgentItem.AgentType.Player,
        instid, 0, 0, 0, 0, 0, 0, firstAware, lastAware, false);

    private static AliveEvent AliveEventFor(StateChange stateChange)
    {
        var agentData = new AgentData(null!, [Slice(0x1234, 0, long.MaxValue)]);
        return new AliveEvent(StateChangeItem(stateChange), agentData);
    }

    private static TestActor Actor(
        string name, long firstAware, ulong sourceAgent, ushort instanceId = 1) => new(
        name,
        WvwExportBuilder.CreateActorOrderKey(
            firstAware,
            firstAware + 10,
            AgentItem.AgentType.Player,
            instanceId,
            42,
            name,
            sourceAgent,
            omitInstanceId: true));

    private static WvwExportBuilder.OwnershipObservationForExport Ownership(
        string agentId, string? ownerActorId, long at, int sourceOrder) => new(
        agentId, 50, 400, at, ownerActorId, sourceOrder);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
