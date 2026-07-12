using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FarmTogether2.ModKit.Tool;

internal static class CandidateVerifier
{
    private static readonly string[] ModFields =
    [
        "schemaVersion", "candidateKind", "artifactName", "assemblyName", "version", "commit", "runId",
        "playerAsset", "playerSha256", "symbolsAsset", "symbolsSha256", "checksumsAsset", "checksumsSha256"
    ];
    private static readonly string[] ReferenceFields =
    [
        "schemaVersion", "candidateKind", "artifactName", "packageId", "packageVersion", "commit", "runId",
        "packageAsset", "packageSha256", "checksumsAsset", "checksumsSha256"
    ];

    public static void WriteMod(
        string directory,
        string artifactName,
        string assemblyName,
        string version,
        string commit,
        long runId)
    {
        ValidateAssemblyName(assemblyName);
        ValidateCommon("Mod", artifactName, assemblyName, version, commit, runId);
        string playerAsset = $"{assemblyName}-v{version}.zip";
        string symbolsAsset = $"{assemblyName}-v{version}-symbols.zip";
        string checksumsAsset = "SHA256SUMS.txt";
        string root = ValidateDirectorySet(directory, [playerAsset, symbolsAsset, checksumsAsset], allowCandidate: true);
        ModPackager.VerifyChecksums(Path.Combine(root, checksumsAsset), [playerAsset, symbolsAsset], root);
        ModCandidate candidate = new(
            1, "Mod", artifactName, assemblyName, version, commit, runId,
            playerAsset, ModPackager.Sha256(Path.Combine(root, playerAsset)),
            symbolsAsset, ModPackager.Sha256(Path.Combine(root, symbolsAsset)),
            checksumsAsset, ModPackager.Sha256(Path.Combine(root, checksumsAsset)));
        WriteAtomic(Path.Combine(root, "candidate.json"), Serialize(candidate));
        Verify(root, "Mod", commit, runId, artifactName);
    }

    public static void WriteReference(
        string directory,
        string artifactName,
        string packageId,
        string packageVersion,
        string commit,
        long runId)
    {
        if (packageId != DeterministicNupkgWriter.PackageId)
            throw new InvalidDataException($"Reference package ID must be {DeterministicNupkgWriter.PackageId}.");
        ValidateCommon("Reference", artifactName, packageId, packageVersion, commit, runId);
        string packageAsset = $"{packageId}.{packageVersion}.nupkg";
        string checksumsAsset = "SHA256SUMS.txt";
        string root = ValidateDirectorySet(directory, [packageAsset, checksumsAsset], allowCandidate: true);
        ModPackager.VerifyChecksums(Path.Combine(root, checksumsAsset), [packageAsset], root);
        ReferenceCandidate candidate = new(
            1, "Reference", artifactName, packageId, packageVersion, commit, runId,
            packageAsset, ModPackager.Sha256(Path.Combine(root, packageAsset)),
            checksumsAsset, ModPackager.Sha256(Path.Combine(root, checksumsAsset)));
        WriteAtomic(Path.Combine(root, "candidate.json"), Serialize(candidate));
        Verify(root, "Reference", commit, runId, artifactName);
    }

    public static void Verify(
        string directory,
        string kind,
        string expectedCommit,
        long expectedRunId,
        string expectedArtifactName)
    {
        ValidateKind(kind);
        string root = Path.GetFullPath(directory);
        SafePath.AssertDirectoryTreeHasNoReparsePoint(root, "Candidate directory");
        string candidatePath = Path.Combine(root, "candidate.json");
        SafePath.AssertRegularFile(candidatePath, "Candidate metadata");
        string declaredKind = ReadDeclaredKind(candidatePath);
        if (declaredKind != kind)
            throw new InvalidDataException($"Candidate kind mismatch: expected {kind}, found {declaredKind}.");

        if (kind == "Mod")
        {
            ModCandidate candidate = ReadMod(candidatePath);
            AssertExpected(candidate.Commit, candidate.RunId, candidate.ArtifactName, expectedCommit, expectedRunId, expectedArtifactName);
            string validatedRoot = ValidateDirectorySet(
                root,
                [candidate.PlayerAsset, candidate.SymbolsAsset, candidate.ChecksumsAsset],
                allowCandidate: true);
            AssertAssetHash(validatedRoot, candidate.PlayerAsset, candidate.PlayerSha256);
            AssertAssetHash(validatedRoot, candidate.SymbolsAsset, candidate.SymbolsSha256);
            ModPackager.VerifyChecksums(Path.Combine(validatedRoot, candidate.ChecksumsAsset), [candidate.PlayerAsset, candidate.SymbolsAsset], validatedRoot);
            AssertAssetHash(validatedRoot, candidate.ChecksumsAsset, candidate.ChecksumsSha256);
        }
        else
        {
            ReferenceCandidate candidate = ReadReference(candidatePath);
            AssertExpected(candidate.Commit, candidate.RunId, candidate.ArtifactName, expectedCommit, expectedRunId, expectedArtifactName);
            string validatedRoot = ValidateDirectorySet(
                root,
                [candidate.PackageAsset, candidate.ChecksumsAsset],
                allowCandidate: true);
            AssertAssetHash(validatedRoot, candidate.PackageAsset, candidate.PackageSha256);
            ModPackager.VerifyChecksums(Path.Combine(validatedRoot, candidate.ChecksumsAsset), [candidate.PackageAsset], validatedRoot);
            AssertAssetHash(validatedRoot, candidate.ChecksumsAsset, candidate.ChecksumsSha256);
        }
    }

