using System.Globalization;
using System.Text;
using StackPivot.Agent.Security;

namespace StackPivot.Agent.Execution;

internal abstract class ComposeNode
{
}

internal sealed class ComposeScalar : ComposeNode
{
    public ComposeScalar(string value, bool isBlockScalar = false, bool isNull = false)
    {
        Value = value;
        IsBlockScalar = isBlockScalar;
        IsNull = isNull;
    }

    public string Value { get; }

    public bool IsBlockScalar { get; }

    public bool IsNull { get; }
}

internal sealed class ComposeMap : ComposeNode
{
    public ComposeMap(IReadOnlyList<ComposeProperty> properties)
    {
        Properties = properties;
    }

    public IReadOnlyList<ComposeProperty> Properties { get; }
}

internal sealed class ComposeSequence : ComposeNode
{
    public ComposeSequence(IReadOnlyList<ComposeNode> items)
    {
        Items = items;
    }

    public IReadOnlyList<ComposeNode> Items { get; }
}

internal sealed class ComposeProperty
{
    public ComposeProperty(string key, ComposeNode value)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; }

    public ComposeNode Value { get; }
}

internal sealed class ComposeYamlParser
{
    private readonly List<YamlLine> lines;
    private int lineIndex;

    public ComposeYamlParser(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lines = Tokenize(input);
    }

    public ComposeNode Parse()
    {
        var first = NextSignificantIndex(lineIndex);
        if (first < 0)
        {
            throw InvalidYaml("Compose document is empty.");
        }

        if (lines[first].Indent != 0)
        {
            throw InvalidYaml("Compose document has an indented root.");
        }

        lineIndex = first;
        var document = ParseBlock(0);
        if (NextSignificantIndex(lineIndex) >= 0)
        {
            throw InvalidYaml("Compose document contains multiple root values.");
        }

        return document;
    }

    private ComposeNode ParseBlock(int indent)
    {
        var next = NextSignificantIndex(lineIndex);
        if (next < 0)
        {
            throw InvalidYaml("Compose document contains an incomplete value.");
        }

        lineIndex = next;
        var line = lines[lineIndex];
        if (line.Indent != indent)
        {
            throw InvalidYaml("Compose document has inconsistent indentation.");
        }

        return IsSequenceItem(line.Content)
            ? ParseSequence(indent)
            : ParseMap(indent);
    }

    private ComposeMap ParseMap(int indent)
    {
        var properties = new List<ComposeProperty>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var next = NextSignificantIndex(lineIndex);
            if (next < 0)
            {
                break;
            }

            lineIndex = next;
            var line = lines[lineIndex];
            if (line.Indent < indent)
            {
                break;
            }

            if (line.Indent > indent || IsSequenceItem(line.Content))
            {
                throw InvalidYaml("Compose mapping has inconsistent indentation.");
            }

            if (!TrySplitMapping(line.Content, out var keyText, out var valueText))
            {
                throw InvalidYaml("Compose mapping entry is malformed.");
            }

            lineIndex++;
            var property = ParseProperty(keyText, valueText, indent);
            if (!keys.Add(property.Key))
            {
                throw InvalidYaml("Compose mapping contains a duplicate key.");
            }

            properties.Add(property);
        }

        if (properties.Count == 0)
        {
            throw InvalidYaml("Compose mapping is empty or malformed.");
        }

