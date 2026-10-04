using System.Text.RegularExpressions;

namespace PgDumpPlane;

/// <summary>
/// <para>既知の構文差を使い、新しいDBから古いDBへの復元SQLを縮退します。</para>
/// <para>Downgrades restore SQL from a newer database using known syntax differences.</para>
/// </summary>
/// <remarks>
/// <para>既知のダンプ構文だけを変換します。正規表現は引用文字列や関数本体を網羅するSQL構文解析器ではなく、変換によって元の意味が変わる場合があります。</para>
/// <para>Only known dump syntax is transformed. Regexes are not a complete SQL parser for quoted strings or routine bodies, and transformations can change original semantics.</para>
/// </remarks>
internal sealed partial class RestoreCompatibilityProcessor
{
    private readonly int targetMajor;
    private readonly bool enabled;
    private readonly HashSet<string> partitionedTables = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <para>作成元が復元先より新しい場合だけ互換処理を有効にします。</para>
    /// <para>Enables compatibility processing only when the source is newer than the target.</para>
    /// </summary>
    /// <param name="sourceMajor">ダンプ元DBのメジャーバージョン。 Source database major version.</param>
    /// <param name="targetMajor">復元先DBのメジャーバージョン。 Target database major version.</param>
    internal RestoreCompatibilityProcessor(int sourceMajor, int targetMajor)
    {
        this.targetMajor = targetMajor;
        enabled = sourceMajor > targetMajor;
    }

    /// <summary>
    /// <para>既知の非対応構文を変換し、除外すべき文ならnullを返します。</para>
    /// <para>Transforms known unsupported syntax and returns null for statements to omit.</para>
    /// </summary>
    /// <param name="statement">セミコロンで終わる解析済みSQL文。 Parsed SQL statement ending with a semicolon.</param>
    /// <returns>変換後のSQL。除外する文の場合はnull。 Transformed SQL, or null when the statement must be omitted.</returns>
    internal string? Process(string statement)
    {
        // 同一版または新しい復元先では一切変換せず、ダンプの元のSQLを保持します。
        // Preserve the original SQL without transformation for equal or newer targets.
        if (!enabled)
            return statement;

        // 先頭のコメントは保存しつつ、文頭から始まる正規表現をSQL本体にだけ適用します。
        // Preserve leading comments while applying anchored patterns to the SQL body.
        var sqlStart = SkipLeadingTrivia(statement);
        var prefix = statement[..sqlStart];
        var sql = statement[sqlStart..];

        if (targetMajor < 17 &&
            (TransactionTimeoutRegex().IsMatch(sql) || RoleTransactionTimeoutRegex().IsMatch(sql)))
            return null;
        if (targetMajor < 14 && ColumnCompressionRegex().IsMatch(sql))
            return null;

        // UNLOGGEDの永続性とNULLの一意性が変わります。構文互換であり、意味を完全には維持しません。
        // Durability and NULL uniqueness semantics change; syntax compatibility does not preserve all behavior.
        if (targetMajor < 15)
        {
            sql = UnloggedSequenceRegex().Replace(sql, "CREATE SEQUENCE", 1);
            sql = NullsNotDistinctRegex().Replace(sql, string.Empty);
        }

        if (targetMajor < 18 && CreateTableRegex().IsMatch(sql))
            sql = DowngradePostgres18TableFeatures(sql);

        // 親テーブル名をCREATE時に記録し、PostgreSQL 12で復元できない後続トリガーを除外します。
        // Record parent names at CREATE time to omit later triggers unsupported on PostgreSQL 12.
        TrackPartitionedTable(sql);
        if (targetMajor < 13 && IsTriggerOnPartitionedTable(sql))
            return null;

        return prefix + sql;
    }

