namespace PgDumpPlane;

/// <summary>Controls how a PgDumpPlane plain-text dump is restored.</summary>
public sealed class PgRestoreOptions
{
    /// <summary>Restore the dump in one transaction. Defaults to <see langword="true"/>.</summary>
    public bool UseTransaction { get; set; } = true;

    /// <summary>
    /// Command timeout in seconds for SQL statements. Zero means no timeout and is the default.
    /// </summary>
    public int CommandTimeout { get; set; }

    /// <summary>Leave the supplied stream or reader open after restoring.</summary>
    public bool LeaveOpen { get; set; } = true;

    internal void Validate()
    {
        if (CommandTimeout < 0)
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout), CommandTimeout, "Command timeout cannot be negative.");
    }
}
