using System.Globalization;

namespace PgDumpPlane;

internal static class SqlText
{
    internal static string Identifier(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    internal static string Qualified(string schema, string name) => $"{Identifier(schema)}.{Identifier(name)}";

    internal static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    internal static string Boolean(bool value) => value ? "true" : "false";

    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
