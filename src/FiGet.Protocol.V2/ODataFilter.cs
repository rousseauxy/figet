using System.Globalization;
using NuGet.Versioning;

namespace FiGet.Protocol.V2;

/// <summary>
/// An OData expression FiGet does not understand. It becomes a 400 naming the expression, never an empty
/// 200: silently answering "no packages" for an unparsed filter is the failure mode that makes
/// <c>Find-Module</c> find nothing on other servers (build plan section 4.3).
/// </summary>
public sealed class ODataFilterException : Exception
{
    public ODataFilterException()
        : this("The expression is not supported.", "")
    {
    }

    public ODataFilterException(string message)
        : this(message, "")
    {
    }

    public ODataFilterException(string message, Exception innerException)
        : base(message, innerException) => Expression = "";

    public ODataFilterException(string message, string expression)
        : base(message) => Expression = expression;

    /// <summary>The offending expression, repeated to the client and the log.</summary>
    public string Expression { get; }
}

/// <summary>
/// A parsed <c>$filter</c>: the predicate itself plus the two facts the endpoints use to avoid reading
/// more of the feed than they must.
/// </summary>
public sealed class ODataFilter
{
    private readonly ODataExpression expression;

    private ODataFilter(ODataExpression expression, string text, string? requiredId, bool latestOnly)
    {
        this.expression = expression;
        Text = text;
        RequiredId = requiredId;
        LatestOnly = latestOnly;
    }

    public string Text { get; }

    /// <summary>The id from a top-level <c>Id eq '…'</c>, so one package can be fetched instead of a page.</summary>
    public string? RequiredId { get; }

    /// <summary>True when a top-level term keeps only the latest version of each package.</summary>
    public bool LatestOnly { get; }

    public bool Matches(V2Row row) => ODataValues.ToBool(expression.Evaluate(row));

    /// <summary>Parses the expression, or throws <see cref="ODataFilterException"/> naming it.</summary>
    public static ODataFilter Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var parsed = new ODataParser(text).ParseFilter();
        var conjuncts = new List<ODataExpression>();
        Flatten(parsed, conjuncts);

        string? requiredId = null;
        var latestOnly = false;
        foreach (var conjunct in conjuncts)
        {
            switch (conjunct)
            {
                case ODataComparison { Operator: "eq", Left: ODataProperty { Name: var name }, Right: ODataLiteral literal }
                    when name.Equals("Id", StringComparison.OrdinalIgnoreCase) && literal.Value is string id:
                    requiredId = id;
                    break;
                case ODataComparison { Operator: "eq", Left: ODataProperty { Name: var flag }, Right: ODataLiteral { Value: true } }
                    when IsLatestFlag(flag):
                    latestOnly = true;
                    break;
                case ODataProperty { Name: var bare } when IsLatestFlag(bare):
                    latestOnly = true;
                    break;
                default:
                    break;
            }
        }

        return new ODataFilter(parsed, text, requiredId, latestOnly);
    }

    private static bool IsLatestFlag(string name) =>
        name.Equals("IsLatestVersion", StringComparison.OrdinalIgnoreCase)
        || name.Equals("IsAbsoluteLatestVersion", StringComparison.OrdinalIgnoreCase);

    private static void Flatten(ODataExpression expression, List<ODataExpression> conjuncts)
    {
        if (expression is ODataLogical { Operator: "and" } logical)
        {
            Flatten(logical.Left, conjuncts);
            Flatten(logical.Right, conjuncts);
            return;
        }

        conjuncts.Add(expression);
    }
}

/// <summary>A parsed <c>$orderby</c>. The default is id ascending, then version descending.</summary>
public sealed class ODataOrderBy
{
    private readonly List<(string Property, bool Descending)> keys;

    private ODataOrderBy(List<(string Property, bool Descending)> keys) => this.keys = keys;

    public static ODataOrderBy Default { get; } = new([("Id", false), ("Version", true)]);

    /// <summary>Version ascending, which is what the reference server returned for FindPackagesById.</summary>
    public static ODataOrderBy VersionAscending { get; } = new([("Version", false)]);

