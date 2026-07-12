namespace FarmTogether2.CompatibilityVerifier;

public static class VerifierCommand
{
    private static readonly string[] RequiredFlags =
    [
        "--contract",
        "--supported-builds",
        "--mod-id",
        "--interop-dir",
        "--stub-plugin",
        "--real-plugin",
        "--player-package",
        "--report"
    ];

    public static VerifyOptions Parse(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count == 0 || !string.Equals(arguments[0], "verify", StringComparison.Ordinal))
            throw new ArgumentException("The first argument must be the exact command 'verify'.", nameof(arguments));
        if (arguments.Count != 1 + RequiredFlags.Length * 2)
            throw new ArgumentException("The verify command requires each supported flag exactly once.", nameof(arguments));

        Dictionary<string, string> values = new(StringComparer.Ordinal);
        for (int index = 1; index < arguments.Count; index += 2)
        {
            string flag = arguments[index];
            string value = arguments[index + 1];
            if (!RequiredFlags.Contains(flag, StringComparer.Ordinal))
                throw new ArgumentException($"Unknown verifier flag '{flag}'.", nameof(arguments));
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Verifier flag '{flag}' requires a value.", nameof(arguments));
            if (!values.TryAdd(flag, value))
                throw new ArgumentException($"Verifier flag '{flag}' was provided more than once.", nameof(arguments));
        }
        if (values.Count != RequiredFlags.Length)
            throw new ArgumentException("The verify command is missing a required flag.", nameof(arguments));

        return new VerifyOptions(
            values["--contract"],
            values["--supported-builds"],
            values["--mod-id"],
            values["--interop-dir"],
            values["--stub-plugin"],
            values["--real-plugin"],
            values["--player-package"],
            values["--report"]);
    }
}

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            VerifyOptions options = VerifierCommand.Parse(args);
            CompatibilityVerifier.Verify(options);
            return 0;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(exception.Message);
            return exception is ArgumentException ? 2 : 1;
        }
    }
}
