using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tooltail.ReleaseAudit;

public sealed record PortablePackageFile(
    string Path,
    long Length,
    string Sha256);

public sealed record PortablePackageManifest(
    string ContractVersion,
    string Product,
    string Version,
    string RuntimeIdentifier,
    string TargetFramework,
    bool SelfContained,
    bool IsCodeSigned,
    string EntryPoint,
    string DataRoot,
    string UninstallScope,
    bool UninstallPreservesData,
    IReadOnlyList<PortablePackageFile> Files);

public sealed record PortablePackageVerification(
    int FileCount,
    long PayloadBytes,
    string ArchiveSha256,
    PortablePackageManifest Manifest);

public sealed record PortableUninstallVerification(
    string PackageSha256,
    int ApphostExitCode,
    bool ProgramDirectoryRemoved,
    bool LocalDataSentinelPreserved,
    string UninstallScope,
    string DataRoot);

public static class PortablePackageApplication
{
    private const int MaximumFileCount = 2000;
    private const long MaximumFileBytes = 256L * 1024 * 1024;
    private const long MaximumPayloadBytes = 512L * 1024 * 1024;
    private const int MaximumManifestBytes = 1024 * 1024;
    private const string ArchiveName = "Tooltail-0.1.0-win-x64-portable.zip";
    private const string PackagePrefix = "Tooltail/";
    private const string ManifestEntryName = "Tooltail/package-manifest.json";
    private const string VerificationMarkerName = ".tooltail-portable-uninstall-fixture";
    private const string LocalDataSentinelName = "retained-user-data.sentinel";
    private static readonly DateTimeOffset DeterministicEntryTime =
        new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling =
            System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };
    private static readonly HashSet<string> RequiredPayload = new(
        [
            "Tooltail.Desktop.exe",
            "Tooltail.Desktop.dll",
            "Tooltail.Desktop.deps.json",
            "Tooltail.Desktop.runtimeconfig.json",
            "coreclr.dll",
            "hostfxr.dll",
            "hostpolicy.dll",
            "PresentationFramework.dll",
        ],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> ForbiddenExtensions = new(
        [
            ".bak",
            ".db",
            ".dmp",
            ".etl",
            ".jsonl",
            ".log",
            ".pdb",
            ".sqlite",
            ".sqlite3",
            ".tmp",
            ".zip",
        ],
        StringComparer.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            if (args.Count == 7 && args[0] == "pack-portable" &&
                args[1] == "--root" && args[3] == "--publish" &&
                args[5] == "--output")
            {
                string root = ValidateRepositoryRoot(args[2]);
                string publish = ValidateExactPath(
                    args[4],
                    Path.Combine(root, "artifacts", "portable", "win-x64", "publish"),
                    "Portable publish root is fixed below repository artifacts.");
                string archive = ValidateExactPath(
                    args[6],
                    Path.Combine(root, "artifacts", "portable", ArchiveName),
                    "Portable archive path is fixed below repository artifacts.");
                ValidateStandardUserManifest(root);
                PortablePackageVerification result = BuildArchive(publish, archive);
                await WriteResultAsync(
                    output,
                    "portable_package.created",
                    new
                    {
                        result.FileCount,
                        result.PayloadBytes,
                        result.ArchiveSha256,
                        result.Manifest.Version,
                        result.Manifest.RuntimeIdentifier,
                        result.Manifest.SelfContained,
                        result.Manifest.IsCodeSigned,
                        result.Manifest.UninstallScope,
                        result.Manifest.UninstallPreservesData,
                    },
                    cancellationToken).ConfigureAwait(false);
                return 0;
            }

            if (args.Count == 7 && args[0] == "verify-uninstall" &&
                args[1] == "--root" && args[3] == "--archive" &&
                args[5] == "--work")
            {
                string root = ValidateRepositoryRoot(args[2]);
                string archive = ValidateExactPath(
                    args[4],
                    Path.Combine(root, "artifacts", "portable", ArchiveName),
                    "Portable archive path is fixed below repository artifacts.");
                string work = ValidateExactPath(
                    args[6],
                    Path.Combine(
                        root,
                        "artifacts",
                        "portable",
                        "uninstall-verification"),
                    "Uninstall verification root is fixed below repository artifacts.");
                PortableUninstallVerification result =
                    await VerifyUninstallFixtureAsync(
                        archive,
                        work,
                        LaunchPackagedApphostAsync,
                        cancellationToken).ConfigureAwait(false);
                await WriteResultAsync(
                    output,
                    "portable_uninstall.verified",
                    result,
                    cancellationToken).ConfigureAwait(false);
                return 0;
            }

            await error.WriteLineAsync(
                "Usage: Tooltail.ReleaseAudit pack-portable --root <repository> " +
                "--publish <fixed-publish-root> --output <fixed-archive> OR " +
                "verify-uninstall --root <repository> --archive <fixed-archive> " +
                "--work <fixed-verification-root>");
            return 2;
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidDataException or IOException or UnauthorizedAccessException or
            JsonException or NotSupportedException)
        {
            await error.WriteLineAsync(JsonSerializer.Serialize(
                new
                {
                    status = "failed",
                    reasonCode = "portable_package.failed",
                    error = exception.Message,
                },
                WriteOptions));
            return 1;
        }
    }

    public static PortablePackageVerification BuildArchive(
        string publishRoot,
        string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publishRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        string root = Path.GetFullPath(publishRoot);
        string archive = Path.GetFullPath(archivePath);
        if (!Directory.Exists(root) || File.Exists(archive) || Directory.Exists(archive) ||
            File.Exists($"{archive}.sha256") || Directory.Exists($"{archive}.sha256"))
        {
            throw new InvalidDataException(
                "Portable packaging requires an existing publish root and absent output slots.");
        }

        ValidateAncestry(root);

        List<PayloadSource> payload = CapturePayload(root);
        PortablePackageManifest manifest = CreateManifest(payload);
        byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, WriteOptions);
        if (manifestBytes.Length > MaximumManifestBytes)
        {
            throw new InvalidDataException("Portable package manifest is oversized.");
        }

        string archiveParent = Path.GetDirectoryName(archive)!;
        Directory.CreateDirectory(archiveParent);
        ValidateAncestry(archiveParent);
        using (FileStream output = new(
                   archive,
                   FileMode.CreateNew,
                   FileAccess.ReadWrite,
                   FileShare.None))
        using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: false))
        {
            foreach (PayloadSource source in payload)
            {
                WritePayloadEntry(zip, source);
            }

            ZipArchiveEntry manifestEntry = zip.CreateEntry(
                ManifestEntryName,
                CompressionLevel.Optimal);
            manifestEntry.LastWriteTime = DeterministicEntryTime;
            using Stream target = manifestEntry.Open();
            target.Write(manifestBytes);
        }

        PortablePackageVerification verification = VerifyArchive(archive);
        WriteSha256Sidecar(archive, verification.ArchiveSha256);
        return verification;
    }

    public static PortablePackageVerification VerifyArchive(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        string archive = Path.GetFullPath(archivePath);
        if (!File.Exists(archive) || Directory.Exists(archive))
        {
            throw new InvalidDataException("Portable archive is missing or not a file.");
        }

        string archiveHash = HashFile(archive);
        using FileStream input = new(
            archive,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using ZipArchive zip = new(input, ZipArchiveMode.Read, leaveOpen: false);
        Dictionary<string, ZipArchiveEntry> entries = new(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            ValidateArchivePath(entry.FullName);
            if (IsSymbolicLinkEntry(entry) || !entries.TryAdd(entry.FullName, entry))
            {
                throw new InvalidDataException(
                    "Portable archive contains a link or duplicate entry.");
            }
        }

        if (!entries.TryGetValue(ManifestEntryName, out ZipArchiveEntry? manifestEntry) ||
            manifestEntry.Length is < 2 or > MaximumManifestBytes)
        {
            throw new InvalidDataException("Portable package manifest is missing or oversized.");
        }

        PortablePackageManifest manifest;
        using (Stream stream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<PortablePackageManifest>(
                    ReadBounded(stream, checked((int)manifestEntry.Length)),
                    ReadOptions) ??
                throw new InvalidDataException("Portable package manifest is empty.");
        }

        ValidateManifest(manifest);
        if (entries.Count != manifest.Files.Count + 1)
        {
            throw new InvalidDataException("Portable archive has unmanifested entries.");
        }

        long total = 0;
        HashSet<string> paths = new(StringComparer.Ordinal);
        foreach (PortablePackageFile file in manifest.Files)
        {
            ValidateRelativePath(file.Path);
            if (!paths.Add(file.Path) || file.Length is < 0 or > MaximumFileBytes ||
                !IsLowerHex64(file.Sha256) ||
                !entries.TryGetValue(PackagePrefix + file.Path, out ZipArchiveEntry? entry) ||
                entry.Length != file.Length)
            {
                throw new InvalidDataException("Portable package file manifest is invalid.");
            }

            using Stream stream = entry.Open();
            string actualHash = HashStream(stream);
            if (!string.Equals(actualHash, file.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Portable package payload hash mismatch.");
            }

            total = checked(total + file.Length);
        }

        if (total > MaximumPayloadBytes || manifest.Files.Count > MaximumFileCount)
        {
            throw new InvalidDataException("Portable package payload exceeds its bounds.");
        }

        RequirePayloadNames(manifest.Files.Select(static file => file.Path));
        return new PortablePackageVerification(
            manifest.Files.Count,
            total,
            archiveHash,
            manifest);
    }

    public static async Task<PortableUninstallVerification> VerifyUninstallFixtureAsync(
        string archivePath,
        string verificationRoot,
        Func<string, CancellationToken, Task<int>> launcher,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        PortablePackageVerification package = VerifyArchive(archivePath);
        string root = Path.GetFullPath(verificationRoot);
        if (Directory.Exists(root) || File.Exists(root))
        {
            throw new InvalidDataException(
                "Portable uninstall verification requires an absent work root.");
        }

        string? parent = Path.GetDirectoryName(root);
        if (parent is null || !Directory.Exists(parent))
        {
            throw new InvalidDataException(
                "Portable uninstall verification parent is unavailable.");
        }

        ValidateAncestry(parent);
        Directory.CreateDirectory(root);
        ValidateAncestry(root);
        string marker = Path.Combine(root, VerificationMarkerName);
        WriteCreateNew(marker, "tooltail.portable-uninstall-fixture/1\n"u8);
        string programRoot = Path.Combine(root, "program");
        string localDataRoot = Path.Combine(root, "local-data");
        Directory.CreateDirectory(programRoot);
        Directory.CreateDirectory(localDataRoot);
        string sentinel = Path.Combine(localDataRoot, LocalDataSentinelName);
        byte[] sentinelBytes = "preserve Tooltail local data during portable removal\n"u8.ToArray();
        WriteCreateNew(sentinel, sentinelBytes);
        ExtractArchive(archivePath, programRoot);

        string executable = Path.Combine(
            programRoot,
            "Tooltail",
            "Tooltail.Desktop.exe");
        int exitCode = await launcher(executable, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidDataException(
                "Packaged apphost smoke failed; program files were retained for inspection.");
        }

        ValidateRemovalBoundary(root, programRoot, marker, sentinel, sentinelBytes);
        ValidateTreeHasNoReparseEntries(programRoot);
        Directory.Delete(programRoot, recursive: true);
        if (Directory.Exists(programRoot) || !File.Exists(sentinel) ||
            !File.ReadAllBytes(sentinel).SequenceEqual(sentinelBytes))
        {
            throw new IOException(
                "Portable removal did not preserve the isolated local-data sentinel.");
        }

        PortableUninstallVerification result = new(
            package.ArchiveSha256,
            exitCode,
            ProgramDirectoryRemoved: true,
            LocalDataSentinelPreserved: true,
            "program_directory_only",
            "%LOCALAPPDATA%\\Tooltail");
        byte[] evidence = JsonSerializer.SerializeToUtf8Bytes(result, WriteOptions);
        WriteCreateNew(
            Path.Combine(root, "uninstall-evidence.json"),
            [.. evidence, (byte)'\n']);
        return result;
    }

    private static List<PayloadSource> CapturePayload(string root)
    {
        Queue<DirectoryInfo> pending = new([new DirectoryInfo(root)]);
        List<PayloadSource> payload = [];
        long total = 0;
        while (pending.TryDequeue(out DirectoryInfo? directory))
        {
            ValidateDirectory(directory.FullName);
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos()
                         .OrderBy(static item => item.Name, StringComparer.Ordinal))
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "Portable publish output contains a reparse or link entry.");
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Enqueue(child);
                    continue;
                }

                FileInfo file = (FileInfo)entry;
                string relative = Path.GetRelativePath(root, file.FullName)
                    .Replace(Path.DirectorySeparatorChar, '/');
                ValidateRelativePath(relative);
                ValidatePayloadName(relative);
                if (file.Length > MaximumFileBytes)
                {
                    throw new InvalidDataException("Portable payload file is oversized.");
                }

                total = checked(total + file.Length);
                if (total > MaximumPayloadBytes || payload.Count >= MaximumFileCount)
                {
                    throw new InvalidDataException("Portable publish output exceeds its bounds.");
                }

                payload.Add(new PayloadSource(
                    relative,
                    file.FullName,
                    file.Length,
                    HashFile(file.FullName)));
            }
        }

        payload.Sort(static (left, right) =>
            StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        RequirePayloadNames(payload.Select(static item => item.RelativePath));
        return payload;
    }

    private static PortablePackageManifest CreateManifest(
        IReadOnlyList<PayloadSource> payload) =>
        new(
            "tooltail.portable-package/1",
            "Tooltail",
            "0.1.0",
            "win-x64",
            "net10.0-windows10.0.22000.0",
            SelfContained: true,
            IsCodeSigned: false,
            "Tooltail.Desktop.exe",
            "%LOCALAPPDATA%\\Tooltail",
            "program_directory_only",
            UninstallPreservesData: true,
            payload.Select(static item => new PortablePackageFile(
                    item.RelativePath,
                    item.Length,
                    item.Sha256))
                .ToArray());

    private static void ValidateManifest(PortablePackageManifest manifest)
    {
        if (manifest.ContractVersion != "tooltail.portable-package/1" ||
            manifest.Product != "Tooltail" || manifest.Version != "0.1.0" ||
            manifest.RuntimeIdentifier != "win-x64" ||
            manifest.TargetFramework != "net10.0-windows10.0.22000.0" ||
            !manifest.SelfContained || manifest.IsCodeSigned ||
            manifest.EntryPoint != "Tooltail.Desktop.exe" ||
            manifest.DataRoot != "%LOCALAPPDATA%\\Tooltail" ||
            manifest.UninstallScope != "program_directory_only" ||
            !manifest.UninstallPreservesData || manifest.Files.Count == 0)
        {
            throw new InvalidDataException("Portable package manifest policy is invalid.");
        }
    }

    private static void WritePayloadEntry(ZipArchive zip, PayloadSource source)
    {
        ZipArchiveEntry entry = zip.CreateEntry(
            PackagePrefix + source.RelativePath,
            CompressionLevel.Optimal);
        entry.LastWriteTime = DeterministicEntryTime;
        using FileStream input = new(
            source.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        using Stream output = entry.Open();
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long length = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            output.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
            length = checked(length + read);
        }

        string actualHash = Convert.ToHexStringLower(hash.GetHashAndReset());
        if (length != source.Length ||
            !string.Equals(actualHash, source.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Portable publish payload changed while packaging.");
        }
    }

    private static void ExtractArchive(string archivePath, string programRoot)
    {
        using ZipArchive zip = ZipFile.OpenRead(archivePath);
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            ValidateArchivePath(entry.FullName);
            if (IsSymbolicLinkEntry(entry))
            {
                throw new InvalidDataException("Portable archive contains a link entry.");
            }

            string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
            string destination = Path.GetFullPath(Path.Combine(programRoot, relative));
            if (!IsContained(programRoot, destination))
            {
                throw new InvalidDataException("Portable archive extraction escaped its root.");
            }

            string destinationParent = Path.GetDirectoryName(destination)!;
            Directory.CreateDirectory(destinationParent);
            ValidateContainedAncestry(programRoot, destinationParent);
            using Stream source = entry.Open();
            using FileStream target = new(
                destination,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            source.CopyTo(target);
            target.Flush(flushToDisk: true);
        }
    }

    private static async Task<int> LaunchPackagedApphostAsync(
        string executable,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new NotSupportedException(
                "Packaged WPF apphost verification requires Windows.");
        }

        ProcessStartInfo start = new(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
            CreateNoWindow = false,
        };
        start.ArgumentList.Add("--window-shell-smoke-test");
        using Process process = Process.Start(start) ??
            throw new IOException("Could not start the packaged Tooltail apphost.");
        using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(5));
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new IOException(
                timeout.IsCancellationRequested
                    ? "Packaged Tooltail apphost smoke timed out."
                    : "Packaged Tooltail apphost smoke was cancelled.");
        }
    }

    private static void ValidateRemovalBoundary(
        string root,
        string programRoot,
        string marker,
        string sentinel,
        byte[] expectedSentinel)
    {
        ValidateDirectory(root);
        ValidateDirectory(programRoot);
        string expectedProgram = Path.Combine(root, "program");
        if (!string.Equals(
                Path.GetFullPath(programRoot),
                Path.GetFullPath(expectedProgram),
                PathComparison()) ||
            !IsRegularFile(marker) ||
            File.ReadAllText(marker) != "tooltail.portable-uninstall-fixture/1\n" ||
            !IsRegularFile(sentinel) ||
            !File.ReadAllBytes(sentinel).SequenceEqual(expectedSentinel))
        {
            throw new InvalidDataException(
                "Portable uninstall fixture identity or data sentinel changed.");
        }
    }

    private static void ValidateStandardUserManifest(string root)
    {
        string text = File.ReadAllText(Path.Combine(
            root,
            "src",
            "Tooltail.Desktop",
            "app.manifest"));
        if (!text.Contains(
                "requestedExecutionLevel level=\"asInvoker\" uiAccess=\"false\"",
                StringComparison.Ordinal) ||
            text.Contains("requireAdministrator", StringComparison.Ordinal) ||
            text.Contains("highestAvailable", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Portable package requires the reviewed standard-user manifest.");
        }
    }

    private static void ValidatePayloadName(string path)
    {
        string extension = Path.GetExtension(path);
        if (ForbiddenExtensions.Contains(extension))
        {
            throw new InvalidDataException(
                "Portable publish output contains prohibited state or debug material.");
        }
    }

    private static void RequirePayloadNames(IEnumerable<string> paths)
    {
        HashSet<string> names = paths
            .Where(static path => !path.Contains('/', StringComparison.Ordinal))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!RequiredPayload.IsSubsetOf(names))
        {
            throw new InvalidDataException(
                "Portable publish output is not a complete self-contained WPF apphost.");
        }
    }

    private static void ValidateArchivePath(string path)
    {
        if (!path.StartsWith(PackagePrefix, StringComparison.Ordinal) ||
            path == PackagePrefix || path.Contains('\\'))
        {
            throw new InvalidDataException("Portable archive entry path is invalid.");
        }

        ValidateRelativePath(path[PackagePrefix.Length..]);
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('/') ||
            path.EndsWith('/') || path.Contains('\\') || path.Contains(':') ||
            path.Split('/').Any(static segment =>
                segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException("Portable package relative path is invalid.");
        }
    }

    private static bool IsSymbolicLinkEntry(ZipArchiveEntry entry)
    {
        int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixType == 0xA000;
    }

    private static byte[] ReadBounded(Stream stream, int length)
    {
        byte[] bytes = new byte[length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("Portable package entry changed while reading.");
        }

        return bytes;
    }

    private static void WriteSha256Sidecar(string archive, string hash)
    {
        string text = $"{hash}  {Path.GetFileName(archive)}\n";
        WriteCreateNew($"{archive}.sha256", StrictUtf8.GetBytes(text));
    }

    private static void WriteCreateNew(string path, ReadOnlySpan<byte> bytes)
    {
        using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static string HashFile(string path)
    {
        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        return HashStream(stream);
    }

    private static string HashStream(Stream stream) =>
        Convert.ToHexStringLower(SHA256.HashData(stream));

    private static bool IsLowerHex64(string value) =>
        value.Length == 64 && value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsContained(string root, string path)
    {
        string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) +
            Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, PathComparison());
    }

    private static void ValidateDirectory(string path)
    {
        DirectoryInfo info = new(path);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "Portable package directory identity is unsafe.");
        }
    }

    private static void ValidateAncestry(string path)
    {
        for (DirectoryInfo? current = new(Path.GetFullPath(path));
             current is not null;
             current = current.Parent)
        {
            ValidateDirectory(current.FullName);
        }
    }

    private static void ValidateTreeHasNoReparseEntries(string root)
    {
        Queue<DirectoryInfo> pending = new([new DirectoryInfo(root)]);
        while (pending.TryDequeue(out DirectoryInfo? directory))
        {
            ValidateDirectory(directory.FullName);
            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException(
                        "Portable uninstall fixture contains an unsafe link.");
                }

                if (entry is DirectoryInfo child)
                {
                    pending.Enqueue(child);
                }
            }
        }
    }

    private static void ValidateContainedAncestry(string root, string path)
    {
        string expectedRoot = Path.GetFullPath(root);
        for (DirectoryInfo? current = new(Path.GetFullPath(path));
             current is not null;
             current = current.Parent)
        {
            ValidateDirectory(current.FullName);
            if (string.Equals(current.FullName, expectedRoot, PathComparison()))
            {
                return;
            }
        }

        throw new InvalidDataException(
            "Portable extraction ancestry did not reach its fixed root.");
    }

    private static bool IsRegularFile(string path)
    {
        FileInfo info = new(path);
        return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) == 0;
    }

    private static string ValidateRepositoryRoot(string value)
    {
        string root = Path.GetFullPath(value);
        if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "Tooltail.sln")))
        {
            throw new ArgumentException(
                "Portable packaging requires the Tooltail repository root.");
        }

        return root;
    }

    private static string ValidateExactPath(
        string value,
        string expected,
        string error)
    {
        string full = Path.GetFullPath(value);
        if (!string.Equals(full, Path.GetFullPath(expected), PathComparison()))
        {
            throw new ArgumentException(error);
        }

        return full;
    }

    private static async Task WriteResultAsync(
        TextWriter output,
        string reasonCode,
        object result,
        CancellationToken cancellationToken)
    {
        string json = JsonSerializer.Serialize(
            new
            {
                status = "passed",
                reasonCode,
                result,
            },
            WriteOptions);
        await output.WriteLineAsync(json.AsMemory(), cancellationToken)
            .ConfigureAwait(false);
    }

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record PayloadSource(
        string RelativePath,
        string FullPath,
        long Length,
        string Sha256);
}
