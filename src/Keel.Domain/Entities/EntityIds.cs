namespace Keel.Domain.Entities;

/// <summary>Identifier generation for entities.</summary>
public static class EntityIds
{
    /// <summary>
    /// A new time-ordered (version 7) GUID. Time ordering keeps SQLite index pages mostly
    /// append-only and lets ids be created before insert (undo, JSON bundles).
    /// </summary>
    public static Guid New() => Guid.CreateVersion7();
}
