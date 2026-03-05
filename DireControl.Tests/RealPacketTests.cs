using AprsSharp.AprsParser;
using DireControl.PathParsing;
using Xunit;
using AprsPacketType = AprsSharp.AprsParser.PacketType;

namespace DireControl.Tests;

// ---------------------------------------------------------------------------
// Shared corpus
// ---------------------------------------------------------------------------

/// <summary>
/// Unique packet strings drawn from the RawPackets.txt corpus.
/// Structurally duplicate layouts are intentionally omitted — every entry
/// here exercises a path, symbol, format, or encoding not covered by another.
/// </summary>
public static class RealPacketData
{
    // ── Weather ──────────────────────────────────────────────────────────────

    // @ + timestamp-z + position + _ weather symbol + full c/s/g/t/r/p/P/h/b fields, via APRS-IS
    public const string Weather_AtPrefix_AllFields =
        "N4YH-13>APRS,TCPIP*,qAC,T2TEXAS:@040426z3520.13N/08510.25W_269/005g009t054r000p000P000h89b10219eCumulusDsVP";

    // @ + _ symbol + AmbientCWOP source, luminosity L field, via APRS-IS
    public const string Weather_AtPrefix_LuminosityField =
        "KE4EST>APRS,TCPIP*,qAC,AMBCWOP-2:@040425z3506.61N/08406.13W_089/000g000t047r000p000P000h95b10261L000AmbientCWOP.com";

    // _ prefix (raw weather, no position), MMDDHHMM timestamp, Ultimeter-style
    public const string Weather_UnderscorePrefix_RawNoPosition =
        "WO4U-13>APW290,WE4MB-3*,WIDE2-1:_03020126c153s000g000t052r000p000P000h61b10220wU2K";

    // @ + _ symbol, RF digipeated (2 hops), Davis Vantage Vue
    public const string Weather_AtPrefix_RfDigipeated =
        "AB4BJ-10>APMI01,WE4MB-3*,WIDE2-1:@020414z3449.43N/08459.08W_029/000g000t055r000p000P000h75b10191WX3in1+ Davis Vantage Vue";

    // _ prefix, RF digipeated (2 hops), RSW all-zero wind fields
    public const string Weather_UnderscorePrefix_RfDigipeated =
        "AJ4FJ-13>APTW14,KN6RO-13*,WIDE1*,WE4MB-3*,WIDE2-1:_03012207c181s000g000t053r000p000P000h83b10210tRSW";

    // ── Position ─────────────────────────────────────────────────────────────

    // ! (no timestamp, no messaging), alternate symbol table S, # digipeater symbol
    public const string Position_BangPrefix_AltTable =
        "WE4MB-3>APNX16,WIDE2-2:!3514.71NS08458.52W# SKYWARN SE TN DIGI";

    // = (no timestamp, with messaging), primary table /, r repeater symbol, PHG field
    public const string Position_EqualsPrefix_PrimaryTable =
        "W4ABZ>APRS,TCPIP*,qAC,T2BC:=3456.16N/08506.36WrPHG6760/146.715 67.0 tone - AllStar Node 510139  - https://lmarc.net";

    // / (timestamp z, no messaging), primary table /, a ambulance symbol + altitude
    public const string Position_SlashPrefix_TimestampZ =
        "KR4BRU-9>APMI0A,WIDE1-1,WIDE2-1,qAR,KA4EMA-3:/040425z3504.36N/08511.40Wa019/000/A=000726Ramble-Ambulance";

    // @ (timestamp z, with messaging), alternate table I, # digipeater symbol, via APRS-IS qAS
    public const string Position_AtPrefix_TimestampZ_AltTable =
        "AK4ZX-15>APMI06,TCPIP*,qAS,AK4ZX:@040425z3431.18NI08420.60W#NGGMRS Flagship APRS Gateway & I-Gate";

    // / (timestamp h local DHH, no messaging), primary table /, > car symbol, course+speed+alt
    public const string Position_SlashPrefix_TimestampH =
        "KU4B-2>APOT21,W4DMM-3*,WIDE1*,WE4MB-3*,WIDE2-1:/025645h3601.45N/08629.16W>027/000!W00!/A=000570";

    // @ (timestamp / local DDHHMM, with messaging), primary table /, y Winlink symbol
    public const string Position_AtPrefix_TimestampSlash =
        "KM4BJZ-1>APX204,KM4BJZ-2*,WE4MB-3*,WIDE2*:@012055/3519.00N/08534.68Wy000/000/Grundy Co ARES KM4BJZ@arrl.net!TNWX";

    // = (no timestamp, with messaging), _ weather-station symbol but NO weather data — PHG comment only
    // This distinguishes symbol-code _ from actual WeatherReport packets.
    public const string Position_WxSymbolNoWxData =
        "WO4U-13>APW290,WE4MB-3*,WIDE2-1:=3555.13N/08504.48W_PHG7358/WinAPRS 2.9.0 -TNCUMCROSSVIL-290-<630>";