        return new ComposeMap(properties);
    }

    private ComposeSequence ParseSequence(int indent)
    {
        var items = new List<ComposeNode>();
        while (true)
        {
            var next = NextSignificantIndex(lineIndex);
            if (next < 0)
            {
                break;
            }

            lineIndex = next;
            var line = lines[lineIndex];
            if (line.Indent < indent)
            {
                break;
            }

            if (line.Indent != indent || !IsSequenceItem(line.Content))
            {
                throw InvalidYaml("Compose sequence has inconsistent indentation.");
            }

            var itemText = line.Content[1..].TrimStart();
            lineIndex++;
            if (itemText.Length == 0)
            {
                items.Add(ParseEmptyValue(indent));
                continue;
            }

            if (TrySplitMapping(itemText, out var keyText, out var valueText))
            {
                items.Add(ParseInlineMapItem(indent, keyText, valueText));
            }
            else
            {
                items.Add(ParseInline(itemText));
                RejectUnexpectedChild(indent);
            }
        }

        if (items.Count == 0)
        {
            throw InvalidYaml("Compose sequence is empty or malformed.");
        }

        return new ComposeSequence(items);
    }

    private ComposeMap ParseInlineMapItem(int sequenceIndent, string firstKeyText, string firstValueText)
    {
        var mapIndent = sequenceIndent + 2;
        var properties = new List<ComposeProperty>
        {
            ParseProperty(firstKeyText, firstValueText, mapIndent)
        };
        var keys = new HashSet<string>(StringComparer.Ordinal)
        {
            properties[0].Key
        };

        while (true)
        {
            var next = NextSignificantIndex(lineIndex);
            if (next < 0)
            {
                break;
            }

            lineIndex = next;
            var line = lines[lineIndex];
            if (line.Indent < mapIndent)
            {
                break;
            }

            if (line.Indent > mapIndent || IsSequenceItem(line.Content))
            {
                throw InvalidYaml("Compose sequence mapping has inconsistent indentation.");
            }

            if (!TrySplitMapping(line.Content, out var keyText, out var valueText))
            {
                throw InvalidYaml("Compose sequence mapping entry is malformed.");
            }

            lineIndex++;
            var property = ParseProperty(keyText, valueText, mapIndent);
            if (!keys.Add(property.Key))
            {
                throw InvalidYaml("Compose mapping contains a duplicate key.");
            }

            properties.Add(property);
        }

        return new ComposeMap(properties);
    }

    private ComposeProperty ParseProperty(string keyText, string valueText, int parentIndent)
    {
        var key = ParseKey(keyText);
        var value = ParseValue(valueText, parentIndent);
        return new ComposeProperty(key, value);
    }

    private ComposeNode ParseValue(string valueText, int parentIndent)
    {
        var value = valueText.Trim();
        if (value.Length == 0)
        {
            return ParseEmptyValue(parentIndent);
        }

        if (value[0] is '|' or '>')
        {
            return ParseBlockScalar(value, parentIndent);
        }

        return ParseInline(value);
    }

    private ComposeNode ParseEmptyValue(int parentIndent)
    {
        var next = NextSignificantIndex(lineIndex);
        if (next >= 0 && lines[next].Indent > parentIndent)
        {
            lineIndex = next;
            return ParseBlock(lines[next].Indent);
        }

        return new ComposeScalar(string.Empty, isNull: true);
    }

    private ComposeScalar ParseBlockScalar(string header, int parentIndent)
    {
        ValidateBlockScalarHeader(header);
        var content = new List<string>();
        var contentIndent = -1;
        while (lineIndex < lines.Count)
        {
            var line = lines[lineIndex];
            if (line.IsBlank)
            {
                content.Add(string.Empty);
                lineIndex++;
                continue;
            }

            if (line.Indent <= parentIndent)
            {
                break;
            }

            contentIndent = contentIndent < 0 ? line.Indent : contentIndent;
            if (line.Indent < contentIndent)
            {
                throw InvalidYaml("Compose block scalar has inconsistent indentation.");
            }

            content.Add(line.Raw.Length >= contentIndent ? line.Raw[contentIndent..] : string.Empty);
            lineIndex++;
        }

        return new ComposeScalar(string.Join("\n", content), isBlockScalar: true);
    }

    private static ComposeNode ParseInline(string text)
    {
        if (text[0] == '{' || text[0] == '[')
        {
            return new FlowParser(text).Parse();
        }

        return ParseScalar(text);
    }

    private static ComposeScalar ParseScalar(string text)
    {
        var value = text.Trim();
        if (value.Length == 0)
        {
            return new ComposeScalar(string.Empty, isNull: true);
        }

        var position = 0;
        if (value[0] is '\'' or '"')
        {
            var parsed = ParseQuoted(value, ref position);
            if (!OnlyWhitespaceRemains(value, position))
            {
                throw InvalidYaml("Compose quoted scalar has trailing content.");
            }

            return new ComposeScalar(parsed);
        }

        RejectUnsupportedScalarTokens(value);
        return new ComposeScalar(value, isNull: IsYamlNull(value));
    }

    private static string ParseKey(string text)
    {
        var value = text.Trim();
        if (value.Length == 0)
        {
            throw InvalidYaml("Compose mapping key is empty.");
        }

        var position = 0;
        if (value[0] is '\'' or '"')
        {
            var key = ParseQuoted(value, ref position);
            if (!OnlyWhitespaceRemains(value, position) || key.Length == 0)
            {
                throw InvalidYaml("Compose mapping key is malformed.");
            }

            return key;
        }

        RejectUnsupportedScalarTokens(value);
        if (value.Contains(':') || value.Any(char.IsControl))
        {
            throw InvalidYaml("Compose mapping key is malformed.");
        }

        return value;
    }

    private static bool TrySplitMapping(
        string text,
        out string key,
        out string value)
    {
        var quote = '\0';
        var escaped = false;
        var flowDepth = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quote != '\0')
            {
                if (quote == '"' && escaped)
                {
                    escaped = false;
                }
                else if (quote == '"' && character == '\\')
                {
                    escaped = true;
                }
                else if (character == quote)
                {
                    if (quote == '\'' && index + 1 < text.Length && text[index + 1] == '\'')
                    {
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                continue;
            }

            if (character is '{' or '[')
            {
                flowDepth++;
                continue;
            }

            if (character is '}' or ']')
            {
                if (flowDepth == 0)
                {
                    break;
                }

                flowDepth--;
                continue;
            }

            if (character == ':' && flowDepth == 0
                && (index + 1 == text.Length || char.IsWhiteSpace(text[index + 1])))
            {
                key = text[..index].Trim();
                value = text[(index + 1)..].Trim();
                return key.Length > 0;
            }
        }

        key = string.Empty;
        value = string.Empty;
        return false;
    }

    private static bool IsSequenceItem(string text)
    {
        return text.Length > 0
            && text[0] == '-'
            && (text.Length == 1 || char.IsWhiteSpace(text[1]));
    }

    private void RejectUnexpectedChild(int parentIndent)
    {
        var next = NextSignificantIndex(lineIndex);
        if (next >= 0 && lines[next].Indent > parentIndent)
        {
            throw InvalidYaml("Compose scalar has an unexpected child value.");
        }
    }

    private int NextSignificantIndex(int start)
    {
        var next = start;
        while (next < lines.Count && lines[next].IsBlank)
        {
            next++;
        }

        return next < lines.Count ? next : -1;
    }

    private static List<YamlLine> Tokenize(string input)
    {
        var result = new List<YamlLine>();
        var rawLines = input.Split('\n');
        foreach (var rawLineWithTerminator in rawLines)
        {
            var rawLine = rawLineWithTerminator.EndsWith('\r')
                ? rawLineWithTerminator[..^1]
                : rawLineWithTerminator;
            if (rawLine.Contains('\0'))
            {
                throw InvalidYaml("Compose document contains a NUL character.");
            }

            var indent = 0;
            while (indent < rawLine.Length && rawLine[indent] == ' ')
            {
                indent++;
            }

            if (indent < rawLine.Length && rawLine[indent] == '\t')
            {
                throw InvalidYaml("Compose document uses tabs for indentation.");
            }

            var raw = rawLine[indent..];
            var content = StripComment(raw).Trim();
            if (content.Length == 0)
            {
                result.Add(new YamlLine(indent, string.Empty, raw, IsBlank: true));
                continue;
            }

            if (content is "---" or "...")
            {
                throw InvalidYaml("Compose document markers are not supported.");
            }

            RejectUnsupportedScalarTokens(content);
            result.Add(new YamlLine(indent, content, raw, IsBlank: false));
        }

        return result;
    }

    private static string StripComment(string text)
    {
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (quote != '\0')
            {
                if (quote == '"' && escaped)
                {
                    escaped = false;
                }
                else if (quote == '"' && character == '\\')
                {
                    escaped = true;
                }
                else if (character == quote)
                {
                    if (quote == '\'' && index + 1 < text.Length && text[index + 1] == '\'')
                    {
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '#' && (index == 0 || char.IsWhiteSpace(text[index - 1])))
            {
                return text[..index];
            }
        }

        return text;
    }

    private static void ValidateBlockScalarHeader(string header)
    {
        for (var index = 1; index < header.Length; index++)
        {
            if (header[index] != '+'
                && header[index] != '-'
                && (header[index] < '0' || header[index] > '9'))
            {
                throw InvalidYaml("Compose block scalar header is malformed.");
            }
        }
    }

    private static bool OnlyWhitespaceRemains(string value, int position)
    {
        for (var index = position; index < value.Length; index++)
        {
            if (!char.IsWhiteSpace(value[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static string ParseQuoted(string value, ref int position)
    {
        var quote = value[position++];
        var builder = new StringBuilder();
        while (position < value.Length)
        {
            var character = value[position++];
            if (character == quote)
            {
                if (quote == '\'' && position < value.Length && value[position] == '\'')
                {
                    builder.Append('\'');
                    position++;
                    continue;
                }

                return builder.ToString();
            }

            if (quote == '"' && character == '\\')
            {
                if (position >= value.Length)
                {
                    throw InvalidYaml("Compose double-quoted scalar has an incomplete escape.");
                }

                builder.Append(ParseEscape(value, ref position));
                continue;
            }

            if (character is '\r' or '\n')
            {
                throw InvalidYaml("Compose quoted scalar cannot span lines.");
            }

            builder.Append(character);
        }

        throw InvalidYaml("Compose quoted scalar is unterminated.");
    }

    private static string ParseEscape(string value, ref int position)
    {
        var escape = value[position++];
        return escape switch
        {
            '0' => "\0",
            'a' => "\a",
            'b' => "\b",
            't' => "\t",
            'n' => "\n",
            'v' => "\v",
            'f' => "\f",
            'r' => "\r",
            'e' => "\e",
            ' ' => " ",
            '"' => "\"",
            '/' => "/",
            '\\' => "\\",
            'x' => ParseUnicodeEscape(value, ref position, 2),
            'u' => ParseUnicodeEscape(value, ref position, 4),
            'U' => ParseUnicodeEscape(value, ref position, 8),
            _ => throw InvalidYaml("Compose double-quoted scalar uses an unsupported escape."),
        };
    }

    private static string ParseUnicodeEscape(string value, ref int position, int digitCount)
    {
        if (position + digitCount > value.Length)
        {
            throw InvalidYaml("Compose double-quoted scalar has an incomplete Unicode escape.");
        }

        var digits = value.Substring(position, digitCount);
        if (!uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var codePoint))
        {
            throw InvalidYaml("Compose double-quoted scalar has an invalid Unicode escape.");
        }

        position += digitCount;
        if (codePoint > 0x10FFFF || (codePoint is >= 0xD800 and <= 0xDFFF))
        {
            throw InvalidYaml("Compose double-quoted scalar has an invalid Unicode code point.");
        }

        return char.ConvertFromUtf32((int)codePoint);
    }

    private static void RejectUnsupportedScalarTokens(string value)
    {
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (quote != '\0')
            {
                if (quote == '"' && escaped)
                {
                    escaped = false;
                }
                else if (quote == '"' && character == '\\')
                {
                    escaped = true;
                }
                else if (character == quote)
                {
                    if (quote == '\'' && index + 1 < value.Length && value[index + 1] == '\'')
                    {
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character is '&' or '*')
            {
                throw InvalidYaml("Compose anchors and aliases are not supported safely.");
            }
        }

        if (quote != '\0' || value.StartsWith('!'))
        {
            throw InvalidYaml("Compose scalar is malformed.");
        }
    }

    private static bool IsYamlNull(string value)
    {
        return value == "~"
            || value.Equals("null", StringComparison.OrdinalIgnoreCase);
    }

    private static PathPolicyException InvalidYaml(string message)
    {
        return new PathPolicyException(message);
    }

    private sealed class FlowParser
    {
        private readonly string text;
        private int position;

        public FlowParser(string text)
        {
            this.text = text;
        }

        public ComposeNode Parse()
        {
            SkipWhitespace();
            var result = ParseNode();
            SkipWhitespace();
            if (position != text.Length)
            {
                throw InvalidYaml("Compose flow value has trailing content.");
            }

            return result;
        }

        private ComposeNode ParseNode()
        {
            SkipWhitespace();
            if (position >= text.Length)
            {
                throw InvalidYaml("Compose flow value is incomplete.");
            }

            return text[position] switch
            {
                '{' => ParseMap(),
                '[' => ParseSequence(),
                '\'' or '"' => new ComposeScalar(ParseQuoted(text, ref position)),
                _ => ParsePlain(),
            };
        }

        private ComposeMap ParseMap()
        {
            position++;
            var properties = new List<ComposeProperty>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            SkipWhitespace();
            if (Consume('}'))
            {
                return new ComposeMap(properties);
            }

            while (true)
            {
                var key = ParseFlowKey();
                SkipWhitespace();
                Require(':');
                SkipWhitespace();
                var value = IsFlowDelimiterAtCurrentPosition()
                    ? new ComposeScalar(string.Empty, isNull: true)
                    : ParseNode();
                if (!keys.Add(key))
                {
                    throw InvalidYaml("Compose flow mapping contains a duplicate key.");
                }

                properties.Add(new ComposeProperty(key, value));
                SkipWhitespace();
                if (Consume('}'))
                {
                    return new ComposeMap(properties);
                }

                Require(',');
                SkipWhitespace();
                if (Consume('}'))
                {
                    return new ComposeMap(properties);
                }
            }
        }

        private ComposeSequence ParseSequence()
        {
            position++;
            var items = new List<ComposeNode>();
            SkipWhitespace();
            if (Consume(']'))
            {
                return new ComposeSequence(items);
            }

            while (true)
            {
                items.Add(ParseNode());
                SkipWhitespace();
                if (Consume(']'))
                {
                    return new ComposeSequence(items);
                }

                Require(',');
                SkipWhitespace();
                if (Consume(']'))
                {
                    return new ComposeSequence(items);
                }
            }
        }

        private ComposeScalar ParsePlain()
        {
            var start = position;
            while (position < text.Length && text[position] is not ',' and not '}' and not ']')
            {
                position++;
            }

            var value = text[start..position].Trim();
            if (value.Length == 0)
            {
                throw InvalidYaml("Compose flow scalar is empty.");
            }

            RejectUnsupportedScalarTokens(value);
            return new ComposeScalar(value, isNull: IsYamlNull(value));
        }

        private string ParseFlowKey()
        {
            SkipWhitespace();
            if (position >= text.Length)
            {
                throw InvalidYaml("Compose flow mapping key is missing.");
            }

            if (text[position] is '\'' or '"')
            {
                var key = ParseQuoted(text, ref position);
                if (key.Length == 0)
                {
                    throw InvalidYaml("Compose flow mapping key is empty.");
                }

                return key;
            }

            var start = position;
            while (position < text.Length && text[position] is not ':' and not ',' and not '}' and not ']')
            {
                position++;
            }

            var value = text[start..position].Trim();
            if (value.Length == 0)
            {
                throw InvalidYaml("Compose flow mapping key is empty.");
            }

            RejectUnsupportedScalarTokens(value);
            return value;
        }

        private bool IsFlowDelimiterAtCurrentPosition()
        {
            return position >= text.Length || text[position] is ',' or '}' or ']';
        }

        private bool Consume(char expected)
        {
            if (position < text.Length && text[position] == expected)
            {
                position++;
                return true;
            }

            return false;
        }

        private void Require(char expected)
        {
            if (!Consume(expected))
            {
                throw InvalidYaml("Compose flow value is malformed.");
            }
        }

        private void SkipWhitespace()
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
            {
                position++;
            }
        }
    }

    private sealed record YamlLine(int Indent, string Content, string Raw, bool IsBlank);
}
