using AprsSharp.AprsParser;
using DireControl.Api.Services;
using DireControl.PathParsing;
using NUnit.Framework;
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

    // ── Gateway / digital voice ──────────────────────────────────────────────

    // D-Star gateway: TOCALL APDG02, overlay 'D', & gateway symbol, frequency + mode in comment
    public const string Position_DStarGateway =
        "N4UUJ-C>APDG02,TCPIP*,qAC,N4UUJ-CS:!3521.92ND08539.79W&RNG0001/A=000010 2m Voice (D-Star) 144.96000MHz +0.0000MHz";

    // DMR gateway: TOCALL APDMR (hypothetical but plausible), frequency in comment
    public const string Position_DmrGateway =
        "W4DMR-1>APDMR1,TCPIP*,qAC,T2BC:!3520.00N/08530.00W&DMR Repeater 442.55000MHz +5.0000MHz";

    // ── Unparseable / unusual ─────────────────────────────────────────────────

    // TOCALL is "ID" (not a standard APRS TOCALL); info starts with 'W' (no APRS type byte)
    public const string Unparseable_IdTocall =
        "WO4U-13>ID,WE4MB-3*,WIDE2-1:WO4U-13/R RELAY/D WO4U-1/B";

    // } prefix = third-party traffic encapsulation; AprsSharp may return Unknown or throw
    public const string Unparseable_ThirdParty =
        "AC4AG-4>APMI06,WE4MB-3*,WIDE2*:}KK4KTV-13>APRS,TCPIP,AC4AG-4*:@020501z3720.22N/08453.10W_042/005g007t038r000p000P000h77b10248L000AmbientCWOP.com";

    // ── Shared TestCaseSource data ──────────────────────────────────────────

    /// <summary>
    /// Weather packets → (tempF, windDirDeg, windSpeedMph, windGustMph, humidityPct, pressureMbar).
    /// pressureMbar = AprsSharp BarometricPressure / 10.0.
    /// </summary>
    public static IEnumerable<TestCaseData> WeatherFieldData()
    {
        yield return new TestCaseData(Weather_AtPrefix_AllFields,        54, 269, 5, 9, 89, 1021.9);
        yield return new TestCaseData(Weather_AtPrefix_LuminosityField,  47,  89, 0, 0, 95, 1026.1);
        yield return new TestCaseData(Weather_AtPrefix_RfDigipeated,     55,  29, 0, 0, 75, 1019.1);
        // Note: _ prefix packets (WO4U-13, AJ4FJ-13) have Type=WeatherReport but
        // InfoField is UnsupportedInfo in AprsSharp 0.4.1 — cannot cast to WeatherInfo.
        // They are exercised only in RawWeatherPacket_UnderscorePrefix_ParsedAsAWeatherVariant.
    }

    /// <summary>
    /// Position packets → expected decimal-degree coordinates (tolerance ±0.001°).
    /// </summary>
    public static IEnumerable<TestCaseData> PositionCoordData()
    {
        yield return new TestCaseData(Position_BangPrefix_AltTable,          35.2452, -84.9753);
        yield return new TestCaseData(Position_EqualsPrefix_PrimaryTable,    34.9360, -85.1060);
        yield return new TestCaseData(Position_SlashPrefix_TimestampZ,       35.0727, -85.1900);
        yield return new TestCaseData(Position_AtPrefix_TimestampZ_AltTable, 34.5197, -84.3433);
        yield return new TestCaseData(Position_SlashPrefix_TimestampH,       36.0242, -86.4860);
        yield return new TestCaseData(Position_DStarGateway,                 35.3653, -85.6632);
    }

    /// <summary>
    /// Message packets → (addressee trimmed, body, messageId — empty string when absent).
    /// </summary>
    public static IEnumerable<TestCaseData> MessageFieldData()
    {
        yield return new TestCaseData(Message_WithNumericId,         "N1YKT-7",  "Was up brother",                                            "108");
        yield return new TestCaseData(Message_AckReply,              "KE9BPC-7", "ack3DF4A",                                                  "");
        yield return new TestCaseData(Message_WithAlphanumericId,    "WY4RC-67", "CQ CQ CQ KE9BPC calling CQ",                               "3DF4A");
        yield return new TestCaseData(Message_NoId_WxBotResponse,    "K2KAZ-7",  "Effort PA. Tonight,Rain Likely and Patchy Fog 60% Low 33", "");
        yield return new TestCaseData(Message_WithId_IgatedDirectRf, "WB2BWU-2", "Please list (QTC #)",                                      "24805");
        // Message_BulletinBln omitted: AprsSharp 0.4.1 throws ArgumentException on construction
        // for BLN* bulletin addresses — tested separately in UnparseablePacketTests.
        yield return new TestCaseData(Message_TelemetryBits,         "KN6RO-13", "BITS.11111111,KN6RO-13 Telemetry",                        "");
    }

    /// <summary>
    /// Packets → (source, tocall, rawPath) for ParseTnc2Header.
    /// Covers internet paths, qAR/qAS, multi-hop RF, ID TOCALL, MIC-E destination.
    /// </summary>
    public static IEnumerable<TestCaseData> Tnc2HeaderData()
    {
        // internet only — TOCALL APRS, path starts with TCPIP*
        yield return new TestCaseData(Weather_AtPrefix_AllFields,
            "N4YH-13",   "APRS",   "TCPIP*,qAC,T2TEXAS");

        // qAR igated from RF direct
        yield return new TestCaseData(Message_WithId_IgatedDirectRf,
            "NTSGTE",    "APN20H", "qAR,WZ0C-4");

        // qAS (server-added), same callsign as igate
        yield return new TestCaseData(Position_AtPrefix_TimestampZ_AltTable,
            "AK4ZX-15",  "APMI06", "TCPIP*,qAS,AK4ZX");

        // single real digi + unused alias + qAR + igate callsign
        yield return new TestCaseData(Object_WithCoords,
            "KC4OJS-3",  "APU25N", "KQ4HOM-1*,WIDE2-1,qAR,KJ4G-2");

        // TOCALL = "ID" (non-standard)
        yield return new TestCaseData(Unparseable_IdTocall,
            "WO4U-13",   "ID",     "WE4MB-3*,WIDE2-1");

        // one unused alias only, no stars
        yield return new TestCaseData(Position_BangPrefix_AltTable,
            "WE4MB-3",   "APNX16", "WIDE2-2");

        // MIC-E packet: destination encodes position, 4 RF hops
        yield return new TestCaseData(MicE_Current,
            "WB7VPC-2",  "S5PR4Q", "W4NAR-2*,WIDE1*,WE4MB-3*,WIDE2*");

        // 5 real starred hops + trailing unused alias — longest path in corpus
        yield return new TestCaseData("WA4HR-2>APDW17,N8DEU-7*,W4GGM-1*,W4DMM-3*,WIDE1*,KM4BJZ-2*,WE4MB-3*,WIDE2-1:!3502.17NS08645.46W#360/000WA4HR-2",
            "WA4HR-2",   "APDW17", "N8DEU-7*,W4GGM-1*,W4DMM-3*,WIDE1*,KM4BJZ-2*,WE4MB-3*,WIDE2-1");
    }

    /// <summary>
    /// Packets → (hopCount, hop callsigns in order).
    /// Covers patterns not already exercised in PathParserTests.cs:
    /// internet 0-hop, qAR 0-hop, 2-hop with alias, 1-hop+qAR, digi-before-alias,
    /// 5-hop chain, non-standard TOCALL.
    /// </summary>
    public static IEnumerable<TestCaseData> HopExtractionData()
    {
        // internet only — no RF hops
        yield return new TestCaseData(Weather_AtPrefix_AllFields,             0, Array.Empty<string>());

        // qAR igated from RF direct — 0 RF hops, igate callsign included
        yield return new TestCaseData(Message_WithId_IgatedDirectRf,          0, new[] { "WZ0C-4" });

        // 2 real hops: KN6RO-13 (no alias) then WE4MB-3 (alias = WIDE2)
        yield return new TestCaseData("YORKSC>APDW16,KN6RO-13*,WE4MB-3*,WIDE2*:!3459.17NI08114.90W#W4PSC DigiGate - York, SC",
          2, new[] { "KN6RO-13", "WE4MB-3" });

        // 1 real hop + qAR: KQ4HOM-1, then WIDE2-1 unused alias, igate KJ4G-2
        yield return new TestCaseData(Object_WithCoords,                      1, new[] { "KQ4HOM-1", "KJ4G-2" });

        // digi-before-alias: K3ODX-10 (unstarred) immediately before WIDE1* (starred alias), igate K3ODX-11
        yield return new TestCaseData("K2KAZ-7>APAT81-1,K3ODX-10,WIDE1*,WIDE2-2,qAR,K3ODX-11::WXBOT    :18330",
          1, new[] { "K3ODX-10", "K3ODX-11" });

        // 5 real hops: N8DEU-7, W4GGM-1, W4DMM-3 (consumes WIDE1*), KM4BJZ-2, WE4MB-3
        yield return new TestCaseData("WA4HR-2>APDW17,N8DEU-7*,W4GGM-1*,W4DMM-3*,WIDE1*,KM4BJZ-2*,WE4MB-3*,WIDE2-1:!3502.17NS08645.46W#360/000WA4HR-2",
          5, new[] { "N8DEU-7", "W4GGM-1", "W4DMM-3", "KM4BJZ-2", "WE4MB-3" });

        // TOCALL = "ID" — unconditional TOCALL exclusion still applies, 1 real hop
        yield return new TestCaseData(Unparseable_IdTocall,                   1, new[] { "WE4MB-3" });
    }
}

