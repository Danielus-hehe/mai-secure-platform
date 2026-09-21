using MAI.BusinessLogic.Ldap;
using MAI.BusinessLogic.Interfaces;
using MAI.Domain.Entities;
using MAI.Domain.Enums;

namespace MAI.Api.Services
{
    /// <summary>Rezultatul unei verificări de parolă, indiferent de sursă.</summary>
    public enum CredentialCheck
    {
        Valid = 0,
        Invalid = 1,

        /// <summary>Contul e de domeniu, dar AD-ul nu a răspuns. NU e „parolă greșită”.</summary>
        DirectoryUnavailable = 2,
    }

    /// <summary>
    /// „Parola asta e a contului?” - o singură întrebare, două surse de adevăr.
    ///
    /// Endpointurile care cer dovada parolei înainte de o operație ireversibilă
    /// (înregistrarea și reîmpachetarea cheilor E2EE) apelau direct
    /// <see cref="IPasswordHasher"/>. Pentru un cont de domeniu, PasswordHash e
    /// gol, deci verificarea ar fi eșuat mereu, iar utilizatorii din AD nu ar fi
    /// putut genera chei - adică n-ar fi putut nici trimite, nici primi fișiere.
    ///
    /// Interfața asta mută decizia „cine verifică” într-un singur loc.
    /// </summary>
    public interface IUserCredentialVerifier
    {
        Task<CredentialCheck> VerifyAsync(User user, string password, CancellationToken ct);

        /// <summary>
        /// Consumă timp comparabil cu o verificare reală, pentru cazurile în care
        /// contul nu există. Fără asta, diferența de latență enumeră conturile.
        /// </summary>
        Task SimulateAsync(CancellationToken ct);
    }

    public sealed class UserCredentialVerifier : IUserCredentialVerifier
    {
        private readonly IPasswordHasher _hasher;
        private readonly IDirectoryService _directory;
        private readonly LdapOptions _ldap;

        public UserCredentialVerifier(
            IPasswordHasher hasher, IDirectoryService directory, LdapOptions ldap)
        {
            _hasher    = hasher;
            _directory = directory;
            _ldap      = ldap;
        }

        public async Task<CredentialCheck> VerifyAsync(User user, string password, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(password)) return CredentialCheck.Invalid;

            if (user.AuthProvider == AuthProvider.Ldap)
            {
                if (!_directory.Enabled) return CredentialCheck.DirectoryUnavailable;

                // DN-ul e forma cea mai precisă și e deja cunoscut din
                // autentificare. UPN-ul rămâne varianta de rezervă pentru
                // conturile legate manual, la care DN-ul încă nu a fost citit.
                var bindName = !string.IsNullOrWhiteSpace(user.DirectoryDn)
                    ? user.DirectoryDn!
                    : DirectoryUsername.ToBindName(user.Username, _ldap.Realm);

                return await _directory.VerifyPasswordAsync(bindName, password, ct)
                    ? CredentialCheck.Valid
                    : CredentialCheck.Invalid;
            }

            // Un cont local fără hash nu e „un cont cu parola goală”: e un cont
            // într-o stare pe care nu o recunoaștem. Se refuză, dar tot după ce
            // se consumă timpul unei verificări reale.
            if (string.IsNullOrEmpty(user.PasswordHash))
            {
                await _hasher.SimulateVerificationAsync(ct);
                return CredentialCheck.Invalid;
            }

            var result = await _hasher.VerifyPasswordAsync(password, user.PasswordHash, ct);
            return result == PasswordVerificationResult.Failed
                ? CredentialCheck.Invalid
                : CredentialCheck.Valid;
        }

        public Task SimulateAsync(CancellationToken ct) => _hasher.SimulateVerificationAsync(ct);
    }
}
