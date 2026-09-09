using System.ComponentModel.DataAnnotations;

namespace MAI.BusinessLogic.Dtos
{
    /// <summary>
    /// Corpul cererii POST /api/Auth/2fa/verify — pasul doi al autentificării.
    ///
    /// Mutat aici din interiorul fișierului de controller: un DTO definit lângă
    /// controllerul care îl consumă nu poate fi refolosit de teste sau de alt
    /// endpoint fără să tragă după el tot ASP.NET-ul.
    /// </summary>
    public class TwoFactorVerifyDto
    {
        /// <summary>
        /// Provocarea primită de la /login. Token opac cu stare pe server, nu un JWT:
        /// un JWT „pe jumătate autentificat” riscă mereu să fie acceptat de
        /// middleware-ul de autentificare pe alte rute.
        /// </summary>
        [Required(ErrorMessage = "Sesiunea de autentificare lipsește.")]
        public string ChallengeToken { get; set; } = string.Empty;

        /// <summary>Codul din aplicația de autentificare SAU un cod de recuperare.</summary>
        [Required(ErrorMessage = "Codul de verificare este obligatoriu.")]
        [StringLength(64, MinimumLength = 4, ErrorMessage = "Cod de lungime invalidă.")]
        public string Code { get; set; } = string.Empty;
    }
}
