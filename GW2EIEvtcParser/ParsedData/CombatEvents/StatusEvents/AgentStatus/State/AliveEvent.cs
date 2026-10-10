using System.Runtime.CompilerServices;
using static GW2EIEvtcParser.ArcDPSEnums;

[assembly: InternalsVisibleTo("GW2EIParser.tst")]

namespace GW2EIEvtcParser.ParsedData;

public class AliveEvent : StatusEvent
{
    /// <summary>
    /// Raw EVTC <see cref="StateChange"/> carried by the combat record this event was built from.
    /// CombatEventFactory only builds <see cref="AliveEvent"/> from
    /// <see cref="StateChange.ChangeUp"/>; the property lets consumers verify
    /// that canonical semantic instead of inferring it.
    /// </summary>
    public StateChange StateChange { get; }

    internal AliveEvent(CombatItem evtcItem, AgentData agentData) : base(evtcItem, agentData)
    {
        StateChange = evtcItem.IsStateChange;
    }

}
