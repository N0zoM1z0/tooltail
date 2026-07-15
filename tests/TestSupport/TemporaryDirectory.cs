using System.Text;

namespace Tooltail.Testing;

public sealed class TemporaryDirectory : IDisposable
{
    private bool disposed;

    public TemporaryDirectory()
    {
        string parent = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tooltail-tests");
        Path = System.IO.Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateTextFile(string relativePath, string contents)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        string rootWithSeparator = Path.EndsWith(System.IO.Path.DirectorySeparatorChar)
            ? Path
            : Path + System.IO.Path.DirectorySeparatorChar;
        string fullPath = System.IO.Path.GetFullPath(relativePath, rootWithSeparator);
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.Ordinal))
        {
            throw new ArgumentOutOfRangeException(
                nameof(relativePath),
                "Test fixture path must remain inside its temporary root.");
        }

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(
            fullPath,
            contents,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return fullPath;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
