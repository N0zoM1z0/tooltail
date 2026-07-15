using Tooltail.Contracts.Json;
using Tooltail.Contracts.Skills;
using Tooltail.Features.FileSkills.Paths;
using Tooltail.Features.FileSkills.Reconciliation;
using Tooltail.Features.FileSkills.Skills;

namespace Tooltail.Features.FileSkills.Compilation;

public static class DeterministicSkillCompiler
{
    private const string OriginQuestionCode = "match.origin_scope";
    private const string FilenameQuestionCode = "match.filename_scope";
    private const string TransformQuestionCode = "transform.filename";

    public static SkillCompilationResult Compile(SkillCompilationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<CompilerRejectedCause> rejected = [];
        if (request.Examples.Count < 2)
        {
            return Result(
                SkillCompilationStatus.NeedsMoreExamples,
                "compiler.needs_two_examples",
                rejectedCauses: rejected);
        }

        if (request.Examples.Count > 5 ||
            request.Examples.Select(static example => example.Id).Distinct().Count() !=
            request.Examples.Count ||
            request.Examples.Distinct(ExampleEffectComparer.Instance).Count() !=
            request.Examples.Count ||
            !IsValidDisplayText(request.Name, 80) ||
            !IsValidDisplayText(request.Description, 400))
        {
            return Result(
                SkillCompilationStatus.InvalidRequest,
                "compiler.examples_invalid",
                rejectedCauses: rejected);
        }

        if (request.Examples.Any(example => example.GrantId != request.RootGrantId) ||
            request.Examples.Select(static example => example.RootIdentity).Distinct().Count() != 1 ||
            !HasOneProvenVolume(request.Examples))
        {
            return Result(
                SkillCompilationStatus.InvalidRequest,
                "compiler.evidence_scope_mismatch",
                rejectedCauses: rejected);
        }

        if (!TryAnswers(request.UserAnswers, out Dictionary<string, string>? answers))
        {
            return Result(
                SkillCompilationStatus.InvalidRequest,
                "compiler.answers_invalid",
                rejectedCauses: rejected);
        }

        ReconciledEffectKind action = request.Examples[0].Effect.Kind;
        if (request.Examples.Any(example => example.Effect.Kind != action) ||
            !ExamplesMatchActionShape(request.Examples, action))
        {
            return Result(
                SkillCompilationStatus.UnsupportedEvidence,
                "compiler.mixed_or_invalid_effects",
                rejectedCauses: rejected);
        }

        string? destinationDirectory = action == ReconciledEffectKind.Renamed
            ? null
            : ConstantDestinationDirectory(request.Examples);
        if (action != ReconciledEffectKind.Renamed && destinationDirectory is null)
        {
            return Result(
                SkillCompilationStatus.NoCandidate,
                "compiler.destination_directory_not_constant",
                rejectedCauses: rejected);
        }

        FilenameTransformation[] transformations = InferFilenameTransformations(request.Examples);
        if (transformations.Length == 0)
        {
            return Result(
                SkillCompilationStatus.NoCandidate,
                "compiler.filename_transform_not_supported",
                rejectedCauses: rejected);
        }

        string? commonOrigin = ConstantOriginDirectory(request.Examples);
        string? commonToken = CommonFilenameToken(request.Examples);
        string[]? extensions = Extensions(request.Examples);
        List<GeneratedCandidate> generated = [];
        foreach (bool includeOrigin in commonOrigin is null ? [false] : new[] { false, true })
        {
            foreach (bool includeToken in commonToken is null ? [false] : new[] { false, true })
            {
                foreach (FilenameTransformation transformation in transformations)
                {
                    string key = CandidateKey(includeOrigin, includeToken, transformation.Key);
                    SkillSpecContract specification = BuildSpecification(
                        request,
                        action,
                        destinationDirectory,
                        includeOrigin ? commonOrigin : null,
                        includeToken ? commonToken : null,
                        extensions,
                        transformation);
                    SkillValidationResult validation = SkillSpecValidator.Validate(specification);
                    if (!validation.IsValid)
                    {
                        rejected.Add(new CompilerRejectedCause(key, "compiler.generated_spec_invalid"));
                        continue;
                    }

                    if (!CandidateExactlyCovers(specification, request.Examples))
                    {
                        rejected.Add(new CompilerRejectedCause(key, "compiler.positive_not_covered"));
                        continue;
                    }

                    if (request.Exclusions.Any(exclusion =>
                            SkillMatcher.Matches(specification.Applicability.Match, exclusion)))
                    {
                        rejected.Add(new CompilerRejectedCause(key, "compiler.matches_exclusion"));
                        continue;
                    }

                    SkillCandidateScore score = new(
                        request.Examples.Count,
                        (includeOrigin ? 1 : 0) +
                        (includeToken ? 1 : 0) +
                        transformation.AssumptionCost,
                        transformation.Template.Length +
                        (includeOrigin ? commonOrigin!.Length : 0) +
                        (includeToken ? commonToken!.Length : 0),
                        transformation.StableSemanticRank,
                        2 - (includeOrigin ? 1 : 0) - (includeToken ? 1 : 0));
                    SkillCandidate candidate = new(
                        key,
                        specification,
                        score,
                        CandidateSummary(action, includeOrigin, includeToken, transformation));
                    generated.Add(
                        new GeneratedCandidate(
                            candidate,
                            includeOrigin,
                            includeToken,
                            transformation.Key));
                }
            }
        }

        if (generated.Count == 0)
        {
            return Result(
                SkillCompilationStatus.NoCandidate,
                "compiler.no_safe_candidate",
                rejectedCauses: rejected);
        }

        List<GeneratedCandidate> filtered = ApplyAnswers(generated, answers!);
        if (filtered.Count == 0)
        {
            return Result(
                SkillCompilationStatus.InvalidRequest,
                "compiler.answer_selects_no_candidate",
                generated.Select(static candidate => candidate.Candidate),
                rejectedCauses: rejected);
        }

        filtered.Sort(GeneratedCandidateComparer.Instance);
        CompilerQuestion[] questions = Questions(filtered, commonOrigin, commonToken, answers!);
        int unresolvedDimensions = UnresolvedDimensionCount(filtered, answers!);
        if (unresolvedDimensions > 2)
        {
            return Result(
                SkillCompilationStatus.NeedsMoreExamples,
                "compiler.material_ambiguity_remains",
                filtered.Select(static candidate => candidate.Candidate),
                rejectedCauses: rejected);
        }

        if (questions.Length > 0)
        {
            return Result(
                SkillCompilationStatus.NeedsClarification,
                "compiler.clarification_required",
                filtered.Select(static candidate => candidate.Candidate),
                questions,
                rejected);
        }

        if (filtered.Count != 1)
        {
            return Result(
                SkillCompilationStatus.NeedsMoreExamples,
                "compiler.candidates_indistinguishable",
                filtered.Select(static candidate => candidate.Candidate),
                rejectedCauses: rejected);
        }

        return Result(
            SkillCompilationStatus.Ready,
            "compiler.ready",
            [filtered[0].Candidate],
            rejectedCauses: rejected);
    }

