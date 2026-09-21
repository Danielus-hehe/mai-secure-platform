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

        /// <summary>
        /// Parola contului a fost stabilită de un administrator (cont nou sau
        /// resetare). Frontend-ul cere schimbarea ei înaintea oricărei alte
        /// acțiuni, iar serverul refuză înregistrarea cheilor E2EE până atunci.
        /// </summary>
        public bool MustChangePassword { get; set; }

        /// <summary>
        /// Rolul contului cere 2FA (TwoFactor:RequiredForPrivilegedRoles), dar
        /// sesiunea nu a fost deschisă cu al doilea factor. Endpointurile
        /// privilegiate răspund 403 până la activarea 2FA și o nouă autentificare.
        /// </summary>
        public bool MfaEnrollmentRequired { get; set; }

        /// <summary>
        /// Cine a verificat parola: baza noastră (Local) sau domeniul (Ldap).
        /// Frontend-ul ascunde formularul de schimbare a parolei pentru
        /// conturile de domeniu - acolo parola se schimbă în AD.
        /// </summary>
        public AuthProvider AuthProvider { get; set; } = Domain.Enums.AuthProvider.Local;

        /// <summary>
        /// Parola din domeniu s-a schimbat după ultima împachetare a cheilor
        /// private E2EE, deci blobul nu se mai poate descuia cu parola de azi.
        /// Frontend-ul cere o singură dată parola veche și reîmpachetează.
        ///
        /// Fără semnalul acesta, utilizatorul ar vedea doar „parolă greșită” la
        /// descuierea cheilor, imediat după un login reușit.
        /// </summary>
        public bool KeyRewrapRequired { get; set; }
    }
}