    public static void VerifyPublished(string publishedDirectory, string candidateDirectory, string kind)
    {
        ValidateKind(kind);
        string candidateRoot = Path.GetFullPath(candidateDirectory);
        string candidatePath = Path.Combine(candidateRoot, "candidate.json");
        SafePath.AssertRegularFile(candidatePath, "Candidate metadata");
        string declaredKind = ReadDeclaredKind(candidatePath);
        if (declaredKind != kind)
            throw new InvalidDataException("Published verification candidate kind mismatch.");

        IReadOnlyList<string> assets;
        if (kind == "Mod")
        {
            ModCandidate candidate = ReadMod(candidatePath);
            Verify(candidateRoot, kind, candidate.Commit, candidate.RunId, candidate.ArtifactName);
            assets = [candidate.PlayerAsset, candidate.SymbolsAsset, candidate.ChecksumsAsset];
        }
        else
        {
            ReferenceCandidate candidate = ReadReference(candidatePath);
            Verify(candidateRoot, kind, candidate.Commit, candidate.RunId, candidate.ArtifactName);
            assets = [candidate.PackageAsset, candidate.ChecksumsAsset];
        }

        string publishedRoot = ValidateDirectorySet(publishedDirectory, assets, allowCandidate: false);
        foreach (string asset in assets)
        {
            string candidateAsset = Path.Combine(candidateRoot, asset);
            string publishedAsset = Path.Combine(publishedRoot, asset);
            if (!FilesAreByteIdentical(candidateAsset, publishedAsset))
                throw new InvalidDataException($"Published asset is not byte-identical to the candidate: {asset}");
        }
    }

    private static ModCandidate ReadMod(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using JsonDocument document = StrictJson.Parse(stream, path);
        Dictionary<string, JsonElement> values = StrictJson.ReadClosedObject(document.RootElement, ModFields, "Mod candidate metadata");
        ModCandidate candidate = new(
            StrictJson.ReadCanonicalOne(values, "schemaVersion", "Mod candidate metadata"),
            StrictJson.ReadString(values, "candidateKind", "Mod candidate metadata"),
            StrictJson.ReadString(values, "artifactName", "Mod candidate metadata"),
            StrictJson.ReadString(values, "assemblyName", "Mod candidate metadata"),
            StrictJson.ReadString(values, "version", "Mod candidate metadata"),
            StrictJson.ReadString(values, "commit", "Mod candidate metadata"),
            StrictJson.ReadPositiveInt64(values, "runId", "Mod candidate metadata"),
            StrictJson.ReadString(values, "playerAsset", "Mod candidate metadata"),
            StrictJson.ReadString(values, "playerSha256", "Mod candidate metadata"),
            StrictJson.ReadString(values, "symbolsAsset", "Mod candidate metadata"),
            StrictJson.ReadString(values, "symbolsSha256", "Mod candidate metadata"),
            StrictJson.ReadString(values, "checksumsAsset", "Mod candidate metadata"),
            StrictJson.ReadString(values, "checksumsSha256", "Mod candidate metadata"));
        ValidateAssemblyName(candidate.AssemblyName);
        ValidateCommon(candidate.CandidateKind, candidate.ArtifactName, candidate.AssemblyName, candidate.Version, candidate.Commit, candidate.RunId);
        if (candidate.CandidateKind != "Mod" || candidate.PlayerAsset != $"{candidate.AssemblyName}-v{candidate.Version}.zip" ||
            candidate.SymbolsAsset != $"{candidate.AssemblyName}-v{candidate.Version}-symbols.zip" || candidate.ChecksumsAsset != "SHA256SUMS.txt")
            throw new InvalidDataException("Mod candidate asset identity is noncanonical.");
        ValidateHashes(candidate.PlayerSha256, candidate.SymbolsSha256, candidate.ChecksumsSha256);
        return candidate;
    }

