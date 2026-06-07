using Microsoft.Extensions.Configuration;

namespace DireControl.Api.Logging;

/// <summary>
/// Configuration source whose values feed the standard Microsoft.Extensions.Logging
/// filters under "Logging:LogLevel:*". It is added to the configuration pipeline at
/// the highest precedence so its values win over appsettings.json. Replacing the
/// values at runtime (see <see cref="RuntimeLoggingConfigProvider.SetLevels"/>) fires
/// a configuration reload, which causes the logging system to re-bind its filters
/// live — exactly as appsettings reloadOnChange does.
/// </summary>
public sealed class RuntimeLoggingConfigSource : IConfigurationSource
{
    public RuntimeLoggingConfigProvider Provider { get; } = new();

    public IConfigurationProvider Build(IConfigurationBuilder builder) => Provider;
}

public sealed class RuntimeLoggingConfigProvider : ConfigurationProvider
{
    /// <summary>
    /// Replaces all runtime overrides with <paramref name="levels"/> (category → level
    /// name) and triggers a reload so the logging filters re-bind.
    /// </summary>
    public void SetLevels(IReadOnlyDictionary<string, string> levels)
    {
        Data = levels.ToDictionary(
            kv => $"Logging:LogLevel:{kv.Key}",
            kv => (string?)kv.Value,
            StringComparer.OrdinalIgnoreCase);

        OnReload();
    }
}
