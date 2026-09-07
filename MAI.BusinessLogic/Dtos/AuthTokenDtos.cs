using System;
using MAI.Domain.Enums;

namespace MAI.BusinessLogic.Dtos
{
    /// <summary>Corpul cererii POST /api/Auth/refresh și POST /api/Auth/logout.</summary>
    public class RefreshTokenRequestDto
    {
        public string RefreshToken { get; set; } = string.Empty;
    }

    /// <summary>Răspunsul emis de login și de refresh.</summary>
    public class TokenResponseDto
    {
        public Guid Id { get; set; }
        public string Username { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Department { get; set; } = string.Empty;
        public UserRole Role { get; set; }

        /// <summary>Access token JWT, cu viață scurtă.</summary>
        public string AccessToken { get; set; } = string.Empty;

        /// <summary>
        /// Alias pentru AccessToken. Păstrat ca să nu se strice codul de frontend
        /// existent care citește câmpul "token".
        /// </summary>
        public string Token { get; set; } = string.Empty;

        /// <summary>Refresh token opac (base64url). Se trimite o singură dată, la emitere.</summary>
        public string RefreshToken { get; set; } = string.Empty;

        /// <summary>Câte secunde mai este valid access token-ul.</summary>
        public int ExpiresIn { get; set; }

        /// <summary>Momentul expirării access token-ului (UTC, ISO 8601).</summary>
        public DateTime AccessTokenExpiresAt { get; set; }

        /// <summary>Momentul expirării refresh token-ului (UTC, ISO 8601).</summary>
        public DateTime RefreshTokenExpiresAt { get; set; }
    }
}