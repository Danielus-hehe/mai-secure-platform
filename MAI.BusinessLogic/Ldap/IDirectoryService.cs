using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MAI.BusinessLogic.Ldap
{
    /// <summary>
    /// Accesul la Active Directory, în întregime doar-citire.
    ///
    /// Interfața există ca restul aplicației (AuthController, KeysController,
    /// provizionarea conturilor) să nu depindă de
    /// System.DirectoryServices.Protocols și să poată fi testat fără domeniu.
    ///
    /// Namespace-ul se numește „Ldap”, nu „Directory”: un namespace
    /// MAI.BusinessLogic.Directory ar fi ascuns clasa System.IO.Directory în tot
    /// proiectul MAI.BusinessLogic (Directory.CreateDirectory din
    /// LocalFileStorage s-ar fi rezolvat la namespace, nu la clasă).
    /// </summary>
    public interface IDirectoryService
    {
        /// <summary>True dacă autentificarea în domeniu e configurată și activă.</summary>
        bool Enabled { get; }

        /// <summary>
        /// Verifică parola prin bind și, la reușită, întoarce contul citit din AD.
        /// Nu aruncă pentru credențiale greșite sau server indisponibil: acelea
        /// sunt rezultate, nu excepții, și trebuie consemnate în audit.
        /// </summary>
        Task<DirectoryAuthResult> AuthenticateAsync(string username, string password, CancellationToken ct);

        /// <summary>
        /// Verifică doar parola unui cont deja cunoscut, fără a reciti
        /// atributele. Folosit de operațiile care cer dovada parolei
        /// (reîmpachetarea cheilor E2EE), unde contul e deja identificat.
        /// </summary>
        Task<bool> VerifyPasswordAsync(string bindName, string password, CancellationToken ct);

        /// <summary>Caută un cont fără să verifice parola. Necesită cont de serviciu.</summary>
        Task<DirectoryUser?> FindUserAsync(string username, CancellationToken ct);

        /// <summary>Unitățile organizatorice de sub baza configurată, pentru importul structurii.</summary>
        Task<IReadOnlyList<DirectoryOrgUnit>> GetOrganizationalUnitsAsync(CancellationToken ct);

        /// <summary>
        /// Test de conexiune pentru pagina de administrare: răspunde DC-ul, e
        /// acceptat certificatul, funcționează contul de serviciu, câte obiecte
        /// se văd. Întoarce și amprenta certificatului, ca administratorul să o
        /// poată fixa în configurare.
        /// </summary>
        Task<DirectoryProbe> TestConnectionAsync(CancellationToken ct);
    }
}
