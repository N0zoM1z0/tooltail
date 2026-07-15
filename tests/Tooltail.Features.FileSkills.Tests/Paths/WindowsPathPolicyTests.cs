using System.Text;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Tests.Paths;

public sealed class WindowsPathPolicyTests
{
    public static TheoryData<string, string> UnsafePaths => new()
    {
        { "", PathSafetyReasonCodes.Empty },
        { "   ", PathSafetyReasonCodes.Empty },
        { "C:\\outside.txt", PathSafetyReasonCodes.DriveRelative },
        { "C:outside.txt", PathSafetyReasonCodes.DriveRelative },
        { "\\\\server\\share\\file.txt", PathSafetyReasonCodes.Unc },
        { "\\\\?\\C:\\file.txt", PathSafetyReasonCodes.Device },
        { "\\\\.\\C:\\file.txt", PathSafetyReasonCodes.Device },
        { "\\??\\C:\\file.txt", PathSafetyReasonCodes.Device },
        { "\\rooted.txt", PathSafetyReasonCodes.Rooted },
        { "/rooted.txt", PathSafetyReasonCodes.Rooted },
        { "folder/file.txt", PathSafetyReasonCodes.InvalidSeparator },
        { "folder\\..\\outside.txt", PathSafetyReasonCodes.Traversal },
        { "folder\\.\\file.txt", PathSafetyReasonCodes.Traversal },
        { "folder\\\\file.txt", PathSafetyReasonCodes.EmptySegment },
        { "file.txt:secret", PathSafetyReasonCodes.AlternateStream },
        { "folder.\\file.txt", PathSafetyReasonCodes.TrailingDotOrSpace },
        { "folder \\file.txt", PathSafetyReasonCodes.TrailingDotOrSpace },
        { "NUL", PathSafetyReasonCodes.ReservedName },
        { "con.txt", PathSafetyReasonCodes.ReservedName },
        { "COM¹.log", PathSafetyReasonCodes.ReservedName },
        { "file?.txt", PathSafetyReasonCodes.InvalidCharacter },
        { "file\u0001.txt", PathSafetyReasonCodes.InvalidCharacter },
    };

    [Theory]
    [MemberData(nameof(UnsafePaths))]
    public void ParserRejectsAdversarialWindowsPathCorpus(string path, string expectedCode)
    {
        PathSafetyResult<WindowsRelativePath> result = WindowsPathPolicy.ParseRelative(path);

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedCode, result.Error?.Code);
    }

    [Fact]
    public void ParserAcceptsBoundedNfcUnicodePath()
    {
        const string input = "Inbox\\Résumé-檔案.txt";

        PathSafetyResult<WindowsRelativePath> result = WindowsPathPolicy.ParseRelative(input);

        Assert.True(result.IsSuccess);
        Assert.Equal(input, result.Value!.Value);
    }

    [Fact]
    public void ParserRejectsEquivalentButNonNfcPath()
    {
        string decomposed = "Inbox\\Re\u0301sume\u0301.txt";
        Assert.False(decomposed.IsNormalized(NormalizationForm.FormC));

        PathSafetyResult<WindowsRelativePath> result = WindowsPathPolicy.ParseRelative(decomposed);

        Assert.False(result.IsSuccess);
        Assert.Equal(PathSafetyReasonCodes.NotNormalized, result.Error?.Code);
    }

    [Fact]
    public void SegmentAndWholePathBoundsFailClosed()
    {
        string acceptedSegment = new('a', WindowsPathPolicy.MaximumSegmentLength);
        string rejectedSegment = new('a', WindowsPathPolicy.MaximumSegmentLength + 1);
        string rejectedPath = string.Join(
            '\\',
            Enumerable.Repeat("bounded-segment", 100));

        Assert.True(WindowsPathPolicy.ParseRelative(acceptedSegment).IsSuccess);
        Assert.Equal(
            PathSafetyReasonCodes.SegmentTooLong,
            WindowsPathPolicy.ParseRelative(rejectedSegment).Error?.Code);
        Assert.Equal(
            PathSafetyReasonCodes.TooLong,
            WindowsPathPolicy.ParseRelative(rejectedPath).Error?.Code);
    }

    [Fact]
    public void ContainmentComparisonRequiresASeparatorBoundaryAndIgnoresWindowsCase()
    {
        Assert.True(WindowsPathPolicy.IsWithinRoot(
            "C:\\Users\\Tester\\Grant",
            "c:\\users\\tester\\grant\\Inbox\\file.txt"));
        Assert.False(WindowsPathPolicy.IsWithinRoot(
            "C:\\Users\\Tester\\Grant",
            "C:\\Users\\Tester\\GrantedElsewhere\\file.txt"));
    }

    [Fact]
    public void AcceptedGeneratedPathsCannotResolvePastTheRootBoundary()
    {
        Random random = new(0x5A17);
        const string root = "C:\\Granted\\Root";
        for (int iteration = 0; iteration < 1000; iteration++)
        {
            string relative = string.Join(
                '\\',
                Enumerable.Range(0, random.Next(1, 6))
                    .Select(_ => $"segment-{random.Next(0, 100000):D5}"));
            PathSafetyResult<WindowsRelativePath> parsed = WindowsPathPolicy.ParseRelative(relative);

            Assert.True(parsed.IsSuccess);
            Assert.True(WindowsPathPolicy.IsWithinRoot(root, $"{root}\\{parsed.Value!.Value}"));
        }
    }

    [Fact]
    public void CaseOnlyRenameIsExplicitlyUnsupported()
    {
        WindowsRelativePath source = WindowsPathPolicy.ParseRelative("Inbox\\Report.txt").Value!;
        WindowsRelativePath destination = WindowsPathPolicy.ParseRelative("Inbox\\report.txt").Value!;

        PathSafetyResult<ValidatedPathPair> result =
            WindowsPathPolicy.ValidateDistinctPair(source, destination);

        Assert.False(result.IsSuccess);
        Assert.Equal(PathSafetyReasonCodes.CaseOnlyChangeUnsupported, result.Error?.Code);
    }
}
