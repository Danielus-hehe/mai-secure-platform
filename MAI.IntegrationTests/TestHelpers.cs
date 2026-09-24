using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MAI.BusinessLogic.Dtos;
using MAI.BusinessLogic.Interfaces;
using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;

namespace MAI.IntegrationTests;

/// <summary>
/// Extensii pentru popularea bazei de test si autentificarea utilizatorilor.
///
/// Toate metodele sunt deterministe: fiecare test isi creeaza utilizatorii
/// sai, cu nume unice (de obicei cu un prefix per test), ca sa nu depinda
/// de ordinea de rulare.
/// </summary>
public static class TestHelpers
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    // ── Seed ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Creeaza un utilizator direct in baza de date, cu parola hash-uita
    /// Argon2id. Returneaza ID-ul. MustChangePassword = false, ca testele
    /// sa poata face login imediat.
    /// </summary>
    public static async Task<User> SeedUserAsync(
        SgdmWebFactory factory,
        string username,
        string password = "TestParola1!",
        UserRole role = UserRole.Utilizator,
        bool withKeys = true,
        string? email = null,
        Guid? orgUnitId = null)
    {
        using var scope = factory.Services.CreateScope();
        var db     = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        var user = new User
        {
            Id                 = Guid.NewGuid(),
            Username           = username,
            Email              = email ?? $"{username}@test.sgdm.local",
            PasswordHash       = await hasher.HashPasswordAsync(password),
            FullName           = username.Replace('.', ' '),
            Role               = role,
            IsActive           = true,
            MustChangePassword = false,
            EmailConfirmed     = true,
            OrgUnitId          = orgUnitId,
        };

        if (withKeys)
        {
            // Chei false dar valide ca structura: testele nu fac criptografie
            // reala, dar controllerele verifica ca PublicKeyEncryption != null.
            user.PublicKeyEncryption = FakeBase64(512);
            user.PublicKeySigning   = FakeBase64(512);
            user.KeysCreatedAt     = DateTime.UtcNow;
        }

        db.Users.Add(user);
        await db.SaveChangesAsync();

        return user;
    }

    /// <summary>
    /// Face login prin POST /api/Auth/login si pune tokenul pe client.
    /// Returneaza raspunsul deserializat (contine ID-ul, rolul etc.).
    /// </summary>
    public static async Task<TokenResponseDto> LoginAsync(
        HttpClient client, string username, string password = "TestParola1!")
    {
        var response = await client.PostAsJsonAsync("/api/Auth/login", new
        {
            username,
            password,
        });

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new HttpRequestException(
                $"Login esuat pentru '{username}': {response.StatusCode} - {body}");
        }

        var dto = await response.Content.ReadFromJsonAsync<TokenResponseDto>(JsonOpts);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", dto!.AccessToken);
        return dto;
    }

    // ── Transfer helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Construieste un request multipart/form-data care imita ce face
    /// browserul la trimiterea unui fisier criptat. Continutul e random
    /// (simulare cifrotext); plicul criptografic e sintetic dar valid
    /// structural.
    /// </summary>
    /// <remarks>
    /// Intoarce Task, desi nu are nimic de asteptat: semnatura ramane cea
    /// folosita de toate testele existente (await BuildUploadContent(...)),
    /// iar Task.FromResult evita avertismentul CS1998 al unei metode async
    /// fara await.
    /// </remarks>
    public static Task<MultipartFormDataContent> BuildUploadContent(
        Guid recipientId,
        string fileName = "raport.pdf.enc",
        int fileSize = 1024,
        TransferCategory category = TransferCategory.General,
        bool allowForward = false,
        DateTime? expiresAt = null,
        List<(Guid userId, string wrappedKey)>? extraRecipients = null)
    {
        var ciphertext = RandomNumberGenerator.GetBytes(fileSize);
        var sha256Hex  = Convert.ToHexString(SHA256.HashData(ciphertext)).ToLowerInvariant();

        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(ciphertext), "File", fileName);
        content.Add(new StringContent(fileName), "FileName");
        content.Add(new StringContent(fileSize.ToString()), "PlaintextSize");
        content.Add(new StringContent(((int)category).ToString()), "Category");
        content.Add(new StringContent(allowForward.ToString()), "AllowForward");
        content.Add(new StringContent(FakeBase64(12)), "Iv");
        content.Add(new StringContent(FakeBase64(384)), "EncryptedKeyForSender");
        content.Add(new StringContent(FakeBase64(384)), "Signature");
        content.Add(new StringContent(sha256Hex), "CiphertextSha256");
        content.Add(new StringContent("AES-256-GCM+RSA-OAEP-3072+RSA-PSS-3072"), "Suite");

        // Lista de destinatari
        var recipients = new List<(Guid userId, string wrappedKey)>
        {
            (recipientId, FakeBase64(384)),
        };

        if (extraRecipients is not null)
            recipients.AddRange(extraRecipients);

        for (int i = 0; i < recipients.Count; i++)
        {
            content.Add(new StringContent(recipients[i].userId.ToString()), $"Recipients[{i}].UserId");
            content.Add(new StringContent(recipients[i].wrappedKey), $"Recipients[{i}].EncryptedKeyForUser");
        }

        if (expiresAt.HasValue)
            content.Add(new StringContent(expiresAt.Value.ToString("o")), "ExpiresAt");

        return Task.FromResult(content);
    }

    // ── Utilitare ─────────────────────────────────────────────────────────

    /// <summary>Base64 fals dar valid structural, de lungime aproximativa.</summary>
    public static string FakeBase64(int bytes)
    {
        var raw = RandomNumberGenerator.GetBytes(bytes);
        return Convert.ToBase64String(raw);
    }

    /// <summary>Deserializeaza un camp dintr-un raspuns JSON.</summary>
    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonSerializer.Deserialize<JsonElement>(text, JsonOpts);
    }
}
