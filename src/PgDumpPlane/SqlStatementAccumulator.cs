using System.Text;

namespace PgDumpPlane;

/// <summary>
/// <para>引用とコメントの状態を行間で保持する逐次SQL文解析器です。</para>
/// <para>Incrementally splits SQL statements while preserving quote and comment state across lines.</para>
/// </summary>
internal sealed class SqlStatementAccumulator
{
    // 引用・コメント状態は行をまたいで維持します。改行だけではSQL文は完成しません。
    // Quote and comment state spans lines; a newline alone does not complete a SQL statement.
    private readonly StringBuilder _buffer = new();
    private bool _hasStatementContent;
    private bool _singleQuoted;
    private bool _escapeString;
    private bool _doubleQuoted;
    private string? _dollarTag;
    private int _blockCommentDepth;

    /// <summary>
    /// <para>SQL本体も引用もブロックコメントもない文境界でのみpsql命令を読めます。</para>
    /// <para>Allows psql directives only at statement boundaries outside SQL content, quotes, and block comments.</para>
    /// </summary>
    internal bool CanReadPsqlCommand =>
        !_hasStatementContent && !_singleQuoted && !_doubleQuoted && _dollarTag is null && _blockCommentDepth == 0;

    /// <summary>
    /// <para>行を蓄積し、引用・コメントの外側にあるセミコロンで完成したSQL文を返します。</para>
    /// <para>Accumulates a line and returns SQL statements completed by semicolons outside quotes and comments.</para>
    /// </summary>
    /// <param name="line">現在処理するテキスト行。 Current input line.</param>
    /// <returns>この行の読み取りで完成したSQL文一覧。未完部分は内部バッファに残ります。 SQL statements completed by this line; unfinished text remains buffered.</returns>
    internal IReadOnlyList<string> AppendLine(string line)
    {
        var statements = new List<string>();
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];

            // ドル引用中は内部のSQL・引用符・セミコロンを解釈せず、同じ終端タグだけを探します。
            // Inside dollar quotes, ignore SQL, quotes, and semicolons and look only for the matching delimiter.
            if (_dollarTag is not null)
            {
                if (line.AsSpan(index).StartsWith(_dollarTag, StringComparison.Ordinal))
                {
                    _buffer.Append(_dollarTag);
                    index += _dollarTag.Length - 1;
                    _dollarTag = null;
                }
                else
                {
                    _buffer.Append(current);
                }
                continue;
            }

            // E文字列ではバックスラッシュが次の文字をエスケープし、通常文字列では一重引用符の二重化を扱います。
            // Escape strings let backslashes consume the next character; ordinary strings use doubled single quotes.
            if (_singleQuoted)
            {
                _buffer.Append(current);
                if (_escapeString && current == '\\' && index + 1 < line.Length)
                {
                    _buffer.Append(line[++index]);
                }
                else if (current == '\'')
                {
                    if (index + 1 < line.Length && line[index + 1] == '\'')
                        _buffer.Append(line[++index]);
                    else
                        _singleQuoted = false;
                }
                continue;
            }

            if (_doubleQuoted)
            {
                _buffer.Append(current);
                if (current == '"')
                {
                    if (index + 1 < line.Length && line[index + 1] == '"')
                        _buffer.Append(line[++index]);
                    else
                        _doubleQuoted = false;
                }
                continue;
            }

            // PostgreSQLのブロックコメントは入れ子にできるため、boolではなく深さで管理します。
            // PostgreSQL block comments can nest, so track depth rather than a Boolean flag.
            if (_blockCommentDepth > 0)
            {
                _buffer.Append(current);
                if (current == '/' && index + 1 < line.Length && line[index + 1] == '*')
                {
                    _buffer.Append(line[++index]);
                    _blockCommentDepth++;
                }
                else if (current == '*' && index + 1 < line.Length && line[index + 1] == '/')
                {
                    _buffer.Append(line[++index]);
                    _blockCommentDepth--;
                }
                continue;
            }

            if (current == '-' && index + 1 < line.Length && line[index + 1] == '-')
            {
                _buffer.Append(line.AsSpan(index));
                break;
            }

            if (current == '/' && index + 1 < line.Length && line[index + 1] == '*')
            {
                _buffer.Append("/*");
                index++;
                _blockCommentDepth = 1;
                continue;
            }

            if (current == '\'')
            {
                _hasStatementContent = true;
                _singleQuoted = true;
                _escapeString = IsEscapeStringStart(line, index);
                _buffer.Append(current);
                continue;
            }

            if (current == '"')
            {
                _hasStatementContent = true;
                _doubleQuoted = true;
                _buffer.Append(current);
                continue;
            }