    // ! prefix, _ weather-station symbol, trivial comment "WXTrak" — no weather fields
    public const string Position_BangPrefix_WxSymbolNoWxData =
        "AJ4FJ-13>APTW14,KN6RO-13*,WIDE1*,WE4MB-3*,WIDE2-1:!3413.75N/08502.50W_WXTrak";

    // ── Objects ── ────────────────────────────────────────────────────────────

    // ; prefix, * live object, timestamp z, human-readable comment
    public const string Object_WithCoords =
        "KC4OJS-3>APU25N,KQ4HOM-1*,WIDE2-1,qAR,KJ4G-2:;Mt_Toppin*021945z3522.03N/08418.09WEThis Saturday, leaving Hardee's @ 9am";

    // ; prefix, * live, timestamp (all-ones = no real time), repeater frequency object
    public const string Object_RepeaterFrequency =
        "KG4FZR-3>APDW17,WE4MB-3*,WIDE1*:;147.060  *111111z3526.24N/08434.95Wr147.060MHz T141 -060 KG4FZR. .www.mcminnarc.com";

    // ── Status ───────────────────────────────────────────────────────────────

    // > prefix, plain text (no grid square, no timestamp)
    public const string Status_PlainText =
        "NZ4K-10>APJYC1,W4DMM-3*,WE4MB-3*,WIDE2*:>WIDE 3-# Digi/Igate";

    // > prefix, Maidenhead grid EM75ndD + # + DX text
    public const string Status_GridSquare_DxInfo =
        "KA4J-3>APWW11,WE4MB-3*,WIDE1*,WIDE2-1:>EM75ndD#DX: AJ4FJ-5 58.3mi 167? 02:19 3419.31N 08438.20W";

    // > prefix, timestamp (DHMMSSz) + plain-text status
    public const string Status_WithTimestamp =
        "KC4OJS-3>APU25N,KQ4HOM-1*,WE4MB-3*,WIDE2*:>011545zExpect Winter Weather this weekend";

    // ── Messages ─────────────────────────────────────────────────────────────

    // :: addressee padded to 9 chars, message body, numeric ID
    public const string Message_WithNumericId =
        "W4KWS-1>APRS,TCPIP*,qAC,THIRD::N1YKT-7  :Was up brother{108";

    // :: ACK reply — body starts with "ack", 5-char base-36 ID
    public const string Message_AckReply =
        "WY4RC-67>APFII0,TCPIP*,qAC,FIFTH::KE9BPC-7 :ack3DF4A";

    // :: message with alphanumeric/base-36 ID, from aprs.fi client
    public const string Message_WithAlphanumericId =
        "KE9BPC-7>APFII0,TCPIP*,qAC,APRSFI::WY4RC-67 :CQ CQ CQ KE9BPC calling CQ{3DF4A";

    // :: WXBOT response, no message ID
    public const string Message_NoId_WxBotResponse =
        "WXBOT>APRS,qAS,KI6WJP::K2KAZ-7  :Effort PA. Tonight,Rain Likely and Patchy Fog 60% Low 33";

    // :: message with ID, igated from RF direct (qAR)
    public const string Message_WithId_IgatedDirectRf =
        "NTSGTE>APN20H,qAR,WZ0C-4::WB2BWU-2 :Please list (QTC #){24805";

    // :: bulletin addressed to BLN* identifier (broadcast, no reply ID)
    public const string Message_BulletinBln =
        "LX0WX-13>APMI06,TCPIP*,qAC,T2UKRAINE::BLNALUX  :XLX Reflector goes C4FM https://xlx270.epf.lu/";

    // :: telemetry metadata "BITS." sent by station to itself
    public const string Message_TelemetryBits =
        "KN6RO-13>APMI06,WE4MB-3*,WIDE2-1::KN6RO-13 :BITS.11111111,KN6RO-13 Telemetry";

    // ── Telemetry ────────────────────────────────────────────────────────────

    // T# prefix, 5 analog channels + 8-bit digital field
    public const string Telemetry_T_Hash =
        "KN6RO-13>APMI06,AJ4FJ-5*,WE4MB-3*,WIDE2*:T#132,179,076,021,066,000,00000000";

    // ── MIC-E ────────────────────────────────────────────────────────────────

    // ` (backtick) prefix = current MIC-E (Kenwood TM-D700/D710A etc.)
    public const string MicE_Current =
        "WB7VPC-2>S5PR4Q,W4NAR-2*,WIDE1*,WE4MB-3*,WIDE2*:`q+~lJM>/>\"\"63}=";

    // ' (apostrophe) prefix = old MIC-E (TM-D710, Peet Bros Ultimeter)
    public const string MicE_Old_PeetBros =
        "KG4LKY-5>SWPU0Q,KG4LKY-2*,AC4AG-4*,WE4MB-3*,WIDE3*:'oSl _/]PEET BROS ULTIMETER 2100 TM-D710=";

