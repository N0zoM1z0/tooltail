using System.Text.RegularExpressions;
using Tooltail.Contracts.Skills;
using Tooltail.Features.FileSkills.Snapshots;

namespace Tooltail.Features.FileSkills.Skills;

public static class SkillMatcher
{
    public static bool Matches(SkillMatchContract match, FolderSnapshotEntry entry)
    {
        ArgumentNullException.ThrowIfNull(match);
        ArgumentNullException.ThrowIfNull(entry);
        if (!match.RegularFilesOnly ||
            entry.Kind != SnapshotEntryKind.File ||
            entry.IsReparsePoint ||
            entry.Length is null)
        {
            return false;
        }

        if (match.MaxBytes is not null && entry.Length.Value > match.MaxBytes.Value)
        {
            return false;
        }

        if (match.OriginRelativeDirectory is not null)
        {
            string expected = match.OriginRelativeDirectory.Replace('/', '\\');
            if (!string.Equals(
                    SkillTemplateEngine.Parent(entry.RelativePath),
                    expected,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        string fileName = SkillTemplateEngine.FileName(entry.RelativePath);
        if (match.Extensions is not null)
        {
            string extension = SkillTemplateEngine.Extension(fileName);
            if (!match.Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return match.Filename is null || FilenameMatches(match.Filename, fileName);
    }

    private static bool FilenameMatches(
        SkillFilenameMatchContract match,
        string fileName)
    {
        StringComparison comparison = match.CaseSensitive
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        if (match.Prefix is not null && !fileName.StartsWith(match.Prefix, comparison))
        {
            return false;
        }

        if (match.Suffix is not null && !fileName.EndsWith(match.Suffix, comparison))
        {
            return false;
        }

        if (match.Contains is not null && !fileName.Contains(match.Contains, comparison))
        {
            return false;
        }

        if (match.SafeRegex is null)
        {
            return true;
        }

        RegexOptions options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
        if (!match.CaseSensitive)
        {
            options |= RegexOptions.IgnoreCase;
        }

        try
        {
            return Regex.IsMatch(
                fileName,
                match.SafeRegex,
                options,
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
