using System.Globalization;

namespace PgDumpPlane;

/// <summary>
/// <para>識別子・リテラル・値をSQL用の文字列へ変換します。</para>
/// <para>Formats identifiers, literals, and values for SQL output.</para>
/// </summary>
internal static class SqlText
{
    /// <summary>
    /// <para>識別子を二重引用符で囲み、内部の二重引用符を二重化します。</para>
    /// <para>Quotes an identifier with double quotes and doubles embedded quotes.</para>
    /// </summary>
    /// <param name="value">変換または解析する入力値。 Input value to format or parse.</param>
    /// <returns>二重引用符で囲まれた識別子。 A double-quoted SQL identifier.</returns>
    internal static string Identifier(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    /// <summary>
    /// <para>スキーマ名とオブジェクト名をそれぞれ引用して結合します。</para>
    /// <para>Quotes the schema and object name separately and joins them.</para>
    /// </summary>
    /// <param name="schema">スキーマ名。 Schema name.</param>
    /// <param name="name">対象の名前。 Target name.</param>
    /// <returns>各部分を引用したスキーマ修飾名。 A schema-qualified name with each part quoted.</returns>
    internal static string Qualified(string schema, string name) => $"{Identifier(schema)}.{Identifier(name)}";

    /// <summary>
    /// <para>標準文字列リテラル用に一重引用符を二重化して値を囲みます。</para>
    /// <para>Formats a standard string literal by doubling embedded single quotes.</para>
    /// </summary>
    /// <param name="value">変換または解析する入力値。 Input value to format or parse.</param>
    /// <returns>一重引用符で囲まれた標準SQL文字列リテラル。 A single-quoted standard SQL string literal.</returns>
    // standard_conforming_strings=onを前提に、一重引用符のみをエスケープします。
    // Assume standard_conforming_strings=on and escape only single quotes.
    internal static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>
    /// <para>bool値をSQLのtrueまたはfalseに変換します。</para>
    /// <para>Formats a Boolean value as SQL true or false.</para>
    /// </summary>
    /// <param name="value">変換または解析する入力値。 Input value to format or parse.</param>
    /// <returns>SQLのtrueまたはfalse。 SQL true or false.</returns>
    internal static string Boolean(bool value) => value ? "true" : "false";

    /// <summary>
    /// <para>ロケールに依存しない整数のSQL表現を返します。</para>
    /// <para>Formats an integer for SQL using invariant culture.</para>
    /// </summary>
    /// <param name="value">変換または解析する入力値。 Input value to format or parse.</param>
    /// <returns>カルチャに依存しない整数表現。 An invariant-culture integer representation.</returns>
    internal static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