// ---------------------------------------------------------------------------
// Packet type classification
// ---------------------------------------------------------------------------

/// <summary>
/// Verifies that AprsSharp assigns the expected <see cref="AprsPacketType"/> to
/// each structurally distinct real packet from the corpus.
/// </summary>
[TestFixture]
public class PacketTypeClassificationTests
{
    private static IEnumerable<TestCaseData> Data()
    {
        // ── Weather ───────────────────────────────────────────────────────────
        // AprsSharp 0.4.1: @ prefix weather packets report PositionWithTimestamp*
        // type enum even though InfoField is a WeatherInfo instance.
        yield return new TestCaseData(RealPacketData.Weather_AtPrefix_AllFields,          AprsPacketType.PositionWithTimestampWithMessaging);
        yield return new TestCaseData(RealPacketData.Weather_AtPrefix_LuminosityField,    AprsPacketType.PositionWithTimestampWithMessaging);
        yield return new TestCaseData(RealPacketData.Weather_AtPrefix_RfDigipeated,       AprsPacketType.PositionWithTimestampWithMessaging);

        // ── Position ──────────────────────────────────────────────────────────
        yield return new TestCaseData(RealPacketData.Position_BangPrefix_AltTable,        AprsPacketType.PositionWithoutTimestampNoMessaging);
        yield return new TestCaseData(RealPacketData.Position_EqualsPrefix_PrimaryTable,  AprsPacketType.PositionWithoutTimestampWithMessaging);
        yield return new TestCaseData(RealPacketData.Position_SlashPrefix_TimestampZ,     AprsPacketType.PositionWithTimestampNoMessaging);
        yield return new TestCaseData(RealPacketData.Position_AtPrefix_TimestampZ_AltTable, AprsPacketType.PositionWithTimestampWithMessaging);
        yield return new TestCaseData(RealPacketData.Position_SlashPrefix_TimestampH,     AprsPacketType.PositionWithTimestampNoMessaging);
        yield return new TestCaseData(RealPacketData.Position_AtPrefix_TimestampSlash,    AprsPacketType.PositionWithTimestampWithMessaging);

        // _ weather-station symbol but no c/s/g/t/r... weather data → still Position
        yield return new TestCaseData(RealPacketData.Position_WxSymbolNoWxData,           AprsPacketType.PositionWithoutTimestampWithMessaging);
        yield return new TestCaseData(RealPacketData.Position_BangPrefix_WxSymbolNoWxData, AprsPacketType.PositionWithoutTimestampNoMessaging);

        // ── Objects ───────────────────────────────────────────────────────────
        yield return new TestCaseData(RealPacketData.Object_WithCoords,                   AprsPacketType.Object);
        yield return new TestCaseData(RealPacketData.Object_RepeaterFrequency,            AprsPacketType.Object);

        // ── Status ────────────────────────────────────────────────────────────
        yield return new TestCaseData(RealPacketData.Status_PlainText,                    AprsPacketType.Status);
        yield return new TestCaseData(RealPacketData.Status_GridSquare_DxInfo,            AprsPacketType.Status);
        yield return new TestCaseData(RealPacketData.Status_WithTimestamp,                AprsPacketType.Status);

        // ── Messages ──────────────────────────────────────────────────────────
        yield return new TestCaseData(RealPacketData.Message_WithNumericId,               AprsPacketType.Message);
        yield return new TestCaseData(RealPacketData.Message_AckReply,                    AprsPacketType.Message);
        // Message_BulletinBln omitted: AprsSharp 0.4.1 throws on construction.
        yield return new TestCaseData(RealPacketData.Message_TelemetryBits,               AprsPacketType.Message);

        // ── Telemetry ─────────────────────────────────────────────────────────
        yield return new TestCaseData(RealPacketData.Telemetry_T_Hash,                    AprsPacketType.TelemetryData);

        // ── MIC-E ─────────────────────────────────────────────────────────────
        // AprsSharp 0.4.1 uses more specific subtypes than the base MIC-E enums.
        yield return new TestCaseData(RealPacketData.MicE_Current,                        AprsPacketType.CurrentMicEDataNotTMD700);
        yield return new TestCaseData(RealPacketData.MicE_Old_PeetBros,                   AprsPacketType.OldMicEDataCurrentTMD700);
    }

