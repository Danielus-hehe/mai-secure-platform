using System.Security.Cryptography;
using System.Text;
using MAI.Api.Configuration;
using MAI.BusinessLogic.Interfaces;
using MAI.BusinessLogic.Storage;
using MAI.BusinessLogic.Storage.Encryption;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace MAI.Tests;

/// <summary>Utilitare comune testelor de criptare a depozitului.</summary>
internal static class Enc
{
    public const int Chunk = 4096;   // minimul formatului: multe segmente cu fișiere mici

    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static MasterKeyRing Ring(string id = "k1") => MasterKeyRing.Parse($"{id}:{NewKey()}", id);

    public static byte[] Bytes(int length)
    {
        var data = new byte[length];
        new Random(length).NextBytes(data);
        return data;
    }

    public static async Task<byte[]> EncryptAsync(byte[] plain, MasterKeyRing ring, int chunk = Chunk)
    {
        using var output = new MemoryStream();
        await StorageEnvelope.EncryptAsync(new MemoryStream(plain), output, ring, chunk);
        return output.ToArray();
    }

    public static async Task<byte[]> DecryptAsync(byte[] stored, MasterKeyRing? ring, bool allowPlaintext = false)
    {
        var opened = await StorageEnvelope.OpenAsync(new MemoryStream(stored), ring, allowPlaintext);
        await using var stream = opened.Stream;
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    public static StorageEncryptionOptions Options(bool allowPlaintext = true, bool verify = true) => new()
    {
        Enabled               = true,
        ActiveKeyId           = "k1",
        MasterKeys            = "configurat-in-test",
        ChunkSizeKb           = 4,
        AllowPlaintextRead    = allowPlaintext,
        VerifyBeforeStreaming = verify,
    };
}

/// <summary>Depozit în memorie: octeții exact cum sunt scriși, fără nicio transformare.</summary>
internal sealed class InMemoryStorage : IFileStorage
{
    public Dictionary<string, byte[]> Objects { get; } = new(StringComparer.Ordinal);

    public string ProviderName => "Memorie";
    public bool SupportsPresignedUrls => true;

    public async Task PutAsync(string key, Stream content, long contentLength, string contentType, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct);
        Assert.Equal(contentLength, buffer.Length);   // Content-Length anunțat trebuie să fie cel real
        Objects[key] = buffer.ToArray();
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) =>
        Objects.TryGetValue(key, out var data)
            ? Task.FromResult<Stream>(new MemoryStream(data, writable: false))
            : throw new FileNotFoundException(key);

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) => Task.FromResult(Objects.ContainsKey(key));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        Objects.Remove(key);
        return Task.CompletedTask;
    }

    public Task<string?> TryCreatePresignedDownloadUrlAsync(string key, TimeSpan lifetime, CancellationToken ct = default) =>
        Task.FromResult<string?>($"http://depozit/{key}");

    public Task<bool> HealthCheckAsync(CancellationToken ct = default) => Task.FromResult(true);
}

// ═════════════════════════════════════════════════════════════════════════════
// Formatul: criptare, decriptare, integritate
// ═════════════════════════════════════════════════════════════════════════════