    private static SkillSpecContract BuildSpecification(
        SkillCompilationRequest request,
        ReconciledEffectKind action,
        string? destinationDirectory,
        string? origin,
        string? filenameToken,
        string[]? extensions,
        FilenameTransformation transformation)
    {
        SkillVariableContract[] variables =
        [
            new SkillVariableContract
            {
                Name = "originalStem",
                Source = SkillVariableSource.OriginalStem,
                Transforms = transformation.StemTransforms,
            },
            new SkillVariableContract
            {
                Name = "originalExtension",
                Source = SkillVariableSource.OriginalExtension,
                Transforms = transformation.ExtensionTransforms,
            },
        ];
        List<SkillStepContract> steps = [];
        if (action is ReconciledEffectKind.Moved or ReconciledEffectKind.Copied)
        {
            steps.Add(
                new EnsureDirectoryStepContract
                {
                    StepId = "ensure_destination",
                    DirectoryTemplate = ToContractPath(destinationDirectory!),
                });
        }

        steps.Add(
            action switch
            {
                ReconciledEffectKind.Renamed => new RenameFileStepContract
                {
                    StepId = "rename_file",
                    DestinationFileNameTemplate = transformation.Template,
                },
                ReconciledEffectKind.Moved => new MoveFileStepContract
                {
                    StepId = "move_file",
                    DestinationDirectoryTemplate = ToContractPath(destinationDirectory!),
                    DestinationFileNameTemplate = transformation.Template,
                },
                ReconciledEffectKind.Copied => new CopyFileStepContract
                {
                    StepId = "copy_file",
                    DestinationDirectoryTemplate = ToContractPath(destinationDirectory!),
                    DestinationFileNameTemplate = transformation.Template,
                },
                _ => throw new ArgumentOutOfRangeException(nameof(action)),
            });

        return new SkillSpecContract
        {
            SchemaVersion = ContractVersions.V1,
            SkillId = request.SkillId.Value,
            Version = request.Version,
            Name = request.Name,
            Description = request.Description,
            CreatedAt = request.CreatedUtc,
            Compiler = new SkillCompilerContract
            {
                Kind = SkillCompilerKind.DeterministicTemplate,
                Version = SkillSpecValidator.SupportedCompilerVersion,
            },
            Applicability = new SkillApplicabilityContract
            {
                RootGrantId = request.RootGrantId.Value,
                Invocation = SkillInvocation.Manual,
                Match = new SkillMatchContract
                {
                    RegularFilesOnly = true,
                    OriginRelativeDirectory = origin is null ? null : ToContractPath(origin),
                    Extensions = extensions,
                    Filename = filenameToken is null
                        ? null
                        : new SkillFilenameMatchContract
                        {
                            Contains = filenameToken,
                            CaseSensitive = false,
                        },
                },
            },
            Variables = variables,
            Steps = steps,
            Policy = new SkillPolicyContract
            {
                Collision = CollisionPolicy.Reject,
                RequireExactPlanApproval = true,
                AllowNetworkPaths = false,
                AllowReparsePoints = false,
                AllowOverwrite = false,
                SameVolumeOnly = true,
            },
            Verification = new SkillVerificationContract
            {
                AllPlannedStepsVerified = true,
                FailOnUnexpectedChange = true,
            },
            Provenance = new SkillProvenanceContract
            {
                TeachingEpisodeIds = request.Examples
                    .Select(static example => example.EpisodeId.Value)
                    .Distinct()
                    .Order()
                    .ToArray(),
                ExampleIds = request.Examples
                    .Select(static example => example.Id.Value)
                    .Order()
                    .ToArray(),
                UserAnswers = request.UserAnswers
                    .OrderBy(static answer => answer.QuestionCode, StringComparer.Ordinal)
                    .ToArray(),
                ParentVersion = request.ParentVersion,
            },
            Compatibility = new SkillCompatibilityContract
            {
                ContractVersion = ContractVersions.V1,
                MinimumExecutorVersion = SkillSpecValidator.SupportedExecutorVersion,
            },
        };
    }