    private static ReferenceCandidate ReadReference(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using JsonDocument document = StrictJson.Parse(stream, path);
        Dictionary<string, JsonElement> values = StrictJson.ReadClosedObject(document.RootElement, ReferenceFields, "Reference candidate metadata");
        ReferenceCandidate candidate = new(
            StrictJson.ReadCanonicalOne(values, "schemaVersion", "Reference candidate metadata"),
            StrictJson.ReadString(values, "candidateKind", "Reference candidate metadata"),
            StrictJson.ReadString(values, "artifactName", "Reference candidate metadata"),
            StrictJson.ReadString(values, "packageId", "Reference candidate metadata"),
            StrictJson.ReadString(values, "packageVersion", "Reference candidate metadata"),
            StrictJson.ReadString(values, "commit", "Reference candidate metadata"),
            StrictJson.ReadPositiveInt64(values, "runId", "Reference candidate metadata"),
            StrictJson.ReadString(values, "packageAsset", "Reference candidate metadata"),
            StrictJson.ReadString(values, "packageSha256", "Reference candidate metadata"),
            StrictJson.ReadString(values, "checksumsAsset", "Reference candidate metadata"),
            StrictJson.ReadString(values, "checksumsSha256", "Reference candidate metadata"));
        ValidateCommon(candidate.CandidateKind, candidate.ArtifactName, candidate.PackageId, candidate.PackageVersion, candidate.Commit, candidate.RunId);
        if (candidate.CandidateKind != "Reference" || candidate.PackageId != DeterministicNupkgWriter.PackageId ||
            candidate.PackageAsset != $"{candidate.PackageId}.{candidate.PackageVersion}.nupkg" || candidate.ChecksumsAsset != "SHA256SUMS.txt")
            throw new InvalidDataException("Reference candidate asset identity is noncanonical.");
        ValidateHashes(candidate.PackageSha256, candidate.ChecksumsSha256);
        return candidate;
    }