    // ── Unparseable / unusual ─────────────────────────────────────────────────

    // TOCALL is "ID" (not a standard APRS TOCALL); info starts with 'W' (no APRS type byte)
    public const string Unparseable_IdTocall =
        "WO4U-13>ID,WE4MB-3*,WIDE2-1:WO4U-13/R RELAY/D WO4U-1/B";

    // } prefix = third-party traffic encapsulation; AprsSharp may return Unknown or throw
    public const string Unparseable_ThirdParty =
        "AC4AG-4>APMI06,WE4MB-3*,WIDE2*:}KK4KTV-13>APRS,TCPIP,AC4AG-4*:@020501z3720.22N/08453.10W_042/005g007t038r000p000P000h77b10248L000AmbientCWOP.com";

    // ── Shared TheoryData ─────────────────────────────────────────────────────

    /// <summary>
    /// Weather packets → (tempF, windDirDeg, windSpeedMph, windGustMph, humidityPct, pressureMbar).
    /// pressureMbar = AprsSharp BarometricPressure / 10.0.
    /// </summary>
    public static TheoryData<string, int, int, int, int, int, double> WeatherFieldData => new()
    {
        { Weather_AtPrefix_AllFields,        54, 269, 5, 9, 89, 1021.9 },
        { Weather_AtPrefix_LuminosityField,  47,  89, 0, 0, 95, 1026.1 },
        { Weather_AtPrefix_RfDigipeated,     55,  29, 0, 0, 75, 1019.1 },
        // Note: _ prefix packets (WO4U-13, AJ4FJ-13) have Type=WeatherReport but
        // InfoField is UnsupportedInfo in AprsSharp 0.4.1 — cannot cast to WeatherInfo.
        // They are exercised only in RawWeatherPacket_UnderscorePrefix_ParsedAsAWeatherVariant.
    };

    /// <summary>
    /// Position packets → expected decimal-degree coordinates (tolerance ±0.001°).
    /// </summary>
    public static TheoryData<string, double, double> PositionCoordData => new()
    {
        { Position_BangPrefix_AltTable,          35.2452, -84.9753 },
        { Position_EqualsPrefix_PrimaryTable,    34.9360, -85.1060 },
        { Position_SlashPrefix_TimestampZ,       35.0727, -85.1900 },
        { Position_AtPrefix_TimestampZ_AltTable, 34.5197, -84.3433 },
        { Position_SlashPrefix_TimestampH,       36.0242, -86.4860 },
    };

    /// <summary>
    /// Message packets → (addressee trimmed, body, messageId — empty string when absent).
    /// </summary>
    public static TheoryData<string, string, string, string> MessageFieldData => new()
    {
        { Message_WithNumericId,         "N1YKT-7",  "Was up brother",                                            "108"   },
        { Message_AckReply,              "KE9BPC-7", "ack3DF4A",                                                  ""      },
        { Message_WithAlphanumericId,    "WY4RC-67", "CQ CQ CQ KE9BPC calling CQ",                               "3DF4A" },
        { Message_NoId_WxBotResponse,    "K2KAZ-7",  "Effort PA. Tonight,Rain Likely and Patchy Fog 60% Low 33", ""      },
        { Message_WithId_IgatedDirectRf, "WB2BWU-2", "Please list (QTC #)",                                      "24805" },
        // Message_BulletinBln omitted: AprsSharp 0.4.1 throws ArgumentException on construction
        // for BLN* bulletin addresses — tested separately in UnparseablePacketTests.
        { Message_TelemetryBits,         "KN6RO-13", "BITS.11111111,KN6RO-13 Telemetry",                        ""      },
    };

    /// <summary>
    /// Packets → (source, tocall, rawPath) for ParseTnc2Header.
    /// Covers internet paths, qAR/qAS, multi-hop RF, ID TOCALL, MIC-E destination.
    /// </summary>
    public static TheoryData<string, string, string, string> Tnc2HeaderData => new()
    {
        // internet only — TOCALL APRS, path starts with TCPIP*
        { Weather_AtPrefix_AllFields,
            "N4YH-13",   "APRS",   "TCPIP*,qAC,T2TEXAS" },

        // qAR igated from RF direct
        { Message_WithId_IgatedDirectRf,
            "NTSGTE",    "APN20H", "qAR,WZ0C-4" },

        // qAS (server-added), same callsign as igate
        { Position_AtPrefix_TimestampZ_AltTable,
            "AK4ZX-15",  "APMI06", "TCPIP*,qAS,AK4ZX" },

        // single real digi + unused alias + qAR + igate callsign
        { Object_WithCoords,
            "KC4OJS-3",  "APU25N", "KQ4HOM-1*,WIDE2-1,qAR,KJ4G-2" },

        // TOCALL = "ID" (non-standard)
        { Unparseable_IdTocall,
            "WO4U-13",   "ID",     "WE4MB-3*,WIDE2-1" },

        // one unused alias only, no stars
        { Position_BangPrefix_AltTable,
            "WE4MB-3",   "APNX16", "WIDE2-2" },

        // MIC-E packet: destination encodes position, 4 RF hops
        { MicE_Current,
            "WB7VPC-2",  "S5PR4Q", "W4NAR-2*,WIDE1*,WE4MB-3*,WIDE2*" },

        // 5 real starred hops + trailing unused alias — longest path in corpus
        { "WA4HR-2>APDW17,N8DEU-7*,W4GGM-1*,W4DMM-3*,WIDE1*,KM4BJZ-2*,WE4MB-3*,WIDE2-1:!3502.17NS08645.46W#360/000WA4HR-2",
            "WA4HR-2",   "APDW17", "N8DEU-7*,W4GGM-1*,W4DMM-3*,WIDE1*,KM4BJZ-2*,WE4MB-3*,WIDE2-1" },
    };

