namespace PgDumpPlane;

internal sealed record DatabaseInfo(string Name, string ServerVersion, int ServerMajorVersion);
internal sealed record SchemaInfo(string Name);
internal sealed record ExtensionInfo(string Name, string Schema, string Version);
internal sealed record EnumTypeInfo(string Schema, string Name, IReadOnlyList<string> Labels);
internal sealed record RoutineInfo(string Schema, string Name, string Definition);

internal sealed record SequenceInfo(
    uint Oid,
    string Schema,
    string Name,
    bool Unlogged,
    string DataType,
    long Start,
    long Minimum,
    long Maximum,
    long Increment,
    long Cache,
    bool Cycle,
    string? OwnedTableSchema,
    string? OwnedTableName,
    string? OwnedColumn,
    bool IsIdentity);

internal sealed record ColumnInfo(
    string Name,
    string DataType,
    bool NotNull,
    string? DefaultExpression,
    char Identity,
    char Generated,
    string? Collation,
    string? Compression,
    string? NotNullConstraintName,
    bool NotNullNoInherit);

internal sealed record TableInfo(
    uint Oid,
    string Schema,
    string Name,
    char Kind,
    bool Unlogged,
    string? PartitionKey,
    uint? ParentOid,
    string? ParentSchema,
    string? ParentName,
    string? PartitionBound,
    string? RelOptions,
    string? Tablespace,
    IReadOnlyList<ColumnInfo> Columns);

internal sealed record ViewInfo(uint Oid, string Schema, string Name, string Definition, string? Options, IReadOnlyList<uint> Dependencies);
internal sealed record ConstraintInfo(string Schema, string Table, string Name, char Type, string Definition);
internal sealed record IndexInfo(string Schema, string Table, string Name, string Definition, bool Clustered, bool ReplicaIdentity);
internal sealed record TriggerInfo(string Schema, string Table, string Name, string Definition);

internal enum SecuredObjectKind
{
    Schema,
    Type,
    Function,
    Procedure,
    Table,
    Sequence,
    View
}

internal sealed record OwnershipInfo(
    SecuredObjectKind Kind,
    string? Schema,
    string Name,
    string? IdentityArguments,
    string Owner);

internal sealed record PrivilegeInfo(string? Grantee, string Privilege, bool IsGrantable);

internal sealed record AccessControlInfo(
    SecuredObjectKind Kind,
    string? Schema,
    string Name,
    string? IdentityArguments,
    string? Column,
    string Owner,
    IReadOnlyList<PrivilegeInfo> Privileges);

internal sealed record CatalogSnapshot(
    DatabaseInfo Database,
    IReadOnlyList<SchemaInfo> Schemas,
    IReadOnlyList<ExtensionInfo> Extensions,
    IReadOnlyList<EnumTypeInfo> Enums,
    IReadOnlyList<RoutineInfo> Routines,
    IReadOnlyList<SequenceInfo> Sequences,
    IReadOnlyList<TableInfo> Tables,
    IReadOnlyList<ViewInfo> Views,
    IReadOnlyList<ConstraintInfo> Constraints,
    IReadOnlyList<IndexInfo> Indexes,
    IReadOnlyList<TriggerInfo> Triggers,
    IReadOnlyList<OwnershipInfo> Ownership,
    IReadOnlyList<AccessControlInfo> AccessControls);
