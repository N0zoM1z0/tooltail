using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Tooltail.Contracts.Json;
using Tooltail.Contracts.Skills;
using Tooltail.Features.FileSkills.Paths;

namespace Tooltail.Features.FileSkills.Skills;

public sealed record SkillValidationError(string Field, string Code, string Message);

public sealed record SkillValidationResult
{
    internal SkillValidationResult(IEnumerable<SkillValidationError> errors)
    {
        SkillValidationError[] materialized = errors.ToArray();
        Errors = new ReadOnlyCollection<SkillValidationError>(materialized);
    }

    public IReadOnlyList<SkillValidationError> Errors { get; }

    public bool IsValid => Errors.Count == 0;
}

public static class SkillSpecValidator
{
    public const string SupportedCompilerVersion = "0.1.0";
    public const string SupportedExecutorVersion = "0.1.0";

    public static SkillValidationResult Validate(SkillSpecContract? specification)
    {
        List<SkillValidationError> errors = [];
        if (specification is null)
        {
            Add(errors, "$", "skill.null", "A SkillSpec is required.");
            return new SkillValidationResult(errors);
        }

        if (!string.Equals(specification.SchemaVersion, ContractVersions.V1, StringComparison.Ordinal))
        {
            Add(errors, "schemaVersion", "skill.schema_unsupported", "The SkillSpec schema is unsupported.");
        }

        if (specification.SkillId == Guid.Empty)
        {
            Add(errors, "skillId", "skill.id_empty", "The skill identifier cannot be empty.");
        }

        if (specification.Version < 1)
        {
            Add(errors, "version", "skill.version_invalid", "The skill version must be positive.");
        }

        ValidateText(specification.Name, 80, "name", errors);
        ValidateText(specification.Description, 400, "description", errors);
        if (specification.CreatedAt.Offset != TimeSpan.Zero)
        {
            Add(errors, "createdAt", "skill.time_not_utc", "Skill creation time must use UTC.");
        }

        ValidateCompiler(specification.Compiler, errors);
        ValidateApplicability(specification.Applicability, errors);
        ValidateVariables(
            specification.Variables,
            specification.Applicability?.Match?.Filename,
            errors,
            out HashSet<string> variableNames);
        ValidateSteps(specification.Steps, variableNames, errors);
        ValidatePolicy(specification.Policy, errors);
        ValidateVerification(specification.Verification, errors);
        ValidateProvenance(specification.Provenance, specification.Version, errors);
        ValidateCompatibility(specification.Compatibility, errors);
        return new SkillValidationResult(errors);
    }

    private static void ValidateCompiler(
        SkillCompilerContract? compiler,
        ICollection<SkillValidationError> errors)
    {
        if (compiler is null)
        {
            Add(errors, "compiler", "skill.compiler_missing", "Compiler metadata is required.");
            return;
        }

        if (compiler.Kind != SkillCompilerKind.DeterministicTemplate)
        {
            Add(errors, "compiler.kind", "skill.compiler_kind_unsupported", "The compiler kind is unsupported.");
        }

        if (!IsSemanticVersion(compiler.Version) ||
            !string.Equals(compiler.Version, SupportedCompilerVersion, StringComparison.Ordinal))
        {
            Add(errors, "compiler.version", "skill.compiler_version_unsupported", "The compiler version is unsupported.");
        }
    }

    private static void ValidateApplicability(
        SkillApplicabilityContract? applicability,
        ICollection<SkillValidationError> errors)
    {
        if (applicability is null)
        {
            Add(errors, "applicability", "skill.applicability_missing", "Applicability is required.");
            return;
        }

        if (applicability.RootGrantId == Guid.Empty)
        {
            Add(errors, "applicability.rootGrantId", "skill.grant_id_empty", "A root grant binding is required.");
        }

        if (applicability.Invocation != SkillInvocation.Manual)
        {
            Add(errors, "applicability.invocation", "skill.invocation_unsupported", "Only manual invocation is supported.");
        }

        SkillMatchContract? match = applicability.Match;
        if (match is null)
        {
            Add(errors, "applicability.match", "skill.match_missing", "A closed match policy is required.");
            return;
        }

        if (!match.RegularFilesOnly)
        {
            Add(errors, "applicability.match.regularFilesOnly", "skill.non_regular_match", "Only regular files may match.");
        }

        if (match.OriginRelativeDirectory is not null &&
            !IsRelativeDirectory(match.OriginRelativeDirectory))
        {
            Add(errors, "applicability.match.originRelativeDirectory", "skill.origin_invalid", "The origin directory is unsafe.");
        }

        if (match.Extensions is not null)
        {
            if (match.Extensions.Count is < 1 or > 32 ||
                match.Extensions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != match.Extensions.Count)
            {
                Add(errors, "applicability.match.extensions", "skill.extensions_invalid", "Extensions must be a bounded unique set.");
            }

            for (int index = 0; index < match.Extensions.Count; index++)
            {
                if (!IsExtension(match.Extensions[index]))
                {
                    Add(errors, $"applicability.match.extensions[{index}]", "skill.extension_invalid", "An extension is invalid.");
                }
            }
        }

        ValidateFilenameMatch(match.Filename, errors);
        if (match.MaxBytes is < 0 or > 1_073_741_824)
        {
            Add(errors, "applicability.match.maxBytes", "skill.max_bytes_invalid", "The file-size bound is invalid.");
        }
    }

