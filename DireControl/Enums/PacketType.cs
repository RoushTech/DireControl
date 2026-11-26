namespace DireControl.Enums;

public enum PacketType
{
    Position,
    Message,
    Weather,
    Telemetry,
    Object,
    Item,
    Status,
    Unknown,
    Unparseable,

    /// <summary>
    /// A third-party ("}") packet a gateway relayed onto RF, whose inner packet is not a
    /// message. The packet stays attributed to the gateway that transmitted it; the inner
    /// station's position/weather is deliberately not applied to anyone.
    /// </summary>
    ThirdParty,

    /// <summary>A station capabilities ("&lt;") packet, e.g. <c>&lt;IGATE,MSG_CNT=0,LOC_CNT=12</c>.</summary>
    StationCapabilities,

    /// <summary>
    /// Plain-text beacon with no APRS data type identifier, e.g. an "ID" packet
    /// (<c>WB4BSD/R WB4BSD-1/B</c>) or a free-text station description.
    /// </summary>
    Beacon,
}