    private static FilenameTransformation[] InferFilenameTransformations(
        IReadOnlyList<TeachingFileExample> examples)
    {
        List<FilenameTransformation> transformations = [];
        string[] sources = examples
            .Select(static example => SkillTemplateEngine.FileName(example.Source.RelativePath))
            .ToArray();
        string[] destinations = examples
            .Select(static example => SkillTemplateEngine.FileName(example.Destination.RelativePath))
            .ToArray();
        if (sources.SequenceEqual(destinations, StringComparer.Ordinal))
        {
            transformations.Add(FilenameTransformation.Preserve);
        }

        if (sources.Select(static value => value.ToLowerInvariant())
            .SequenceEqual(destinations, StringComparer.Ordinal) &&
            sources.Where((source, index) => source != destinations[index]).Any())
        {
            transformations.Add(FilenameTransformation.Lowercase);
        }

        if (sources.Select(static value => value.ToUpperInvariant())
            .SequenceEqual(destinations, StringComparer.Ordinal) &&
            sources.Where((source, index) => source != destinations[index]).Any())
        {
            transformations.Add(FilenameTransformation.Uppercase);
        }

        FilenameTransformation? affix = InferAffix(examples);
        if (affix is not null)
        {
            transformations.Add(affix);
        }

        return transformations
            .DistinctBy(static transformation => transformation.Key, StringComparer.Ordinal)
            .OrderBy(static transformation => transformation.Key, StringComparer.Ordinal)
            .ToArray();
    }

