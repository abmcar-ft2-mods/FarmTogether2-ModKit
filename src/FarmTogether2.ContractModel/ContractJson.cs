using System.Text.Json;
using System.Text.Json.Serialization;

namespace FarmTogether2.ContractModel;

public static class ContractJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static GameApiContract Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            using FileStream stream = File.OpenRead(path);
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Disallow,
                AllowTrailingCommas = false
            });
            ValidateContract(document.RootElement);

            return document.RootElement.Deserialize<GameApiContract>(Options)
                ?? throw new InvalidDataException($"Contract is empty: {path}");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Contract is not valid closed JSON: {path}", exception);
        }
    }

    private static void ValidateContract(JsonElement root)
    {
        ValidateObject(root, "contract", "schemaVersion", "directTypes", "directMembers", "enumValues", "runtimeTargets");
        int schemaVersion = ReadInt32(root, "schemaVersion", "contract");
        if (schemaVersion != 1)
            throw new InvalidDataException($"Unsupported contract schema {schemaVersion}.");

        ValidateArray(root, "directTypes", "contract", (item, context) =>
        {
            ValidateObject(item, context, "assembly", "type", "kind", "baseType", "supportOnly");
            ReadRequiredString(item, "assembly", context);
            ReadRequiredString(item, "type", context);
            ValidateAllowedString(item, "kind", context, "class", "delegate", "enum", "interface", "struct");
            ReadNullableString(item, "baseType", context);
            ReadBoolean(item, "supportOnly", context);
        });

        ValidateArray(root, "directMembers", "contract", (item, context) =>
        {
            ValidateObject(item, context, "assembly", "declaringType", "kind", "signature", "isStatic");
            ReadRequiredString(item, "assembly", context);
            ReadRequiredString(item, "declaringType", context);
            ValidateAllowedString(item, "kind", context, "field", "method", "property");
            ReadRequiredString(item, "signature", context);
            ReadBoolean(item, "isStatic", context);
        });

        ValidateArray(root, "enumValues", "contract", (item, context) =>
        {
            ValidateObject(item, context, "assembly", "enumType", "name", "value");
            ReadRequiredString(item, "assembly", context);
            ReadRequiredString(item, "enumType", context);
            ReadRequiredString(item, "name", context);
            ReadInt64(item, "value", context);
        });

        ValidateArray(root, "runtimeTargets", "contract", (item, context) =>
        {
            ValidateObject(item, context, "assembly", "type", "kind", "signature", "required", "modIds");
            ReadRequiredString(item, "assembly", context);
            ReadRequiredString(item, "type", context);
            ValidateAllowedString(item, "kind", context, "field", "method", "property");
            ReadRequiredString(item, "signature", context);
            ReadBoolean(item, "required", context);
            ValidateStringArray(item, "modIds", context, requireNonEmpty: true);
        });

        ValidateUniqueAndSorted(root.GetProperty("directTypes"), "directTypes", item =>
            $"{item.GetProperty("assembly").GetString()}\u001f{item.GetProperty("type").GetString()}");
        ValidateUniqueAndSorted(root.GetProperty("directMembers"), "directMembers", item =>
            $"{item.GetProperty("assembly").GetString()}\u001f{item.GetProperty("declaringType").GetString()}\u001f{item.GetProperty("signature").GetString()}");
        ValidateUniqueAndSorted(root.GetProperty("enumValues"), "enumValues", item =>
            $"{item.GetProperty("assembly").GetString()}\u001f{item.GetProperty("enumType").GetString()}\u001f{item.GetProperty("name").GetString()}");
        ValidateUniqueAndSorted(root.GetProperty("runtimeTargets"), "runtimeTargets", item =>
            $"{item.GetProperty("assembly").GetString()}\u001f{item.GetProperty("type").GetString()}\u001f{item.GetProperty("kind").GetString()}\u001f{item.GetProperty("signature").GetString()}");
    }

    private static void ValidateObject(JsonElement element, string context, params string[] expectedNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{context} must be a JSON object.");

        HashSet<string> expected = new(expectedNames, StringComparer.Ordinal);
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new InvalidDataException($"{context} contains duplicate property '{property.Name}'.");
            if (!expected.Contains(property.Name))
                throw new InvalidDataException($"{context} contains unexpected property '{property.Name}'.");
        }

        if (!seen.SetEquals(expected))
        {
            string missing = string.Join(", ", expected.Except(seen).Order(StringComparer.Ordinal));
            throw new InvalidDataException($"{context} is missing required properties: {missing}.");
        }
    }

    private static void ValidateArray(
        JsonElement parent,
        string propertyName,
        string context,
        Action<JsonElement, string> validateItem)
    {
        JsonElement array = parent.GetProperty(propertyName);
        if (array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{context}.{propertyName} must be a JSON array.");

        int index = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            validateItem(item, $"{context}.{propertyName}[{index}]");
            index++;
        }
    }

    private static void ValidateStringArray(
        JsonElement parent,
        string propertyName,
        string context,
        bool requireNonEmpty)
    {
        JsonElement array = parent.GetProperty(propertyName);
        if (array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{context}.{propertyName} must be a JSON array.");

        List<string> values = [];
        foreach (JsonElement item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new InvalidDataException($"{context}.{propertyName} must contain nonempty strings.");
            values.Add(item.GetString()!);
        }
        if (requireNonEmpty && values.Count == 0)
            throw new InvalidDataException($"{context}.{propertyName} must not be empty.");
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new InvalidDataException($"{context}.{propertyName} contains duplicate values.");
        if (!values.SequenceEqual(values.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException($"{context}.{propertyName} must use ordinal order.");
    }

    private static void ValidateUniqueAndSorted(JsonElement array, string context, Func<JsonElement, string> keySelector)
    {
        List<string> keys = array.EnumerateArray().Select(keySelector).ToList();
        if (keys.Distinct(StringComparer.Ordinal).Count() != keys.Count)
            throw new InvalidDataException($"contract.{context} contains duplicate semantic entries.");
        if (!keys.SequenceEqual(keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException($"contract.{context} must use ordinal order.");
    }

    private static string ReadRequiredString(JsonElement parent, string propertyName, string context)
    {
        JsonElement value = parent.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"{context}.{propertyName} must be a nonempty JSON string.");
        return value.GetString()!;
    }

    private static string? ReadNullableString(JsonElement parent, string propertyName, string context)
    {
        JsonElement value = parent.GetProperty(propertyName);
        if (value.ValueKind == JsonValueKind.Null)
            return null;
        return ReadRequiredString(parent, propertyName, context);
    }

    private static void ValidateAllowedString(
        JsonElement parent,
        string propertyName,
        string context,
        params string[] allowed)
    {
        string value = ReadRequiredString(parent, propertyName, context);
        if (!allowed.Contains(value, StringComparer.Ordinal))
            throw new InvalidDataException($"{context}.{propertyName} has unsupported value '{value}'.");
    }

    private static int ReadInt32(JsonElement parent, string propertyName, string context)
    {
        JsonElement value = parent.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int result))
            throw new InvalidDataException($"{context}.{propertyName} must be a JSON integer.");
        return result;
    }

    private static long ReadInt64(JsonElement parent, string propertyName, string context)
    {
        JsonElement value = parent.GetProperty(propertyName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long result))
            throw new InvalidDataException($"{context}.{propertyName} must be a JSON integer.");
        return result;
    }

    private static bool ReadBoolean(JsonElement parent, string propertyName, string context)
    {
        JsonElement value = parent.GetProperty(propertyName);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"{context}.{propertyName} must be a JSON boolean.");
        return value.GetBoolean();
    }
}
