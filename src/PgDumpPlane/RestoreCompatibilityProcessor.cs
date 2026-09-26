using System.Text.RegularExpressions;

namespace PgDumpPlane;

internal sealed partial class RestoreCompatibilityProcessor
{
    private readonly int targetMajor;
    private readonly bool enabled;
    private readonly HashSet<string> partitionedTables = new(StringComparer.OrdinalIgnoreCase);

    internal RestoreCompatibilityProcessor(int sourceMajor, int targetMajor)
    {
        this.targetMajor = targetMajor;
        enabled = sourceMajor > targetMajor;
    }

    internal string? Process(string statement)
    {
        if (!enabled)
            return statement;

        var sqlStart = SkipLeadingTrivia(statement);
        var prefix = statement[..sqlStart];
        var sql = statement[sqlStart..];

        if (targetMajor < 17 &&
            (TransactionTimeoutRegex().IsMatch(sql) || RoleTransactionTimeoutRegex().IsMatch(sql)))
            return null;
        if (targetMajor < 14 && ColumnCompressionRegex().IsMatch(sql))
            return null;

        if (targetMajor < 15)
        {
            sql = UnloggedSequenceRegex().Replace(sql, "CREATE SEQUENCE", 1);
            sql = NullsNotDistinctRegex().Replace(sql, string.Empty);
        }

        if (targetMajor < 18 && CreateTableRegex().IsMatch(sql))
            sql = DowngradePostgres18TableFeatures(sql);

        TrackPartitionedTable(sql);
        if (targetMajor < 13 && IsTriggerOnPartitionedTable(sql))
            return null;

        return prefix + sql;
    }

    private static string DowngradePostgres18TableFeatures(string sql)
    {
        sql = NoInheritNotNullRegex().Replace(sql, string.Empty);
        sql = NamedNotNullRegex().Replace(sql, " NOT NULL");

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

    private static string AddStoredAfterGeneratedExpression(string line, int generatedStart)
    {
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

    private void TrackPartitionedTable(string sql)
    {
        if (!PartitionByRegex().IsMatch(sql))
            return;
        var match = CreateTableNameRegex().Match(sql);
        if (match.Success)
            partitionedTables.Add(match.Groups["name"].Value);
    }

    private bool IsTriggerOnPartitionedTable(string sql)
    {
        var match = CreateTriggerTableRegex().Match(sql);
        return match.Success && partitionedTables.Contains(match.Groups["name"].Value);
    }

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

    private const string Identifier = "(?:\"(?:[^\"]|\"\")*\"|[A-Za-z_][A-Za-z0-9_$]*)";
    private const string QualifiedIdentifier = Identifier + "(?:\\." + Identifier + ")?";

    [GeneratedRegex(@"^SET\s+transaction_timeout\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TransactionTimeoutRegex();

    [GeneratedRegex("^ALTER\\s+(?:ROLE|USER)\\b[\\s\\S]*\\bSET\\s+\"?transaction_timeout\"?\\s+(?:TO|=)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RoleTransactionTimeoutRegex();

    [GeneratedRegex(@"^ALTER\s+TABLE\b[\s\S]*\bSET\s+COMPRESSION\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ColumnCompressionRegex();

    [GeneratedRegex(@"^CREATE\s+UNLOGGED\s+SEQUENCE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnloggedSequenceRegex();

    [GeneratedRegex(@"\s+NULLS\s+NOT\s+DISTINCT\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NullsNotDistinctRegex();

    [GeneratedRegex(@"^CREATE\s+(?:UNLOGGED\s+)?TABLE\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTableRegex();

    [GeneratedRegex("\\s+CONSTRAINT\\s+(?:\"(?:[^\"]|\"\")*\"|[A-Za-z_][A-Za-z0-9_$]*)\\s+NOT\\s+NULL\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamedNotNullRegex();

    [GeneratedRegex("\\s+(?:CONSTRAINT\\s+(?:\"(?:[^\"]|\"\")*\"|[A-Za-z_][A-Za-z0-9_$]*)\\s+)?NOT\\s+NULL\\s+NO\\s+INHERIT\\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoInheritNotNullRegex();

    [GeneratedRegex(@"\bGENERATED\s+ALWAYS\s+AS\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VirtualGeneratedColumnRegex();

    [GeneratedRegex(@"\bPARTITION\s+BY\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartitionByRegex();

    [GeneratedRegex("^CREATE\\s+(?:UNLOGGED\\s+)?TABLE\\s+(?<name>" + QualifiedIdentifier + ")", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTableNameRegex();

    [GeneratedRegex("^CREATE\\s+TRIGGER\\b[\\s\\S]*?\\sON\\s+(?<name>" + QualifiedIdentifier + ")\\s", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateTriggerTableRegex();
}
