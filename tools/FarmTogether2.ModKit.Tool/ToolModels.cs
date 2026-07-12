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
