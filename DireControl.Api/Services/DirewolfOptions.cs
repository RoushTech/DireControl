namespace DireControl.Api.Services;

public class DirewolfOptions
{
    public const string Section = "Direwolf";

    /// <summary>
    /// Whether the external KISS TCP TNC backend runs at all.  Disable when
    /// operating on the native sound modem alone.
    /// </summary>
    public bool Enabled { get; set; } = true;

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 8001;
    public int ReconnectDelaySeconds { get; set; } = 5;
}
