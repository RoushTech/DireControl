namespace DireControl.Modem.Ax25.Lapb;

/// <summary>LAPB link state (AX.25 v2.x data-link states, condensed).</summary>
public enum LapbState
{
    Disconnected = 0,
    AwaitingConnection,
    Connected,
    AwaitingRelease,
    TimerRecovery,
}

/// <summary>Timers the LAPB machine asks its host to run.</summary>
public enum LapbTimer
{
    /// <summary>Retransmission / outstanding-frame timer.</summary>
    T1 = 1,

    /// <summary>Delayed-acknowledgement timer.</summary>
    T2,

    /// <summary>Idle link-check timer.</summary>
    T3,
}

/// <summary>Why a link left the connected states.</summary>
public enum LapbDisconnectReason
{
    Unknown = 0,

    /// <summary>We requested the disconnect (DISC sent and answered or timed out).</summary>
    LocalRequest,

    /// <summary>The remote sent DISC.</summary>
    RemoteDisc,

    /// <summary>The remote sent DM (refused or dead link).</summary>
    RemoteDm,

    /// <summary>N2 retries exhausted without a response.</summary>
    RetryExhausted,

    /// <summary>Repeated FRMR / protocol-error resets exhausted the reset budget.</summary>
    FrmrReceived,

    /// <summary>Unrecoverable protocol error.</summary>
    ProtocolError,
}
