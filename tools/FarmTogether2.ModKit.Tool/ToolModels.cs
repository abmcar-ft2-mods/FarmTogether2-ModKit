using System.Text;
using System.Text.Json;

namespace FarmTogether2.ModKit.Tool;

internal sealed record ModKitLock(
    int SchemaVersion,
    string Repository,
    string WorkflowCommit,
    string PackageId,
    string PackageVersion,
    string ReleaseTag,
    string AssetName,
    string Sha256);

internal sealed record ModConfiguration(
    int SchemaVersion,
    string Id,
    string DisplayName,
    string AssemblyName,
    string Version,
    string Project,
    IReadOnlyList<string> TestProjects,
    IReadOnlyList<string> GuardScripts,
    string InstallReadme,
    string SupportedSteamBuild);

internal sealed record ModCandidate(
    int SchemaVersion,
    string CandidateKind,
    string ArtifactName,
    string AssemblyName,
    string Version,
    string Commit,
    long RunId,
    string PlayerAsset,
    string PlayerSha256,
    string SymbolsAsset,
    string SymbolsSha256,
    string ChecksumsAsset,
    string ChecksumsSha256);

internal sealed record ReferenceCandidate(
    int SchemaVersion,
    string CandidateKind,
    string ArtifactName,
    string PackageId,
    string PackageVersion,
    string Commit,
    long RunId,
    string PackageAsset,
    string PackageSha256,
    string ChecksumsAsset,
    string ChecksumsSha256);

internal static class StrictModJson
{
    private static readonly string[] Fields =
    [
        "schemaVersion",
        "id",
        "displayName",
        "assemblyName",
        "version",
        "project",
        "testProjects",
        "guardScripts",
        "installReadme",
        "supportedSteamBuild"
    ];

