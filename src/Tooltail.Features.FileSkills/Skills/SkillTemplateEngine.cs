using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tooltail.Contracts.Skills;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Skills;

public enum SkillTemplateKind
{
    FileName,
    RelativePath,
}

public sealed record SkillTemplateError(string Code, string Field);

public readonly record struct SkillTemplateResult(
    bool IsSuccess,
    string? Value,
    SkillTemplateError? Error)
{
    public static SkillTemplateResult Success(string value) => new(true, value, null);

    public static SkillTemplateResult Failure(string code, string field) =>
        new(false, null, new SkillTemplateError(code, field));
}

public static class SkillTemplateEngine
{
    private const int MaximumRenderedPathLength = 1024;

    public static SkillTemplateError? Validate(
        string? template,
        SkillTemplateKind kind,
        IReadOnlySet<string> declaredVariables,
        string field)
    {
        ArgumentNullException.ThrowIfNull(declaredVariables);
        ArgumentException.ThrowIfNullOrWhiteSpace(field);
        if (string.IsNullOrWhiteSpace(template))
        {
            return new SkillTemplateError("template.empty", field);
        }

        int maximumLength = kind == SkillTemplateKind.FileName ? 220 : 300;
        if (template.Length > maximumLength || template.Contains('\\'))
        {
            return new SkillTemplateError("template.invalid_shape", field);
        }

        if (kind == SkillTemplateKind.FileName && template.Contains('/'))
        {
            return new SkillTemplateError("template.filename_contains_separator", field);
        }

        if (!TryTokenize(template, out List<TemplatePart>? parts))
        {
            return new SkillTemplateError("template.invalid_syntax", field);
        }

        foreach (TemplatePart part in parts!)
        {
            if (part.IsVariable && !declaredVariables.Contains(part.Value))
            {
                return new SkillTemplateError("template.variable_undefined", field);
            }
        }

        string placeholder = string.Concat(
            parts!.Select(static part => part.IsVariable ? "x" : part.Value));
        string windowsPath = placeholder.Replace('/', '\\');
        PathSafetyResult<WindowsRelativePath> parsed = WindowsPathPolicy.ParseRelative(windowsPath);
        return parsed.IsSuccess
            ? null
            : new SkillTemplateError(parsed.Error!.Code, field);
    }

    public static SkillTemplateResult Render(
        string template,
        SkillTemplateKind kind,
        IReadOnlyDictionary<string, string> variables,
        string field)
    {
        ArgumentNullException.ThrowIfNull(variables);
        HashSet<string> declared = variables.Keys.ToHashSet(StringComparer.Ordinal);
        SkillTemplateError? validation = Validate(template, kind, declared, field);
        if (validation is not null)
        {
            return SkillTemplateResult.Failure(validation.Code, validation.Field);
        }

        TryTokenize(template, out List<TemplatePart>? parts);
        StringBuilder rendered = new(template.Length);
        foreach (TemplatePart part in parts!)
        {
            if (!part.IsVariable)
            {
                rendered.Append(part.Value);
                continue;
            }

            if (!variables.TryGetValue(part.Value, out string? value))
            {
                return SkillTemplateResult.Failure("template.variable_unbound", field);
            }

            rendered.Append(value);
            if (rendered.Length > MaximumRenderedPathLength)
            {
                return SkillTemplateResult.Failure("template.rendered_too_long", field);
            }
        }

        string windowsPath = rendered.ToString().Replace('/', '\\');
        PathSafetyResult<WindowsRelativePath> parsed = WindowsPathPolicy.ParseRelative(windowsPath);
        if (!parsed.IsSuccess)
        {
            return SkillTemplateResult.Failure(parsed.Error!.Code, field);
        }

        if (kind == SkillTemplateKind.FileName && parsed.Value!.Value.Contains('\\'))
        {
            return SkillTemplateResult.Failure("template.filename_contains_separator", field);
        }

        return SkillTemplateResult.Success(parsed.Value!.Value);
    }

    public static SkillTemplateResult BindVariables(
        IReadOnlyList<SkillVariableContract> definitions,
        FolderSnapshotEntry source,
        SkillFilenameMatchContract? filenameMatch,
        IReadOnlyDictionary<string, string>? userParameters,
        out IReadOnlyDictionary<string, string>? values)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(source);
        values = null;
        string fileName = FileName(source.RelativePath);
        string stem = Stem(fileName);
        string extension = Extension(fileName);
        Match? regexMatch = null;
        Dictionary<string, string> bound = new(StringComparer.Ordinal);

