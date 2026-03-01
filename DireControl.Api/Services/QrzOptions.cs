namespace DireControl.Api.Services;

public class QrzOptions
{
    public const string Section = "QRZ";

    public string? Username { get; set; }
    public string? Password { get; set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Username) && !string.IsNullOrWhiteSpace(Password);
}
