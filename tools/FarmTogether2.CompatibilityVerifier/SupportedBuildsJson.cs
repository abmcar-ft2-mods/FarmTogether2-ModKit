using System.Text.Json;

namespace FarmTogether2.CompatibilityVerifier;

internal static class SupportedBuildsJson
{
    internal static SupportedBuilds Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new InvalidDataException("Supported builds file does not exist.");

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(fullPath), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
            return Parse(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Supported builds is not valid closed JSON.", exception);
        }
    }

    private static SupportedBuilds Parse(JsonElement root)
    {
        ValidateObject(root, "supported builds", "schemaVersion", "mods", "builds", "assemblies");
        if (ReadInteger(root, "schemaVersion", "supported builds") != 1)
            throw new InvalidDataException("Supported builds has an unsupported schemaVersion.");

        JsonElement modsElement = root.GetProperty("mods");
        ValidateObject(modsElement, "supported builds mods", ApprovedApi.ModAssemblies.Keys.ToArray());
        SortedDictionary<string, string> mods = new(StringComparer.Ordinal);
        foreach (string modId in ApprovedApi.ModAssemblies.Keys)
        {
            JsonElement entry = modsElement.GetProperty(modId);
            ValidateObject(entry, $"supported mod '{modId}'", "steamBuildId");
            string buildId = ReadRequiredString(entry, "steamBuildId", $"supported mod '{modId}'");
            if (!IsDecimalDigits(buildId))
                throw new InvalidDataException($"Supported mod '{modId}' has an invalid Steam build ID.");
            mods.Add(modId, buildId);
        }

        JsonElement buildsElement = root.GetProperty("builds");
        if (buildsElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Supported builds builds must be a JSON object.");
        List<JsonProperty> buildProperties = EnumerateUniqueProperties(buildsElement, "supported builds builds");
        if (buildProperties.Count != 2)
            throw new InvalidDataException("Supported builds must contain exactly two builds.");

        SortedDictionary<string, SupportedBuild> builds = new(StringComparer.Ordinal);
        foreach (JsonProperty property in buildProperties)
        {
            string buildId = property.Name;
            if (!IsDecimalDigits(buildId))
                throw new InvalidDataException($"Supported builds contains invalid build ID '{buildId}'.");
            JsonElement entry = property.Value;
            ValidateObject(entry, $"supported build '{buildId}'", "aggregateSha256", "assemblyMetadataSha256");
            string aggregate = ReadFingerprint(entry, "aggregateSha256", $"supported build '{buildId}'");
            JsonElement hashesElement = entry.GetProperty("assemblyMetadataSha256");
            ValidateObject(hashesElement, $"supported build '{buildId}' hashes", ApprovedApi.Assemblies.Keys.ToArray());
            SortedDictionary<string, string> hashes = new(StringComparer.Ordinal);
            foreach (string assemblyName in ApprovedApi.Assemblies.Keys)
                hashes.Add(assemblyName, ReadFingerprint(hashesElement, assemblyName, $"supported build '{buildId}' hashes"));
            string expectedAggregate = CanonicalMetadata.ComputeAggregate(hashes);
            if (!string.Equals(aggregate, expectedAggregate, StringComparison.Ordinal))
                throw new InvalidDataException($"Supported build '{buildId}' has an inconsistent aggregate fingerprint.");
            builds.Add(buildId, new SupportedBuild(aggregate, hashes));
        }

        foreach ((string modId, string buildId) in mods)
        {
            if (!builds.ContainsKey(buildId))
                throw new InvalidDataException($"Supported mod '{modId}' refers to a missing build.");
        }
        foreach (string buildId in builds.Keys)
        {
            if (!mods.Values.Contains(buildId, StringComparer.Ordinal))
                throw new InvalidDataException($"Supported build '{buildId}' is not selected by any mod.");
        }

        JsonElement assembliesElement = root.GetProperty("assemblies");
        if (assembliesElement.ValueKind != JsonValueKind.Array || assembliesElement.GetArrayLength() != ApprovedApi.Assemblies.Count)
            throw new InvalidDataException("Supported builds must list exactly seven assembly identities.");
        SortedDictionary<string, AssemblyIdentity> assemblies = new(StringComparer.Ordinal);
        int identityIndex = 0;
        foreach (JsonElement item in assembliesElement.EnumerateArray())
        {
            string context = $"supported assembly identity [{identityIndex}]";
            ValidateObject(item, context, "name", "version", "culture", "publicKeyToken");
            string name = ReadRequiredString(item, "name", context);
            string version = ReadRequiredString(item, "version", context);
            string culture = ReadStringAllowEmpty(item, "culture", context);
            string token = ReadStringAllowEmpty(item, "publicKeyToken", context);
            if (!ApprovedApi.Assemblies.TryGetValue(name, out string? approvedVersion) || assemblies.ContainsKey(name))
                throw new InvalidDataException($"Supported builds contains duplicate or unknown assembly '{name}'.");
            if (!string.Equals(version, approvedVersion, StringComparison.Ordinal) || culture.Length != 0 || token.Length != 0)
                throw new InvalidDataException($"Supported builds contains an unapproved identity for '{name}'.");
            assemblies.Add(name, new AssemblyIdentity(name, version, culture, token));
            identityIndex++;
        }

        return new SupportedBuilds(mods, builds, assemblies);
    }

    private static void ValidateObject(JsonElement element, string context, params string[] expectedNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{context} must be a JSON object.");
        HashSet<string> expected = new(expectedNames, StringComparer.Ordinal);
        List<JsonProperty> properties = EnumerateUniqueProperties(element, context);
        foreach (JsonProperty property in properties)
        {
            if (!expected.Contains(property.Name))
                throw new InvalidDataException($"{context} contains unexpected property '{property.Name}'.");
        }
        HashSet<string> actual = new(properties.Select(property => property.Name), StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
            throw new InvalidDataException($"{context} has a missing property.");
    }

    private static List<JsonProperty> EnumerateUniqueProperties(JsonElement element, string context)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        List<JsonProperty> properties = [];
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new InvalidDataException($"{context} contains duplicate property '{property.Name}'.");
            properties.Add(property);
        }
        return properties;
    }

    private static int ReadInteger(JsonElement parent, string name, string context)
    {
        JsonElement value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
            throw new InvalidDataException($"{context}.{name} must be a JSON integer.");
        return result;
    }

    private static string ReadRequiredString(JsonElement parent, string name, string context)
    {
        JsonElement value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"{context}.{name} must be a nonempty JSON string.");
        return value.GetString()!;
    }

    private static string ReadStringAllowEmpty(JsonElement parent, string name, string context)
    {
        JsonElement value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException($"{context}.{name} must be a JSON string.");
        return value.GetString()!;
    }

    private static string ReadFingerprint(JsonElement parent, string name, string context)
    {
        string value = ReadRequiredString(parent, name, context);
        if (value.Length != 64 || value == new string('0', 64) || value.Any(character => !IsLowerHex(character)))
            throw new InvalidDataException($"{context}.{name} is not a canonical lowercase SHA-256 fingerprint.");
        return value;
    }

    private static bool IsDecimalDigits(string value) => value.Length != 0 && value.All(character => character is >= '0' and <= '9');

    private static bool IsLowerHex(char character) => character is >= '0' and <= '9' or >= 'a' and <= 'f';
}