        foreach (SkillVariableContract definition in definitions)
        {
            string? value = definition.Source switch
            {
                SkillVariableSource.OriginalStem => stem,
                SkillVariableSource.OriginalExtension => extension,
                SkillVariableSource.FileCreatedYear =>
                    source.CreationUtc.Year.ToString("0000", CultureInfo.InvariantCulture),
                SkillVariableSource.FileCreatedMonth =>
                    source.CreationUtc.Month.ToString("00", CultureInfo.InvariantCulture),
                SkillVariableSource.FileModifiedYear =>
                    source.LastWriteUtc.Year.ToString("0000", CultureInfo.InvariantCulture),
                SkillVariableSource.FileModifiedMonth =>
                    source.LastWriteUtc.Month.ToString("00", CultureInfo.InvariantCulture),
                SkillVariableSource.UserParameter when
                    definition.Argument is not null &&
                    userParameters is not null &&
                    userParameters.TryGetValue(definition.Argument, out string? parameter) => parameter,
                SkillVariableSource.RegexCapture when
                    definition.Argument is not null &&
                    filenameMatch?.SafeRegex is not null =>
                    CaptureRegexGroup(
                        filenameMatch,
                        fileName,
                        definition.Argument,
                        ref regexMatch),
                _ => null,
            };
            if (value is null ||
                (value.Length == 0 && definition.Source != SkillVariableSource.OriginalExtension))
            {
                return SkillTemplateResult.Failure(
                    "template.variable_source_unavailable",
                    $"variables.{definition.Name}");
            }

            foreach (SkillVariableTransform transform in definition.Transforms)
            {
                value = transform switch
                {
                    SkillVariableTransform.Lowercase => value.ToLowerInvariant(),
                    SkillVariableTransform.Uppercase => value.ToUpperInvariant(),
                    SkillVariableTransform.SlugHyphen => SlugHyphen(value),
                    _ => throw new ArgumentOutOfRangeException(nameof(definitions)),
                };
            }

            if ((value.Length == 0 &&
                    definition.Source != SkillVariableSource.OriginalExtension) ||
                value.Length > 220 ||
                !bound.TryAdd(definition.Name, value))
            {
                return SkillTemplateResult.Failure(
                    "template.variable_value_invalid",
                    $"variables.{definition.Name}");
            }
        }

        values = bound;
        return SkillTemplateResult.Success("variables.bound");
    }

    internal static string FileName(string relativePath)
    {
        int separator = relativePath.LastIndexOf('\\');
        return separator < 0 ? relativePath : relativePath[(separator + 1)..];
    }

    internal static string Parent(string relativePath)
    {
        int separator = relativePath.LastIndexOf('\\');
        return separator < 0 ? string.Empty : relativePath[..separator];
    }

    internal static string Stem(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot <= 0 ? fileName : fileName[..dot];
    }

    internal static string Extension(string fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot <= 0 ? string.Empty : fileName[dot..];
    }

    private static string? CaptureRegexGroup(
        SkillFilenameMatchContract filenameMatch,
        string fileName,
        string groupName,
        ref Match? cachedMatch)
    {
        try
        {
            if (cachedMatch is null)
            {
                RegexOptions options =
                    RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
                if (!filenameMatch.CaseSensitive)
                {
                    options |= RegexOptions.IgnoreCase;
                }

                Regex regex = new(
                    filenameMatch.SafeRegex!,
                    options,
                    TimeSpan.FromMilliseconds(100));
                cachedMatch = regex.Match(fileName);
            }

            Group group = cachedMatch.Groups[groupName];
            return cachedMatch.Success && group.Success ? group.Value : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    internal static string SlugHyphen(string value)
    {
        StringBuilder result = new(value.Length);
        bool pendingSeparator = false;
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune))
            {
                if (pendingSeparator && result.Length > 0)
                {
                    result.Append('-');
                }

                result.Append(rune.ToString());
                pendingSeparator = false;
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return result.ToString();
    }

    private static bool TryTokenize(
        string template,
        out List<TemplatePart>? parts)
    {
        parts = [];
        int position = 0;
        while (position < template.Length)
        {
            int opening = template.IndexOf("{{", position, StringComparison.Ordinal);
            int strayClosing = template.IndexOf("}}", position, StringComparison.Ordinal);
            if (strayClosing >= 0 && (opening < 0 || strayClosing < opening))
            {
                parts = null;
                return false;
            }

            if (opening < 0)
            {
                string tail = template[position..];
                if (tail.Contains('{') || tail.Contains('}'))
                {
                    parts = null;
                    return false;
                }

                parts.Add(new TemplatePart(IsVariable: false, tail));
                break;
            }

            if (opening > position)
            {
                string literal = template[position..opening];
                if (literal.Contains('{') || literal.Contains('}'))
                {
                    parts = null;
                    return false;
                }

                parts.Add(new TemplatePart(IsVariable: false, literal));
            }

            int closing = template.IndexOf("}}", opening + 2, StringComparison.Ordinal);
            if (closing < 0)
            {
                parts = null;
                return false;
            }

            string variable = template[(opening + 2)..closing];
            if (variable.Length is 0 or > 32 ||
                !char.IsAsciiLetter(variable[0]) ||
                variable.Any(static character => !char.IsAsciiLetterOrDigit(character)))
            {
                parts = null;
                return false;
            }

            parts.Add(new TemplatePart(IsVariable: true, variable));
            position = closing + 2;
        }

        return parts.Count > 0;
    }

    private sealed record TemplatePart(bool IsVariable, string Value);
}