    private static FilenameTransformation? InferAffix(
        IReadOnlyList<TeachingFileExample> examples)
    {
        string? commonPrefix = null;
        string? commonSuffix = null;
        foreach (TeachingFileExample example in examples)
        {
            string sourceName = SkillTemplateEngine.FileName(example.Source.RelativePath);
            string destinationName = SkillTemplateEngine.FileName(example.Destination.RelativePath);
            string sourceExtension = SkillTemplateEngine.Extension(sourceName);
            string destinationExtension = SkillTemplateEngine.Extension(destinationName);
            if (!string.Equals(sourceExtension, destinationExtension, StringComparison.Ordinal))
            {
                return null;
            }

            string sourceStem = SkillTemplateEngine.Stem(sourceName);
            string destinationStem = SkillTemplateEngine.Stem(destinationName);
            int position = destinationStem.IndexOf(sourceStem, StringComparison.Ordinal);
            if (position < 0)
            {
                return null;
            }

            string prefix = destinationStem[..position];
            string suffix = destinationStem[(position + sourceStem.Length)..];
            if (prefix.Length == 0 && suffix.Length == 0)
            {
                return null;
            }

            commonPrefix ??= prefix;
            commonSuffix ??= suffix;
            if (!string.Equals(commonPrefix, prefix, StringComparison.Ordinal) ||
                !string.Equals(commonSuffix, suffix, StringComparison.Ordinal))
            {
                return null;
            }
        }

        return FilenameTransformation.Affix(commonPrefix!, commonSuffix!);
    }

    private static string? ConstantOriginDirectory(IReadOnlyList<TeachingFileExample> examples)
    {
        return ConstantWindowsPath(examples.Select(static example =>
            SkillTemplateEngine.Parent(example.Source.RelativePath)));
    }

    private static string? ConstantDestinationDirectory(
        IReadOnlyList<TeachingFileExample> examples)
    {
        return ConstantWindowsPath(examples.Select(static example =>
            SkillTemplateEngine.Parent(example.Destination.RelativePath)));
    }