    public static ModConfiguration Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        SafePath.AssertRegularFile(fullPath, "Mod configuration");
        using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using JsonDocument document = StrictJson.Parse(stream, fullPath);
        Dictionary<string, JsonElement> properties = StrictJson.ReadClosedObject(document.RootElement, Fields, "Mod configuration");
        int schemaVersion = StrictJson.ReadCanonicalOne(properties, "schemaVersion", "Mod configuration");
        ModConfiguration config = new(
            schemaVersion,
            StrictJson.ReadString(properties, "id", "Mod configuration"),
            StrictJson.ReadString(properties, "displayName", "Mod configuration"),
            StrictJson.ReadString(properties, "assemblyName", "Mod configuration"),
            StrictJson.ReadString(properties, "version", "Mod configuration"),
            StrictJson.ReadString(properties, "project", "Mod configuration"),
            StrictJson.ReadStringArray(properties, "testProjects", "Mod configuration"),
            StrictJson.ReadStringArray(properties, "guardScripts", "Mod configuration"),
            StrictJson.ReadString(properties, "installReadme", "Mod configuration"),
            StrictJson.ReadString(properties, "supportedSteamBuild", "Mod configuration"));
        Validate(config);
        return config;
    }

    public static void Validate(ModConfiguration config)
    {
        if (config.SchemaVersion != 1)
            throw new InvalidDataException("Unsupported mod configuration schema version.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                config.Id,
                "^[a-z0-9]+(?:[.-][a-z0-9]+)+$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("Mod id must be a canonical lowercase dotted identifier.");
        if (string.IsNullOrWhiteSpace(config.DisplayName) || config.DisplayName != config.DisplayName.Trim())
            throw new InvalidDataException("Mod displayName must be non-empty canonical text.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                config.AssemblyName,
                "^[A-Za-z_][A-Za-z0-9_.]*$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("Mod assemblyName is noncanonical.");
        if (!StrictLockJson.IsCanonicalVersion(config.Version))
            throw new InvalidDataException("Mod version must be canonical stable SemVer.");
        AssertRelativePath(config.Project, ".csproj", "project");
        AssertDistinctPaths(config.TestProjects, ".csproj", "testProjects");
        AssertDistinctPaths(config.GuardScripts, ".ps1", "guardScripts");
        AssertRelativePath(config.InstallReadme, ".txt", "installReadme");
        if (!System.Text.RegularExpressions.Regex.IsMatch(
                config.SupportedSteamBuild,
                "^[1-9][0-9]*$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("supportedSteamBuild must be a canonical positive decimal string.");
    }

    public static string ResolveRepositoryPath(string repositoryRoot, string relativePath, string label)
    {
        string root = Path.GetFullPath(repositoryRoot);
        string candidate = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string relative = Path.GetRelativePath(root, candidate);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException($"Mod {label} escapes the repository root.");
        return candidate;
    }

    private static void AssertDistinctPaths(IReadOnlyList<string> paths, string extension, string field)
    {
        if (paths.Count != paths.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidDataException($"Mod {field} must not contain duplicate paths.");
        foreach (string path in paths)
            AssertRelativePath(path, extension, field);
    }

    private static void AssertRelativePath(string path, string extension, string field)
    {
        if (string.IsNullOrWhiteSpace(path) || path != path.Trim() || path.Any(char.IsControl) || path.Contains('\\') || path.Contains(':') ||
            path.StartsWith("/", StringComparison.Ordinal) ||
            path.Split('/').Any(segment => segment.Length == 0 || segment is "." or "..") ||
            !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Mod {field} contains an unsafe or noncanonical relative path: {path}");
        }
    }
}

internal static class StrictJson
{
    public static JsonDocument Parse(Stream stream, string label)
    {
        try
        {
            return JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Invalid JSON in {label}: {exception.Message}", exception);
        }
    }

    public static Dictionary<string, JsonElement> ReadClosedObject(
        JsonElement element,
        IReadOnlyCollection<string> fields,
        string label)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{label} must be a JSON object.");
        Dictionary<string, JsonElement> properties = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value.Clone()))
                throw new InvalidDataException($"{label} contains duplicate field '{property.Name}'.");
        }
        if (properties.Count != fields.Count || fields.Any(field => !properties.ContainsKey(field)))
            throw new InvalidDataException($"{label} does not match its closed schema.");
        return properties;
    }

    public static int ReadCanonicalOne(Dictionary<string, JsonElement> properties, string field, string label)
    {
        JsonElement value = properties[field];
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result) || result != 1 || value.GetRawText() != "1")
            throw new InvalidDataException($"{label} field '{field}' must be the JSON integer 1.");
        return result;
    }

    public static long ReadPositiveInt64(Dictionary<string, JsonElement> properties, string field, string label)
    {
        JsonElement value = properties[field];
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long result) || result <= 0 ||
            value.GetRawText() != result.ToString(System.Globalization.CultureInfo.InvariantCulture))
            throw new InvalidDataException($"{label} field '{field}' must be a canonical positive JSON integer.");
        return result;
    }

    public static string ReadString(Dictionary<string, JsonElement> properties, string field, string label)
    {
        JsonElement value = properties[field];
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{label} field '{field}' must be a JSON string.");
        return value.GetString() ?? throw new InvalidDataException($"{label} field '{field}' must not be null.");
    }

    public static IReadOnlyList<string> ReadStringArray(Dictionary<string, JsonElement> properties, string field, string label)
    {
        JsonElement value = properties[field];
        if (value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{label} field '{field}' must be a JSON string array.");
        List<string> result = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not string text)
                throw new InvalidDataException($"{label} field '{field}' must contain only JSON strings.");
            result.Add(text);
        }
        return result;
    }
}

internal static class StrictLockJson
{
    private static readonly string[] Fields =
    [
        "schemaVersion",
        "repository",
        "workflowCommit",
        "packageId",
        "packageVersion",
        "releaseTag",
        "assetName",
        "sha256"
    ];

