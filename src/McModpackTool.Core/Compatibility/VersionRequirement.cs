using System.Globalization;
using System.Text.RegularExpressions;

namespace McModpackTool.Core.Compatibility;

public enum VersionRequirementResult
{
    Satisfied,
    NotSatisfied,
    Unknown,
}

/// <summary>
/// Evaluates the common subset shared by Fabric/Quilt semantic predicates and Forge/NeoForge
/// Maven ranges. Invalid or loader-specific expressions produce <see cref="VersionRequirementResult.Unknown"/>
/// instead of a false incompatibility report.
/// </summary>
public static partial class VersionRequirement
{
    public static VersionRequirementResult Evaluate(string? requirement, string? actualVersion)
    {
        var expression = (requirement ?? string.Empty).Trim();
        if (expression.Length == 0 || expression is "*" or "[*]" or "(,)" or "[,)" or "(,]")
        {
            return VersionRequirementResult.Satisfied;
        }
        if (!ComparableModVersion.TryParse(actualVersion, out var actual))
        {
            return VersionRequirementResult.Unknown;
        }

        if (LooksLikeMavenRange(expression))
        {
            return EvaluateMaven(expression, actual);
        }

        var alternatives = OrSeparator().Split(expression);
        var sawKnownAlternative = false;
        var sawUnknownAlternative = false;
        foreach (var alternative in alternatives)
        {
            var result = EvaluateConjunction(alternative.Trim(), actual);
            if (result == VersionRequirementResult.Satisfied)
            {
                return result;
            }
            sawKnownAlternative |= result == VersionRequirementResult.NotSatisfied;
            sawUnknownAlternative |= result == VersionRequirementResult.Unknown;
        }
        return sawUnknownAlternative || !sawKnownAlternative
            ? VersionRequirementResult.Unknown
            : VersionRequirementResult.NotSatisfied;
    }

    private static VersionRequirementResult EvaluateConjunction(string expression, ComparableModVersion actual)
    {
        if (expression.Length == 0)
        {
            return VersionRequirementResult.Unknown;
        }

        var hyphen = HyphenRange().Match(expression);
        if (hyphen.Success)
        {
            if (!ComparableModVersion.TryParse(hyphen.Groups[1].Value, out var lower) ||
                !ComparableModVersion.TryParse(hyphen.Groups[2].Value, out var upper))
            {
                return VersionRequirementResult.Unknown;
            }
            return actual.CompareTo(lower) >= 0 && actual.CompareTo(upper) <= 0
                ? VersionRequirementResult.Satisfied
                : VersionRequirementResult.NotSatisfied;
        }

        var tokens = ComparatorToken().Matches(expression);
        if (tokens.Count == 0)
        {
            return VersionRequirementResult.Unknown;
        }

        var consumed = string.Concat(tokens.Select(match => match.Value));
        var compactExpression = Regex.Replace(expression, @"[\s,]+", string.Empty);
        var compactConsumed = Regex.Replace(consumed, @"[\s,]+", string.Empty);
        if (!string.Equals(compactExpression, compactConsumed, StringComparison.Ordinal))
        {
            return VersionRequirementResult.Unknown;
        }

        foreach (Match token in tokens)
        {
            var result = EvaluateComparator(token.Value.Trim().TrimEnd(','), actual);
            if (result != VersionRequirementResult.Satisfied)
            {
                return result;
            }
        }
        return VersionRequirementResult.Satisfied;
    }

    private static VersionRequirementResult EvaluateComparator(string token, ComparableModVersion actual)
    {
        if (token is "*" or "x" or "X")
        {
            return VersionRequirementResult.Satisfied;
        }

        var match = SingleComparator().Match(token);
        if (!match.Success)
        {
            return VersionRequirementResult.Unknown;
        }
        var operation = match.Groups[1].Value;
        var operandText = match.Groups[2].Value;

        if (ContainsWildcard(operandText))
        {
            if (operation.Length > 0 && operation is not "=" and not "==")
            {
                return VersionRequirementResult.Unknown;
            }
            return EvaluateWildcard(operandText, actual);
        }

        if (!ComparableModVersion.TryParse(operandText, out var operand))
        {
            return VersionRequirementResult.Unknown;
        }

        if (operation is "~" or "~=")
        {
            var upper = operand.IncrementForTilde(CountNumericComponents(operandText));
            return actual.CompareTo(operand) >= 0 && actual.CompareTo(upper) < 0
                ? VersionRequirementResult.Satisfied
                : VersionRequirementResult.NotSatisfied;
        }
        if (operation == "^")
        {
            var upper = operand.IncrementForCaret();
            return actual.CompareTo(operand) >= 0 && actual.CompareTo(upper) < 0
                ? VersionRequirementResult.Satisfied
                : VersionRequirementResult.NotSatisfied;
        }

        var comparison = actual.CompareTo(operand);
        var satisfied = operation switch
        {
            ">" => comparison > 0,
            ">=" => comparison >= 0,
            "<" => comparison < 0,
            "<=" => comparison <= 0,
            "!=" => comparison != 0,
            "=" or "==" or "" => comparison == 0,
            _ => false,
        };
        return satisfied ? VersionRequirementResult.Satisfied : VersionRequirementResult.NotSatisfied;
    }

