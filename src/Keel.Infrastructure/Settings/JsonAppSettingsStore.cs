using System.Text.Json;
using System.Text.Json.Serialization;
using Keel.Application.Files;
using Keel.Application.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Keel.Infrastructure.Settings;

/// <summary>
/// Stores <see cref="AppSettings"/> as JSON in <c>settings.json</c>. Writes are atomic
/// (temp file then replace). A missing or unreadable file yields defaults and is never
/// overwritten until the next explicit update.
/// </summary>
public sealed partial class JsonAppSettingsStore : IAppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private AppSettings? _current;

    /// <summary>Creates a store for the settings file of <paramref name="dataDirectory"/>.</summary>
    public JsonAppSettingsStore(IDataDirectory dataDirectory, ILogger<JsonAppSettingsStore>? logger = null)
        : this(dataDirectory?.SettingsFile ?? throw new ArgumentNullException(nameof(dataDirectory)), logger)
    {
    }

    /// <summary>Creates a store for an explicit file path.</summary>
    public JsonAppSettingsStore(string path, ILogger<JsonAppSettingsStore>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <inheritdoc />
    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return _current ??= Load();
            }
        }
    }

    /// <inheritdoc />
    public AppSettings Update(Func<AppSettings, AppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            var next = update(_current ??= Load());
            Write(next);
            _current = next;
            return next;
        }
    }

    /// <inheritdoc />
    public Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken ct) =>
        Task.Run(() => Update(update), ct);

    private AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            return new AppSettings();
        }

        try
        {
            var json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            LogUnreadable(_logger, ex);
            return new AppSettings();
        }
    }

    private void Write(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "settings.json could not be read; using defaults")]
    private static partial void LogUnreadable(ILogger logger, Exception exception);
}
