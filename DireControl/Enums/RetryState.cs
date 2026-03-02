namespace DireControl.Enums;

public enum RetryState
{
    Pending = 0,
    Retrying = 1,
    Acknowledged = 2,
    Failed = 3,
    Cancelled = 4,
}
