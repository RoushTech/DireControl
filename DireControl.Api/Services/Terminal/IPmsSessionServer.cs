using DireControl.Api.Services.Ax25;

namespace DireControl.Api.Services.Terminal;

/// <summary>
/// Marker for the PMS's inbound-session handler so the terminal's local
/// preview can serve a pseudo-session through the exact same code path as a
/// real inbound RF connection.  Registered in DI when the PMS feature exists.
/// </summary>
public interface IPmsSessionServer : IAx25InboundHandler;
