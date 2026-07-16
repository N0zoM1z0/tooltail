using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Tooltail.ReleaseAudit;

public static partial class ReleaseAuditApplication
{
    private const int MaximumTrackedFileBytes = 2 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
    private static readonly JsonSerializerOptions StrictReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        if (args.Count != 5 || args[0] != "verify" ||
            args[1] != "--root" || args[3] != "--output")
        {
            await error.WriteLineAsync(
                "Usage: Tooltail.ReleaseAudit verify --root <repository> --output <repository\\artifacts\\release-audit>");
            return 2;
        }

        try
        {
            string root = ValidateRoot(args[2]);
            string artifactRoot = ValidateArtifactRoot(root, args[4]);
            Directory.CreateDirectory(artifactRoot);

            List<FrozenFile> frozen = VerifySchemaFreeze(root);
            VerifyPinnedWorkflowActions(root);
            List<DependencyEvidence> dependencies = VerifyDependencies(root);
            IReadOnlyList<string> trackedFiles = await ReadTrackedFilesAsync(
                root,
                cancellationToken).ConfigureAwait(false);
            VerifyNoSecrets(root, trackedFiles);

            string sbomPath = Path.Combine(artifactRoot, "tooltail.spdx.json");
            string evidencePath = Path.Combine(
                artifactRoot,
                "release-evidence.json");
            await WriteJsonAsync(
                sbomPath,
                BuildSpdx(dependencies, frozen),
                cancellationToken).ConfigureAwait(false);
            await WriteJsonAsync(
                evidencePath,
                BuildEvidence(dependencies, frozen, trackedFiles.Count),
                cancellationToken).ConfigureAwait(false);

            await output.WriteLineAsync(JsonSerializer.Serialize(
                new
                {
                    status = "passed",
                    reasonCode = "release_audit.verified",
                    dependencyCount = dependencies.Count,
                    frozenFileCount = frozen.Count,
                    trackedFileCount = trackedFiles.Count,
                    sbomPath,
                    evidencePath,
                },
                JsonOptions));
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidDataException or IOException or UnauthorizedAccessException or
            JsonException or System.Xml.XmlException)
        {
            await error.WriteLineAsync(JsonSerializer.Serialize(
                new
                {
                    status = "failed",
                    reasonCode = "release_audit.failed",
                    error = exception.Message,
                },
                JsonOptions));
            return 1;
        }
    }

    private static List<FrozenFile> VerifySchemaFreeze(string root)
    {
        string manifestPath = Path.Combine(root, "eng", "schema-freeze-v1.json");
        SchemaFreezeManifest manifest = ReadJson<SchemaFreezeManifest>(manifestPath);
        if (manifest.ContractVersion != "tooltail.schema-freeze/1" ||
            manifest.Files.Count != 10)
        {
            throw new InvalidDataException("Schema freeze manifest shape is invalid.");
        }

        HashSet<string> paths = new(StringComparer.Ordinal);
        List<FrozenFile> result = new(manifest.Files.Count);
        foreach (FrozenFile expected in manifest.Files.OrderBy(static item => item.Path))
        {
            if (!paths.Add(expected.Path) ||
                (!expected.Path.StartsWith("docs/schemas/", StringComparison.Ordinal) &&
                 !expected.Path.StartsWith("docs/examples/", StringComparison.Ordinal)) ||
                !LowerHex64().IsMatch(expected.Sha256))
            {
                throw new InvalidDataException("Schema freeze entry is invalid.");
            }

            string path = Path.Combine(root, expected.Path.Replace('/', Path.DirectorySeparatorChar));
            string actual = HashNormalizedText(path);
            if (!string.Equals(actual, expected.Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Frozen contract changed without a compatibility decision: {expected.Path}.");
            }

            result.Add(expected);
        }

        return result;
    }

    private static void VerifyPinnedWorkflowActions(string root)
    {
        string workflowRoot = Path.Combine(root, ".github", "workflows");
        foreach (string path in Directory.EnumerateFiles(workflowRoot, "*.yml"))
        {
            foreach (string rawLine in File.ReadLines(path))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("uses:", StringComparison.Ordinal))
                {
                    continue;
                }

                string reference = line[5..].Split('#', 2)[0].Trim();
                int marker = reference.LastIndexOf('@');
                if (marker < 1 ||
                    !LowerHex40().IsMatch(reference[(marker + 1)..]))
                {
                    throw new InvalidDataException(
                        $"Workflow action is not pinned to a full commit: {reference}.");
                }
            }
        }
    }

    private static List<DependencyEvidence> VerifyDependencies(string root)
    {
        DependencyReviewManifest review = ReadJson<DependencyReviewManifest>(
            Path.Combine(root, "eng", "dependency-review.json"));
        if (review.ContractVersion != "tooltail.dependency-review/1")
        {
            throw new InvalidDataException("Dependency review version is unsupported.");
        }

        Dictionary<string, string> packages = new(StringComparer.OrdinalIgnoreCase);
        foreach (string lockPath in Directory.EnumerateFiles(
            root,
            "packages.lock.json",
            SearchOption.AllDirectories).Where(static path =>
                !IsBuildDirectory(path)))
        {
            using JsonDocument document = JsonDocument.Parse(
                File.ReadAllBytes(lockPath),
                new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement dependencies = document.RootElement.GetProperty("dependencies");
            foreach (JsonProperty framework in dependencies.EnumerateObject())
            {
                foreach (JsonProperty package in framework.Value.EnumerateObject())
                {
                    string type = package.Value.GetProperty("type").GetString()!;
                    if (type == "Project")
                    {
                        continue;
                    }

                    string version = package.Value.GetProperty("resolved").GetString()!;
                    if (packages.TryGetValue(package.Name, out string? existing) &&
                        !string.Equals(existing, version, StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            $"Package uses multiple resolved versions: {package.Name}.");
                    }

                    packages[package.Name] = version;
                }
            }
        }

        string packageRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ??
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget",
                "packages");
        List<DependencyEvidence> evidence = new(packages.Count);
        foreach ((string id, string version) in packages.OrderBy(static item => item.Key))
        {
            DependencyReviewRule rule = review.Rules.SingleOrDefault(candidate =>
                    RuleMatches(candidate, id, version)) ??
                throw new InvalidDataException(
                    $"Dependency has no exact reviewed rule: {id} {version}.");
            NuspecEvidence nuspec = ReadNuspec(packageRoot, id, version);
            if (!string.Equals(
                    nuspec.ObservedLicense,
                    rule.ObservedLicense,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Dependency license metadata drifted: {id} {version}.");
            }

            evidence.Add(new DependencyEvidence(
                id,
                version,
                nuspec.ObservedLicense,
                rule.ConcludedLicense,
                nuspec.RepositoryUrl,
                rule.Scope,
                nuspec.ExtractedLicenseText));
        }

        return evidence;
    }

    private static async Task<IReadOnlyList<string>> ReadTrackedFilesAsync(
        string root,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo start = new("git")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("ls-files");
        start.ArgumentList.Add("-z");
        using Process process = Process.Start(start) ??
            throw new IOException("Could not start git for the tracked-file boundary.");
        string stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken)
            .ConfigureAwait(false);
        string stderr = await process.StandardError.ReadToEndAsync(cancellationToken)
            .ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0 || stderr.Length != 0)
        {
            throw new IOException("Git tracked-file enumeration failed.");
        }

        return stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
    }

    private static void VerifyNoSecrets(
        string root,
        IReadOnlyList<string> trackedFiles)
    {
        foreach (string relative in trackedFiles)
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            FileInfo info = new(path);
            if (!info.Exists || info.Length > MaximumTrackedFileBytes ||
                IsLikelyBinary(path))
            {
                continue;
            }

            string text;
            try
            {
                text = StrictUtf8.GetString(File.ReadAllBytes(path));
            }
            catch (DecoderFallbackException)
            {
                continue;
            }

            if (PrivateKey().IsMatch(text) || AwsKey().IsMatch(text) ||
                GitHubToken().IsMatch(text) || GoogleApiKey().IsMatch(text) ||
                SlackToken().IsMatch(text))
            {
                throw new InvalidDataException(
                    $"Tracked file matches a prohibited secret pattern: {relative}.");
            }
        }
    }

    private static object BuildSpdx(
        IReadOnlyList<DependencyEvidence> dependencies,
        IReadOnlyList<FrozenFile> frozen)
    {
        string namespaceHash = Convert.ToHexStringLower(SHA256.HashData(
            StrictUtf8.GetBytes(string.Join(
                '\n',
                dependencies.Select(static item => $"{item.Id}@{item.Version}")
                    .Concat(frozen.Select(static item => $"{item.Path}:{item.Sha256}"))))));
        object[] packages = dependencies.Select((item, index) => new
        {
            SPDXID = $"SPDXRef-Package-{index + 1}",
            name = item.Id,
            versionInfo = item.Version,
            downloadLocation = item.RepositoryUrl ?? "NOASSERTION",
            filesAnalyzed = false,
            licenseDeclared = item.ObservedLicense.StartsWith(
                "LicenseRef-",
                StringComparison.Ordinal)
                ? item.ObservedLicense
                : item.ConcludedLicense,
            licenseConcluded = item.ConcludedLicense,
            supplier = "NOASSERTION",
            comment = item.Scope,
        }).Cast<object>().ToArray();
        object[] extracted = dependencies
            .Where(static item => item.ObservedLicense.StartsWith(
                "LicenseRef-",
                StringComparison.Ordinal))
            .GroupBy(static item => item.ObservedLicense, StringComparer.Ordinal)
            .Select(group => new
            {
                licenseId = group.Key,
                extractedText = group.Select(static item => item.ExtractedLicenseText)
                    .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text)) ??
                    "See the reviewed NuGet package metadata and repository license.",
            })
            .Cast<object>()
            .ToArray();
        return new
        {
            spdxVersion = "SPDX-2.3",
            dataLicense = "CC0-1.0",
            SPDXID = "SPDXRef-DOCUMENT",
            name = "Tooltail dependency SBOM",
            documentNamespace = $"https://spdx.tooltail.dev/v0.1/{namespaceHash}",
            creationInfo = new
            {
                created = "2026-07-16T00:00:00Z",
                creators = new[] { "Tool: Tooltail.ReleaseAudit/1" },
            },
            packages,
            relationships = packages.Select(package => new
            {
                spdxElementId = "SPDXRef-DOCUMENT",
                relationshipType = "DESCRIBES",
                relatedSpdxElement = package.GetType().GetProperty("SPDXID")!
                    .GetValue(package),
            }),
            hasExtractedLicensingInfos = extracted,
        };
    }

    private static object BuildEvidence(
        IReadOnlyList<DependencyEvidence> dependencies,
        IReadOnlyList<FrozenFile> frozen,
        int trackedFileCount) => new
        {
            contractVersion = "tooltail.release-evidence/1",
            generatedAt = "2026-07-16T00:00:00Z",
            sourceCommit = Environment.GetEnvironmentVariable("GITHUB_SHA") ??
                "local-working-tree",
            sdkVersion = Environment.Version.ToString(),
            checks = new
            {
                packageLocks = "verified",
                dependencyLicenses = "verified_with_reviewed_exceptions",
                workflowActions = "commit_pinned",
                schemaFreeze = "verified",
                trackedFileSecrets = "no_pattern_match",
                automaticUpload = false,
            },
            dependencyCount = dependencies.Count,
            frozenFiles = frozen,
            trackedFileCount,
            externalBlockers = new[]
            {
                "repository owner license decision",
                "code-signing identity and credentials",
                "attended Windows accessibility and monitor matrix",
                "independent security review",
            },
        };

    private static NuspecEvidence ReadNuspec(
        string packageRoot,
        string id,
        string version)
    {
        string directory = Path.Combine(packageRoot, id.ToLowerInvariant(), version);
        string nuspecPath = Path.Combine(directory, $"{id.ToLowerInvariant()}.nuspec");
        XDocument document = XDocument.Load(nuspecPath, LoadOptions.None);
        XElement metadata = document.Descendants()
            .Single(element => element.Name.LocalName == "metadata");
        XElement? license = metadata.Elements()
            .SingleOrDefault(element => element.Name.LocalName == "license");
        string observed;
        string? extracted = null;
        if (license?.Attribute("type")?.Value == "expression")
        {
            observed = license.Value.Trim();
        }
        else if (license?.Attribute("type")?.Value == "file")
        {
            string file = license.Value.Trim();
            observed = $"LicenseRef-{Path.GetFileNameWithoutExtension(file)}";
            string licensePath = Path.Combine(directory, file);
            extracted = File.ReadAllText(licensePath);
        }
        else
        {
            observed = "LicenseRef-LEGACY-URL";
        }

        string? repository = metadata.Elements()
            .SingleOrDefault(element => element.Name.LocalName == "repository")?
            .Attribute("url")?.Value;
        repository ??= metadata.Elements()
            .SingleOrDefault(element => element.Name.LocalName == "projectUrl")?
            .Value;
        return new NuspecEvidence(observed, repository, extracted);
    }

    private static bool RuleMatches(
        DependencyReviewRule rule,
        string id,
        string version)
    {
        bool idMatches = rule.Id.EndsWith('*')
            ? id.StartsWith(rule.Id[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(rule.Id, id, StringComparison.OrdinalIgnoreCase);
        return idMatches && string.Equals(rule.Version, version, StringComparison.Ordinal);
    }

    private static T ReadJson<T>(string path) where T : class =>
        JsonSerializer.Deserialize<T>(
            File.ReadAllBytes(path),
            StrictReadOptions) ??
        throw new InvalidDataException($"JSON document is empty: {path}.");

    private static async Task WriteJsonAsync(
        string path,
        object value,
        CancellationToken cancellationToken)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        await using FileStream stream = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string ValidateRoot(string value)
    {
        string root = Path.GetFullPath(value);
        if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "Tooltail.sln")))
        {
            throw new ArgumentException("Release audit requires the Tooltail repository root.");
        }

        return root;
    }

    private static string ValidateArtifactRoot(string root, string value)
    {
        string full = Path.GetFullPath(value);
        string expected = Path.Combine(root, "artifacts", "release-audit");
        if (!string.Equals(full, expected, PathComparison()))
        {
            throw new ArgumentException(
                "Release audit output is fixed to artifacts/release-audit.");
        }

        return full;
    }

    private static string HashNormalizedText(string path)
    {
        string text = StrictUtf8.GetString(File.ReadAllBytes(path))
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        return Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(text)));
    }

    private static bool IsBuildDirectory(string path) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(static segment => segment is "bin" or "obj" or "artifacts");

    private static bool IsLikelyBinary(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".ico" or ".dll" or
            ".exe" or ".pdb" or ".zip" or ".nupkg" or ".snupkg" or ".db";

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex64();

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex40();

    [GeneratedRegex("-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----", RegexOptions.CultureInvariant)]
    private static partial Regex PrivateKey();

    [GeneratedRegex("AKIA[0-9A-Z]{16}", RegexOptions.CultureInvariant)]
    private static partial Regex AwsKey();

    [GeneratedRegex("(?:gh[pousr]_[A-Za-z0-9_]{30,}|github_pat_[A-Za-z0-9_]{40,})", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubToken();

    [GeneratedRegex("AIza[0-9A-Za-z_-]{35}", RegexOptions.CultureInvariant)]
    private static partial Regex GoogleApiKey();

    [GeneratedRegex("xox[baprs]-[A-Za-z0-9-]{10,}", RegexOptions.CultureInvariant)]
    private static partial Regex SlackToken();

    private sealed record SchemaFreezeManifest(
        string ContractVersion,
        IReadOnlyList<FrozenFile> Files);

    public sealed record FrozenFile(string Path, string Sha256);

    private sealed record DependencyReviewManifest(
        string ContractVersion,
        IReadOnlyList<DependencyReviewRule> Rules);

    private sealed record DependencyReviewRule(
        string Id,
        string Version,
        string ObservedLicense,
        string ConcludedLicense,
        string Scope);

    private sealed record NuspecEvidence(
        string ObservedLicense,
        string? RepositoryUrl,
        string? ExtractedLicenseText);

    private sealed record DependencyEvidence(
        string Id,
        string Version,
        string ObservedLicense,
        string ConcludedLicense,
        string? RepositoryUrl,
        string Scope,
        string? ExtractedLicenseText);
}