    public static ODataOrderBy Parse(string? text, ODataOrderBy fallback)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        var keys = new List<(string, bool)>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var property = words[0];
            var descending = words.Length > 1 && words[1].Equals("desc", StringComparison.OrdinalIgnoreCase);
            if (words.Length > 2 || (words.Length == 2 && !descending && !words[1].Equals("asc", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ODataFilterException($"Cannot order by '{part}'.", text);
            }

            if (!V2Row.IsKnownProperty(property))
            {
                throw new ODataFilterException($"Cannot order by unknown property '{property}'.", text);
            }

            keys.Add((property, descending));
        }

        return new ODataOrderBy(keys);
    }

    public IReadOnlyList<V2Row> Sort(IEnumerable<V2Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = rows.ToList();
        list.Sort(Compare);
        return list;
    }

    private int Compare(V2Row left, V2Row right)
    {
        foreach (var (property, descending) in keys)
        {
            int result;
            if (V2Row.IsVersionProperty(property))
            {
                result = VersionComparer.Default.Compare(left.Version, right.Version);
            }
            else
            {
                result = ODataValues.Compare(left.Property(property), right.Property(property));
            }

            if (result != 0)
            {
                return descending ? -result : result;
            }
        }

        return 0;
    }
}

/// <summary>Value semantics shared by the filter and the ordering.</summary>
internal static class ODataValues
{
    public static bool ToBool(object? value) => value is bool flag && flag;

    /// <summary>
    /// Ordinal, case-insensitive for text, chronological for dates, numeric for numbers. Versions are
    /// compared by the caller, because string order would put 1.10.0 before 1.9.0.
    /// </summary>
    public static int Compare(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null ? 0 : left is null ? -1 : 1;
        }

        if (left is string leftText && right is string rightText)
        {
            return string.Compare(leftText, rightText, StringComparison.OrdinalIgnoreCase);
        }

        if (left is DateTime leftDate && right is DateTime rightDate)
        {
            return leftDate.CompareTo(rightDate);
        }

        if (left is bool leftFlag && right is bool rightFlag)
        {
            return leftFlag.CompareTo(rightFlag);
        }