    private static string[]? Extensions(IReadOnlyList<TeachingFileExample> examples)
    {
        string[] values = examples
            .Select(example => SkillTemplateEngine.Extension(
                SkillTemplateEngine.FileName(example.Source.RelativePath)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        return values.Length > 0 && values.All(static value =>
                value.Length is >= 2 and <= 17 &&
                value[0] == '.' &&
                value.Skip(1).All(char.IsAsciiLetterOrDigit))
            ? values
            : null;
    }

    private static string? CommonFilenameToken(IReadOnlyList<TeachingFileExample> examples)
    {
        HashSet<string>? common = null;
        foreach (TeachingFileExample example in examples)
        {
            string stem = SkillTemplateEngine.Stem(
                SkillTemplateEngine.FileName(example.Source.RelativePath));
            HashSet<string> tokens = stem
                .Split(
                    stem.Where(static character => !char.IsAsciiLetterOrDigit(character))
                        .Distinct()
                        .ToArray(),
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(static token =>
                    token.Length >= 3 && token.Any(char.IsAsciiLetter))
                .Select(static token => token.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            common = common is null
                ? tokens
                : common.Intersect(tokens, StringComparer.OrdinalIgnoreCase).ToHashSet(
                    StringComparer.OrdinalIgnoreCase);
        }

        return common?
            .OrderByDescending(static token => token.Length)
            .ThenBy(static token => token, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    private static bool ExamplesMatchActionShape(
        IEnumerable<TeachingFileExample> examples,
        ReconciledEffectKind action) =>
        examples.All(example =>
        {
            string sourceParent = SkillTemplateEngine.Parent(example.Source.RelativePath);
            string destinationParent = SkillTemplateEngine.Parent(example.Destination.RelativePath);
            return action switch
            {
                ReconciledEffectKind.Renamed => string.Equals(
                    sourceParent,
                    destinationParent,
                    StringComparison.OrdinalIgnoreCase),
                ReconciledEffectKind.Moved => !string.Equals(
                    sourceParent,
                    destinationParent,
                    StringComparison.OrdinalIgnoreCase),
                ReconciledEffectKind.Copied => true,
                _ => false,
            };
        });

    private static List<GeneratedCandidate> ApplyAnswers(
        IEnumerable<GeneratedCandidate> candidates,
        Dictionary<string, string> answers)
    {
        IEnumerable<GeneratedCandidate> filtered = candidates;
        if (answers.TryGetValue(OriginQuestionCode, out string? origin))
        {
            bool include = origin == "same_directory";
            filtered = filtered.Where(candidate => candidate.IncludesOrigin == include);
        }

        if (answers.TryGetValue(FilenameQuestionCode, out string? filename))
        {
            bool include = filename == "contains_token";
            filtered = filtered.Where(candidate => candidate.IncludesFilenameToken == include);
        }

        if (answers.TryGetValue(TransformQuestionCode, out string? transform))
        {
            filtered = filtered.Where(candidate => candidate.TransformationKey == transform);
        }

        return filtered.ToList();
    }

    private static CompilerQuestion[] Questions(
        IReadOnlyCollection<GeneratedCandidate> candidates,
        string? commonOrigin,
        string? commonToken,
        Dictionary<string, string> answers)
    {
        List<CompilerQuestion> questions = [];
        if (!answers.ContainsKey(OriginQuestionCode) &&
            candidates.Select(static candidate => candidate.IncludesOrigin).Distinct().Count() > 1)
        {
            questions.Add(
                new CompilerQuestion(
                    OriginQuestionCode,
                    "applicability.match.originRelativeDirectory",
                    "Should the skill match only files from the demonstrated source directory?",
                    [
                        new CompilerQuestionOption(
                            "same_directory",
                            commonOrigin!.Length == 0
                                ? "Only the granted root directory"
                                : $"Only {ToContractPath(commonOrigin)}"),
                        new CompilerQuestionOption("any_directory", "Any directory in the granted root"),
                    ]));
        }

        if (!answers.ContainsKey(FilenameQuestionCode) &&
            candidates.Select(static candidate => candidate.IncludesFilenameToken).Distinct().Count() > 1)
        {
            questions.Add(
                new CompilerQuestion(
                    FilenameQuestionCode,
                    "applicability.match.filename.contains",
                    "Should matching filenames contain the demonstrated common token?",
                    [
                        new CompilerQuestionOption("contains_token", $"Contains {commonToken}"),
                        new CompilerQuestionOption("any_filename", "Any filename with the selected extension"),
                    ]));
        }

        string[] transforms = candidates
            .Select(static candidate => candidate.TransformationKey)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!answers.ContainsKey(TransformQuestionCode) && transforms.Length > 1)
        {
            questions.Add(
                new CompilerQuestion(
                    TransformQuestionCode,
                    "steps.destinationFileNameTemplate",
                    "Which observed filename transformation should remain part of the skill?",
                    transforms.Select(value => new CompilerQuestionOption(value, value))));
        }

        return questions.Take(2).ToArray();
    }

    private static int UnresolvedDimensionCount(
        IReadOnlyCollection<GeneratedCandidate> candidates,
        Dictionary<string, string> answers)
    {
        int count = 0;
        if (!answers.ContainsKey(OriginQuestionCode) &&
            candidates.Select(static candidate => candidate.IncludesOrigin).Distinct().Count() > 1)
        {
            count++;
        }

        if (!answers.ContainsKey(FilenameQuestionCode) &&
            candidates.Select(static candidate => candidate.IncludesFilenameToken).Distinct().Count() > 1)
        {
            count++;
        }

        if (!answers.ContainsKey(TransformQuestionCode) &&
            candidates.Select(static candidate => candidate.TransformationKey).Distinct().Count() > 1)
        {
            count++;
        }

        return count;
    }

    private static bool TryAnswers(
        IReadOnlyList<SkillUserAnswerContract> userAnswers,
        out Dictionary<string, string>? answers)
    {
        answers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (userAnswers.Count > 16)
        {
            return false;
        }

        foreach (SkillUserAnswerContract answer in userAnswers)
        {
            if (string.IsNullOrWhiteSpace(answer.QuestionCode) ||
                answer.SelectedValue is null ||
                !answers.TryAdd(answer.QuestionCode, answer.SelectedValue))
            {
                return false;
            }

            bool recognizedValue = answer.QuestionCode switch
            {
                OriginQuestionCode => answer.SelectedValue is "same_directory" or "any_directory",
                FilenameQuestionCode => answer.SelectedValue is "contains_token" or "any_filename",
                TransformQuestionCode => answer.SelectedValue.Length is > 0 and <= 80,
                _ => false,
            };
            if (!recognizedValue)
            {
                return false;
            }
        }

        return true;
    }

    private static string CandidateKey(bool origin, bool token, string transformation) =>
        $"origin={(origin ? 1 : 0)};token={(token ? 1 : 0)};transform={transformation}";

    private static string CandidateSummary(
        ReconciledEffectKind action,
        bool origin,
        bool token,
        FilenameTransformation transformation) =>
        $"{action}; origin={(origin ? "fixed" : "any")}; filename={(token ? "token" : "extension")}; transform={transformation.Key}";

    private static string ToContractPath(string windowsPath) => windowsPath.Replace('\\', '/');

    private static string? ConstantWindowsPath(IEnumerable<string> paths)
    {
        string[] distinct = paths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        return distinct.Length == 1 ? distinct[0] : null;
    }

    private static bool HasOneProvenVolume(IEnumerable<TeachingFileExample> examples)
    {
        string? commonVolume = null;
        foreach (TeachingFileExample example in examples)
        {
            string? sourceVolume = example.Source.VolumeIdentity;
            string? destinationVolume = example.Destination.VolumeIdentity;
            if (sourceVolume is null ||
                destinationVolume is null ||
                !string.Equals(sourceVolume, destinationVolume, StringComparison.Ordinal))
            {
                return false;
            }

            commonVolume ??= sourceVolume;
            if (!string.Equals(commonVolume, sourceVolume, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return commonVolume is not null;
    }

    private static bool CandidateExactlyCovers(
        SkillSpecContract specification,
        IReadOnlyList<TeachingFileExample> examples)
    {
        HashSet<string> destinations = new(StringComparer.OrdinalIgnoreCase);
        SkillStepContract action = specification.Steps
            .Single(static step => step is not EnsureDirectoryStepContract);
        foreach (TeachingFileExample example in examples)
        {
            if (!SkillMatcher.Matches(specification.Applicability.Match, example.Source) ||
                !ActionMatchesEffect(action, example.Effect.Kind))
            {
                return false;
            }

            SkillTemplateResult binding = SkillTemplateEngine.BindVariables(
                specification.Variables,
                example.Source,
                specification.Applicability.Match.Filename,
                userParameters: null,
                out IReadOnlyDictionary<string, string>? variables);
            if (!binding.IsSuccess ||
                !TryRenderCandidateDestination(
                    action,
                    example.Source.RelativePath,
                    variables!,
                    out string? destination) ||
                destination is null ||
                !string.Equals(
                    destination,
                    example.Destination.RelativePath,
                    StringComparison.Ordinal) ||
                !destinations.Add(destination))
            {
                return false;
            }

            PathSafetyResult<WindowsRelativePath> sourcePath =
                WindowsPathPolicy.ParseRelative(example.Source.RelativePath);
            PathSafetyResult<WindowsRelativePath> destinationPath =
                WindowsPathPolicy.ParseRelative(destination);
            if (!sourcePath.IsSuccess ||
                !destinationPath.IsSuccess ||
                !WindowsPathPolicy.ValidateDistinctPair(
                    sourcePath.Value!,
                    destinationPath.Value!).IsSuccess)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryRenderCandidateDestination(
        SkillStepContract action,
        string source,
        IReadOnlyDictionary<string, string> variables,
        out string? destination)
    {
        destination = null;
        string directory;
        string? directoryTemplate = action switch
        {
            MoveFileStepContract move => move.DestinationDirectoryTemplate,
            CopyFileStepContract copy => copy.DestinationDirectoryTemplate,
            _ => null,
        };
        if (directoryTemplate is null)
        {
            directory = SkillTemplateEngine.Parent(source);
        }
        else
        {
            SkillTemplateResult renderedDirectory = SkillTemplateEngine.Render(
                directoryTemplate,
                SkillTemplateKind.RelativePath,
                variables,
                "steps.destinationDirectoryTemplate");
            if (!renderedDirectory.IsSuccess)
            {
                return false;
            }

            directory = renderedDirectory.Value!;
        }

        string? filenameTemplate = action switch
        {
            RenameFileStepContract rename => rename.DestinationFileNameTemplate,
            MoveFileStepContract move => move.DestinationFileNameTemplate,
            CopyFileStepContract copy => copy.DestinationFileNameTemplate,
            _ => null,
        };
        string filename;
        if (filenameTemplate is null)
        {
            filename = SkillTemplateEngine.FileName(source);
        }
        else
        {
            SkillTemplateResult renderedFilename = SkillTemplateEngine.Render(
                filenameTemplate,
                SkillTemplateKind.FileName,
                variables,
                "steps.destinationFileNameTemplate");
            if (!renderedFilename.IsSuccess)
            {
                return false;
            }

            filename = renderedFilename.Value!;
        }

        destination = directory.Length == 0 ? filename : $"{directory}\\{filename}";
        return true;
    }

    private static bool ActionMatchesEffect(
        SkillStepContract action,
        ReconciledEffectKind effect) =>
        (action, effect) switch
        {
            (RenameFileStepContract, ReconciledEffectKind.Renamed) => true,
            (MoveFileStepContract, ReconciledEffectKind.Moved) => true,
            (CopyFileStepContract, ReconciledEffectKind.Copied) => true,
            _ => false,
        };

    private static bool IsValidDisplayText(string value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        !value.Any(char.IsControl);

    private static SkillCompilationResult Result(
        SkillCompilationStatus status,
        string reasonCode,
        IEnumerable<SkillCandidate>? candidates = null,
        IEnumerable<CompilerQuestion>? questions = null,
        IEnumerable<CompilerRejectedCause>? rejectedCauses = null) =>
        new(status, reasonCode, candidates ?? [], questions ?? [], rejectedCauses ?? []);

    private sealed record GeneratedCandidate(
        SkillCandidate Candidate,
        bool IncludesOrigin,
        bool IncludesFilenameToken,
        string TransformationKey);

    private sealed class GeneratedCandidateComparer : IComparer<GeneratedCandidate>
    {
        public static GeneratedCandidateComparer Instance { get; } = new();

        public int Compare(GeneratedCandidate? left, GeneratedCandidate? right)
        {
            int coverage = Nullable.Compare(
                right?.Candidate.Score.ExactCoverage,
                left?.Candidate.Score.ExactCoverage);
            if (coverage != 0)
            {
                return coverage;
            }

            int assumptions = Nullable.Compare(
                left?.Candidate.Score.AssumptionCount,
                right?.Candidate.Score.AssumptionCount);
            if (assumptions != 0)
            {
                return assumptions;
            }

            int explanation = Nullable.Compare(
                left?.Candidate.Score.ExplanationLength,
                right?.Candidate.Score.ExplanationLength);
            if (explanation != 0)
            {
                return explanation;
            }

            int stable = Nullable.Compare(
                left?.Candidate.Score.StableSemanticRank,
                right?.Candidate.Score.StableSemanticRank);
            if (stable != 0)
            {
                return stable;
            }

            int collision = Nullable.Compare(
                left?.Candidate.Score.CollisionRiskRank,
                right?.Candidate.Score.CollisionRiskRank);
            return collision != 0
                ? collision
                : StringComparer.Ordinal.Compare(left?.Candidate.Key, right?.Candidate.Key);
        }
    }

    private sealed record FilenameTransformation(
        string Key,
        string Template,
        SkillVariableTransform[] StemTransforms,
        SkillVariableTransform[] ExtensionTransforms,
        int AssumptionCost,
        int StableSemanticRank)
    {
        public static FilenameTransformation Preserve { get; } = new(
            "preserve",
            "{{originalStem}}{{originalExtension}}",
            [],
            [],
            0,
            0);

        public static FilenameTransformation Lowercase { get; } = new(
            "lowercase",
            "{{originalStem}}{{originalExtension}}",
            [SkillVariableTransform.Lowercase],
            [SkillVariableTransform.Lowercase],
            1,
            2);

        public static FilenameTransformation Uppercase { get; } = new(
            "uppercase",
            "{{originalStem}}{{originalExtension}}",
            [SkillVariableTransform.Uppercase],
            [SkillVariableTransform.Uppercase],
            1,
            2);

        public static FilenameTransformation Affix(string prefix, string suffix) =>
            new(
                "affix",
                $"{prefix}{{{{originalStem}}}}{suffix}{{{{originalExtension}}}}",
                [],
                [],
                1,
                1);
    }

    private sealed class ExampleEffectComparer : IEqualityComparer<TeachingFileExample>
    {
        public static ExampleEffectComparer Instance { get; } = new();

        public bool Equals(TeachingFileExample? left, TeachingFileExample? right) =>
            ReferenceEquals(left, right) ||
            (left is not null &&
             right is not null &&
             left.Effect.Kind == right.Effect.Kind &&
             string.Equals(
                 left.Source.RelativePath,
                 right.Source.RelativePath,
                 StringComparison.OrdinalIgnoreCase) &&
             string.Equals(
                 left.Destination.RelativePath,
                 right.Destination.RelativePath,
                 StringComparison.OrdinalIgnoreCase));

        public int GetHashCode(TeachingFileExample value)
        {
            HashCode hash = new();
            hash.Add(value.Effect.Kind);
            hash.Add(value.Source.RelativePath, StringComparer.OrdinalIgnoreCase);
            hash.Add(value.Destination.RelativePath, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }
}