    /// <summary>
    /// Packets → (hopCount, hop callsigns in order).
    /// Covers patterns not already exercised in PathParserTests.cs:
    /// internet 0-hop, qAR 0-hop, 2-hop with alias, 1-hop+qAR, digi-before-alias,
    /// 5-hop chain, non-standard TOCALL.
    /// </summary>
    public static TheoryData<string, int, string[]> HopExtractionData => new()
    {
        // internet only — no RF hops
        { Weather_AtPrefix_AllFields,             0, Array.Empty<string>() },

        // qAR igated from RF direct — 0 hops
        { Message_WithId_IgatedDirectRf,          0, Array.Empty<string>() },

        // 2 real hops: KN6RO-13 (no alias) then WE4MB-3 (alias = WIDE2)
        { "YORKSC>APDW16,KN6RO-13*,WE4MB-3*,WIDE2*:!3459.17NI08114.90W#W4PSC DigiGate - York, SC",
          2, new[] { "KN6RO-13", "WE4MB-3" } },

        // 1 real hop + qAR: KQ4HOM-1, then WIDE2-1 unused alias
        { Object_WithCoords,                      1, new[] { "KQ4HOM-1" } },

        // digi-before-alias: K3ODX-10 (unstarred) immediately before WIDE1* (starred alias)
        { "K2KAZ-7>APAT81-1,K3ODX-10,WIDE1*,WIDE2-2,qAR,K3ODX-11::WXBOT    :18330",
          1, new[] { "K3ODX-10" } },

        // 5 real hops: N8DEU-7, W4GGM-1, W4DMM-3 (consumes WIDE1*), KM4BJZ-2, WE4MB-3
        { "WA4HR-2>APDW17,N8DEU-7*,W4GGM-1*,W4DMM-3*,WIDE1*,KM4BJZ-2*,WE4MB-3*,WIDE2-1:!3502.17NS08645.46W#360/000WA4HR-2",
          5, new[] { "N8DEU-7", "W4GGM-1", "W4DMM-3", "KM4BJZ-2", "WE4MB-3" } },

        // TOCALL = "ID" — unconditional TOCALL exclusion still applies, 1 real hop
        { Unparseable_IdTocall,                   1, new[] { "WE4MB-3" } },
    };
}

