using System.Text.Json;
using GW2EIBuilders;
using GW2EIJSON;
using GW2EIParserCommons;
using GW2EIEvtcParser.ParsedData;
using NUnit.Framework;

namespace GW2EIEvtcParser.tst;

public sealed class WvwExportTests
{
    private static readonly int[] ExpectedSequences = [0, 1, 2];
    private static readonly int[] ExpectedTimes = [10, 20, 20];
    private static readonly string[] ExpectedKinds = ["damage", "death", "down"];

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
        var first = WvwExportBuilder.CreateActorOrderKey(
            10, 100, AgentItem.AgentType.Player, 123, 42, "same", 0x1234, omitInstanceId: true);
        var repeatedParse = WvwExportBuilder.CreateActorOrderKey(
            10, 100, AgentItem.AgentType.Player, 999, 42, "same", 0x1234, omitInstanceId: true);

        Assert.That(first, Is.EqualTo(repeatedParse));
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
    public void V2EventDomainRetainsEventsAfterActiveFightEnd()
    {
        long eventDomainEnd = WvwExportBuilder.GetEventDomainEndMs(56_789, 57_100);

        Assert.That(56_974, Is.GreaterThan(56_789).And.LessThanOrEqualTo(eventDomainEnd));
        Assert.Throws<InvalidDataException>(() => WvwExportBuilder.GetEventDomainEndMs(100, 99));
    }

    [Test]
    public void V2HealingDeduplicatesSharedSourceAndRecipientIndexes()
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        object eventRecord = new();

        Assert.That(WvwExportBuilder.TryAddHealingEventOnce(seen, eventRecord), Is.True);
        Assert.That(WvwExportBuilder.TryAddHealingEventOnce(seen, eventRecord), Is.False);
    }

    private static TestActor Actor(string name, long firstAware, ulong sourceAgent) => new(
        name,
        new WvwExportBuilder.ActorOrderKey(
            firstAware,
            firstAware + 10,
            GW2EIEvtcParser.ParsedData.AgentItem.AgentType.Player,
            1,
            42,
            name,
            sourceAgent));

    private static WvwExportBuilder.OwnershipObservationForExport Ownership(
        string agentId, string? ownerActorId, long at, int sourceOrder) => new(
        agentId, 50, 400, at, ownerActorId, sourceOrder);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };
}
