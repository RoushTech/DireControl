namespace DireControl.Enums;

/// <summary>Kind of mail stored in the PMS (personal message system).</summary>
public enum PmsMessageType
{
    Unknown = 0,

    /// <summary>Private mail addressed to one callsign.</summary>
    Private,

    /// <summary>Bulletin visible to every connecting station.</summary>
    Bulletin,
}
