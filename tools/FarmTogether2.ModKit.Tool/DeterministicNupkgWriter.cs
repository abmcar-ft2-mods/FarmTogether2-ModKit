using System.IO.Compression;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using NuGet.Packaging;

namespace FarmTogether2.ModKit.Tool;

internal static class DeterministicNupkgWriter
{
    internal const string PackageId = "FarmTogether2.GameApi.Ref";
    private const string TargetFramework = "net6.0";
    private static readonly DateTimeOffset FixedTimestamp = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly IReadOnlyDictionary<string, Version> ExpectedAssemblies =
        new Dictionary<string, Version>(StringComparer.Ordinal)
        {
            ["Assembly-CSharp"] = new(0, 0, 0, 0),
            ["Il2Cppmscorlib"] = new(4, 0, 0, 0),
            ["MilkstoneUnityExtensions"] = new(1, 0, 0, 0),
            ["UnityEngine.CoreModule"] = new(0, 0, 0, 0),
            ["UnityEngine.IMGUIModule"] = new(0, 0, 0, 0),
            ["UnityEngine.InputLegacyModule"] = new(0, 0, 0, 0),
            ["UnityEngine.TextRenderingModule"] = new(0, 0, 0, 0)
        };

    public static void Write(string outputPath, string packageId, string version, IReadOnlyList<string> assemblyPaths)
    {
        if (packageId != PackageId)
            throw new InvalidDataException($"Package ID must be {PackageId}.");
        if (!StrictLockJson.IsCanonicalVersion(version))
            throw new InvalidDataException("Package version must be canonical stable SemVer.");

        string output = Path.GetFullPath(outputPath);
        string? parent = Path.GetDirectoryName(output);
        if (string.IsNullOrEmpty(parent))
            throw new DirectoryNotFoundException($"Package output parent does not exist: {parent}");
        SafePath.AssertDirectory(parent, "Package output parent");
        if (Directory.Exists(output))
            throw new InvalidDataException($"Package output must not be a directory: {output}");
        if (File.Exists(output))
            SafePath.AssertRegularFile(output, "Existing package output");
        if (Path.GetFileName(output) != $"{packageId}.{version}.nupkg")
            throw new InvalidDataException("Package output filename must exactly match package ID and version.");

        SortedDictionary<string, byte[]> assemblies = LoadAssemblies(assemblyPaths);
        SortedDictionary<string, byte[]> entries = BuildEntries(packageId, version, assemblies);
        string temporary = Path.Combine(parent, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.preparing");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8))
                {
                    foreach ((string path, byte[] bytes) in entries)
                    {
                        ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
                        entry.LastWriteTime = FixedTimestamp;
                        entry.ExternalAttributes = 0;
                        using Stream target = entry.Open();
                        target.Write(bytes);
                    }
                }
                stream.Flush(flushToDisk: true);
            }
            VerifyPackage(temporary, packageId, version);
            SafePath.AssertDirectory(parent, "Package output parent");
            if (File.Exists(output))
                SafePath.AssertRegularFile(output, "Existing package output");
            File.Move(temporary, output, overwrite: true);
            VerifyPackage(output, packageId, version);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    public static void VerifyExisting(string packagePath, string packageId, string version)
    {
        if (packageId != PackageId)
            throw new InvalidDataException($"Package ID must be {PackageId}.");
        if (!StrictLockJson.IsCanonicalVersion(version))
            throw new InvalidDataException("Package version must be canonical stable SemVer.");
        string path = Path.GetFullPath(packagePath);
        SafePath.AssertRegularFile(path, "Reference package");
        VerifyPackage(path, packageId, version);
    }

    private static SortedDictionary<string, byte[]> LoadAssemblies(IReadOnlyList<string> paths)
    {
        if (paths.Count != ExpectedAssemblies.Count)
            throw new InvalidDataException($"Exactly {ExpectedAssemblies.Count} stub assemblies are required.");
        SortedDictionary<string, byte[]> result = new(StringComparer.Ordinal);
        foreach (string suppliedPath in paths)
        {
            string path = Path.GetFullPath(suppliedPath);
            SafePath.AssertRegularFile(path, "Stub assembly");
            string name = Path.GetFileNameWithoutExtension(path);
            if (Path.GetExtension(path) != ".dll" || !ExpectedAssemblies.TryGetValue(name, out Version? expectedVersion))
                throw new InvalidDataException($"Unexpected stub assembly filename: {Path.GetFileName(path)}");
            if (result.ContainsKey(name))
                throw new InvalidDataException($"Duplicate stub assembly: {name}");

            AssemblyName identity = AssemblyName.GetAssemblyName(path);
            if ((identity.GetPublicKeyToken()?.Length ?? 0) != 0)
                throw new InvalidDataException($"Stub assembly identity contains a public key: {path}");
            if (identity.Name != name || identity.Version != expectedVersion ||
                !string.IsNullOrEmpty(identity.CultureName))
            {
                throw new InvalidDataException($"Stub assembly identity is noncanonical: {path}");
            }
            byte[] bytes = File.ReadAllBytes(path);
            SafePath.AssertRegularFile(path, "Stub assembly");
            AssemblyName identityAfterRead = AssemblyName.GetAssemblyName(path);
            using FileStream verificationStream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            byte[] verificationHash = SHA256.HashData(verificationStream);
            if (identityAfterRead.FullName != identity.FullName ||
                !verificationHash.SequenceEqual(SHA256.HashData(bytes)))
            {
                throw new IOException($"Stub assembly changed while it was read: {path}");
            }
            result.Add(name, bytes);
        }
        if (!result.Keys.SequenceEqual(ExpectedAssemblies.Keys.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Stub assembly allowlist is incomplete.");
        return result;
    }

    private static SortedDictionary<string, byte[]> BuildEntries(
        string packageId,
        string version,
        IReadOnlyDictionary<string, byte[]> assemblies)
    {
        string coreName = LowerSha256($"{packageId}\n{version}\ncore-properties")[..32];
        string corePath = $"package/services/metadata/core-properties/{coreName}.psmdcp";
        string manifestRelationshipId = "R" + UpperSha256($"{packageId}\n{version}\nmanifest")[..16];
        string coreRelationshipId = "R" + UpperSha256($"{packageId}\n{version}\ncore")[..16];
        SortedDictionary<string, byte[]> entries = new(StringComparer.Ordinal)
        {
            [$"{packageId}.nuspec"] = Utf8(Nuspec(packageId, version)),
            ["[Content_Types].xml"] = Utf8(ContentTypes()),
            ["_rels/.rels"] = Utf8(Relationships(packageId, corePath, manifestRelationshipId, coreRelationshipId)),
            [corePath] = Utf8(CoreProperties(packageId, version))
        };
        foreach ((string name, byte[] bytes) in assemblies)
            entries.Add($"ref/{TargetFramework}/{name}.dll", bytes);
        return entries;
    }

    private static void VerifyPackage(string path, string packageId, string version)
    {
        byte[] packageHashBeforeRead = HashFile(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using PackageArchiveReader reader = new(stream, leaveStreamOpen: false);
        if (reader.IsSignedAsync(CancellationToken.None).GetAwaiter().GetResult())
            throw new InvalidDataException("Reference package must not be signed.");
        NuGet.Packaging.Core.PackageIdentity identity = reader.GetIdentity();
        if (identity.Id != packageId || identity.Version.ToNormalizedString() != version)
            throw new InvalidDataException("Reference package identity mismatch.");
        string[] files = reader.GetFiles().Order(StringComparer.Ordinal).ToArray();
        string[] referenceFiles = files.Where(file => file.StartsWith($"ref/{TargetFramework}/", StringComparison.Ordinal)).ToArray();
        string[] expected = ExpectedAssemblies.Keys
            .Select(name => $"ref/{TargetFramework}/{name}.dll")
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (!referenceFiles.SequenceEqual(expected, StringComparer.Ordinal))
            throw new InvalidDataException("Reference package does not contain the closed seven-DLL boundary.");
        if (files.Any(file =>
                file.StartsWith("lib/", StringComparison.Ordinal) ||
                file.StartsWith("runtimes/", StringComparison.Ordinal) ||
                file.StartsWith("tools/", StringComparison.Ordinal) ||
                file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                (file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) &&
                 file != "[Content_Types].xml")))
        {
            throw new InvalidDataException("Reference package contains a forbidden asset path.");
        }

        using ZipArchive archive = new(File.OpenRead(path), ZipArchiveMode.Read, leaveOpen: false, Encoding.UTF8);
        string[] entryNames = archive.Entries.Select(entry => entry.FullName).ToArray();
        string coreName = LowerSha256($"{packageId}\n{version}\ncore-properties")[..32];
        string[] expectedEntries =
        [
            $"{packageId}.nuspec",
            "[Content_Types].xml",
            "_rels/.rels",
            $"package/services/metadata/core-properties/{coreName}.psmdcp",
            .. ExpectedAssemblies.Keys.Select(name => $"ref/{TargetFramework}/{name}.dll")
        ];
        Array.Sort(expectedEntries, StringComparer.Ordinal);
        if (!entryNames.SequenceEqual(expectedEntries, StringComparer.Ordinal))
            throw new InvalidDataException("Reference package ZIP entries do not match the closed package layout.");
        if (!entryNames.SequenceEqual(entryNames.Order(StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidDataException("Reference package entries are not in ordinal order.");
        ZipArchiveEntry? noncanonical = archive.Entries.FirstOrDefault(entry =>
            entry.LastWriteTime.DateTime != FixedTimestamp.DateTime || entry.ExternalAttributes != 0);
        if (noncanonical is not null)
        {
            throw new InvalidDataException(
                $"Reference package ZIP metadata is noncanonical: {noncanonical.FullName} " +
                $"timestamp={noncanonical.LastWriteTime:O} attributes={noncanonical.ExternalAttributes}.");
        }
        if (entryNames.Distinct(StringComparer.Ordinal).Count() != entryNames.Length ||
            entryNames.Any(IsUnsafePackagePath))
            throw new InvalidDataException("Reference package contains duplicate or unsafe paths.");
        foreach ((string name, Version expectedVersion) in ExpectedAssemblies)
        {
            string entryPath = $"ref/{TargetFramework}/{name}.dll";
            ZipArchiveEntry entry = archive.GetEntry(entryPath)
                ?? throw new InvalidDataException($"Reference package assembly is missing: {entryPath}");
            VerifyAssemblyEntry(entry, name, expectedVersion);
        }
        if (!packageHashBeforeRead.SequenceEqual(HashFile(path)))
            throw new IOException("Reference package changed while it was read.");
    }

    private static void VerifyAssemblyEntry(ZipArchiveEntry entry, string expectedName, Version expectedVersion)
    {
        byte[] firstBytes = ReadEntryBytes(entry);
        AssemblyIdentity firstIdentity = ReadAssemblyIdentity(firstBytes, entry.FullName);
        byte[] secondBytes = ReadEntryBytes(entry);
        AssemblyIdentity secondIdentity = ReadAssemblyIdentity(secondBytes, entry.FullName);
        if (!firstBytes.SequenceEqual(secondBytes) || firstIdentity != secondIdentity)
            throw new IOException($"Reference package assembly changed while it was read: {entry.FullName}");
        if (firstIdentity.Name != expectedName || firstIdentity.Version != expectedVersion ||
            firstIdentity.HasPublicKey || !string.IsNullOrEmpty(firstIdentity.Culture))
        {
            throw new InvalidDataException(
                $"Reference package assembly identity is noncanonical: {entry.FullName}");
        }
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry)
    {
        using Stream source = entry.Open();
        using MemoryStream destination = new();
        source.CopyTo(destination);
        return destination.ToArray();
    }

    private static AssemblyIdentity ReadAssemblyIdentity(byte[] bytes, string label)
    {
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            using PEReader peReader = new(stream);
            if (!peReader.HasMetadata)
                throw new BadImageFormatException("The PE image has no metadata.");
            MetadataReader metadata = peReader.GetMetadataReader();
            if (!metadata.IsAssembly)
                throw new BadImageFormatException("The PE image is not an assembly.");
            AssemblyDefinition definition = metadata.GetAssemblyDefinition();
            string culture = definition.Culture.IsNil ? string.Empty : metadata.GetString(definition.Culture);
            bool hasPublicKey = !definition.PublicKey.IsNil && metadata.GetBlobBytes(definition.PublicKey).Length != 0;
            hasPublicKey |= (definition.Flags & AssemblyFlags.PublicKey) != 0;
            return new AssemblyIdentity(
                metadata.GetString(definition.Name),
                definition.Version,
                culture,
                hasPublicKey);
        }
        catch (Exception exception) when (exception is BadImageFormatException or InvalidOperationException)
        {
            throw new InvalidDataException($"Reference package assembly is invalid: {label}", exception);
        }
    }

    private static byte[] HashFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return SHA256.HashData(stream);
    }

    private static bool IsUnsafePackagePath(string path) =>
        string.IsNullOrEmpty(path) || path.Contains('\\') || path.StartsWith('/') || path.Contains(':') ||
        path.Split('/').Any(segment => segment is "" or "." or "..");

    private static string Nuspec(string packageId, string version) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
          <metadata>
            <id>{packageId}</id>
            <version>{version}</version>
            <authors>Abmcar</authors>
            <requireLicenseAcceptance>false</requireLicenseAcceptance>
            <license type="expression">MIT</license>
            <description>Compile-time references for supported Farm Together 2 mod builds.</description>
          </metadata>
        </package>
        """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    private static string Relationships(string packageId, string corePath, string manifestId, string coreId) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Type="http://schemas.microsoft.com/packaging/2010/07/manifest" Target="/{packageId}.nuspec" Id="{manifestId}" />
          <Relationship Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="/{corePath}" Id="{coreId}" />
        </Relationships>
        """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    private static string ContentTypes() => """
        <?xml version="1.0" encoding="utf-8"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml" />
          <Default Extension="psmdcp" ContentType="application/vnd.openxmlformats-package.core-properties+xml" />
          <Default Extension="dll" ContentType="application/octet" />
          <Default Extension="nuspec" ContentType="application/octet" />
        </Types>
        """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    private static string CoreProperties(string packageId, string version) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <coreProperties xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:dcterms="http://purl.org/dc/terms/" xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns="http://schemas.openxmlformats.org/package/2006/metadata/core-properties">
          <dc:creator>Abmcar</dc:creator>
          <dc:description>Compile-time references for supported Farm Together 2 mod builds.</dc:description>
          <dc:identifier>{packageId}</dc:identifier>
          <version>{version}</version>
          <lastModifiedBy>FarmTogether2.ModKit.Tool</lastModifiedBy>
          <dcterms:created xsi:type="dcterms:W3CDTF">2000-01-01T00:00:00Z</dcterms:created>
          <dcterms:modified xsi:type="dcterms:W3CDTF">2000-01-01T00:00:00Z</dcterms:modified>
        </coreProperties>
        """.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n";

    private static byte[] Utf8(string value) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(value);
    private static string LowerSha256(string value) => Convert.ToHexString(SHA256.HashData(Utf8(value))).ToLowerInvariant();
    private static string UpperSha256(string value) => Convert.ToHexString(SHA256.HashData(Utf8(value)));

    private readonly record struct AssemblyIdentity(
        string Name,
        Version Version,
        string Culture,
        bool HasPublicKey);
}