    private static VersionRequirementResult EvaluateWildcard(string operand, ComparableModVersion actual)
    {
        var clean = StripVersionPrefixAndSuffix(operand);
        var parts = clean.Split('.');
        var wildcardIndex = Array.FindIndex(parts, part => part is "*" or "x" or "X");
        if (wildcardIndex < 0)
        {
            return VersionRequirementResult.Unknown;
        }
        if (wildcardIndex == 0)
        {
            return VersionRequirementResult.Satisfied;
        }

        var lowerParts = new int[Math.Max(3, parts.Length)];
        for (var index = 0; index < wildcardIndex; index++)
        {
            if (!int.TryParse(parts[index], NumberStyles.None, CultureInfo.InvariantCulture, out lowerParts[index]))
            {
                return VersionRequirementResult.Unknown;
            }
        }
        var upperParts = (int[])lowerParts.Clone();
        upperParts[wildcardIndex - 1]++;
        for (var index = wildcardIndex; index < upperParts.Length; index++)
        {
            upperParts[index] = 0;
        }

        var lower = ComparableModVersion.FromNumeric(lowerParts);
        var upper = ComparableModVersion.FromNumeric(upperParts);
        return actual.CompareTo(lower) >= 0 && actual.CompareTo(upper) < 0
            ? VersionRequirementResult.Satisfied
            : VersionRequirementResult.NotSatisfied;
    }

    private static VersionRequirementResult EvaluateMaven(string expression, ComparableModVersion actual)
    {
        var matches = MavenRange().Matches(expression);
        if (matches.Count == 0)
        {
            return VersionRequirementResult.Unknown;
        }

        var consumed = string.Concat(matches.Select(match => match.Value));
        if (!string.Equals(
                Regex.Replace(expression, @"\s+", string.Empty),
                Regex.Replace(consumed, @"\s+", string.Empty),
                StringComparison.Ordinal))
        {
            return VersionRequirementResult.Unknown;
        }

        var sawKnown = false;
        var sawUnknown = false;
        foreach (Match match in matches)
        {
            var open = match.Groups[1].Value[0];
            var body = match.Groups[2].Value.Trim();
            var close = match.Groups[3].Value[0];
            var comma = body.IndexOf(',');

            if (comma < 0)
            {
                if (open != '[' || close != ']' || !ComparableModVersion.TryParse(body, out var exact))
                {
                    sawUnknown = true;
                    continue;
                }
                sawKnown = true;
                if (actual.CompareTo(exact) == 0)
                {
                    return VersionRequirementResult.Satisfied;
                }
                continue;
            }

            var lowerText = body[..comma].Trim();
            var upperText = body[(comma + 1)..].Trim();
            ComparableModVersion lower = default;
            ComparableModVersion upper = default;
            var hasLower = lowerText.Length > 0;
            var hasUpper = upperText.Length > 0;
            if ((hasLower && !ComparableModVersion.TryParse(lowerText, out lower)) ||
                (hasUpper && !ComparableModVersion.TryParse(upperText, out upper)))
            {
                sawUnknown = true;
                continue;
            }
            sawKnown = true;
            var lowerOk = !hasLower || (open == '[' ? actual.CompareTo(lower) >= 0 : actual.CompareTo(lower) > 0);
            var upperOk = !hasUpper || (close == ']' ? actual.CompareTo(upper) <= 0 : actual.CompareTo(upper) < 0);
            if (lowerOk && upperOk)
            {
                return VersionRequirementResult.Satisfied;
            }
        }
        return sawUnknown || !sawKnown
            ? VersionRequirementResult.Unknown
            : VersionRequirementResult.NotSatisfied;
    }

    private static bool LooksLikeMavenRange(string expression) =>
        expression.TrimStart().StartsWith('[') || expression.TrimStart().StartsWith('(');

    private static bool ContainsWildcard(string value) => value.Split('.', '-', '+')
        .Any(part => part is "*" or "x" or "X");

    private static string StripVersionPrefixAndSuffix(string value)
    {
        var clean = value.Trim();
        if (clean.StartsWith('v') || clean.StartsWith('V'))
        {
            clean = clean[1..];
        }
        var suffix = clean.IndexOfAny(['-', '+']);
        return suffix >= 0 ? clean[..suffix] : clean;
    }

    private static int CountNumericComponents(string value) =>
        StripVersionPrefixAndSuffix(value).Split('.', StringSplitOptions.RemoveEmptyEntries).Length;

    [GeneratedRegex(@"\s*\|\|\s*", RegexOptions.CultureInvariant)]
    private static partial Regex OrSeparator();

