using System.Text.Json;

namespace FarmTogether2.ContractModel;

public static class ContractJson
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    public static GameApiContract Load(string path)
    {
        using FileStream stream = File.OpenRead(path);
        GameApiContract contract = JsonSerializer.Deserialize<GameApiContract>(stream, Options)
            ?? throw new InvalidDataException($"Contract is empty: {path}");
        if (contract.SchemaVersion != 1)
            throw new InvalidDataException($"Unsupported contract schema {contract.SchemaVersion}.");
        return contract;
    }
}