    private static string ReadDeclaredKind(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using JsonDocument document = StrictJson.Parse(stream, path);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Candidate metadata must be a JSON object.");
        JsonProperty[] matches = document.RootElement.EnumerateObject().Where(property => property.Name == "candidateKind").ToArray();
        if (matches.Length != 1 || matches[0].Value.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Candidate metadata must contain exactly one string candidateKind.");
        return matches[0].Value.GetString()!;
    }

    private static string ValidateDirectorySet(string directory, IReadOnlyCollection<string> assets, bool allowCandidate)
    {
        string root = Path.GetFullPath(directory);
        SafePath.AssertDirectoryTreeHasNoReparsePoint(root, "Candidate asset directory");
        foreach (string asset in assets)
            ValidateAssetName(asset);
        string[] expected = assets.Concat(allowCandidate && File.Exists(Path.Combine(root, "candidate.json")) ? ["candidate.json"] : []).Order(StringComparer.Ordinal).ToArray();
        string[] actual = Directory.EnumerateFileSystemEntries(root).Select(path => Path.GetFileName(path)!).Order(StringComparer.Ordinal).ToArray();
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("Candidate or published directory must contain exactly its closed asset set.");
        foreach (string asset in actual)
            SafePath.AssertRegularFile(Path.Combine(root, asset), $"Candidate asset {asset}");
        return root;
    }

    private static void ValidateCommon(string kind, string artifactName, string identity, string version, string commit, long runId)
    {
        ValidateKind(kind);
        if (!StrictLockJson.IsCanonicalVersion(version))
            throw new InvalidDataException("Candidate version must be canonical stable SemVer.");
        if (!IsLowerHex(commit, 40))
            throw new InvalidDataException("Candidate commit must be lowercase 40-hex.");
        if (runId <= 0)
            throw new InvalidDataException("Candidate runId must be positive.");
        string candidateName = $"{identity}-candidate-{commit}";
        string previewName = $"{identity}-preview-{commit}";
        if (artifactName != candidateName && (kind != "Mod" || artifactName != previewName))
            throw new InvalidDataException("Candidate artifactName must bind its identity, purpose, and commit.");
    }

    private static void ValidateKind(string kind)
    {
        if (kind is not ("Mod" or "Reference"))
            throw new InvalidDataException("Candidate kind must be Mod or Reference.");
    }

    private static void ValidateAssemblyName(string assemblyName)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(assemblyName, "^[A-Za-z_][A-Za-z0-9_.]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new InvalidDataException("Candidate assemblyName is noncanonical.");
    }

    private static void ValidateAssetName(string name)
    {
        if (string.IsNullOrEmpty(name) || Path.GetFileName(name) != name || name.Contains(':') || name.Contains('\\') || name is "." or "..")
            throw new InvalidDataException("Candidate contains an unsafe asset name.");
    }

    private static void ValidateHashes(params string[] hashes)
    {
        if (hashes.Any(hash => !IsLowerHex(hash, 64)))
            throw new InvalidDataException("Candidate SHA-256 values must be lowercase 64-hex.");
    }

    private static void AssertExpected(string commit, long runId, string artifactName, string expectedCommit, long expectedRunId, string expectedArtifactName)
    {
        if (commit != expectedCommit)
            throw new InvalidDataException("Candidate commit differs from the expected commit.");
        if (runId != expectedRunId)
            throw new InvalidDataException("Candidate runId differs from the expected run ID.");
        if (artifactName != expectedArtifactName)
            throw new InvalidDataException("Candidate artifactName differs from the expected artifact name.");
    }

    private static void AssertAssetHash(string root, string name, string expected)
    {
        if (ModPackager.Sha256(Path.Combine(root, name)) != expected)
            throw new InvalidDataException($"Candidate SHA-256 mismatch for {name}.");
    }

    private static bool FilesAreByteIdentical(string leftPath, string rightPath)
    {
        using FileStream left = new(leftPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using FileStream right = new(rightPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (left.Length != right.Length)
            return false;

        Span<byte> leftBuffer = stackalloc byte[8192];
        Span<byte> rightBuffer = stackalloc byte[8192];
        while (true)
        {
            int leftRead = ReadBlock(left, leftBuffer);
            int rightRead = ReadBlock(right, rightBuffer);
            if (leftRead != rightRead || !leftBuffer[..leftRead].SequenceEqual(rightBuffer[..rightRead]))
                return false;
            if (leftRead == 0)
                return true;
        }
    }

    private static int ReadBlock(Stream stream, Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer[total..]);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (!SHA256.HashData(File.ReadAllBytes(temporary)).SequenceEqual(SHA256.HashData(bytes)))
                throw new IOException("Candidate metadata staging verification failed.");
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static byte[] Serialize(ModCandidate candidate)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", candidate.SchemaVersion);
            writer.WriteString("candidateKind", candidate.CandidateKind);
            writer.WriteString("artifactName", candidate.ArtifactName);
            writer.WriteString("assemblyName", candidate.AssemblyName);
            writer.WriteString("version", candidate.Version);
            writer.WriteString("commit", candidate.Commit);
            writer.WriteNumber("runId", candidate.RunId);
            writer.WriteString("playerAsset", candidate.PlayerAsset);
            writer.WriteString("playerSha256", candidate.PlayerSha256);
            writer.WriteString("symbolsAsset", candidate.SymbolsAsset);
            writer.WriteString("symbolsSha256", candidate.SymbolsSha256);
            writer.WriteString("checksumsAsset", candidate.ChecksumsAsset);
            writer.WriteString("checksumsSha256", candidate.ChecksumsSha256);
            writer.WriteEndObject();
        }
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static byte[] Serialize(ReferenceCandidate candidate)
    {
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", candidate.SchemaVersion);
            writer.WriteString("candidateKind", candidate.CandidateKind);
            writer.WriteString("artifactName", candidate.ArtifactName);
            writer.WriteString("packageId", candidate.PackageId);
            writer.WriteString("packageVersion", candidate.PackageVersion);
            writer.WriteString("commit", candidate.Commit);
            writer.WriteNumber("runId", candidate.RunId);
            writer.WriteString("packageAsset", candidate.PackageAsset);
            writer.WriteString("packageSha256", candidate.PackageSha256);
            writer.WriteString("checksumsAsset", candidate.ChecksumsAsset);
            writer.WriteString("checksumsSha256", candidate.ChecksumsSha256);
            writer.WriteEndObject();
        }
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static bool IsLowerHex(string value, int length) => value.Length == length && value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
