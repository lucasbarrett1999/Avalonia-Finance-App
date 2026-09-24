using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Keel.Infrastructure.Persistence;

/// <summary>Stores <see cref="DateTime"/> as UTC and marks values read back as <see cref="DateTimeKind.Utc"/>.</summary>
public sealed class UtcDateTimeConverter()
    : ValueConverter<DateTime, DateTime>(
        v => v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
