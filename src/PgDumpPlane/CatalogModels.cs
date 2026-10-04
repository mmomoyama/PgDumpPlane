namespace PgDumpPlane;

/// <summary>
/// <para>接続先DBとダンプヘッダー用のサーバーバージョン情報です。</para>
/// <para>Identifies the database and server version for the dump header.</para>
/// </summary>
internal sealed record DatabaseInfo(string Name, string ServerVersion, int ServerMajorVersion);
/// <summary>
/// <para>出力対象のユーザースキーマです。</para>
/// <para>Describes a user schema selected for output.</para>
/// </summary>
internal sealed record SchemaInfo(string Name);
/// <summary>
/// <para>拡張機能名とインストール先スキーマです。</para>
/// <para>Describes an extension and its installation schema.</para>
/// </summary>
internal sealed record ExtensionInfo(string Name, string Schema);
/// <summary>
/// <para>ラベルの定義順を保持する列挙型情報です。</para>
/// <para>Describes an enum preserving its declared label order.</para>
/// </summary>
internal sealed record EnumTypeInfo(string Schema, string Name, IReadOnlyList<string> Labels);
/// <summary>
/// <para>サーバーが生成した関数またはプロシージャの定義です。</para>
/// <para>Contains a server-rendered function or procedure definition.</para>
/// </summary>
internal sealed record RoutineInfo(string Schema, string Name, string Definition);

/// <summary>
/// <para>シーケンスの設定と所有列・IDENTITYとの関係です。</para>
/// <para>Describes sequence settings and owning-column or identity relationships.</para>
/// </summary>
/// <remarks>
/// <para>Oidはサーバー内の関連付けに使用します。IsIdentity=trueのシーケンスは列定義から作成されます。所有列がない場合、Owned系の値はnullです。</para>
/// <para>Oid links catalog objects. Identity sequences are created by column definitions; ownership fields are null when there is no owning column.</para>
/// </remarks>
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

/// <summary>
/// <para>型、既定値、生成方式、圧縮、NOT NULLを含む列定義です。</para>
/// <para>Describes a column's type, default, generation, compression, and NOT NULL properties.</para>
/// </summary>
/// <remarks>
/// <para>Identityはa=ALWAYS、d=BY DEFAULT、\0=通常列です。Generatedはs=STORED、v=VIRTUAL、\0=非生成列です。生成列ではDefaultExpressionが生成式を保持します。</para>
/// <para>Identity codes are a=ALWAYS, d=BY DEFAULT, and \0=ordinary. Generated codes are s=STORED, v=VIRTUAL, and \0=not generated. DefaultExpression holds the expression for generated columns.</para>
/// </remarks>
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

/// <summary>
/// <para>依存順序とCREATE文に必要なテーブル・パーティション情報です。</para>
/// <para>Contains table and partition metadata for dependency ordering and CREATE statements.</para>
/// </summary>
/// <remarks>
/// <para>Kindはr=通常テーブル、p=パーティション親です。ParentOidとPartitionBoundを合わせて宣言的パーティションを識別します。OIDはダンプ中のSQL名として出力しません。</para>
/// <para>Kind is r=ordinary table or p=partitioned parent. ParentOid with PartitionBound identifies declarative partitions. OIDs are not emitted as object names in dump SQL.</para>
/// </remarks>
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

/// <summary>
/// <para>ビュー定義と、先に作成する必要のあるビューのOID一覧です。</para>
/// <para>Contains a view definition and OIDs of views that must be created first.</para>
/// </summary>
internal sealed record ViewInfo(uint Oid, string Schema, string Name, string Definition, string? Options, IReadOnlyList<uint> Dependencies);
/// <summary>
/// <para>テーブル制約の種別とサーバーが生成した定義です。</para>
/// <para>Describes a table constraint's kind and server-rendered definition.</para>
/// </summary>
/// <remarks>
/// <para>Typeはp=主キー、u=一意、f=外部キー、c=CHECK、x=排他制約です。NOT NULLはColumnInfoで扱います。</para>
/// <para>Type is p=primary, u=unique, f=foreign, c=check, or x=exclusion. NOT NULL is represented by ColumnInfo.</para>
/// </remarks>
internal sealed record ConstraintInfo(string Schema, string Table, string Name, char Type, string Definition);
/// <summary>
/// <para>独立索引の定義とCLUSTER・レプリカ識別設定です。</para>
/// <para>Describes a standalone index and its clustering and replica identity settings.</para>
/// </summary>
internal sealed record IndexInfo(string Schema, string Table, string Name, string Definition, bool Clustered, bool ReplicaIdentity);
/// <summary>
/// <para>ユーザー定義トリガーの対象テーブルと定義です。</para>
/// <para>Describes a user trigger's target table and definition.</para>
/// </summary>
internal sealed record TriggerInfo(string Schema, string Table, string Name, string Definition);

/// <summary>
/// <para>所有者変更と権限復元に対応するオブジェクト種別です。</para>
/// <para>Identifies object kinds supported by ownership and privilege restoration.</para>
/// </summary>
internal enum SecuredObjectKind
{
    /// <summary>スキーマ。 Schema.</summary>
    Schema,
    /// <summary>列挙型。 Enum type.</summary>
    Type,
    /// <summary>関数。 Function.</summary>
    Function,
    /// <summary>プロシージャ。 Procedure.</summary>
    Procedure,
    /// <summary>テーブル。 Table.</summary>
    Table,
    /// <summary>シーケンス。 Sequence.</summary>
    Sequence,
    /// <summary>ビュー。 View.</summary>
    View
}

/// <summary>
/// <para>所有者変更の対象を、種別・名前・関数の引数で識別します。</para>
/// <para>Identifies an ownership target by kind, name, and routine identity arguments.</para>
/// </summary>
internal sealed record OwnershipInfo(
    SecuredObjectKind Kind,
    string? Schema,
    string Name,
    string? IdentityArguments,
    string Owner);

/// <summary>
/// <para>権限の付与先と再付与可否です。nullの付与先はPUBLICを表します。</para>
/// <para>Describes a grantee and grant option; a null grantee represents PUBLIC.</para>
/// </summary>
internal sealed record PrivilegeInfo(string? Grantee, string Privilege, bool IsGrantable);

/// <summary>
/// <para>オブジェクトまたは列の所有者と、明示された権限一覧です。</para>
/// <para>Describes an object or column owner and its explicit privileges.</para>
/// </summary>
/// <remarks>
/// <para>Column=nullはオブジェクト全体のACLです。Privilegesが空でも、明示的に権限を取り消した状態の復元に必要です。</para>
/// <para>Column=null denotes the object ACL. An empty Privileges list still matters when restoring explicitly revoked permissions.</para>
/// </remarks>
internal sealed record AccessControlInfo(
    SecuredObjectKind Kind,
    string? Schema,
    string Name,
    string? IdentityArguments,
    string? Column,
    string Owner,
    IReadOnlyList<PrivilegeInfo> Privileges);

/// <summary>
/// <para>クラスタ共通のロール設定を名前と値で保持します。</para>
/// <para>Contains a cluster-wide role setting name and value.</para>
/// </summary>
internal sealed record RoleSettingInfo(string Role, string Name, string Value);

/// <summary>
/// <para>一度のダンプで使用するオブジェクト定義と権限の読み取り結果です。</para>
/// <para>Groups catalog definitions and security metadata used by one dump.</para>
/// </summary>
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
    IReadOnlyList<AccessControlInfo> AccessControls,
    IReadOnlyList<RoleSettingInfo> RoleSettings);