    private static void ValidateFilenameMatch(
        SkillFilenameMatchContract? filename,
        ICollection<SkillValidationError> errors)
    {
        if (filename is null)
        {
            return;
        }

        if (filename.Prefix is null &&
            filename.Suffix is null &&
            filename.Contains is null &&
            filename.SafeRegex is null)
        {
            Add(errors, "applicability.match.filename", "skill.filename_match_empty", "A filename matcher needs a predicate.");
        }

        ValidateBoundedLiteral(filename.Prefix, "applicability.match.filename.prefix", errors);
        ValidateBoundedLiteral(filename.Suffix, "applicability.match.filename.suffix", errors);
        ValidateBoundedLiteral(filename.Contains, "applicability.match.filename.contains", errors);
        if (filename.SafeRegex is not null)
        {
            if (filename.SafeRegex.Length is 0 or > 256 ||
                !filename.SafeRegex.StartsWith("\\A", StringComparison.Ordinal) ||
                !filename.SafeRegex.EndsWith("\\z", StringComparison.Ordinal))
            {
                Add(errors, "applicability.match.filename.safeRegex", "skill.regex_not_anchored", "A safe regex must be bounded and anchored.");
            }
            else
            {
                try
                {
                    _ = new Regex(
                        filename.SafeRegex,
                        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                        TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException)
                {
                    Add(errors, "applicability.match.filename.safeRegex", "skill.regex_unsupported", "The regex uses unsupported syntax.");
                }
            }
        }
    }

    private static void ValidateVariables(
        IReadOnlyList<SkillVariableContract>? variables,
        SkillFilenameMatchContract? filenameMatch,
        ICollection<SkillValidationError> errors,
        out HashSet<string> variableNames)
    {
        variableNames = new HashSet<string>(StringComparer.Ordinal);
        if (variables is null)
        {
            Add(errors, "variables", "skill.variables_missing", "The variable list is required.");
            return;
        }

        if (variables.Count > 32)
        {
            Add(errors, "variables", "skill.variables_too_many", "The variable list exceeds its bound.");
        }

        for (int index = 0; index < variables.Count; index++)
        {
            SkillVariableContract? variable = variables[index];
            string field = $"variables[{index}]";
            if (variable is null)
            {
                Add(errors, field, "skill.variable_null", "A variable cannot be null.");
                continue;
            }

            if (!IsVariableName(variable.Name) || !variableNames.Add(variable.Name))
            {
                Add(errors, $"{field}.name", "skill.variable_name_invalid", "Variable names must be unique identifiers.");
            }

            if (!Enum.IsDefined(variable.Source))
            {
                Add(errors, $"{field}.source", "skill.variable_source_unsupported", "The variable source is unsupported.");
            }

            bool needsArgument = variable.Source is
                SkillVariableSource.RegexCapture or SkillVariableSource.UserParameter;
            if (needsArgument != !string.IsNullOrWhiteSpace(variable.Argument) ||
                variable.Argument?.Length > 80)
            {
                Add(errors, $"{field}.argument", "skill.variable_argument_invalid", "The variable argument does not match its source.");
            }

            if (variable.Source == SkillVariableSource.RegexCapture &&
                !string.IsNullOrWhiteSpace(variable.Argument) &&
                !RegexContainsNamedGroup(filenameMatch?.SafeRegex, variable.Argument))
            {
                Add(errors, $"{field}.argument", "skill.regex_capture_group_missing", "The regex capture source must name a declared matcher group.");
            }

            if (variable.Transforms is null ||
                variable.Transforms.Count > 4 ||
                variable.Transforms.Any(static transform => !Enum.IsDefined(transform)) ||
                variable.Transforms.Distinct().Count() != variable.Transforms.Count)
            {
                Add(errors, $"{field}.transforms", "skill.variable_transforms_invalid", "Variable transforms must be bounded and unique.");
            }
        }
    }

    private static void ValidateSteps(
        IReadOnlyList<SkillStepContract>? steps,
        IReadOnlySet<string> variableNames,
        ICollection<SkillValidationError> errors)
    {
        if (steps is null || steps.Count is < 1 or > 16)
        {
            Add(errors, "steps", "skill.steps_invalid", "A SkillSpec needs one to sixteen closed steps.");
            return;
        }

        HashSet<string> stepIds = new(StringComparer.Ordinal);
        int mutableSteps = 0;
        for (int index = 0; index < steps.Count; index++)
        {
            SkillStepContract? step = steps[index];
            string field = $"steps[{index}]";
            if (step is null)
            {
                Add(errors, field, "skill.step_null", "A step cannot be null.");
                continue;
            }

            if (!IsStepId(step.StepId) || !stepIds.Add(step.StepId))
            {
                Add(errors, $"{field}.stepId", "skill.step_id_invalid", "Step identifiers must be unique and bounded.");
            }

            SkillTemplateError? templateError = step switch
            {
                EnsureDirectoryStepContract ensure => SkillTemplateEngine.Validate(
                    ensure.DirectoryTemplate,
                    SkillTemplateKind.RelativePath,
                    variableNames,
                    $"{field}.directoryTemplate"),
                RenameFileStepContract rename => SkillTemplateEngine.Validate(
                    rename.DestinationFileNameTemplate,
                    SkillTemplateKind.FileName,
                    variableNames,
                    $"{field}.destinationFileNameTemplate"),
                MoveFileStepContract move => ValidateMoveLike(
                    move.DestinationDirectoryTemplate,
                    move.DestinationFileNameTemplate,
                    variableNames,
                    field),
                CopyFileStepContract copy => ValidateMoveLike(
                    copy.DestinationDirectoryTemplate,
                    copy.DestinationFileNameTemplate,
                    variableNames,
                    field),
                _ => new SkillTemplateError("skill.action_unsupported", field),
            };
            if (step is not EnsureDirectoryStepContract)
            {
                mutableSteps++;
            }

            if (templateError is not null)
            {
                Add(errors, templateError.Field, templateError.Code, "The step template is invalid.");
            }
        }

        if (mutableSteps != 1)
        {
            Add(errors, "steps", "skill.mutable_step_count_unsupported", "Compiler version 0.1 requires exactly one file action.");
        }
    }

    private static SkillTemplateError? ValidateMoveLike(
        string? directoryTemplate,
        string? filenameTemplate,
        IReadOnlySet<string> variables,
        string field)
    {
        SkillTemplateError? directory = SkillTemplateEngine.Validate(
            directoryTemplate,
            SkillTemplateKind.RelativePath,
            variables,
            $"{field}.destinationDirectoryTemplate");
        return directory ?? (filenameTemplate is null
            ? null
            : SkillTemplateEngine.Validate(
                filenameTemplate,
                SkillTemplateKind.FileName,
                variables,
                $"{field}.destinationFileNameTemplate"));
    }

    private static void ValidatePolicy(
        SkillPolicyContract? policy,
        ICollection<SkillValidationError> errors)
    {
        if (policy is null ||
            policy.Collision != CollisionPolicy.Reject ||
            !policy.RequireExactPlanApproval ||
            policy.AllowNetworkPaths ||
            policy.AllowReparsePoints ||
            policy.AllowOverwrite ||
            !policy.SameVolumeOnly)
        {
            Add(errors, "policy", "skill.policy_unsafe", "The v0.1 fail-closed policy is required.");
        }
    }

    private static void ValidateVerification(
        SkillVerificationContract? verification,
        ICollection<SkillValidationError> errors)
    {
        if (verification is null ||
            !verification.AllPlannedStepsVerified ||
            !verification.FailOnUnexpectedChange)
        {
            Add(errors, "verification", "skill.verification_unsafe", "Every step and unexpected change must be verified.");
        }
    }

    private static void ValidateProvenance(
        SkillProvenanceContract? provenance,
        int currentVersion,
        ICollection<SkillValidationError> errors)
    {
        if (provenance is null)
        {
            Add(errors, "provenance", "skill.provenance_missing", "Teaching provenance is required.");
            return;
        }

        if (!ValidUniqueIds(provenance.TeachingEpisodeIds, minimum: 1, maximum: int.MaxValue))
        {
            Add(errors, "provenance.teachingEpisodeIds", "skill.episode_ids_invalid", "Teaching episode identifiers are invalid.");
        }

        if (!ValidUniqueIds(provenance.ExampleIds, minimum: 1, maximum: 50))
        {
            Add(errors, "provenance.exampleIds", "skill.example_ids_invalid", "Example identifiers are invalid.");
        }

        if (provenance.UserAnswers is null || provenance.UserAnswers.Count > 16)
        {
            Add(errors, "provenance.userAnswers", "skill.answers_invalid", "User answers exceed their bound.");
        }
        else
        {
            HashSet<string> questionCodes = new(StringComparer.Ordinal);
            for (int index = 0; index < provenance.UserAnswers.Count; index++)
            {
                SkillUserAnswerContract? answer = provenance.UserAnswers[index];
                if (answer is null ||
                    !IsQuestionCode(answer.QuestionCode) ||
                    !questionCodes.Add(answer.QuestionCode) ||
                    answer.SelectedValue is null ||
                    answer.SelectedValue.Length > 160 ||
                    answer.SelectedValue.Any(char.IsControl))
                {
                    Add(errors, $"provenance.userAnswers[{index}]", "skill.answer_invalid", "A user answer is invalid.");
                }
            }
        }

        if (provenance.ParentVersion is < 1 ||
            provenance.ParentVersion >= currentVersion)
        {
            Add(errors, "provenance.parentVersion", "skill.parent_version_invalid", "A parent version must be positive and older than this version.");
        }
    }

    private static void ValidateCompatibility(
        SkillCompatibilityContract? compatibility,
        ICollection<SkillValidationError> errors)
    {
        if (compatibility is null ||
            !string.Equals(compatibility.ContractVersion, ContractVersions.V1, StringComparison.Ordinal) ||
            !string.Equals(
                compatibility.MinimumExecutorVersion,
                SupportedExecutorVersion,
                StringComparison.Ordinal) ||
            !IsSemanticVersion(compatibility.MinimumExecutorVersion))
        {
            Add(errors, "compatibility", "skill.compatibility_unsupported", "The SkillSpec compatibility range is unsupported.");
        }
    }

    private static bool IsRelativeDirectory(string value)
    {
        if (value.Length > 240 || value.Contains('\\') || value.StartsWith('/'))
        {
            return false;
        }

        if (value.Length == 0)
        {
            return true;
        }

        return WindowsPathPolicy.ParseRelative(value.Replace('/', '\\')).IsSuccess;
    }

    private static bool IsExtension(string? value) =>
        value is { Length: >= 2 and <= 17 } &&
        value[0] == '.' &&
        value.Skip(1).All(char.IsAsciiLetterOrDigit);

    private static bool RegexContainsNamedGroup(string? pattern, string groupName)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return false;
        }

