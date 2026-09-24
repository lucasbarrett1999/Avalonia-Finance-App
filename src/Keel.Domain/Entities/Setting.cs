namespace Keel.Domain.Entities;

/// <summary>A setting that belongs with the data file (app-level settings live in settings.json).</summary>
public class Setting
{
    /// <summary>Key (primary key).</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Value as JSON.</summary>
    public string ValueJson { get; set; } = "null";
}