// ---------------------------------------------------------------------------
// Packet type classification
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that AprsSharp assigns the expected <see cref="AprsPacketType"/> to
/// each structurally distinct real packet from the corpus.
/// </summary>
public class PacketTypeClassificationTests
{
    public static TheoryData<string, AprsPacketType?> Data => new()
    {
        // ── Weather ───────────────────────────────────────────────────────────
        // AprsSharp 0.4.1: @ prefix weather packets report PositionWithTimestamp*
        // type enum even though InfoField is a WeatherInfo instance.
        { RealPacketData.Weather_AtPrefix_AllFields,          AprsPacketType.PositionWithTimestampWithMessaging },
        { RealPacketData.Weather_AtPrefix_LuminosityField,    AprsPacketType.PositionWithTimestampWithMessaging },
        { RealPacketData.Weather_AtPrefix_RfDigipeated,       AprsPacketType.PositionWithTimestampWithMessaging },

        // ── Position ──────────────────────────────────────────────────────────
        { RealPacketData.Position_BangPrefix_AltTable,        AprsPacketType.PositionWithoutTimestampNoMessaging  },
        { RealPacketData.Position_EqualsPrefix_PrimaryTable,  AprsPacketType.PositionWithoutTimestampWithMessaging },
        { RealPacketData.Position_SlashPrefix_TimestampZ,     AprsPacketType.PositionWithTimestampNoMessaging     },
        { RealPacketData.Position_AtPrefix_TimestampZ_AltTable, AprsPacketType.PositionWithTimestampWithMessaging },
        { RealPacketData.Position_SlashPrefix_TimestampH,     AprsPacketType.PositionWithTimestampNoMessaging     },
        { RealPacketData.Position_AtPrefix_TimestampSlash,    AprsPacketType.PositionWithTimestampWithMessaging   },

        // _ weather-station symbol but no c/s/g/t/r... weather data → still Position
        { RealPacketData.Position_WxSymbolNoWxData,           AprsPacketType.PositionWithoutTimestampWithMessaging },
        { RealPacketData.Position_BangPrefix_WxSymbolNoWxData, AprsPacketType.PositionWithoutTimestampNoMessaging  },

        // ── Objects ───────────────────────────────────────────────────────────
        { RealPacketData.Object_WithCoords,                   AprsPacketType.Object },
        { RealPacketData.Object_RepeaterFrequency,            AprsPacketType.Object },

        // ── Status ────────────────────────────────────────────────────────────
        { RealPacketData.Status_PlainText,                    AprsPacketType.Status },
        { RealPacketData.Status_GridSquare_DxInfo,            AprsPacketType.Status },
        { RealPacketData.Status_WithTimestamp,                AprsPacketType.Status },

        // ── Messages ──────────────────────────────────────────────────────────
        { RealPacketData.Message_WithNumericId,               AprsPacketType.Message },
        { RealPacketData.Message_AckReply,                    AprsPacketType.Message },
        // Message_BulletinBln omitted: AprsSharp 0.4.1 throws on construction.
        { RealPacketData.Message_TelemetryBits,               AprsPacketType.Message },

        // ── Telemetry ─────────────────────────────────────────────────────────
        { RealPacketData.Telemetry_T_Hash,                    AprsPacketType.TelemetryData },

        // ── MIC-E ─────────────────────────────────────────────────────────────
        // AprsSharp 0.4.1 uses more specific subtypes than the base MIC-E enums.
        { RealPacketData.MicE_Current,                        AprsPacketType.CurrentMicEDataNotTMD700 },
        { RealPacketData.MicE_Old_PeetBros,                   AprsPacketType.OldMicEDataCurrentTMD700 },
    };

    [Theory]
    [MemberData(nameof(Data))]
    public void Packet_ParsesToExpectedAprsType(string raw, AprsPacketType? expected)
    {
        var packet = new Packet(raw);
        Assert.Equal(expected, packet.InfoField?.Type);
    }

    /// <summary>
    /// Raw weather reports using the _ data-type-identifier prefix (no position) may be
    /// classified as either WeatherReport or PeetBrosUIIWeatherStation depending on
    /// the AprsSharp version.  Either is acceptable and maps to our Weather type.
    /// </summary>
    [Theory]
    [InlineData(RealPacketData.Weather_UnderscorePrefix_RawNoPosition)]
    [InlineData(RealPacketData.Weather_UnderscorePrefix_RfDigipeated)]
    public void RawWeatherPacket_UnderscorePrefix_ParsedAsAWeatherVariant(string raw)
    {
        var type = new Packet(raw).InfoField?.Type;
        Assert.True(
            type is AprsPacketType.WeatherReport or AprsPacketType.PeetBrosUIIWeatherStation,
            $"Expected a weather variant but got {type}");
    }
}

// ---------------------------------------------------------------------------
// ParseTnc2Header — real diverse paths
// ---------------------------------------------------------------------------

/// <summary>
/// Validates <see cref="AprsPathParser.ParseTnc2Header"/> against real corpus packets,
/// covering internet paths, qAR/qAS, multi-hop RF, non-standard TOCALLs, and MIC-E.
/// </summary>
public class ParseTnc2HeaderRealPacketTests
{
    [Theory]
    [MemberData(nameof(RealPacketData.Tnc2HeaderData), MemberType = typeof(RealPacketData))]
    public void ParseTnc2Header_RealPacket_CorrectHeaderExtracted(
        string raw,
        string expectedSource,
        string expectedTocall,
        string expectedRawPath)
    {
        var (source, tocall, rawPath) = AprsPathParser.ParseTnc2Header(raw);

        Assert.Equal(expectedSource, source);
        Assert.Equal(expectedTocall, tocall);
        Assert.Equal(expectedRawPath, rawPath);
    }
}

// ---------------------------------------------------------------------------
// ExtractViaHops — real diverse paths
// ---------------------------------------------------------------------------

/// <summary>
/// Validates <see cref="AprsPathParser.ExtractViaHops"/> against real corpus packets.
/// Covers patterns not already in <see cref="PathParserTests"/>:
/// internet/qAR zero-hop, 2-hop with alias, single digi + qAR, digi-before-alias,
/// 5-hop chain, and TOCALL exclusion with a non-standard TOCALL.
/// </summary>
public class HopExtractionRealPacketTests
{
    [Theory]
    [MemberData(nameof(RealPacketData.HopExtractionData), MemberType = typeof(RealPacketData))]
    public void ExtractViaHops_RealPacket_CorrectHopsExtracted(
        string raw,
        int expectedHopCount,
        string[] expectedCallsigns)
    {
        var aprs = new Packet(raw);
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.Equal(expectedHopCount, hopCount);
        Assert.Equal(expectedCallsigns, hops.Select(h => h.Callsign).ToArray());
    }