public class StorageEnvelopeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(Enc.Chunk - 1)]
    [InlineData(Enc.Chunk)]              // multiplu exact: urmează un segment final gol
    [InlineData(Enc.Chunk + 1)]
    [InlineData(3 * Enc.Chunk)]
    [InlineData(100_000)]
    public async Task Criptare_Decriptare_RestituieExactOriginalul(int length)
    {
        var ring  = Enc.Ring();
        var plain = Enc.Bytes(length);

        var stored = await Enc.EncryptAsync(plain, ring);

        Assert.Equal(StorageEnvelope.GetEncryptedLength(length, "k1".Length, Enc.Chunk), stored.Length);
        Assert.Equal(plain, await Enc.DecryptAsync(stored, ring));
    }

    [Fact]
    public async Task Decriptare_CitireSincrona_FunctioneazaLaFel()
    {
        var ring  = Enc.Ring();
        var plain = Enc.Bytes(3 * Enc.Chunk + 17);
        var stored = await Enc.EncryptAsync(plain, ring);

        var opened = await StorageEnvelope.OpenAsync(new MemoryStream(stored), ring, allowPlaintext: false);
        using var stream = opened.Stream;
        using var output = new MemoryStream();
        stream.CopyTo(output);

        Assert.Equal(plain, output.ToArray());
    }

    [Fact]
    public async Task Cifrotext_IncepeCuMagic_SiNuContineTextulInClar()
    {
        var plain  = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("ORDIN SECRET nr. 142/2026. ", 50)));
        var stored = await Enc.EncryptAsync(plain, Enc.Ring());

        Assert.True(StorageEnvelope.HasMagic(stored));
        Assert.DoesNotContain("ORDIN SECRET", Encoding.UTF8.GetString(stored));
    }

    [Fact]
    public async Task AcelasiContinut_DeDouaOri_DaCifrotexteDiferite()
    {
        // Cheie de fișier nouă la fiecare criptare: două documente identice nu se
        // pot recunoaște ca identice privind doar depozitul.
        var ring  = Enc.Ring();
        var plain = Enc.Bytes(5000);

        Assert.NotEqual(await Enc.EncryptAsync(plain, ring), await Enc.EncryptAsync(plain, ring));
    }

    [Theory]
    [InlineData(0.10)]
    [InlineData(0.50)]
    [InlineData(0.99)]
    public async Task OctetModificatInCorp_EsteDetectat(double position)
    {
        var ring   = Enc.Ring();
        var stored = await Enc.EncryptAsync(Enc.Bytes(3 * Enc.Chunk + 100), ring);

        var header = StorageEnvelope.HeaderLength("k1".Length);
        var index  = header + (int)((stored.Length - header - 1) * position);
        stored[index] ^= 0x01;

        await Assert.ThrowsAnyAsync<CryptographicException>(() => Enc.DecryptAsync(stored, ring));
    }

    [Fact]
    public async Task UltimulOctetTaiat_EsteDetectat()
    {
        var ring   = Enc.Ring();
        var stored = await Enc.EncryptAsync(Enc.Bytes(2 * Enc.Chunk + 10), ring);

        await Assert.ThrowsAnyAsync<CryptographicException>(() => Enc.DecryptAsync(stored[..^1], ring));
    }

    [Fact]
    public async Task TaiatExactLaGranitaUnuiSegment_EsteDetectat()
    {
        // 2 segmente pline + segmentul final gol (doar eticheta). Tăiat fără
        // segmentul final, restul ar decripta corect segment cu segment - doar
        // marcajul „ultimul” din nonce arată că lipsește ceva.
        var ring   = Enc.Ring();
        var stored = await Enc.EncryptAsync(Enc.Bytes(2 * Enc.Chunk), ring);

        await Assert.ThrowsAnyAsync<CryptographicException>(
            () => Enc.DecryptAsync(stored[..^StorageEnvelope.TagSize], ring));
    }

    [Fact]
    public async Task SegmenteInversate_SuntDetectate()
    {
        var ring   = Enc.Ring();
        var stored = await Enc.EncryptAsync(Enc.Bytes(3 * Enc.Chunk), ring);

        var header  = StorageEnvelope.HeaderLength("k1".Length);
        var segment = Enc.Chunk + StorageEnvelope.TagSize;
        var first   = stored.AsSpan(header, segment).ToArray();
        stored.AsSpan(header + segment, segment).CopyTo(stored.AsSpan(header, segment));
        first.CopyTo(stored.AsSpan(header + segment, segment));

        await Assert.ThrowsAnyAsync<CryptographicException>(() => Enc.DecryptAsync(stored, ring));
    }

    [Fact]
    public async Task DimensiuneaSegmentuluiModificataInAntet_EsteDetectata()
    {
        var ring   = Enc.Ring();
        var stored = await Enc.EncryptAsync(Enc.Bytes(10_000), ring);

        // Dimensiunea segmentului e imediat după identificatorul cheii.
        var offset = StorageEnvelope.PrefixLength + 1 + "k1".Length;
        stored[offset + 2] ^= 0x20;   // 4096 → 12288, tot în limitele formatului

        await Assert.ThrowsAnyAsync<CryptographicException>(() => Enc.DecryptAsync(stored, ring));
    }

    [Fact]
    public async Task AltaCheiePrincipalaCuAcelasiId_NuDecripteaza()
    {
        var stored = await Enc.EncryptAsync(Enc.Bytes(1000), Enc.Ring("k1"));

        await Assert.ThrowsAnyAsync<CryptographicException>(() => Enc.DecryptAsync(stored, Enc.Ring("k1")));
    }

    [Fact]
    public async Task CheiePrincipalaScoasaDinConfigurare_MesajCuIdentificatorul()
    {
        var stored = await Enc.EncryptAsync(Enc.Bytes(1000), Enc.Ring("vechi"));

        var ex = await Assert.ThrowsAnyAsync<CryptographicException>(() => Enc.DecryptAsync(stored, Enc.Ring("nou")));
        Assert.Contains("vechi", ex.Message);
    }

    [Fact]
    public async Task ObiectInClar_SeCitesteDoarDacaEstePermis()
    {
        var plain = Encoding.UTF8.GetBytes("document vechi, scris inainte de criptare");

        Assert.Equal(plain, await Enc.DecryptAsync(plain, Enc.Ring(), allowPlaintext: true));
        await Assert.ThrowsAsync<PlaintextObjectRejectedException>(() => Enc.DecryptAsync(plain, Enc.Ring()));
    }

    [Fact]
    public async Task ObiectInClarMaiScurtDecatMagicul_SeCitesteIntegral()
    {
        var plain = new byte[] { 1, 2, 3 };
        Assert.Equal(plain, await Enc.DecryptAsync(plain, null, allowPlaintext: true));
    }

    [Fact]
    public async Task ReImpachetare_SchimbaDoarAntetul_CorpulRamaneIdentic()
    {
        var k1 = Enc.NewKey();
        var k2 = Enc.NewKey();
        var vechi = MasterKeyRing.Parse($"k1:{k1}", "k1");
        var tranzitie = MasterKeyRing.Parse($"k1:{k1};k2:{k2}", "k2");
        var doarNoua = MasterKeyRing.Parse($"k2:{k2}", "k2");

        var plain  = Enc.Bytes(3 * Enc.Chunk + 5);
        var stored = await Enc.EncryptAsync(plain, vechi);

        var probe = await StorageEnvelope.ProbeAsync(new MemoryStream(stored));
        var newHeader = StorageEnvelope.RewrapHeader(probe.Header!, tranzitie);
        var body = stored[probe.Header!.Length..];
        var rewrapped = newHeader.Concat(body).ToArray();

        Assert.Equal(plain, await Enc.DecryptAsync(rewrapped, doarNoua));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => Enc.DecryptAsync(rewrapped, vechi));
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Cheile principale și configurarea
// ═════════════════════════════════════════════════════════════════════════════

