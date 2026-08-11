using System.Text;
using System.Text.RegularExpressions;

namespace McModpackTool.Core.Compatibility;

public static partial class ArtifactMetadataReader
{
    private static partial void ParseForgeTomlCore(string text, MetadataBuilder builder, bool neoForge)
    {
        builder.Loader = Prefer(builder.Loader, neoForge ? "neoforge" : "forge");
        var section = string.Empty;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void FlushSection()
        {
            if (section.Equals("mods", StringComparison.OrdinalIgnoreCase))
            {
                var id = values.GetValueOrDefault("modId", string.Empty);
                AddIdentity(builder.ModIds, id);
                builder.Id = Prefer(builder.Id, id);
                builder.Name = Prefer(builder.Name, values.GetValueOrDefault("displayName", string.Empty));
                builder.Description = Prefer(builder.Description, values.GetValueOrDefault("description", string.Empty));
                var version = values.GetValueOrDefault("version", string.Empty);
                if (!version.Contains("${", StringComparison.Ordinal))
                {
                    builder.Version = Prefer(builder.Version, version);
                }
            }
            else if (section.StartsWith("dependencies.", StringComparison.OrdinalIgnoreCase))
            {
                AddForgeDependency(values, builder.Relations);
            }
            values.Clear();
        }

        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } rawLine)
        {
            var line = StripTomlComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }
            var sectionMatch = TomlArraySection().Match(line);
            if (sectionMatch.Success)
            {
                FlushSection();
                section = sectionMatch.Groups[1].Value.Trim();
                continue;
            }

            var assignment = TomlAssignment().Match(line);
            if (!assignment.Success)
            {
                continue;
            }
            values[assignment.Groups[1].Value] = DecodeTomlScalar(assignment.Groups[2].Value.Trim());
        }
        FlushSection();
    }

    private static void AddForgeDependency(
        IReadOnlyDictionary<string, string> values,
        ICollection<CompatibilityRelation> destination)
    {
        var reference = values.GetValueOrDefault("modId", string.Empty).Trim();
        if (reference.Length == 0)
        {
            return;
        }

        var declaredType = values.GetValueOrDefault("type", string.Empty).Trim().ToLowerInvariant();
        var mandatoryText = values.GetValueOrDefault("mandatory", string.Empty).Trim();
        var mandatory = !bool.TryParse(mandatoryText, out var parsedMandatory) || parsedMandatory;
        string? kind = declaredType switch
        {
            "required" => CompatibilityRelationKinds.Required,
            "incompatible" => CompatibilityRelationKinds.Incompatible,
            "optional" or "discouraged" => null,
            _ => mandatory ? CompatibilityRelationKinds.Required : null,
        };
        if (kind is null)
        {
            return;
        }

        destination.Add(new CompatibilityRelation
        {
            Kind = kind,
            Reference = reference,
            ExactReference = reference,
            ReferenceType = CompatibilityReferenceTypes.ModId,
            VersionRequirement = values.GetValueOrDefault("versionRange", string.Empty).Trim(),
        });
    }

    private static string StripTomlComment(string line)
    {
        var builder = new StringBuilder(line.Length);
        var quote = '\0';
        var escaped = false;
        foreach (var character in line)
        {
            if (escaped)
            {
                builder.Append(character);
                escaped = false;
                continue;
            }
            if (quote == '"' && character == '\\')
            {
                builder.Append(character);
                escaped = true;
                continue;
            }
            if (character is '\'' or '"')
            {
                if (quote == '\0')
                {
                    quote = character;
                }
                else if (quote == character)
                {
                    quote = '\0';
                }
                builder.Append(character);
                continue;
            }
            if (character == '#' && quote == '\0')
            {
                break;
            }
            builder.Append(character);
        }
        return builder.ToString();
    }

    private static string DecodeTomlScalar(string value)
    {
        if (value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') ||
                                  (value[0] == '\'' && value[^1] == '\'')))
        {
            var body = value[1..^1];
            if (value[0] == '\'')
            {
                return body;
            }
            return body
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal)
                .Replace("\\n", "\n", StringComparison.Ordinal)
                .Replace("\\r", "\r", StringComparison.Ordinal)
                .Replace("\\t", "\t", StringComparison.Ordinal);
        }
        return value.Trim();
    }

    [GeneratedRegex(@"^\[\[\s*([^\]]+?)\s*\]\]$", RegexOptions.CultureInvariant)]
    private static partial Regex TomlArraySection();

    [GeneratedRegex(@"^([A-Za-z0-9_-]+)\s*=\s*(.+)$", RegexOptions.CultureInvariant)]
    private static partial Regex TomlAssignment();
}