    /// <summary>
    /// YORKSC 2-hop: KN6RO-13* has no following alias; WE4MB-3* consumes the WIDE2* alias.
    /// Verifies AliasUsed is set only for the second hop.
    /// </summary>
    [Fact]
    public void ExtractViaHops_TwoHops_FirstNoAlias_SecondConsumesAlias()
    {
        const string raw = "YORKSC>APDW16,KN6RO-13*,WE4MB-3*,WIDE2*:!3459.17NI08114.90W#W4PSC DigiGate - York, SC";
        var aprs = new Packet(raw);
        var (hops, _) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.Equal(2, hops.Count);
        Assert.Null(hops[0].AliasUsed);
        Assert.Equal("WIDE2", hops[1].AliasUsed);
    }

    /// <summary>
    /// K2KAZ-7 real-world digi-before-alias: K3ODX-10 (unstarred) followed by WIDE1* (starred).
    /// Verifies alias is attached to the unstarred callsign preceding the starred alias.
    /// </summary>
    [Fact]
    public void ExtractViaHops_DigiBeforeAlias_RealWorldPacket_AliasAttached()
    {
        const string raw = "K2KAZ-7>APAT81-1,K3ODX-10,WIDE1*,WIDE2-2,qAR,K3ODX-11::WXBOT    :18330";
        var aprs = new Packet(raw);
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.Equal(1, hopCount);
        Assert.Single(hops);
        Assert.Equal("K3ODX-10", hops[0].Callsign);
        Assert.Equal("WIDE1", hops[0].AliasUsed);
    }

    /// <summary>
    /// WA4HR-2 5-hop chain: W4DMM-3* is immediately followed by WIDE1* alias; verifies
    /// only W4DMM-3 gets AliasUsed set, and neighbouring real hops stay null.
    /// </summary>
    [Fact]
    public void ExtractViaHops_FiveHopChain_MiddleHopConsumesAlias()
    {
        const string raw = "WA4HR-2>APDW17,N8DEU-7*,W4GGM-1*,W4DMM-3*,WIDE1*,KM4BJZ-2*,WE4MB-3*,WIDE2-1:!3502.17NS08645.46W#360/000WA4HR-2";
        var aprs = new Packet(raw);
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.Equal(5, hopCount);
        Assert.Equal(5, hops.Count);

        Assert.Equal("N8DEU-7", hops[0].Callsign); Assert.Null(hops[0].AliasUsed);
        Assert.Equal("W4GGM-1", hops[1].Callsign); Assert.Null(hops[1].AliasUsed);
        Assert.Equal("W4DMM-3", hops[2].Callsign); Assert.Equal("WIDE1", hops[2].AliasUsed);
        Assert.Equal("KM4BJZ-2", hops[3].Callsign); Assert.Null(hops[3].AliasUsed);
        Assert.Equal("WE4MB-3", hops[4].Callsign); Assert.Null(hops[4].AliasUsed);
    }
}

// ---------------------------------------------------------------------------
// Weather payload
// ---------------------------------------------------------------------------

/// <summary>
/// Validates that <see cref="WeatherInfo"/> fields parsed from real weather packets
/// match the values encoded in the on-air packet string.
/// AprsSharp units: Temperature °F (int), WindSpeed/Gust mph (int),
/// WindDirection degrees (int), Humidity % (int), BarometricPressure tenths-of-mbar (int).
/// </summary>
public class WeatherPayloadTests
{
    [Theory]
    [MemberData(nameof(RealPacketData.WeatherFieldData), MemberType = typeof(RealPacketData))]
    public void WeatherPacket_ParsesFieldsCorrectly(
        string raw,
        int expectedTempF,
        int expectedWindDir,
        int expectedWindSpeed,
        int expectedWindGust,
        int expectedHumidity,
        double expectedPressureMbar)
    {
        var aprs = new Packet(raw);
        var wx = Assert.IsAssignableFrom<WeatherInfo>(aprs.InfoField);

        Assert.Equal(expectedTempF, wx.Temperature);
        Assert.Equal(expectedWindDir, wx.WindDirection);
        Assert.Equal(expectedWindSpeed, wx.WindSpeed);
        Assert.Equal(expectedWindGust, wx.WindGust);
        Assert.Equal(expectedHumidity, wx.Humidity);
        Assert.NotNull(wx.BarometricPressure);
        Assert.Equal(expectedPressureMbar, wx.BarometricPressure!.Value / 10.0, precision: 1);
    }

