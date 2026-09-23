namespace PgDumpPlane;

/// <summary>Specifies how table rows are represented in the dump.</summary>
public enum PgDumpDataFormat
{
    /// <summary>Use PostgreSQL text COPY blocks. This is the default and fastest format.</summary>
    Copy,

    /// <summary>Use one explicit-column INSERT statement per row.</summary>
    Inserts
}

/// <summary>Controls which parts of a database are written to the SQL script.</summary>
public sealed class PgDumpOptions
{
    /// <summary>Only these schemas are dumped. An empty collection means all user schemas.</summary>
    public ISet<string> IncludeSchemas { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Schemas to omit. This is applied after <see cref="IncludeSchemas"/>.</summary>
    public ISet<string> ExcludeSchemas { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>Write object definitions. Defaults to <see langword="true"/>.</summary>
    public bool IncludeSchema { get; set; } = true;

    /// <summary>Write table rows and sequence state. Defaults to <see langword="true"/>.</summary>
    public bool IncludeData { get; set; } = true;

    /// <summary>Controls whether rows are written as COPY blocks or INSERT statements.</summary>
    public PgDumpDataFormat DataFormat { get; set; } = PgDumpDataFormat.Copy;

    /// <summary>Dump unlogged table rows. This corresponds to the inverse of pg_dump's --no-unlogged-table-data.</summary>
    public bool IncludeUnloggedTableData { get; set; } = true;

    /// <summary>Use a SERIALIZABLE, READ ONLY, DEFERRABLE snapshot instead of REPEATABLE READ, READ ONLY.</summary>
    public bool SerializableDeferrable { get; set; }

    /// <summary>Emit psql's \restrict guard, as current pg_dump versions do.</summary>
    public bool UsePsqlRestrict { get; set; } = true;

    /// <summary>Write ownership for supported database objects. Defaults to <see langword="true"/>.</summary>
    public bool IncludeOwnership { get; set; } = true;

    /// <summary>Write explicit object and column access privileges. Defaults to <see langword="true"/>.</summary>
    public bool IncludePrivileges { get; set; } = true;

    /// <summary>Leave the supplied stream or writer open after dumping.</summary>
    public bool LeaveOpen { get; set; } = true;

    internal void Validate()
    {
        if (!IncludeSchema && !IncludeData)
            throw new ArgumentException("At least one of IncludeSchema and IncludeData must be enabled.");
        if (!Enum.IsDefined(DataFormat))
            throw new ArgumentOutOfRangeException(nameof(DataFormat), DataFormat, "Unknown data format.");
    }

    internal bool Includes(string schema) =>
        (IncludeSchemas.Count == 0 || IncludeSchemas.Contains(schema)) &&
        !ExcludeSchemas.Contains(schema);
}