    /// <summary>
    /// <para>PostgreSQL 18のNOT NULLと生成列の構文を古いバージョン向けに縮退します。</para>
    /// <para>Downgrades PostgreSQL 18 NOT NULL and generated-column syntax for older targets.</para>
    /// </summary>
    /// <param name="sql">処理または実行するSQL文字列。 SQL text to process or execute.</param>
    /// <returns>古い復元先向けにNOT NULLと生成列を変換したSQL。 SQL with NOT NULL and generated columns downgraded for older targets.</returns>
    private static string DowngradePostgres18TableFeatures(string sql)
    {
        // 通常のNOT NULLにすると子テーブルへ制約が波及するため、NO INHERIT付きは制約ごと除外します。
        // Ordinary NOT NULL would propagate to child tables, so omit the entire NO INHERIT constraint.
        sql = NoInheritNotNullRegex().Replace(sql, string.Empty);
        sql = NamedNotNullRegex().Replace(sql, " NOT NULL");

        // ダンプの行単位の列定義を前提に変換します。任意のSQL全体を解析する処理ではありません。
        // Transformation assumes line-based column definitions in dumps; this is not a general SQL parser.
        var lines = sql.Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var generated = VirtualGeneratedColumnRegex().Match(line);
            if (!generated.Success)
                continue;
            lines[index] = AddStoredAfterGeneratedExpression(line, generated.Index);
        }
        return string.Join('\n', lines);
    }

    /// <summary>
    /// <para>引用文字列内の括弧を無視して生成式の末尾を探し、STOREDを補います。</para>
    /// <para>Finds the generated expression's closing parenthesis outside quotes and adds STORED.</para>
    /// </summary>
    /// <param name="line">現在処理するテキスト行。 Current input line.</param>
    /// <param name="generatedStart">生成列の句が始まる位置。 Offset where the generated-column clause begins.</param>
    /// <returns>STOREDを補った行。既にSTOREDがあるか式末尾を検出できなければ元の行。 The line with STORED added, or the original if STORED exists or the expression end is not found.</returns>
    private static string AddStoredAfterGeneratedExpression(string line, int generatedStart)
    {
        // 生成式内の関数呼び出しなどの括弧を深さで追跡し、引用された括弧は数えません。
        // Track nested expression parentheses by depth without counting parentheses inside quotes.
        var open = line.IndexOf('(', generatedStart);
        var depth = 0;
        var singleQuoted = false;
        var doubleQuoted = false;
        for (var index = open; index >= 0 && index < line.Length; index++)
        {
            var current = line[index];
            if (singleQuoted)
            {
                if (current == '\'' && index + 1 < line.Length && line[index + 1] == '\'')
                    index++;
                else if (current == '\'')
                    singleQuoted = false;
                continue;
            }
            if (doubleQuoted)
            {
                if (current == '"' && index + 1 < line.Length && line[index + 1] == '"')
                    index++;
                else if (current == '"')
                    doubleQuoted = false;
                continue;
            }
            if (current == '\'')
            {
                singleQuoted = true;
                continue;
            }
            if (current == '"')
            {
                doubleQuoted = true;
                continue;
            }
            if (current == '(')
                depth++;
            else if (current == ')' && --depth == 0)
            {
                var remainder = line.AsSpan(index + 1).TrimStart();
                return remainder.StartsWith("STORED", StringComparison.OrdinalIgnoreCase)
                    ? line
                    : line.Insert(index + 1, " STORED");
            }
        }
        return line;
    }

    /// <summary>
    /// <para>CREATE TABLE文にPARTITION BYがあれば、後続トリガーの判定用に名前を記録します。</para>
    /// <para>Records names of CREATE TABLE statements with PARTITION BY for later trigger checks.</para>
    /// </summary>
    /// <param name="sql">処理または実行するSQL文字列。 SQL text to process or execute.</param>
    private void TrackPartitionedTable(string sql)
    {
        if (!PartitionByRegex().IsMatch(sql))
            return;
        var match = CreateTableNameRegex().Match(sql);
        if (match.Success)
            partitionedTables.Add(match.Groups["name"].Value);
    }

    /// <summary>
    /// <para>CREATE TRIGGERの対象が記録済みのパーティション親テーブルか判定します。</para>
    /// <para>Checks whether a CREATE TRIGGER targets a recorded partitioned parent table.</para>
    /// </summary>
    /// <param name="sql">処理または実行するSQL文字列。 SQL text to process or execute.</param>
    /// <returns>記録済みのパーティション親に対するトリガーならtrue。 True for a trigger on a recorded partitioned parent.</returns>
    private bool IsTriggerOnPartitionedTable(string sql)
    {
        var match = CreateTriggerTableRegex().Match(sql);
        return match.Success && partitionedTables.Contains(match.Groups["name"].Value);
    }

    /// <summary>
    /// <para>先頭の空白とコメントを読み飛ばし、SQL本体の開始位置を返します。</para>
    /// <para>Skips leading whitespace and comments and returns the SQL start offset.</para>
    /// </summary>
    /// <param name="sql">処理または実行するSQL文字列。 SQL text to process or execute.</param>
    /// <returns>コメントと空白の後にあるSQLの開始位置。 Offset of SQL after leading comments and whitespace.</returns>
    private static int SkipLeadingTrivia(string sql)
    {
        var index = 0;
        while (index < sql.Length)
        {
            while (index < sql.Length && char.IsWhiteSpace(sql[index]))
                index++;
            if (index + 1 < sql.Length && sql[index] == '-' && sql[index + 1] == '-')
            {
                var newline = sql.IndexOf('\n', index + 2);
                if (newline < 0)
                    return sql.Length;
                index = newline + 1;
                continue;
            }
            if (index + 1 < sql.Length && sql[index] == '/' && sql[index + 1] == '*')
            {
                var end = sql.IndexOf("*/", index + 2, StringComparison.Ordinal);
                if (end < 0)
                    return sql.Length;
                index = end + 2;
                continue;
            }
            break;
        }
        return index;
    }

    // 引用識別子では二重化された引用符を許可し、修飾名は最大でスキーマ名とオブジェクト名です。
    // Quoted identifiers allow doubled quotes; qualified names have at most schema and object parts.
    private const string Identifier = "(?:\"(?:[^\"]|\"\")*\"|[A-Za-z_][A-Za-z0-9_$]*)";
    private const string QualifiedIdentifier = Identifier + "(?:\\." + Identifier + ")?";

    /// <summary>
    /// <para>transaction_timeoutのセッション設定を検出する生成済み正規表現を返します。</para>
    /// <para>Returns the generated regex detecting session transaction_timeout settings.</para>
    /// </summary>
    /// <returns>対応する構文を検出する正規表現。 The regular expression matching the corresponding syntax.</returns>
    [GeneratedRegex(@"^SET\s+transaction_timeout\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TransactionTimeoutRegex();

    /// <summary>
    /// <para>ロールのtransaction_timeout設定を検出する生成済み正規表現を返します。</para>
    /// <para>Returns the generated regex detecting role transaction_timeout settings.</para>
    /// </summary>
    /// <returns>対応する構文を検出する正規表現。 The regular expression matching the corresponding syntax.</returns>
    [GeneratedRegex("^ALTER\\s+(?:ROLE|USER)\\b[\\s\\S]*\\bSET\\s+\"?transaction_timeout\"?\\s+(?:TO|=)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RoleTransactionTimeoutRegex();

    /// <summary>
    /// <para>列圧縮を設定するALTER TABLE文の正規表現を返します。</para>
    /// <para>Returns the generated regex detecting ALTER TABLE column compression settings.</para>
    /// </summary>
    /// <returns>対応する構文を検出する正規表現。 The regular expression matching the corresponding syntax.</returns>
    [GeneratedRegex(@"^ALTER\s+TABLE\b[\s\S]*\bSET\s+COMPRESSION\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ColumnCompressionRegex();

    /// <summary>
    /// <para>CREATE UNLOGGED SEQUENCEを検出する正規表現を返します。</para>
    /// <para>Returns the generated regex detecting CREATE UNLOGGED SEQUENCE.</para>
    /// </summary>
    /// <returns>対応する構文を検出する正規表現。 The regular expression matching the corresponding syntax.</returns>
    [GeneratedRegex(@"^CREATE\s+UNLOGGED\s+SEQUENCE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnloggedSequenceRegex();

    /// <summary>
    /// <para>NULLS NOT DISTINCT句を検出する正規表現を返します。</para>
    /// <para>Returns the generated regex detecting NULLS NOT DISTINCT clauses.</para>
    /// </summary>
    /// <returns>対応する構文を検出する正規表現。 The regular expression matching the corresponding syntax.</returns>
    [GeneratedRegex(@"\s+NULLS\s+NOT\s+DISTINCT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NullsNotDistinctRegex();

    /// <summary>
    /// <para>通常またはUNLOGGEDのCREATE TABLE文を検出する正規表現を返します。</para>
    /// <para>Returns the generated regex detecting ordinary or UNLOGGED CREATE TABLE statements.</para>
    /// </summary>
    /// <returns>対応する構文を検出する正規表現。 The regular expression matching the corresponding syntax.</returns>
    [GeneratedRegex(@"^CREATE\s+(?:UNLOGGED\s+)?TABLE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTableRegex();

    /// <summary>
    /// <para>名前付きNOT NULL制約を検出する正規表現を返します。</para>
    /// <para>Returns the generated regex detecting named NOT NULL constraints.</para>
    /// </summary>
    /// <returns>対応する構文を検出する正規表現。 The regular expression matching the corresponding syntax.</returns>
    [GeneratedRegex("\\s+CONSTRAINT\\s+(?:\"(?:[^\"]|\"\")*\"|[A-Za-z_][A-Za-z0-9_$]*)\\s+NOT\\s+NULL\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedNotNullRegex();

    /// <summary>
    /// <para>NO INHERIT付きNOT NULL制約を検出する正規表現を返します。</para>
    /// <para>Returns the generated regex detecting NOT NULL NO INHERIT constraints.</para>
    /// </summary>
    /// <returns>対応する構文を検出する正規表現。 The regular expression matching the corresponding syntax.</returns>
    [GeneratedRegex("\\s+(?:CONSTRAINT\\s+(?:\"(?:[^\"]|\"\")*\"|[A-Za-z_][A-Za-z0-9_$]*)\\s+)?NOT\\s+NULL\\s+NO\\s+INHERIT\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoInheritNotNullRegex();

    /// <summary>
    /// <para>生成列の式の開始を検出する正規表現を返します。</para>
    /// <para>Returns the generated regex detecting the start of a generated-column expression.</para>
    /// </summary>
    /// <returns>対応する構文を検出する正規表現。 The regular expression matching the corresponding syntax.</returns>
    [GeneratedRegex(@"\bGENERATED\s+ALWAYS\s+AS\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VirtualGeneratedColumnRegex();

    /// <summary>
    /// <para>PARTITION BY句を検出する正規表現を返します。</para>
    /// <para>Returns the generated regex detecting PARTITION BY clauses.</para>
    /// </summary>
    /// <returns>対応する構文を検出する正規表現。 The regular expression matching the corresponding syntax.</returns>
    [GeneratedRegex(@"\bPARTITION\s+BY\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartitionByRegex();

    /// <summary>
    /// <para>引用済み名を含めてCREATE TABLEの対象名を抽出する正規表現を返します。</para>
    /// <para>Returns the generated regex extracting CREATE TABLE names, including quoted names.</para>
    /// </summary>
    /// <returns>対応する構文を検出する正規表現。 The regular expression matching the corresponding syntax.</returns>
    [GeneratedRegex("^CREATE\\s+(?:UNLOGGED\\s+)?TABLE\\s+(?<name>" + QualifiedIdentifier + ")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTableNameRegex();

    /// <summary>
    /// <para>CREATE TRIGGER文から対象テーブル名を抽出する正規表現を返します。</para>
    /// <para>Returns the generated regex extracting the target table of CREATE TRIGGER.</para>
    /// </summary>
    /// <returns>対応する構文を検出する正規表現。 The regular expression matching the corresponding syntax.</returns>
    [GeneratedRegex("^CREATE\\s+TRIGGER\\b[\\s\\S]*?\\sON\\s+(?<name>" + QualifiedIdentifier + ")\\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTriggerTableRegex();
}
