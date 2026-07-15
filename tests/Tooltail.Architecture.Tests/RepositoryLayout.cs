namespace Tooltail.Architecture.Tests;

internal static class RepositoryLayout
{
    public static string FindRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Tooltail.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Tooltail.sln from the architecture test output directory.");
    }
}
