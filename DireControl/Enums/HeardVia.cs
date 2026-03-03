namespace DireControl.Enums;

public enum HeardVia
{
    Unknown = 0,
    Direct = 1,          // RF, no digipeater hops
    Digi = 2,            // RF, via one or more digipeaters
    DirectAndDigi = 3,   // Station heard both ways across recent packets
    Internet = 4,        // Via APRS-IS, originated from internet-connected client (qAC, TCPIP)
    IgateRf = 5,         // Via APRS-IS, originated on RF and igated direct (qAR, qAO, HopCount == 0)
    IgateRfDigi = 6,     // Via APRS-IS, originated on RF via digipeater(s) then igated (qAR, qAO, HopCount > 0)
}