            if (current == '$' && TryReadDollarTag(line, index, out var tag))
            {
                _hasStatementContent = true;
                _dollarTag = tag;
                _buffer.Append(tag);
                index += tag.Length - 1;
                continue;
            }

            // ここに到達するセミコロンだけがSQL本体の区切りです。コメントだけの文は返しません。
            // Only semicolons reaching this branch delimit SQL; do not emit comment-only statements.
            if (current == ';')
            {
                _buffer.Append(current);
                if (_hasStatementContent)
                    statements.Add(TakeStatement());
                continue;
            }

            if (!char.IsWhiteSpace(current))
                _hasStatementContent = true;
            _buffer.Append(current);
        }

        // 行末コメントが次行まで飲み込まないよう、読み取った行の改行を復元します。
        // Restore line endings so a line comment does not swallow the next input line.
        if (_buffer.Length > 0)
            _buffer.Append('\n');
        return statements;
    }

    /// <summary>
    /// <para>未完了の文、引用、ブロックコメントが残っていないことを検証します。</para>
    /// <para>Verifies that no unfinished statement, quote, or block comment remains.</para>
    /// </summary>
    internal void Complete()
    {
        if (_hasStatementContent || _singleQuoted || _doubleQuoted || _dollarTag is not null || _blockCommentDepth != 0)
            throw new InvalidDataException("The dump ended in the middle of a SQL statement.");
    }

    /// <summary>
    /// <para>完成したSQL文を取り出し、次の文の蓄積状態をリセットします。</para>
    /// <para>Extracts a completed SQL statement and resets the accumulation state for the next.</para>
    /// </summary>
    /// <returns>蓄積済みのSQL文。 The accumulated SQL statement.</returns>
    private string TakeStatement()
    {
        var statement = _buffer.ToString();
        _buffer.Clear();
        _hasStatementContent = false;
        return statement;
    }

    /// <summary>
    /// <para>引用の直前が識別子の一部ではないEまたはU&amp;プレフィックスか判定します。</para>
    /// <para>Detects an E or U&amp; prefix before a quote when it is not part of an identifier.</para>
    /// </summary>
    /// <param name="line">現在処理するテキスト行。 Current input line.</param>
    /// <param name="quoteIndex">引用符の位置。 Offset of the quote character.</param>
    /// <returns>解析器がエスケープ文字列として扱うプレフィックスならtrue。 True for a prefix handled as an escape string by the parser.</returns>
    private static bool IsEscapeStringStart(string line, int quoteIndex)
    {
        if (quoteIndex >= 1 && (line[quoteIndex - 1] is 'E' or 'e'))
            return quoteIndex == 1 || !IsIdentifierCharacter(line[quoteIndex - 2]);
        return quoteIndex >= 2 && line[quoteIndex - 1] == '&' && (line[quoteIndex - 2] is 'U' or 'u') &&
            (quoteIndex == 2 || !IsIdentifierCharacter(line[quoteIndex - 3]));
    }

    /// <summary>
    /// <para>識別子を構成し得る文字かを判定します。</para>
    /// <para>Checks whether a character can be part of an identifier.</para>
    /// </summary>
    /// <param name="value">変換または解析する入力値。 Input value to format or parse.</param>
    /// <returns>英数字・アンダースコア・ドル記号ならtrue。 True for letters, digits, underscores, or dollar signs.</returns>
    private static bool IsIdentifierCharacter(char value) => char.IsLetterOrDigit(value) || value is '_' or '$';

    /// <summary>
    /// <para>空タグまたは識別子形式のドル引用タグを読み取ります。</para>
    /// <para>Reads an empty or identifier-shaped dollar-quote delimiter.</para>
    /// </summary>
    /// <param name="line">現在処理するテキスト行。 Current input line.</param>
    /// <param name="start">解析を開始する位置。 Offset at which parsing begins.</param>
    /// <param name="tag">読み取ったドル引用タグ。失敗時は空文字列。 Parsed dollar-quote tag, or an empty string on failure.</param>
    /// <returns>有効なドル引用タグを読み取れた場合はtrue。 True when a valid dollar-quote delimiter was read.</returns>
    private static bool TryReadDollarTag(string line, int start, out string tag)
    {
        var end = line.IndexOf('$', start + 1);
        if (end < 0)
        {
            tag = string.Empty;
            return false;
        }

        var name = line.AsSpan(start + 1, end - start - 1);
        if (name.Length > 0 && !(char.IsLetter(name[0]) || name[0] == '_'))
        {
            tag = string.Empty;
            return false;
        }
        for (var index = 1; index < name.Length; index++)
        {
            if (!(char.IsLetterOrDigit(name[index]) || name[index] == '_'))
            {
                tag = string.Empty;
                return false;
            }
        }

        tag = line[start..(end + 1)];
        return true;
    }
}
