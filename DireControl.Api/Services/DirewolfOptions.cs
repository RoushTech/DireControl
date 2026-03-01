namespace DireControl.Api.Services;

public class DirewolfOptions
{
    public const string Section = "Direwolf";

    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 8001;
    public int ReconnectDelaySeconds { get; set; } = 5;
}
