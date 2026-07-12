namespace FarmTogether2.ContractModel;

public sealed record GameApiContract(
    int SchemaVersion,
    IReadOnlyList<DirectTypeContract> DirectTypes,
    IReadOnlyList<DirectMemberContract> DirectMembers,
    IReadOnlyList<EnumValueContract> EnumValues,
    IReadOnlyList<RuntimeTargetContract> RuntimeTargets);

public sealed record DirectTypeContract(
    string Assembly,
    string Type,
    string Kind,
    string? BaseType,
    bool SupportOnly = false);

public sealed record DirectMemberContract(
    string Assembly,
    string DeclaringType,
    string Kind,
    string Signature,
    bool IsStatic);

public sealed record EnumValueContract(
    string Assembly,
    string EnumType,
    string Name,
    long Value);

public sealed record RuntimeTargetContract(
    string Assembly,
    string Type,
    string Kind,
    string Signature,
    bool Required,
    IReadOnlyList<string> ModIds);