        try
        {
            Regex regex = new(
                pattern,
                RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                TimeSpan.FromMilliseconds(100));
            return regex.GetGroupNames().Contains(groupName, StringComparer.Ordinal) &&
                !string.Equals(groupName, "0", StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool IsVariableName(string? value) =>
        value is { Length: >= 1 and <= 32 } &&
        char.IsAsciiLetter(value[0]) &&
        value.All(char.IsAsciiLetterOrDigit);

    private static bool IsStepId(string? value) =>
        value is { Length: >= 1 and <= 32 } &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static bool IsQuestionCode(string? value) =>
        value is { Length: >= 1 and <= 64 } &&
        value[0] is >= 'a' and <= 'z' &&
        value.All(static character =>
            character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-' or '.');

    private static bool IsSemanticVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] versionAndLabel = value.Split('-', 2);
        string[] core = versionAndLabel[0].Split('.');
        if (core.Length != 3 || core.Any(static part =>
                part.Length == 0 ||
                (part.Length > 1 && part[0] == '0') ||
                !part.All(char.IsAsciiDigit)))
        {
            return false;
        }

        return versionAndLabel.Length == 1 ||
            (versionAndLabel[1].Length > 0 &&
             versionAndLabel[1].All(static character =>
                 char.IsAsciiLetterOrDigit(character) || character is '-' or '.'));
    }

    private static bool ValidUniqueIds(
        IReadOnlyList<Guid>? values,
        int minimum,
        int maximum) =>
        values is not null &&
        values.Count >= minimum &&
        values.Count <= maximum &&
        values.All(static value => value != Guid.Empty) &&
        values.Distinct().Count() == values.Count;

    private static void ValidateText(
        string? value,
        int maximumLength,
        string field,
        ICollection<SkillValidationError> errors)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > maximumLength ||
            value.Any(char.IsControl))
        {
            Add(errors, field, "skill.text_invalid", "A bounded display string is invalid.");
        }
    }

    private static void ValidateBoundedLiteral(
        string? value,
        string field,
        ICollection<SkillValidationError> errors)
    {
        if (value is null)
        {
            return;
        }

        const string forbidden = "\\/:*?\"<>|";
        if (value.Length is 0 or > 80 ||
            value.Any(character => char.IsControl(character) || forbidden.Contains(character)))
        {
            Add(errors, field, "skill.literal_invalid", "A filename literal is unsafe.");
        }
    }

    private static void Add(
        ICollection<SkillValidationError> errors,
        string field,
        string code,
        string message) =>
        errors.Add(new SkillValidationError(field, code, message));
}