public class MasterKeyRingTests
{
    [Fact]
    public void Parse_MaiMulteChei_ActivaEsteCeaCeruta()
    {
        var ring = MasterKeyRing.Parse($"k1:{Enc.NewKey()}; k2:{Enc.NewKey()}", "k2");

        Assert.Equal("k2", ring.ActiveKeyId);
        Assert.True(ring.Contains("k1"));
        Assert.Equal(2, ring.KeyIds.Count);
    }

    [Fact]
    public void Parse_OSinguraCheieFaraActiva_EsteFolositaEa()
    {
        Assert.Equal("unic", MasterKeyRing.Parse($"unic:{Enc.NewKey()}", null).ActiveKeyId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("k1")]                                          // fără cheie
    [InlineData("k1:nu-e-base64!")]
    [InlineData("k1:AAAAAAAAAAAAAAAAAAAAAA==")]                 // 16 octeți
    [InlineData("k1:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]  // 32 de zerouri
    [InlineData("id cu spatiu:AAAA")]
    [InlineData("k1:GENERATI_CU_openssl_rand_base64_32")]      // șablonul din .env.example
    public void Parse_ConfigurareInvalida_EsteRefuzata(string spec)
    {
        Assert.Throws<InvalidOperationException>(() => MasterKeyRing.Parse(spec, "k1"));
    }

    [Fact]
    public void Parse_IdentificatorDuplicat_EsteRefuzat()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MasterKeyRing.Parse($"k1:{Enc.NewKey()};k1:{Enc.NewKey()}", "k1"));
    }

    [Fact]
    public void Parse_CheiaActivaInexistenta_EsteRefuzata()
    {
        Assert.Throws<InvalidOperationException>(() => MasterKeyRing.Parse($"k1:{Enc.NewKey()}", "k9"));
    }

    [Fact]
    public void Parse_MaiMulteCheiFaraActiva_EsteRefuzata()
    {
        Assert.Throws<InvalidOperationException>(() =>
            MasterKeyRing.Parse($"k1:{Enc.NewKey()};k2:{Enc.NewKey()}", null));
    }

    [Fact]
    public void Parse_MesajulDeEroare_NuContineCheia()
    {
        var shortKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20));
        var ex = Assert.Throws<InvalidOperationException>(() => MasterKeyRing.Parse($"k1:{shortKey}", "k1"));
        Assert.DoesNotContain(shortKey, ex.Message);
    }

    [Fact]
    public void GenerateEntry_ProduceOCheieAcceptataDeParse()
    {
        var entry = MasterKeyRing.GenerateEntry("k2026");
        Assert.Equal("k2026", MasterKeyRing.Parse(entry, "k2026").ActiveKeyId);
    }

    [Fact]
    public void Options_ActivaFaraChei_RefuzaPornirea()
    {
        var options = new StorageEncryptionOptions { Enabled = true, MasterKeys = "" };
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Options_DezactivataFaraChei_EsteAcceptata()
    {
        new StorageEncryptionOptions { Enabled = false, MasterKeys = "" }.Validate();
    }

    [Theory]
    [InlineData("documents/abc/v1.pdf", true)]
    [InlineData("internal/2026/09/x.pdf", true)]
    [InlineData("transfers/2026/09/x.enc", false)]
    [InlineData("documentsX/y", false)]
    [InlineData("directia-it/2026/09/x.enc", false)]
    public void Options_Prefixe_SeAplicaDoarDocumentelor(string key, bool expected)
    {
        Assert.Equal(expected, new StorageEncryptionOptions().AppliesTo(key));
    }

    [Fact]
    public void Options_PrefixeleSeNormalizeaza()
    {
        var options = new StorageEncryptionOptions { EncryptedPrefixes = " /documents , internal/,documents/ " };
        Assert.Equal(new[] { "documents/", "internal/" }, options.PrefixList.ToArray());
    }

    [Theory]
    [InlineData("STORAGE_ENCRYPTION_ENABLED", "StorageEncryption__Enabled")]
    [InlineData("STORAGE_ENCRYPTION_ACTIVE_KEY", "StorageEncryption__ActiveKeyId")]
    [InlineData("STORAGE_ENCRYPTION_ALLOW_PLAINTEXT", "StorageEncryption__AllowPlaintextRead")]
    public void DotEnv_VariabileleCriptarii_AuAceeasiMapareCaInCompose(string envKey, string configKey)
    {
        Assert.True(DotEnvLoader.ComposeMapping.TryGetValue(envKey, out var targets));
        Assert.Contains(configKey, targets);
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Decoratorul peste depozit
// ═════════════════════════════════════════════════════════════════════════════

public class EncryptingFileStorageTests
{
    private static (EncryptingFileStorage Storage, InMemoryStorage Raw, MasterKeyRing Ring) Create(
        bool allowPlaintext = true, bool verify = true)
    {
        var raw  = new InMemoryStorage();
        var ring = Enc.Ring();
        var storage = new EncryptingFileStorage(
            raw, Enc.Options(allowPlaintext, verify), ring, NullLogger<EncryptingFileStorage>.Instance);
        return (storage, raw, ring);
    }

    private static async Task Put(IFileStorage storage, string key, byte[] data) =>
        await storage.PutAsync(key, new MemoryStream(data), data.Length, "application/pdf");

    private static async Task<byte[]> Read(IFileStorage storage, string key)
    {
        await using var stream = await storage.OpenReadAsync(key);
        using var output = new MemoryStream();
        await stream.CopyToAsync(output);
        return output.ToArray();
    }

    [Theory]
    [InlineData("documents/d1/v1.pdf")]
    [InlineData("internal/2026/09/d2-x.pdf")]
    public async Task Document_InDepozitECifrotext_LaCitireEOriginalul(string key)
    {
        var (storage, raw, _) = Create();
        var plain = Enc.Bytes(20_000);

        await Put(storage, key, plain);

        Assert.True(StorageEnvelope.HasMagic(raw.Objects[key]));
        Assert.NotEqual(plain.Length, raw.Objects[key].Length);
        Assert.Equal(plain, await Read(storage, key));
    }

    [Fact]
    public async Task Transfer_TreceNeatins()
    {
        // Cifrotextul E2EE din browser ajunge în depozit octet cu octet.
        var (storage, raw, _) = Create();
        var e2ee = Enc.Bytes(5000);

        await Put(storage, "transfers/2026/09/t1.enc", e2ee);

        Assert.Equal(e2ee, raw.Objects["transfers/2026/09/t1.enc"]);
    }

    [Fact]
    public async Task UrlPresemnat_NuSeEmitePentruDocumenteCriptate()
    {
        var (storage, _, _) = Create();

        Assert.Null(await storage.TryCreatePresignedDownloadUrlAsync("documents/d1/v1.pdf", TimeSpan.FromMinutes(5)));
        Assert.NotNull(await storage.TryCreatePresignedDownloadUrlAsync("transfers/2026/09/t1.enc", TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task DocumentAlteratInDepozit_EsteRefuzatInainteDeLivrare()
    {
        var (storage, raw, _) = Create();
        await Put(storage, "documents/d1/v1.pdf", Enc.Bytes(3 * Enc.Chunk + 50));

        // Alterare spre finalul fișierului: fără verificarea prealabilă, primele
        // segmente ar fi pornit deja spre browser.
        raw.Objects["documents/d1/v1.pdf"][^20] ^= 0xFF;

        await Assert.ThrowsAnyAsync<CryptographicException>(() => storage.OpenReadAsync("documents/d1/v1.pdf"));
    }

    [Fact]
    public async Task DocumentVechiInClar_SeCitesteCatTimpEPermis()
    {
        var (storage, raw, _) = Create(allowPlaintext: true);
        var legacy = Encoding.UTF8.GetBytes("scris inainte de criptare");
        raw.Objects["documents/vechi/v1.pdf"] = legacy;

        Assert.Equal(legacy, await Read(storage, "documents/vechi/v1.pdf"));
    }

    [Fact]
    public async Task ObiectInClarStrecurat_EsteRefuzatCandCitireaInClarEOprita()
    {
        var (storage, raw, _) = Create(allowPlaintext: false);
        await Put(storage, "documents/d1/v1.pdf", Enc.Bytes(1000));

        // Cineva cu acces la MinIO înlocuiește documentul criptat cu altul, în clar.
        raw.Objects["documents/d1/v1.pdf"] = Encoding.UTF8.GetBytes("document falsificat");

        await Assert.ThrowsAsync<PlaintextObjectRejectedException>(() => storage.OpenReadAsync("documents/d1/v1.pdf"));
    }

    [Fact]
    public async Task FaraVerificarePrealabila_SeCitesteTotCorect()
    {
        var (storage, _, _) = Create(verify: false);
        var plain = Enc.Bytes(3 * Enc.Chunk + 1);

        await Put(storage, "documents/d1/v1.pdf", plain);

        Assert.Equal(plain, await Read(storage, "documents/d1/v1.pdf"));
    }

    [Fact]
    public async Task CriptareDezactivata_ScrieInClar_DarCitesteCeEDejaCriptat()
    {
        var raw  = new InMemoryStorage();
        var ring = Enc.Ring();
        var enabled = new EncryptingFileStorage(raw, Enc.Options(), ring, NullLogger<EncryptingFileStorage>.Instance);

        var old = Enc.Bytes(3000);
        await Put(enabled, "documents/a/v1.pdf", old);

        var disabledOptions = Enc.Options();
        disabledOptions.Enabled = false;
        var disabled = new EncryptingFileStorage(raw, disabledOptions, ring, NullLogger<EncryptingFileStorage>.Instance);

        var fresh = Enc.Bytes(2000);
        await Put(disabled, "documents/b/v1.pdf", fresh);

        Assert.Equal(fresh, raw.Objects["documents/b/v1.pdf"]);
        Assert.Equal(old, await Read(disabled, "documents/a/v1.pdf"));
    }
}

// ═════════════════════════════════════════════════════════════════════════════
// Scriptul de recriptare
// ═════════════════════════════════════════════════════════════════════════════

public class StorageRecryptorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"sgdm-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    /// <summary>Discul local, nu memoria: acolo un fișier deschis nu poate fi rescris.</summary>
    private LocalFileStorage Disk() =>
        new(new StorageOptions { Provider = "Local", LocalRootPath = _root }, NullLogger<LocalFileStorage>.Instance);

    private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    private static async Task<byte[]> RawBytes(IFileStorage storage, string key)
    {
        await using var s = await storage.OpenReadAsync(key);
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms);
        return ms.ToArray();
    }

    private static StorageRecryptor Recryptor(IFileStorage raw, MasterKeyRing ring) =>
        new(raw, ring, Enc.Options(), NullLogger.Instance);

    [Fact]
    public async Task DocumentInClar_EsteCriptat_ApoiARuleazaIdempotent()
    {
        var disk  = Disk();
        var ring  = Enc.Ring();
        var plain = Enc.Bytes(10_000);
        await disk.PutAsync("documents/d1/v1.pdf", new MemoryStream(plain), plain.Length, "application/pdf");

        var item = new RecryptItem("documents/d1/v1.pdf", Sha(plain), "d1 v1");

        var first = await Recryptor(disk, ring).ProcessAsync(item, dryRun: false);
        Assert.Equal(RecryptOutcome.Encrypted, first.Outcome);

        var stored = await RawBytes(disk, "documents/d1/v1.pdf");
        Assert.True(StorageEnvelope.HasMagic(stored));
        Assert.Equal(plain, await Enc.DecryptAsync(stored, ring));

        var second = await Recryptor(disk, ring).ProcessAsync(item, dryRun: false);
        Assert.Equal(RecryptOutcome.AlreadyCurrent, second.Outcome);
    }

    [Fact]
    public async Task AmprentaDiferitaDeBaza_ObiectulRamaneNeatins()
    {
        var raw   = new InMemoryStorage();
        var plain = Enc.Bytes(3000);
        raw.Objects["documents/d1/v1.pdf"] = plain;

        var result = await Recryptor(raw, Enc.Ring())
            .ProcessAsync(new RecryptItem("documents/d1/v1.pdf", Sha(Enc.Bytes(2999)), "d1"), dryRun: false);

        Assert.Equal(RecryptOutcome.ChecksumMismatch, result.Outcome);
        Assert.Equal(plain, raw.Objects["documents/d1/v1.pdf"]);
    }

    [Fact]
    public async Task Simulare_NuScrieNimic()
    {
        var raw   = new InMemoryStorage();
        var plain = Enc.Bytes(3000);
        raw.Objects["internal/2026/09/x.pdf"] = plain;

        var result = await Recryptor(raw, Enc.Ring())
            .ProcessAsync(new RecryptItem("internal/2026/09/x.pdf", Sha(plain), "x"), dryRun: true);

        Assert.Equal(RecryptOutcome.WouldEncrypt, result.Outcome);
        Assert.Equal(plain, raw.Objects["internal/2026/09/x.pdf"]);
    }

    [Fact]
    public async Task Rotire_ReImpacheteaza_IarCheiaVecheNuMaiEsteNecesara()
    {
        var disk = Disk();
        var k1 = Enc.NewKey();
        var k2 = Enc.NewKey();
        var vechi     = MasterKeyRing.Parse($"k1:{k1}", "k1");
        var tranzitie = MasterKeyRing.Parse($"k1:{k1};k2:{k2}", "k2");
        var doarNoua  = MasterKeyRing.Parse($"k2:{k2}", "k2");

        var plain = Enc.Bytes(3 * Enc.Chunk + 7);
        var writer = new EncryptingFileStorage(disk, Enc.Options(), vechi, NullLogger<EncryptingFileStorage>.Instance);
        await writer.PutAsync("documents/d1/v1.pdf", new MemoryStream(plain), plain.Length, "application/pdf");

        var result = await Recryptor(disk, tranzitie)
            .ProcessAsync(new RecryptItem("documents/d1/v1.pdf", Sha(plain), "d1"), dryRun: false);

        Assert.Equal(RecryptOutcome.Rewrapped, result.Outcome);
        Assert.Equal(plain, await Enc.DecryptAsync(await RawBytes(disk, "documents/d1/v1.pdf"), doarNoua));
    }

    [Fact]
    public async Task CheiaVecheLipsa_EsteRaportata_NuStricaObiectul()
    {
        var raw = new InMemoryStorage();
        var stored = await Enc.EncryptAsync(Enc.Bytes(2000), Enc.Ring("disparuta"));
        raw.Objects["documents/d1/v1.pdf"] = stored;

        var result = await Recryptor(raw, Enc.Ring("k1"))
            .ProcessAsync(new RecryptItem("documents/d1/v1.pdf", null, "d1"), dryRun: false);

        Assert.Equal(RecryptOutcome.Failed, result.Outcome);
        Assert.Equal(stored, raw.Objects["documents/d1/v1.pdf"]);
    }

    [Fact]
    public async Task ObiectLipsa_EsteRaportat()
    {
        var result = await Recryptor(new InMemoryStorage(), Enc.Ring())
            .ProcessAsync(new RecryptItem("documents/nu-exista/v1.pdf", null, "?"), dryRun: false);

        Assert.Equal(RecryptOutcome.Missing, result.Outcome);
        Assert.True(result.IsProblem);
    }
}