    public static ModKitLock Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        SafePath.AssertRegularFile(fullPath, "Lock JSON");
        using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Parse(stream, fullPath);
    }

    public static ModKitLock Parse(Stream stream, string label)
    {
        JsonDocumentOptions options = new()
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow
        };
        using JsonDocument document = JsonDocument.Parse(stream, options);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Lock JSON must be an object: {label}");

        Dictionary<string, JsonElement> properties = new(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            if (!properties.TryAdd(property.Name, property.Value.Clone()))
                throw new InvalidDataException($"Lock JSON contains duplicate field '{property.Name}': {label}");
        }
        if (properties.Count != Fields.Length || Fields.Any(field => !properties.ContainsKey(field)))
            throw new InvalidDataException($"Lock JSON must contain exactly the closed eight-field schema: {label}");

        if (properties["schemaVersion"].ValueKind != JsonValueKind.Number ||
            !properties["schemaVersion"].TryGetInt32(out int schemaVersion) || schemaVersion != 1 ||
            properties["schemaVersion"].GetRawText() != "1")
        {
            throw new InvalidDataException($"schemaVersion must be the JSON integer 1: {label}");
        }

        string ReadString(string field)
        {
            JsonElement value = properties[field];
            if (value.ValueKind != JsonValueKind.String)
                throw new InvalidDataException($"Lock field '{field}' must be a JSON string: {label}");
            return value.GetString() ?? throw new InvalidDataException($"Lock field '{field}' must not be null: {label}");
        }

        ModKitLock result = new(
            schemaVersion,
            ReadString("repository"),
            ReadString("workflowCommit"),
            ReadString("packageId"),
            ReadString("packageVersion"),
            ReadString("releaseTag"),
            ReadString("assetName"),
            ReadString("sha256"));
        Validate(result);
        return result;
    }

    public static byte[] Serialize(ModKitLock value)
    {
        Validate(value);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", value.SchemaVersion);
            writer.WriteString("repository", value.Repository);
            writer.WriteString("workflowCommit", value.WorkflowCommit);
            writer.WriteString("packageId", value.PackageId);
            writer.WriteString("packageVersion", value.PackageVersion);
            writer.WriteString("releaseTag", value.ReleaseTag);
            writer.WriteString("assetName", value.AssetName);
            writer.WriteString("sha256", value.Sha256);
            writer.WriteEndObject();
        }
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    public static void Validate(ModKitLock value)
    {
        if (value.SchemaVersion != 1)
            throw new InvalidDataException("Unsupported lock schema version.");
        if (!System.Text.RegularExpressions.Regex.IsMatch(value.Repository, "^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
            value.Repository.Split('/').Any(segment => segment is "." or ".."))
            throw new InvalidDataException("Lock repository must be owner/name.");
        if (!IsLowerHex(value.WorkflowCommit, 40))
            throw new InvalidDataException("Lock workflowCommit must be lowercase 40-hex.");
        if (value.PackageId != DeterministicNupkgWriter.PackageId)
            throw new InvalidDataException($"Lock packageId must be {DeterministicNupkgWriter.PackageId}.");
        if (!IsCanonicalVersion(value.PackageVersion))
            throw new InvalidDataException("Lock packageVersion must be canonical stable SemVer.");
        if (value.ReleaseTag != $"v{value.PackageVersion}")
            throw new InvalidDataException("Lock releaseTag must exactly match packageVersion.");
        if (value.AssetName != $"{value.PackageId}.{value.PackageVersion}.nupkg")
            throw new InvalidDataException("Lock assetName must exactly match the package identity.");
        if (!IsLowerHex(value.Sha256, 64))
            throw new InvalidDataException("Lock sha256 must be lowercase 64-hex.");
    }

    public static bool IsCanonicalVersion(string value) =>
        System.Text.RegularExpressions.Regex.IsMatch(
            value,
            "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsLowerHex(string value, int length) =>
        value.Length == length && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}

internal static class SafePath
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public static void AssertRegularFile(string path, string label)
    {
        string fullPath = Path.GetFullPath(path);
        AssertNoReparseAncestor(fullPath, label);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"{label} does not exist.", fullPath);
        FileAttributes attributes = File.GetAttributes(fullPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException($"{label} must be a regular file: {fullPath}");
    }

    public static void AssertDirectory(string path, string label)
    {
        string fullPath = Path.GetFullPath(path);
        AssertNoReparseAncestor(fullPath, label);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"{label} does not exist: {fullPath}");
        FileAttributes attributes = File.GetAttributes(fullPath);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException($"{label} must not be a symlink or reparse point: {fullPath}");
    }

    public static void AssertDirectoryTreeHasNoReparsePoint(string path, string label)
    {
        AssertDirectory(path, label);
        foreach (string entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
        {
            FileAttributes attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"{label} contains a symlink or reparse point: {entry}");
        }
    }

    public static void AssertNoReparseAncestor(string path, string label)
    {
        string current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                FileAttributes attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"{label} contains a symlink or reparse-point ancestor: {current}");
            }

            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, PathComparison))
                break;
            current = parent;
        }
    }
}
