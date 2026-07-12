namespace FarmTogether2.ModKit.Tool;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length < 2)
                throw new ArgumentException("Expected a command group and command.");

            CommandOptions options = CommandOptions.Parse(args[2..]);
            switch ((args[0], args[1]))
            {
                case ("ref-package", "write"):
                    {
                        string output = options.RequireSingle("--output");
                        string packageId = options.RequireSingle("--package-id");
                        string version = options.RequireSingle("--version");
                        IReadOnlyList<string> assemblies = options.RequireMany("--assembly");
                        options.AssertAllConsumed();
                        DeterministicNupkgWriter.Write(
                            output,
                            packageId,
                            version,
                            assemblies);
                        break;
                    }
                case ("ref-package", "verify"):
                    {
                        string package = options.RequireSingle("--package");
                        string packageId = options.RequireSingle("--package-id");
                        string version = options.RequireSingle("--version");
                        options.AssertAllConsumed();
                        DeterministicNupkgWriter.VerifyExisting(package, packageId, version);
                        break;
                    }
                case ("lock", "write"):
                    {
                        string output = options.RequireSingle("--output");
                        ModKitLock value = options.ToLockFile();
                        options.AssertAllConsumed();
                        LockFileResolver.Write(
                            output,
                            value);
                        break;
                    }
                case ("lock", "verify"):
                    {
                        string file = options.RequireSingle("--file");
                        options.AssertAllConsumed();
                        _ = LockFileResolver.Read(file);
                        break;
                    }
                case ("mod-package", "write"):
                    {
                        string modConfig = options.RequireSingle("--mod-config");
                        string repositoryRoot = options.RequireSingle("--repository-root");
                        string pluginDll = options.RequireSingle("--plugin-dll");
                        string pluginPdb = options.RequireSingle("--plugin-pdb");
                        string msbuildVersion = options.RequireSingle("--msbuild-version");
                        string output = options.RequireSingle("--output");
                        options.AssertAllConsumed();
                        ModPackager.Write(modConfig, repositoryRoot, pluginDll, pluginPdb, msbuildVersion, output);
                        break;
                    }
                case ("mod-config", "verify"):
                    {
                        string file = options.RequireSingle("--file");
                        options.AssertAllConsumed();
                        _ = StrictModJson.Read(file);
                        break;
                    }
                case ("mod-package", "verify"):
                    {
                        string modConfig = options.RequireSingle("--mod-config");
                        string artifacts = options.RequireSingle("--artifacts");
                        options.AssertAllConsumed();
                        ModPackager.Verify(modConfig, artifacts);
                        break;
                    }
                case ("candidate", "write-mod"):
                    {
                        string directory = options.RequireSingle("--directory");
                        string artifactName = options.RequireSingle("--artifact-name");
                        string assemblyName = options.RequireSingle("--assembly-name");
                        string version = options.RequireSingle("--version");
                        string commit = options.RequireSingle("--commit");
                        long runId = options.RequirePositiveInt64("--run-id");
                        options.AssertAllConsumed();
                        CandidateVerifier.WriteMod(directory, artifactName, assemblyName, version, commit, runId);
                        break;
                    }
                case ("candidate", "write-reference"):
                    {
                        string directory = options.RequireSingle("--directory");
                        string artifactName = options.RequireSingle("--artifact-name");
                        string packageId = options.RequireSingle("--package-id");
                        string packageVersion = options.RequireSingle("--package-version");
                        string commit = options.RequireSingle("--commit");
                        long runId = options.RequirePositiveInt64("--run-id");
                        options.AssertAllConsumed();
                        CandidateVerifier.WriteReference(directory, artifactName, packageId, packageVersion, commit, runId);
                        break;
                    }
                case ("candidate", "verify"):
                    {
                        string directory = options.RequireSingle("--directory");
                        string kind = options.RequireSingle("--kind");
                        string expectedCommit = options.RequireSingle("--expected-commit");
                        long expectedRunId = options.RequirePositiveInt64("--expected-run-id");
                        string expectedArtifactName = options.RequireSingle("--expected-artifact-name");
                        options.AssertAllConsumed();
                        CandidateVerifier.Verify(directory, kind, expectedCommit, expectedRunId, expectedArtifactName);
                        break;
                    }
                case ("candidate", "verify-published"):
                    {
                        string published = options.RequireSingle("--published-directory");
                        string candidate = options.RequireSingle("--candidate-directory");
                        string kind = options.RequireSingle("--kind");
                        options.AssertAllConsumed();
                        CandidateVerifier.VerifyPublished(published, candidate, kind);
                        break;
                    }
                default:
                    throw new ArgumentException($"Unknown command '{args[0]} {args[1]}'.");
            }
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }
}

internal sealed class CommandOptions
{
    private readonly Dictionary<string, List<string>> _values;
    private readonly HashSet<string> _consumed = new(StringComparer.Ordinal);

    private CommandOptions(Dictionary<string, List<string>> values) => _values = values;

    public static CommandOptions Parse(IReadOnlyList<string> args)
    {
        Dictionary<string, List<string>> values = new(StringComparer.Ordinal);
        for (int index = 0; index < args.Count; index += 2)
        {
            string name = args[index];
            if (!name.StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Count)
                throw new ArgumentException($"Invalid command option near '{name}'.");
            if (!values.TryGetValue(name, out List<string>? occurrences))
            {
                occurrences = [];
                values.Add(name, occurrences);
            }
            occurrences.Add(args[index + 1]);
        }
        return new CommandOptions(values);
    }

    public string RequireSingle(string name)
    {
        _consumed.Add(name);
        if (!_values.TryGetValue(name, out List<string>? values) || values.Count != 1 || string.IsNullOrEmpty(values[0]))
            throw new ArgumentException($"Option {name} must occur exactly once with a non-empty value.");
        return values[0];
    }

    public IReadOnlyList<string> RequireMany(string name)
    {
        _consumed.Add(name);
        if (!_values.TryGetValue(name, out List<string>? values) || values.Count == 0 || values.Any(string.IsNullOrEmpty))
            throw new ArgumentException($"Option {name} must occur at least once with non-empty values.");
        return values;
    }

    public long RequirePositiveInt64(string name)
    {
        string value = RequireSingle(name);
        if (!long.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long result) ||
            result <= 0 || result.ToString(System.Globalization.CultureInfo.InvariantCulture) != value)
            throw new ArgumentException($"Option {name} must be a canonical positive decimal integer.");
        return result;
    }

    public ModKitLock ToLockFile() => new(
        SchemaVersion: ParseSchemaVersion(RequireSingle("--schema-version")),
        Repository: RequireSingle("--repository"),
        WorkflowCommit: RequireSingle("--workflow-commit"),
        PackageId: RequireSingle("--package-id"),
        PackageVersion: RequireSingle("--package-version"),
        ReleaseTag: RequireSingle("--release-tag"),
        AssetName: RequireSingle("--asset-name"),
        Sha256: RequireSingle("--sha256"));

    public void AssertAllConsumed()
    {
        string[] unknown = _values.Keys.Where(key => !_consumed.Contains(key)).Order(StringComparer.Ordinal).ToArray();
        if (unknown.Length != 0)
            throw new ArgumentException($"Unknown command options: {string.Join(", ", unknown)}.");
    }

    private static int ParseSchemaVersion(string value) => value == "1"
        ? 1
        : throw new ArgumentException("Schema version must be the canonical integer 1.");
}