    /// <summary>
    /// Weather packets must have rainfall fields populated (may be zero, must not be null).
    /// </summary>
    [Theory]
    [InlineData(RealPacketData.Weather_AtPrefix_AllFields)]
    [InlineData(RealPacketData.Weather_AtPrefix_LuminosityField)]
    [InlineData(RealPacketData.Weather_AtPrefix_RfDigipeated)]
    public void WeatherPacket_RainfallFields_NotNull(string raw)
    {
        var wx = Assert.IsAssignableFrom<WeatherInfo>(new Packet(raw).InfoField);
        Assert.NotNull(wx.Rainfall1Hour);
        Assert.NotNull(wx.Rainfall24Hour);
        Assert.NotNull(wx.RainfallSinceMidnight);
    }
}

// ---------------------------------------------------------------------------
// Position payload
// ---------------------------------------------------------------------------

/// <summary>
/// Validates decoded coordinates from real position packets.
/// Tolerance ±0.001° covers the inherent APRS resolution of 0.01 arc-minutes.
/// </summary>
public class PositionPayloadTests
{
    private const double CoordTolerance = 0.001;

    [Theory]
    [MemberData(nameof(RealPacketData.PositionCoordData), MemberType = typeof(RealPacketData))]
    public void PositionPacket_DecodesCoordinates(string raw, double expectedLat, double expectedLon)
    {
        var aprs = new Packet(raw);
        var pos = Assert.IsAssignableFrom<PositionInfo>(aprs.InfoField);

        var coord = pos.Position!.Coordinates;
        Assert.False(double.IsNaN(coord.Latitude), "Latitude was NaN");
        Assert.False(double.IsNaN(coord.Longitude), "Longitude was NaN");
        Assert.InRange(coord.Latitude, expectedLat - CoordTolerance, expectedLat + CoordTolerance);
        Assert.InRange(coord.Longitude, expectedLon - CoordTolerance, expectedLon + CoordTolerance);
    }

    /// <summary>
    /// Position packets carrying the _ weather-station symbol but no weather data fields
    /// are classified by AprsSharp 0.4.1 as WeatherInfo (with a Position-category type enum).
    /// They have valid position coordinates despite being WeatherInfo instances.
    /// </summary>
    [Theory]
    [InlineData(RealPacketData.Position_WxSymbolNoWxData)]
    [InlineData(RealPacketData.Position_BangPrefix_WxSymbolNoWxData)]
    public void PositionPacket_WxSymbolWithoutWxData_IsWeatherInfoWithValidCoords(string raw)
    {
        var aprs = new Packet(raw);
        // AprsSharp returns WeatherInfo even without weather fields — type enum is Position.
        var wx = Assert.IsAssignableFrom<WeatherInfo>(aprs.InfoField);
        Assert.NotNull(wx.Position);
        Assert.False(double.IsNaN(wx.Position!.Coordinates.Latitude));
        Assert.False(double.IsNaN(wx.Position!.Coordinates.Longitude));
    }
}

// ---------------------------------------------------------------------------
// Message payload
// ---------------------------------------------------------------------------

/// <summary>
/// Validates addressee, body, and message ID extracted from real message packets.
/// Addresses are trimmed for comparison because APRS pads them to 9 characters.
/// </summary>
public class MessagePayloadTests
{
    [Theory]
    [MemberData(nameof(RealPacketData.MessageFieldData), MemberType = typeof(RealPacketData))]
    public void MessagePacket_ParsesFieldsCorrectly(
        string raw,
        string expectedAddressee,
        string expectedBody,
        string expectedMessageId)
    {
        var aprs = new Packet(raw);
        var msg = Assert.IsAssignableFrom<MessageInfo>(aprs.InfoField);

        Assert.Equal(expectedAddressee, msg.Addressee?.Trim());
        Assert.Equal(expectedBody, msg.Content);
        Assert.Equal(expectedMessageId, msg.Id ?? string.Empty);
    }

    /// <summary>
    /// All message packets in the corpus must produce a non-null addressee.
    /// </summary>
    [Theory]
    [InlineData(RealPacketData.Message_WithNumericId)]
    [InlineData(RealPacketData.Message_AckReply)]
    [InlineData(RealPacketData.Message_WithAlphanumericId)]
    [InlineData(RealPacketData.Message_NoId_WxBotResponse)]
    [InlineData(RealPacketData.Message_WithId_IgatedDirectRf)]
    // Message_BulletinBln omitted: AprsSharp 0.4.1 throws on construction.
    [InlineData(RealPacketData.Message_TelemetryBits)]
    public void MessagePacket_AddresseeIsNotNull(string raw)
    {
        var msg = Assert.IsAssignableFrom<MessageInfo>(new Packet(raw).InfoField);
        Assert.NotNull(msg.Addressee);
        Assert.NotEmpty(msg.Addressee!.Trim());
    }
}

// ---------------------------------------------------------------------------
// Status payload
// ---------------------------------------------------------------------------

