namespace PgDumpPlane;

/// <summary>
/// <para>ダンプ内のテーブル行の表現形式を指定します。</para>
/// <para>Specifies how table rows are represented in the dump.</para>
/// </summary>
public enum PgDumpDataFormat
{
    /// <summary>
    /// <para>PostgreSQLのテキストCOPYブロックを使用します。既定かつ最速の形式です。</para>
    /// <para>Use PostgreSQL text COPY blocks. This is the default and fastest format.</para>
    /// </summary>
    Copy,

    /// <summary>
    /// <para>列を明示したINSERT文を行ごとに出力します。</para>
    /// <para>Use one explicit-column INSERT statement per row.</para>
    /// </summary>
    Inserts
}

/// <summary>
/// <para>SQLスクリプトに出力するデータベースの内容を制御します。</para>
/// <para>Controls which parts of a database are written to the SQL script.</para>
/// </summary>
public sealed class PgDumpOptions
{
    /// <summary>
    /// <para>指定されたスキーマのみを出力します。空集合の場合は全ユーザースキーマが対象です。</para>
    /// <para>Only these schemas are dumped. An empty collection means all user schemas.</para>
    /// </summary>
    public ISet<string> IncludeSchemas { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// <para>除外するスキーマを指定します。<see cref="IncludeSchemas"/>の判定後に適用されます。</para>
    /// <para>Schemas to omit. This is applied after <see cref="IncludeSchemas"/>.</para>
    /// </summary>
    public ISet<string> ExcludeSchemas { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// <para>オブジェクト定義を出力します。既定値は<see langword="true"/>です。</para>
    /// <para>Write object definitions. Defaults to <see langword="true"/>.</para>
    /// </summary>
    public bool IncludeSchema { get; set; } = true;

    /// <summary>
    /// <para>テーブル行とシーケンス状態を出力します。既定値は<see langword="true"/>です。</para>
    /// <para>Write table rows and sequence state. Defaults to <see langword="true"/>.</para>
    /// </summary>
    public bool IncludeData { get; set; } = true;

    /// <summary>
    /// <para>行をCOPYブロックとINSERT文のどちらで出力するかを指定します。</para>
    /// <para>Controls whether rows are written as COPY blocks or INSERT statements.</para>
    /// </summary>
    public PgDumpDataFormat DataFormat { get; set; } = PgDumpDataFormat.Copy;

    /// <summary>
    /// <para>UNLOGGEDテーブルの行を出力します。pg_dumpの--no-unlogged-table-dataとは逆の指定です。</para>
    /// <para>Dump unlogged table rows. This corresponds to the inverse of pg_dump's --no-unlogged-table-data.</para>
    /// </summary>
    public bool IncludeUnloggedTableData { get; set; } = true;

    /// <summary>
    /// <para>REPEATABLE READ・READ ONLYの代わりにSERIALIZABLE・READ ONLY・DEFERRABLEを使用します。</para>
    /// <para>Use a SERIALIZABLE, READ ONLY, DEFERRABLE snapshot instead of REPEATABLE READ, READ ONLY.</para>
    /// </summary>
    public bool SerializableDeferrable { get; set; }

    /// <summary>
    /// <para>pg_dumpと同様にpsqlの\restrictガードを出力します。</para>
    /// <para>Emit psql's \restrict guard, as current pg_dump versions do.</para>
    /// </summary>
    public bool UsePsqlRestrict { get; set; } = true;

    /// <summary>
    /// <para>対応オブジェクトの所有者を出力します。既定値は<see langword="true"/>です。</para>
    /// <para>Write ownership for supported database objects. Defaults to <see langword="true"/>.</para>
    /// </summary>
    public bool IncludeOwnership { get; set; } = true;

    /// <summary>
    /// <para>明示されたオブジェクト権限と列権限を出力します。既定値は<see langword="true"/>です。</para>
    /// <para>Write explicit object and column access privileges. Defaults to <see langword="true"/>.</para>
    /// </summary>
    public bool IncludePrivileges { get; set; } = true;

    /// <summary>
    /// <para>クラスタ共通のロール設定を出力します。復元先には対象ロールが必要です。既定値は<see langword="false"/>です。</para>
    /// <para>Write cluster-wide settings for roles that have them. The roles must already exist when restoring. Defaults to <see langword="false"/>.</para>
    /// </summary>
    public bool IncludeRoleSettings { get; set; }

    /// <summary>
    /// <para>ダンプ完了後も呼び出し元のストリームまたはライターを開いたままにします。</para>
    /// <para>Leave the supplied stream or writer open after dumping.</para>
    /// </summary>
    public bool LeaveOpen { get; set; } = true;

    /// <summary>
    /// <para>設定値を検証し、不正な値がある場合は例外を送出します。</para>
    /// <para>Validates option values and throws for invalid configuration.</para>
    /// </summary>
    internal void Validate()
    {
        if (!IncludeSchema && !IncludeData)
            throw new ArgumentException("At least one of IncludeSchema and IncludeData must be enabled.");
        if (!Enum.IsDefined(DataFormat))
            throw new ArgumentOutOfRangeException(nameof(DataFormat), DataFormat, "Unknown data format.");
    }

    /// <summary>
    /// <para>対象スキーマ条件を適用した後、除外条件を優先して判定します。</para>
    /// <para>Applies the include filter, then gives the exclude filter precedence.</para>
    /// </summary>
    /// <param name="schema">スキーマ名。 Schema name.</param>
    /// <returns>出力対象に含まれ、かつ除外されていないスキーマならtrue。 True for an included schema that is not excluded.</returns>
    internal bool Includes(string schema) =>
        (IncludeSchemas.Count == 0 || IncludeSchemas.Contains(schema)) &&
        !ExcludeSchemas.Contains(schema);
}