    [GeneratedRegex(@"^\s*([^\s]+)\s+-\s+([^\s]+)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex HyphenRange();

    [GeneratedRegex(@"(?:>=|<=|!=|==|~=|>|<|=|~|\^)?\s*(?:[vV]?[0-9xX*]+(?:\.[0-9A-Za-z*_-]+)*(?:\+[0-9A-Za-z.-]+)?|[xX*])\s*,?", RegexOptions.CultureInvariant)]
    private static partial Regex ComparatorToken();

    [GeneratedRegex(@"^(>=|<=|!=|==|~=|>|<|=|~|\^)?\s*([vV]?[0-9xX*]+(?:\.[0-9A-Za-z*_-]+)*(?:\+[0-9A-Za-z.-]+)?|[xX*])$", RegexOptions.CultureInvariant)]
    private static partial Regex SingleComparator();

    [GeneratedRegex(@"([\[(])\s*([^\])]*?)\s*([\])])\s*,?", RegexOptions.CultureInvariant)]
    private static partial Regex MavenRange();
}

internal readonly struct ComparableModVersion : IComparable<ComparableModVersion>
{
    private readonly int[] _numeric;
    private readonly string[] _preRelease;

    private ComparableModVersion(int[] numeric, string[] preRelease)
    {
        _numeric = numeric;
        _preRelease = preRelease;
    }

    public static ComparableModVersion FromNumeric(params int[] parts) =>
        new((int[])parts.Clone(), Array.Empty<string>());

    public static bool TryParse(string? input, out ComparableModVersion version)
    {
        version = default;
        var value = (input ?? string.Empty).Trim();
        if (value.StartsWith('v') || value.StartsWith('V'))
        {
            value = value[1..];
        }
        var buildIndex = value.IndexOf('+');
        if (buildIndex >= 0)
        {
            value = value[..buildIndex];
        }
        var preReleaseIndex = value.IndexOf('-');
        var core = preReleaseIndex >= 0 ? value[..preReleaseIndex] : value;
        var preRelease = preReleaseIndex >= 0
            ? value[(preReleaseIndex + 1)..].Split('.', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
        var pieces = core.Split('.');
        if (pieces.Length == 0 || pieces.Any(piece =>
                !int.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }
        var numeric = pieces
            .Select(piece => int.Parse(piece, NumberStyles.None, CultureInfo.InvariantCulture))
            .ToArray();
        version = new ComparableModVersion(numeric, preRelease);
        return true;
    }

    public ComparableModVersion IncrementForTilde(int specifiedComponents)
    {
        var values = PaddedNumeric();
        var index = specifiedComponents <= 1 ? 0 : 1;
        values[index]++;
        for (var current = index + 1; current < values.Length; current++)
        {
            values[current] = 0;
        }
        return FromNumeric(values);
    }

    public ComparableModVersion IncrementForCaret()
    {
        var values = PaddedNumeric();
        var index = Array.FindIndex(values, value => value != 0);
        if (index < 0)
        {
            index = values.Length - 1;
        }
        values[index]++;
        for (var current = index + 1; current < values.Length; current++)
        {
            values[current] = 0;
        }
        return FromNumeric(values);
    }

    public int CompareTo(ComparableModVersion other)
    {
        var leftNumeric = _numeric ?? Array.Empty<int>();
        var rightNumeric = other._numeric ?? Array.Empty<int>();
        var componentCount = Math.Max(leftNumeric.Length, rightNumeric.Length);
        for (var index = 0; index < componentCount; index++)
        {
            var left = index < leftNumeric.Length ? leftNumeric[index] : 0;
            var right = index < rightNumeric.Length ? rightNumeric[index] : 0;
            var comparison = left.CompareTo(right);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        var leftPre = _preRelease ?? Array.Empty<string>();
        var rightPre = other._preRelease ?? Array.Empty<string>();
        if (leftPre.Length == 0 || rightPre.Length == 0)
        {
            return leftPre.Length == rightPre.Length ? 0 : leftPre.Length == 0 ? 1 : -1;
        }
        for (var index = 0; index < Math.Max(leftPre.Length, rightPre.Length); index++)
        {
            if (index >= leftPre.Length)
            {
                return -1;
            }
            if (index >= rightPre.Length)
            {
                return 1;
            }
            var comparison = ComparePreReleaseIdentifier(leftPre[index], rightPre[index]);
            if (comparison != 0)
            {
                return comparison;
            }
        }
        return 0;
    }

    private int[] PaddedNumeric()
    {
        var result = new int[Math.Max(3, _numeric?.Length ?? 0)];
        if (_numeric is not null)
        {
            Array.Copy(_numeric, result, _numeric.Length);
        }
        return result;
    }

    private static int ComparePreReleaseIdentifier(string left, string right)
    {
        var leftNumeric = int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
        var rightNumeric = int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
        if (leftNumeric && rightNumeric)
        {
            return leftNumber.CompareTo(rightNumber);
        }
        if (leftNumeric != rightNumeric)
        {
            return leftNumeric ? -1 : 1;
        }
        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
    }
}