/// <summary>
/// Validates comment text extracted from real status packets.
/// </summary>
public class StatusPayloadTests
{
    /// <summary>
    /// Plain-text status (no embedded grid square or timestamp) — full comment returned as-is.
    /// </summary>
    [Fact]
    public void StatusPacket_PlainText_ReturnsFullComment()
    {
        var aprs = new Packet(RealPacketData.Status_PlainText);
        var status = Assert.IsAssignableFrom<StatusInfo>(aprs.InfoField);
        Assert.Equal("WIDE 3-# Digi/Igate", status.Comment);
    }

    /// <summary>
    /// Status with Maidenhead grid square prefix — the substantive text after grid/symbol
    /// must be present in the comment field.
    /// </summary>
    [Fact]
    public void StatusPacket_WithGridSquare_CommentContainsDxText()
    {
        var aprs = new Packet(RealPacketData.Status_GridSquare_DxInfo);
        var status = Assert.IsAssignableFrom<StatusInfo>(aprs.InfoField);
        Assert.NotNull(status.Comment);
        Assert.Contains("AJ4FJ-5", status.Comment);
    }

    /// <summary>
    /// Status with a leading timestamp (DHMMSSz) — the free-text portion must survive.
    /// </summary>
    [Fact]
    public void StatusPacket_WithTimestampPrefix_CommentPreservesText()
    {
        var aprs = new Packet(RealPacketData.Status_WithTimestamp);
        var status = Assert.IsAssignableFrom<StatusInfo>(aprs.InfoField);
        Assert.NotNull(status.Comment);
        Assert.Contains("Expect Winter Weather this weekend", status.Comment);
    }
}

// ---------------------------------------------------------------------------
// Unparseable / unusual packets
// ---------------------------------------------------------------------------

/// <summary>
/// Documents how AprsSharp handles packets that carry no recognised APRS payload type.
/// In production the parsing service catches all exceptions and marks these Unparseable;
/// these tests verify the observable AprsSharp behaviour for each category.
/// </summary>
public class UnparseablePacketTests
{
    /// <summary>
    /// An "ID" TOCALL packet carries free-text in the info field beginning with a callsign
    /// character ('W', 'K', etc.).  AprsSharp should return null or Unknown for the type —
    /// it must not misidentify it as a position or weather packet.
    /// </summary>
    [Fact]
    public void IdTocallPacket_TypeIsNullOrUnknown_NotAPositionOrWeather()
    {
        var packet = new Packet(RealPacketData.Unparseable_IdTocall);
        var type = packet.InfoField?.Type;

        Assert.True(
            type is null or AprsPacketType.Unknown,
            $"Expected null or Unknown for ID-TOCALL packet but got {type}");
    }

    /// <summary>
    /// Third-party traffic packets (} prefix).
    /// AprsSharp 0.4.1 returns ThirdPartyTraffic (not a structured payload type).
    /// The parsing service's Unparseable fallback handles it correctly.
    /// </summary>
    [Fact]
    public void ThirdPartyPacket_DoesNotReturnPositionOrWeatherType()
    {
        AprsPacketType? type = null;
        var ex = Record.Exception(() =>
        {
            var packet = new Packet(RealPacketData.Unparseable_ThirdParty);
            type = packet.InfoField?.Type;
        });

        if (ex is null)
        {
            // Parsed without exception — type must not be a structured payload type
            Assert.True(
                type is null or AprsPacketType.Unknown or AprsPacketType.ThirdPartyTraffic,
                $"Third-party packet classified as {type}; expected null, Unknown, or ThirdPartyTraffic");
        }
        // If an exception was thrown, that is also acceptable behaviour
        // (the service catches it and marks the packet Unparseable).
    }

    /// <summary>
    /// BLN* bulletin message addresses cause AprsSharp 0.4.1 to throw ArgumentException.
    /// The parsing service catches this and marks the packet Unparseable.
    /// </summary>
    [Fact]
    public void BulletinBln_PacketConstruction_Throws()
    {
        var ex = Record.Exception(() => new Packet(RealPacketData.Message_BulletinBln));
        Assert.NotNull(ex);
        Assert.IsType<ArgumentException>(ex);
    }

    /// <summary>
    /// ParseTnc2Header must never throw on any packet in the corpus,
    /// regardless of how unusual the TOCALL or info field is.
    /// </summary>
    [Theory]
    [InlineData(RealPacketData.Unparseable_IdTocall)]
    [InlineData(RealPacketData.Unparseable_ThirdParty)]
    [InlineData(RealPacketData.MicE_Current)]
    [InlineData(RealPacketData.MicE_Old_PeetBros)]
    [InlineData(RealPacketData.Message_BulletinBln)]
    public void ParseTnc2Header_UnusualPackets_NeverThrows(string raw)
    {
        var ex = Record.Exception(() => AprsPathParser.ParseTnc2Header(raw));
        Assert.Null(ex);
    }
}
