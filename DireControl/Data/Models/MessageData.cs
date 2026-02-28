namespace DireControl.Data.Models;

public class MessageData
{
    public string Addressee { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string? MessageId { get; set; }
}
