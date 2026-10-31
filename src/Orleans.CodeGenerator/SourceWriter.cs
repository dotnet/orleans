using System.Diagnostics;
using System.Text;

namespace Orleans.CodeGenerator;

/// <summary>
/// Writes generated C# directly from emission plans.
/// </summary>
/// <remarks>
/// The layout is the one established by the former whole-file <c>NormalizeWhitespace</c> pass: four-space
/// indentation, CRLF line separators, no terminator after the final line, and one blank line after a closing
/// brace unless the next line closes the enclosing block or continues the same statement. Callers supply
/// complete, already-spelled C# fragments; the writer owns only line structure and indentation.
/// </remarks>
internal sealed class SourceWriter
{
    private const string NewLine = "\r\n";
    private readonly StringBuilder _builder;
    private readonly int _initialIndentation;
    private int _indentation;
    private bool _hasLine;
    private bool _blankLinePending;

    /// <param name="capacity">The initial character capacity.</param>
    /// <param name="initialIndentation">The indentation of the first line, for a fragment which a caller inserts at that depth.</param>
    public SourceWriter(int capacity = 1024, int initialIndentation = 0)
    {
        _builder = new StringBuilder(capacity);
        _indentation = _initialIndentation = initialIndentation;
    }

    /// <summary>Starts a new line at the current indentation. Text is appended with <see cref="Write(string)"/>.</summary>
    public SourceWriter StartLine()
    {
        if (_hasLine)
        {
            _builder.Append(NewLine);
            if (_blankLinePending)
            {
                _builder.Append(NewLine);
            }
        }

        _hasLine = true;
        _blankLinePending = false;
        _builder.Append(' ', _indentation * 4);
        return this;
    }

    public SourceWriter Write(string text)
    {
        _builder.Append(text);
        return this;
    }

    public SourceWriter Write(char value)
    {
        _builder.Append(value);
        return this;
    }

    public void WriteLine(string text) => StartLine().Write(text);

    public void OpenBlock()
    {
        WriteLine("{");
        _indentation++;
    }

    public void CloseBlock()
    {
        Debug.Assert(_indentation > 0);
        _blankLinePending = false;
        _indentation--;
        WriteLine("}");
        _blankLinePending = true;
    }

    /// <summary>Writes an empty block, <c>{</c> and <c>}</c> on separate lines.</summary>
    public void EmptyBlock()
    {
        OpenBlock();
        CloseBlock();
    }

    /// <summary>Suppresses the blank line after a closing brace when the next line continues the statement, such as <c>else</c>.</summary>
    public void ContinueStatement() => _blankLinePending = false;

    /// <summary>Requests one blank line before the next line.</summary>
    public void BlankLine() => _blankLinePending = true;

    public void Indent() => _indentation++;

    public void Unindent()
    {
        Debug.Assert(_indentation > 0);
        _indentation--;
    }

    /// <summary>
    /// Writes lines produced by another writer at the current indentation; the fragment must begin with its own indentation.
    /// </summary>
    public void WriteFragment(string fragment)
    {
        if (fragment.Length == 0)
        {
            return;
        }

        if (_hasLine)
        {
            _builder.Append(NewLine);
            if (_blankLinePending)
            {
                _builder.Append(NewLine);
            }
        }

        _hasLine = true;
        _blankLinePending = false;
        _builder.Append(fragment);
    }

    public override string ToString()
    {
        Debug.Assert(_indentation == _initialIndentation);
        return _builder.ToString();
    }
}