        return ToNumber(left).CompareTo(ToNumber(right));
    }

    public static string ToText(object? value) => value switch
    {
        null => "",
        string text => text,
        bool flag => flag ? "true" : "false",
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    private static double ToNumber(object value) => value switch
    {
        long number => number,
        int number => number,
        double number => number,
        _ => double.TryParse(ToText(value), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0,
    };
}

internal abstract class ODataExpression
{
    public abstract object? Evaluate(V2Row row);
}

internal sealed class ODataLiteral(object? value) : ODataExpression
{
    public object? Value { get; } = value;

    public override object? Evaluate(V2Row row) => Value;
}

internal sealed class ODataProperty(string name) : ODataExpression
{
    public string Name { get; } = name;

    public override object? Evaluate(V2Row row) => row.Property(Name);
}

internal sealed class ODataLogical(string op, ODataExpression left, ODataExpression right) : ODataExpression
{
    public string Operator { get; } = op;

    public ODataExpression Left { get; } = left;

    public ODataExpression Right { get; } = right;

    public override object Evaluate(V2Row row) => Operator == "and"
        ? ODataValues.ToBool(Left.Evaluate(row)) && ODataValues.ToBool(Right.Evaluate(row))
        : ODataValues.ToBool(Left.Evaluate(row)) || ODataValues.ToBool(Right.Evaluate(row));
}

internal sealed class ODataNot(ODataExpression inner) : ODataExpression
{
    public override object Evaluate(V2Row row) => !ODataValues.ToBool(inner.Evaluate(row));
}

internal sealed class ODataComparison(string op, ODataExpression left, ODataExpression right) : ODataExpression
{
    public string Operator { get; } = op;

    public ODataExpression Left { get; } = left;

    public ODataExpression Right { get; } = right;

    public override object Evaluate(V2Row row)
    {
        var comparison = CompareOperands(row);
        return Operator switch
        {
            "eq" => comparison == 0,
            "ne" => comparison != 0,
            "gt" => comparison > 0,
            "ge" => comparison >= 0,
            "lt" => comparison < 0,
            "le" => comparison <= 0,
            _ => throw new ODataFilterException($"Unknown operator '{Operator}'.", Operator),
        };
    }

    private int CompareOperands(V2Row row)
    {
        var left = Left.Evaluate(row);
        var right = Right.Evaluate(row);
        if (IsVersionOperand(Left) || IsVersionOperand(Right))
        {
            var leftVersion = ToVersion(left);
            var rightVersion = ToVersion(right);
            if (leftVersion is not null && rightVersion is not null)
            {
                return VersionComparer.Default.Compare(leftVersion, rightVersion);
            }
        }

        return ODataValues.Compare(left, right);
    }

    private static bool IsVersionOperand(ODataExpression expression) =>
        expression is ODataProperty property && V2Row.IsVersionProperty(property.Name);

    private static NuGetVersion? ToVersion(object? value) =>
        value is string text && NuGetVersion.TryParse(text, out var version) ? version : null;
}

internal sealed class ODataFunction(string name, IReadOnlyList<ODataExpression> arguments) : ODataExpression
{
    private static readonly Dictionary<string, int> Arities = new(StringComparer.OrdinalIgnoreCase)
    {
        ["substringof"] = 2,
        ["startswith"] = 2,
        ["endswith"] = 2,
        ["tolower"] = 1,
        ["toupper"] = 1,
        ["indexof"] = 2,
        ["trim"] = 1,
    };

    /// <summary>Checked while parsing, so an unknown function fails before any row is looked at.</summary>
    public static void Validate(string name, int argumentCount, string expression)
    {
        if (!Arities.TryGetValue(name, out var arity))
        {
            throw new ODataFilterException($"Unknown function '{name}'.", expression);
        }

        if (argumentCount != arity)
        {
            throw new ODataFilterException($"Function '{name}' takes {arity} argument(s).", expression);
        }
    }

    public override object? Evaluate(V2Row row)
    {
        switch (name.ToUpperInvariant())
        {
            case "SUBSTRINGOF":
                Expect(2);
                return ODataValues.ToText(arguments[1].Evaluate(row))
                    .Contains(ODataValues.ToText(arguments[0].Evaluate(row)), StringComparison.OrdinalIgnoreCase);
            case "STARTSWITH":
                Expect(2);
                return ODataValues.ToText(arguments[0].Evaluate(row))
                    .StartsWith(ODataValues.ToText(arguments[1].Evaluate(row)), StringComparison.OrdinalIgnoreCase);
            case "ENDSWITH":
                Expect(2);
                return ODataValues.ToText(arguments[0].Evaluate(row))
                    .EndsWith(ODataValues.ToText(arguments[1].Evaluate(row)), StringComparison.OrdinalIgnoreCase);
            case "TOLOWER":
                Expect(1);
                return ODataValues.ToText(arguments[0].Evaluate(row)).ToUpperInvariant().ToLowerInvariant();
            case "TOUPPER":
                Expect(1);
                return ODataValues.ToText(arguments[0].Evaluate(row)).ToUpperInvariant();
            case "INDEXOF":
                Expect(2);
                return (long)ODataValues.ToText(arguments[0].Evaluate(row))
                    .IndexOf(ODataValues.ToText(arguments[1].Evaluate(row)), StringComparison.OrdinalIgnoreCase);
            case "TRIM":
                Expect(1);
                return ODataValues.ToText(arguments[0].Evaluate(row)).Trim();
            default:
                throw new ODataFilterException($"Unknown function '{name}'.", name);
        }
    }

    private void Expect(int count)
    {
        if (arguments.Count != count)
        {
            throw new ODataFilterException($"Function '{name}' takes {count} argument(s).", name);
        }
    }
}

/// <summary>
/// Recursive-descent parser for the subset of OData the NuGet clients actually send, recorded in phase 0
/// and listed in docs/protocol-v2.md. No OData library: the grammar is small and must fail loudly.
/// </summary>
internal sealed class ODataParser
{
    private readonly string text;
    private int position;

    public ODataParser(string text)
    {
        this.text = text;
        position = 0;
    }

    public ODataExpression ParseFilter()
    {
        var expression = ParseOr();
        SkipWhitespace();
        if (position < text.Length)
        {
            throw new ODataFilterException($"Unexpected '{text[position..]}' in the filter.", text);
        }

        return expression;
    }

    private ODataExpression ParseOr()
    {
        var left = ParseAnd();
        while (TryKeyword("or"))
        {
            left = new ODataLogical("or", left, ParseAnd());
        }

        return left;
    }

    private ODataExpression ParseAnd()
    {
        var left = ParseUnary();
        while (TryKeyword("and"))
        {
            left = new ODataLogical("and", left, ParseUnary());
        }

        return left;
    }

    private ODataExpression ParseUnary() => TryKeyword("not") ? new ODataNot(ParseUnary()) : ParseComparison();

    private ODataExpression ParseComparison()
    {
        var left = ParseOperand();
        SkipWhitespace();
        foreach (var op in Operators)
        {
            if (TryKeyword(op))
            {
                return new ODataComparison(op, left, ParseOperand());
            }
        }

        return left;
    }

    private static readonly string[] Operators = ["eq", "ne", "ge", "gt", "le", "lt"];

    private ODataExpression ParseOperand()
    {
        SkipWhitespace();
        if (position >= text.Length)
        {
            throw new ODataFilterException("The filter ends where a value was expected.", text);
        }

        if (text[position] == '(')
        {
            position++;
            var inner = ParseOr();
            SkipWhitespace();
            if (position >= text.Length || text[position] != ')')
            {
                throw new ODataFilterException("A closing parenthesis is missing.", text);
            }

            position++;
            return inner;
        }

        if (text[position] == '\'')
        {
            return new ODataLiteral(ReadString());
        }

        if (char.IsDigit(text[position]) || text[position] == '-')
        {
            return new ODataLiteral(ReadNumber());
        }

        var word = ReadWord();
        if (word.Length == 0)
        {
            throw new ODataFilterException($"Unexpected '{text[position..]}' in the filter.", text);
        }

        SkipWhitespace();
        if (position < text.Length && text[position] == '(')
        {
            position++;
            var arguments = new List<ODataExpression>();
            SkipWhitespace();
            if (position < text.Length && text[position] == ')')
            {
                position++;
                ODataFunction.Validate(word, arguments.Count, text);
                return new ODataFunction(word, arguments);
            }

            while (true)
            {
                arguments.Add(ParseOr());
                SkipWhitespace();
                if (position < text.Length && text[position] == ',')
                {
                    position++;
                    continue;
                }

                if (position < text.Length && text[position] == ')')
                {
                    position++;
                    break;
                }

                throw new ODataFilterException($"Function '{word}' has a malformed argument list.", text);
            }

            ODataFunction.Validate(word, arguments.Count, text);
            return new ODataFunction(word, arguments);
        }

        return word.ToUpperInvariant() switch
        {
            "TRUE" => new ODataLiteral(true),
            "FALSE" => new ODataLiteral(false),
            "NULL" => new ODataLiteral(null),
            _ => V2Row.IsKnownProperty(word)
                ? new ODataProperty(word)
                : throw new ODataFilterException($"Unknown property '{word}'.", text),
        };
    }

    private string ReadString()
    {
        position++;
        var value = new System.Text.StringBuilder();
        while (position < text.Length)
        {
            if (text[position] == '\'')
            {
                if (position + 1 < text.Length && text[position + 1] == '\'')
                {
                    value.Append('\'');
                    position += 2;
                    continue;
                }

                position++;
                return value.ToString();
            }

            value.Append(text[position]);
            position++;
        }

        throw new ODataFilterException("A string literal is not closed.", text);
    }

    private object ReadNumber()
    {
        var start = position;
        if (text[position] == '-')
        {
            position++;
        }

        while (position < text.Length && (char.IsDigit(text[position]) || text[position] == '.'))
        {
            position++;
        }

        var literal = text[start..position];
        if (long.TryParse(literal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
        {
            return whole;
        }

        if (double.TryParse(literal, NumberStyles.Any, CultureInfo.InvariantCulture, out var real))
        {
            return real;
        }

        throw new ODataFilterException($"'{literal}' is not a number.", text);
    }

    private string ReadWord()
    {
        var start = position;
        while (position < text.Length && (char.IsLetterOrDigit(text[position]) || text[position] == '_'))
        {
            position++;
        }

        return text[start..position];
    }

    /// <summary>Consumes the keyword when it is the next word, so 'and' does not match 'android'.</summary>
    private bool TryKeyword(string keyword)
    {
        SkipWhitespace();
        var save = position;
        var word = ReadWord();
        if (word.Equals(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        position = save;
        return false;
    }

    private void SkipWhitespace()
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
        {
            position++;
        }
    }
}
