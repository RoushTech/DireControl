namespace DireControl.Api.Controllers.Models;

/// <summary>Current runtime log-level overrides plus the choices available in the UI.</summary>
public sealed class LogLevelsResponse
{
    /// <summary>Categories that currently have an explicit override.</summary>
    public required List<LogLevelDto> Overrides { get; init; }

    /// <summary>Selectable level names, most-verbose first.</summary>
    public required string[] AvailableLevels { get; init; }

    /// <summary>Categories surfaced by default in the UI.</summary>
    public required string[] CommonCategories { get; init; }
}

public sealed class LogLevelDto
{
    public required string Category { get; init; }
    public required string Level { get; init; }
}

/// <summary>
/// Sets a category's level override. A null/empty <see cref="Level"/> removes the
/// override so the category inherits its appsettings value again.
/// </summary>
public sealed class UpdateLogLevelRequest
{
    public required string Category { get; init; }
    public string? Level { get; init; }
}
