using System.Text;

namespace PgDumpPlane;

internal sealed class SqlStatementAccumulator
{
    private readonly StringBuilder _buffer = new();
    private bool _hasStatementContent;
    private bool _singleQuoted;
    private bool _escapeString;
    private bool _doubleQuoted;
    private string? _dollarTag;
    private int _blockCommentDepth;

    internal bool CanReadPsqlCommand =>
        !_hasStatementContent && !_singleQuoted && !_doubleQuoted && _dollarTag is null && _blockCommentDepth == 0;

    internal IReadOnlyList<string> AppendLine(string line)
    {
        var statements = new List<string>();
        for (var index = 0; index < line.Length; index++)
        {
            var current = line[index];

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

        if (_buffer.Length > 0)
            _buffer.Append('\n');
        return statements;
    }

    internal void Complete()
    {
        if (_hasStatementContent || _singleQuoted || _doubleQuoted || _dollarTag is not null || _blockCommentDepth != 0)
            throw new InvalidDataException("The dump ended in the middle of a SQL statement.");
    }

    private string TakeStatement()
    {
        var statement = _buffer.ToString();
        _buffer.Clear();
        _hasStatementContent = false;
        return statement;
    }

    private static bool IsEscapeStringStart(string line, int quoteIndex)
    {
        if (quoteIndex >= 1 && (line[quoteIndex - 1] is 'E' or 'e'))
            return quoteIndex == 1 || !IsIdentifierCharacter(line[quoteIndex - 2]);
        return quoteIndex >= 2 && line[quoteIndex - 1] == '&' && (line[quoteIndex - 2] is 'U' or 'u') &&
            (quoteIndex == 2 || !IsIdentifierCharacter(line[quoteIndex - 3]));
    }

    private static bool IsIdentifierCharacter(char value) => char.IsLetterOrDigit(value) || value is '_' or '$';

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
