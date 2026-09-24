namespace Keel.Infrastructure.Tests.Sync;

/// <summary>A fact that runs only when every named environment variable is set (e.g. sandbox keys).</summary>
public sealed class EnvFactAttribute : FactAttribute
{
    public EnvFactAttribute(params string[] variables)
    {
        var missing = variables.Where(v => string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))).ToList();
        if (missing.Count > 0)
        {
            Skip = "Set " + string.Join(" and ", missing) + " to run this test.";
        }
    }
}

/// <summary>A fact that runs only on the named OS ("windows", "macos", "linux").</summary>
public sealed class OsFactAttribute : FactAttribute
{
    public OsFactAttribute(string os)
    {
        if (!OperatingSystem.IsOSPlatform(os))
        {
            Skip = "Runs on " + os + " only.";
        }
    }
}