    [TestCaseSource(nameof(Data))]
    public void Packet_ParsesToExpectedAprsType(string raw, AprsPacketType? expected)
    {
        var packet = new Packet(raw);
        Assert.That(packet.InfoField?.Type, Is.EqualTo(expected));
    }

    /// <summary>
    /// Raw weather reports using the _ data-type-identifier prefix (no position) may be
    /// classified as either WeatherReport or PeetBrosUIIWeatherStation depending on
    /// the AprsSharp version.  Either is acceptable and maps to our Weather type.
    /// </summary>
    [TestCase(RealPacketData.Weather_UnderscorePrefix_RawNoPosition)]
    [TestCase(RealPacketData.Weather_UnderscorePrefix_RfDigipeated)]
    public void RawWeatherPacket_UnderscorePrefix_ParsedAsAWeatherVariant(string raw)
    {
        var type = new Packet(raw).InfoField?.Type;
        Assert.That(
            type is AprsPacketType.WeatherReport or AprsPacketType.PeetBrosUIIWeatherStation,
            Is.True,
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
[TestFixture]
public class ParseTnc2HeaderRealPacketTests
{
    [TestCaseSource(typeof(RealPacketData), nameof(RealPacketData.Tnc2HeaderData))]
    public void ParseTnc2Header_RealPacket_CorrectHeaderExtracted(
        string raw,
        string expectedSource,
        string expectedTocall,
        string expectedRawPath)
    {
        var (source, tocall, rawPath) = AprsPathParser.ParseTnc2Header(raw);

        Assert.That(source, Is.EqualTo(expectedSource));
        Assert.That(tocall, Is.EqualTo(expectedTocall));
        Assert.That(rawPath, Is.EqualTo(expectedRawPath));
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
[TestFixture]
public class HopExtractionRealPacketTests
{
    [TestCaseSource(typeof(RealPacketData), nameof(RealPacketData.HopExtractionData))]
    public void ExtractViaHops_RealPacket_CorrectHopsExtracted(
        string raw,
        int expectedHopCount,
        string[] expectedCallsigns)
    {
        var aprs = new Packet(raw);
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.That(hopCount, Is.EqualTo(expectedHopCount));
        Assert.That(hops.Select(h => h.Callsign).ToArray(), Is.EqualTo(expectedCallsigns));
    }

    /// <summary>
    /// YORKSC 2-hop: KN6RO-13* has no following alias; WE4MB-3* consumes the WIDE2* alias.
    /// Verifies AliasUsed is set only for the second hop.
    /// </summary>
    [Test]
    public void ExtractViaHops_TwoHops_FirstNoAlias_SecondConsumesAlias()
    {
        const string raw = "YORKSC>APDW16,KN6RO-13*,WE4MB-3*,WIDE2*:!3459.17NI08114.90W#W4PSC DigiGate - York, SC";
        var aprs = new Packet(raw);
        var (hops, _) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.That(hops.Count, Is.EqualTo(2));
        Assert.That(hops[0].AliasUsed, Is.Null);
        Assert.That(hops[1].AliasUsed, Is.EqualTo("WIDE2"));
    }

    /// <summary>
    /// K2KAZ-7 real-world digi-before-alias: K3ODX-10 (unstarred) followed by WIDE1* (starred).
    /// Verifies alias is attached to the unstarred callsign preceding the starred alias.
    /// </summary>
    [Test]
    public void ExtractViaHops_DigiBeforeAlias_RealWorldPacket_AliasAttached()
    {
        const string raw = "K2KAZ-7>APAT81-1,K3ODX-10,WIDE1*,WIDE2-2,qAR,K3ODX-11::WXBOT    :18330";
        var aprs = new Packet(raw);
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.That(hopCount, Is.EqualTo(1));
        Assert.That(hops.Count, Is.EqualTo(2));
        Assert.That(hops[0].Callsign, Is.EqualTo("K3ODX-10"));
        Assert.That(hops[0].AliasUsed, Is.EqualTo("WIDE1"));
        Assert.That(hops[0].IsIgate, Is.False);
        Assert.That(hops[1].Callsign, Is.EqualTo("K3ODX-11"));
        Assert.That(hops[1].IsIgate, Is.True);
    }

    /// <summary>
    /// WA4HR-2 5-hop chain: W4DMM-3* is immediately followed by WIDE1* alias; verifies
    /// only W4DMM-3 gets AliasUsed set, and neighbouring real hops stay null.
    /// </summary>
    [Test]
    public void ExtractViaHops_FiveHopChain_MiddleHopConsumesAlias()
    {
        const string raw = "WA4HR-2>APDW17,N8DEU-7*,W4GGM-1*,W4DMM-3*,WIDE1*,KM4BJZ-2*,WE4MB-3*,WIDE2-1:!3502.17NS08645.46W#360/000WA4HR-2";
        var aprs = new Packet(raw);
        var (hops, hopCount) = AprsPathParser.ExtractViaHops(aprs.Path);

        Assert.That(hopCount, Is.EqualTo(5));
        Assert.That(hops.Count, Is.EqualTo(5));

        Assert.That(hops[0].Callsign, Is.EqualTo("N8DEU-7")); Assert.That(hops[0].AliasUsed, Is.Null);
        Assert.That(hops[1].Callsign, Is.EqualTo("W4GGM-1")); Assert.That(hops[1].AliasUsed, Is.Null);
        Assert.That(hops[2].Callsign, Is.EqualTo("W4DMM-3")); Assert.That(hops[2].AliasUsed, Is.EqualTo("WIDE1"));
        Assert.That(hops[3].Callsign, Is.EqualTo("KM4BJZ-2")); Assert.That(hops[3].AliasUsed, Is.Null);
        Assert.That(hops[4].Callsign, Is.EqualTo("WE4MB-3")); Assert.That(hops[4].AliasUsed, Is.Null);
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
[TestFixture]
public class WeatherPayloadTests
{
    [TestCaseSource(typeof(RealPacketData), nameof(RealPacketData.WeatherFieldData))]
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
        var wx = aprs.InfoField as WeatherInfo;
        Assert.That(wx, Is.Not.Null);
        Assert.That(wx, Is.AssignableTo<WeatherInfo>());

        Assert.That(wx!.Temperature, Is.EqualTo(expectedTempF));
        Assert.That(wx.WindDirection, Is.EqualTo(expectedWindDir));
        Assert.That(wx.WindSpeed, Is.EqualTo(expectedWindSpeed));
        Assert.That(wx.WindGust, Is.EqualTo(expectedWindGust));
        Assert.That(wx.Humidity, Is.EqualTo(expectedHumidity));
        Assert.That(wx.BarometricPressure, Is.Not.Null);
        Assert.That(wx.BarometricPressure!.Value / 10.0, Is.EqualTo(expectedPressureMbar).Within(0.1));
    }

    /// <summary>
    /// Weather packets must have rainfall fields populated (may be zero, must not be null).
    /// </summary>
    [TestCase(RealPacketData.Weather_AtPrefix_AllFields)]
    [TestCase(RealPacketData.Weather_AtPrefix_LuminosityField)]
    [TestCase(RealPacketData.Weather_AtPrefix_RfDigipeated)]
    public void WeatherPacket_RainfallFields_NotNull(string raw)
    {
        var wx = new Packet(raw).InfoField as WeatherInfo;
        Assert.That(wx, Is.Not.Null);
        Assert.That(wx, Is.AssignableTo<WeatherInfo>());
        Assert.That(wx!.Rainfall1Hour, Is.Not.Null);
        Assert.That(wx.Rainfall24Hour, Is.Not.Null);
        Assert.That(wx.RainfallSinceMidnight, Is.Not.Null);
    }
}

// ---------------------------------------------------------------------------
// Position payload
// ---------------------------------------------------------------------------

/// <summary>
/// Validates decoded coordinates from real position packets.
/// Tolerance ±0.001° covers the inherent APRS resolution of 0.01 arc-minutes.
/// </summary>
[TestFixture]
public class PositionPayloadTests
{
    private const double CoordTolerance = 0.001;

    [TestCaseSource(typeof(RealPacketData), nameof(RealPacketData.PositionCoordData))]
    public void PositionPacket_DecodesCoordinates(string raw, double expectedLat, double expectedLon)
    {
        var aprs = new Packet(raw);
        var pos = aprs.InfoField as PositionInfo;
        Assert.That(pos, Is.Not.Null);
        Assert.That(pos, Is.AssignableTo<PositionInfo>());

        var coord = pos!.Position!.Coordinates;
        Assert.That(double.IsNaN(coord.Latitude), Is.False, "Latitude was NaN");
        Assert.That(double.IsNaN(coord.Longitude), Is.False, "Longitude was NaN");
        Assert.That(coord.Latitude, Is.InRange(expectedLat - CoordTolerance, expectedLat + CoordTolerance));
        Assert.That(coord.Longitude, Is.InRange(expectedLon - CoordTolerance, expectedLon + CoordTolerance));
    }

    /// <summary>
    /// Position packets carrying the _ weather-station symbol but no weather data fields
    /// are classified by AprsSharp 0.4.1 as WeatherInfo (with a Position-category type enum).
    /// They have valid position coordinates despite being WeatherInfo instances.
    /// </summary>
    [TestCase(RealPacketData.Position_WxSymbolNoWxData)]
    [TestCase(RealPacketData.Position_BangPrefix_WxSymbolNoWxData)]
    public void PositionPacket_WxSymbolWithoutWxData_IsWeatherInfoWithValidCoords(string raw)
    {
        var aprs = new Packet(raw);
        // AprsSharp returns WeatherInfo even without weather fields — type enum is Position.
        var wx = aprs.InfoField as WeatherInfo;
        Assert.That(wx, Is.Not.Null);
        Assert.That(wx, Is.AssignableTo<WeatherInfo>());
        Assert.That(wx!.Position, Is.Not.Null);
        Assert.That(double.IsNaN(wx.Position!.Coordinates.Latitude), Is.False);
        Assert.That(double.IsNaN(wx.Position!.Coordinates.Longitude), Is.False);
    }
}

// ---------------------------------------------------------------------------
// Message payload
// ---------------------------------------------------------------------------

/// <summary>
/// Validates addressee, body, and message ID extracted from real message packets.
/// Addresses are trimmed for comparison because APRS pads them to 9 characters.
/// </summary>
[TestFixture]
public class MessagePayloadTests
{
    [TestCaseSource(typeof(RealPacketData), nameof(RealPacketData.MessageFieldData))]
    public void MessagePacket_ParsesFieldsCorrectly(
        string raw,
        string expectedAddressee,
        string expectedBody,
        string expectedMessageId)
    {
        var aprs = new Packet(raw);
        var msg = aprs.InfoField as MessageInfo;
        Assert.That(msg, Is.Not.Null);
        Assert.That(msg, Is.AssignableTo<MessageInfo>());

        Assert.That(msg!.Addressee?.Trim(), Is.EqualTo(expectedAddressee));
        Assert.That(msg.Content, Is.EqualTo(expectedBody));
        Assert.That(msg.Id ?? string.Empty, Is.EqualTo(expectedMessageId));
    }

    /// <summary>
    /// All message packets in the corpus must produce a non-null addressee.
    /// </summary>
    [TestCase(RealPacketData.Message_WithNumericId)]
    [TestCase(RealPacketData.Message_AckReply)]
    [TestCase(RealPacketData.Message_WithAlphanumericId)]
    [TestCase(RealPacketData.Message_NoId_WxBotResponse)]
    [TestCase(RealPacketData.Message_WithId_IgatedDirectRf)]
    // Message_BulletinBln omitted: AprsSharp 0.4.1 throws on construction.
    [TestCase(RealPacketData.Message_TelemetryBits)]
    public void MessagePacket_AddresseeIsNotNull(string raw)
    {
        var msg = new Packet(raw).InfoField as MessageInfo;
        Assert.That(msg, Is.Not.Null);
        Assert.That(msg, Is.AssignableTo<MessageInfo>());
        Assert.That(msg!.Addressee, Is.Not.Null);
        Assert.That(msg.Addressee!.Trim(), Is.Not.Empty);
    }
}

// ---------------------------------------------------------------------------
// Status payload
// ---------------------------------------------------------------------------

/// <summary>
/// Validates comment text extracted from real status packets.
/// </summary>
[TestFixture]
public class StatusPayloadTests
{
    /// <summary>
    /// Plain-text status (no embedded grid square or timestamp) — full comment returned as-is.
    /// </summary>
    [Test]
    public void StatusPacket_PlainText_ReturnsFullComment()
    {
        var aprs = new Packet(RealPacketData.Status_PlainText);
        var status = aprs.InfoField as StatusInfo;
        Assert.That(status, Is.Not.Null);
        Assert.That(status, Is.AssignableTo<StatusInfo>());
        Assert.That(status!.Comment, Is.EqualTo("WIDE 3-# Digi/Igate"));
    }

    /// <summary>
    /// Status with Maidenhead grid square prefix — the substantive text after grid/symbol
    /// must be present in the comment field.
    /// </summary>
    [Test]
    public void StatusPacket_WithGridSquare_CommentContainsDxText()
    {
        var aprs = new Packet(RealPacketData.Status_GridSquare_DxInfo);
        var status = aprs.InfoField as StatusInfo;
        Assert.That(status, Is.Not.Null);
        Assert.That(status, Is.AssignableTo<StatusInfo>());
        Assert.That(status!.Comment, Is.Not.Null);
        Assert.That(status.Comment, Does.Contain("AJ4FJ-5"));
    }

    /// <summary>
    /// Status with a leading timestamp (DHMMSSz) — the free-text portion must survive.
    /// </summary>
    [Test]
    public void StatusPacket_WithTimestampPrefix_CommentPreservesText()
    {
        var aprs = new Packet(RealPacketData.Status_WithTimestamp);
        var status = aprs.InfoField as StatusInfo;
        Assert.That(status, Is.Not.Null);
        Assert.That(status, Is.AssignableTo<StatusInfo>());
        Assert.That(status!.Comment, Is.Not.Null);
        Assert.That(status.Comment, Does.Contain("Expect Winter Weather this weekend"));
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
[TestFixture]
public class UnparseablePacketTests
{
    /// <summary>
    /// An "ID" TOCALL packet carries free-text in the info field beginning with a callsign
    /// character ('W', 'K', etc.).  AprsSharp should return null or Unknown for the type —
    /// it must not misidentify it as a position or weather packet.
    /// </summary>
    [Test]
    public void IdTocallPacket_TypeIsNullOrUnknown_NotAPositionOrWeather()
    {
        var packet = new Packet(RealPacketData.Unparseable_IdTocall);
        var type = packet.InfoField?.Type;

        Assert.That(
            type is null or AprsPacketType.Unknown,
            Is.True,
            $"Expected null or Unknown for ID-TOCALL packet but got {type}");
    }

    /// <summary>
    /// Third-party traffic packets (} prefix).
    /// AprsSharp 0.4.1 returns ThirdPartyTraffic (not a structured payload type).
    /// The parsing service's Unparseable fallback handles it correctly.
    /// </summary>
    [Test]
    public void ThirdPartyPacket_DoesNotReturnPositionOrWeatherType()
    {
        AprsPacketType? type = null;
        Exception? ex = null;
        try
        {
            var packet = new Packet(RealPacketData.Unparseable_ThirdParty);
            type = packet.InfoField?.Type;
        }
        catch (Exception e)
        {
            ex = e;
        }

        if (ex is null)
        {
            // Parsed without exception — type must not be a structured payload type
            Assert.That(
                type is null or AprsPacketType.Unknown or AprsPacketType.ThirdPartyTraffic,
                Is.True,
                $"Third-party packet classified as {type}; expected null, Unknown, or ThirdPartyTraffic");
        }
        // If an exception was thrown, that is also acceptable behaviour
        // (the service catches it and marks the packet Unparseable).
    }

    /// <summary>
    /// BLN* bulletin message addresses previously caused AprsSharp to throw ArgumentException.
    /// Now the parser gracefully falls back to UnsupportedInfo instead of throwing.
    /// </summary>
    [Test]
    public void BulletinBln_PacketConstruction_DoesNotThrow()
    {
        Assert.DoesNotThrow(() => new Packet(RealPacketData.Message_BulletinBln));
    }

    /// <summary>
    /// ParseTnc2Header must never throw on any packet in the corpus,
    /// regardless of how unusual the TOCALL or info field is.
    /// </summary>
    [TestCase(RealPacketData.Unparseable_IdTocall)]
    [TestCase(RealPacketData.Unparseable_ThirdParty)]
    [TestCase(RealPacketData.MicE_Current)]
    [TestCase(RealPacketData.MicE_Old_PeetBros)]
    [TestCase(RealPacketData.Message_BulletinBln)]
    public void ParseTnc2Header_UnusualPackets_NeverThrows(string raw)
    {
        Assert.DoesNotThrow(() => AprsPathParser.ParseTnc2Header(raw));
    }
}

// ---------------------------------------------------------------------------
// Mode / frequency / gateway detection
// ---------------------------------------------------------------------------

/// <summary>
/// Validates the pure helpers for extracting mode, frequency, and gateway
/// classification from TOCALL prefixes and packet comments.
/// </summary>
[TestFixture]
public class ModeFrequencyDetectionTests
{
    // ── DetectMode ────────────────────────────────────────────────────────────

    [TestCase("APDG02", null, "D-Star")]
    [TestCase("APDMR1", null, "DMR")]
    [TestCase("APYSF1", null, "YSF")]
    [TestCase("APWIR0", null, "WIRES-X")]
    [TestCase("APBM1A", null, "DMR")]
    public void DetectMode_FromTocall_ReturnsExpectedMode(string tocall, string? comment, string expected)
    {
        var mode = AprsPacketParsingService.DetectMode(tocall, comment);
        Assert.That(mode, Is.EqualTo(expected));
    }

    [TestCase("APRS", "2m Voice (D-Star) 144.96000MHz", "D-Star")]
    [TestCase("APRS", "DMR Repeater 442.55000MHz", "DMR")]
    [TestCase("APRS", "YSF Gateway on 446.500MHz", "YSF")]
    [TestCase("APRS", "WIRES-X node active", "WIRES-X")]
    [TestCase("APRS", "AllStar Node 510139", "AllStar")]
    [TestCase("APRS", "EchoLink Node active", "AllStar")]
    public void DetectMode_FromComment_ReturnsExpectedMode(string tocall, string comment, string expected)
    {
        var mode = AprsPacketParsingService.DetectMode(tocall, comment);
        Assert.That(mode, Is.EqualTo(expected));
    }

    [Test]
    public void DetectMode_UnknownTocall_NoModeKeywords_ReturnsNull()
    {
        var mode = AprsPacketParsingService.DetectMode("APRS", "SKYWARN SE TN DIGI");
        Assert.That(mode, Is.Null);
    }

    [Test]
    public void DetectMode_NullInputs_ReturnsNull()
    {
        Assert.That(AprsPacketParsingService.DetectMode(null, null), Is.Null);
        Assert.That(AprsPacketParsingService.DetectMode(null, ""), Is.Null);
        Assert.That(AprsPacketParsingService.DetectMode("", null), Is.Null);
    }

    // ── ParseFrequency ───────────────────────────────────────────────────────

    [TestCase("RNG0001/A=000010 2m Voice (D-Star) 144.96000MHz +0.0000MHz", "144.96000")]
    [TestCase("147.060MHz T141 -060", "147.060")]
    [TestCase("PHG6760/146.715 67.0 tone", null)]  // no "MHz" suffix
    [TestCase("DMR Repeater 442.55000MHz +5.0000MHz", "442.55000")]
    public void ParseFrequency_ExtractsFirstFrequencyWithMhzSuffix(string comment, string? expected)
    {
        var freq = AprsPacketParsingService.ParseFrequency(comment);
        Assert.That(freq, Is.EqualTo(expected));
    }

    [Test]
    public void ParseFrequency_NullOrEmpty_ReturnsNull()
    {
        Assert.That(AprsPacketParsingService.ParseFrequency(null), Is.Null);
        Assert.That(AprsPacketParsingService.ParseFrequency(""), Is.Null);
    }

    // ── IsGatewayTocall ──────────────────────────────────────────────────────

    [TestCase("APDG02", true)]
    [TestCase("APDMR1", true)]
    [TestCase("APYSF1", true)]
    [TestCase("APBM1A", true)]
    [TestCase("APRS", false)]
    [TestCase("APNX16", false)]   // AllStar node — not a digital voice gateway
    [TestCase("APU25N", false)]
    [TestCase(null, false)]
    [TestCase("", false)]
    public void IsGatewayTocall_ClassifiesCorrectly(string? tocall, bool expected)
    {
        Assert.That(AprsPacketParsingService.IsGatewayTocall(tocall), Is.EqualTo(expected));
    }

    // ── Full packet round-trip ───────────────────────────────────────────────

    [Test]
    public void DStarGatewayPacket_ParsesPositionAndComment()
    {
        var aprs = new Packet(RealPacketData.Position_DStarGateway);
        var pos = aprs.InfoField as PositionInfo;
        Assert.That(pos, Is.Not.Null);
        Assert.That(pos, Is.AssignableTo<PositionInfo>());

        Assert.That(pos!.Position, Is.Not.Null);
        Assert.That(pos.Position!.Coordinates.Latitude, Is.InRange(35.36, 35.37));
        Assert.That(pos.Position.Coordinates.Longitude, Is.InRange(-85.67, -85.66));
        Assert.That(pos.Position.SymbolTableIdentifier, Is.EqualTo('D'));
        Assert.That(pos.Position.SymbolCode, Is.EqualTo('&'));
        Assert.That(pos.Comment, Does.Contain("144.96000MHz"));
    }

    [Test]
    public void DStarGatewayPacket_DetectsModeAndFrequency()
    {
        var (_, tocall, _) = AprsPathParser.ParseTnc2Header(RealPacketData.Position_DStarGateway);
        var aprs = new Packet(RealPacketData.Position_DStarGateway);
        var pos = aprs.InfoField as PositionInfo;
        Assert.That(pos, Is.Not.Null);
        Assert.That(pos, Is.AssignableTo<PositionInfo>());

        var mode = AprsPacketParsingService.DetectMode(tocall, pos!.Comment);
        var freq = AprsPacketParsingService.ParseFrequency(pos.Comment);

        Assert.That(mode, Is.EqualTo("D-Star"));
        Assert.That(freq, Is.EqualTo("144.96000"));
        Assert.That(AprsPacketParsingService.IsGatewayTocall(tocall), Is.True);
    }
}
