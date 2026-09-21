using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MAI.BusinessLogic.Ldap
{
    /// <summary>
    /// Implementarea folosită când Ldap:Enabled este false.
    ///
    /// Există ca restul codului să injecteze mereu <see cref="IDirectoryService"/>,
    /// fără „if (ldapEnabled)” prin controllere și fără dependințe opționale în
    /// constructori. Tot ce poate fi întrebat răspunde „nu”, iar operațiile de
    /// administrare aruncă un mesaj clar în loc să pară că nu fac nimic.
    /// </summary>
    public sealed class DisabledDirectoryService : IDirectoryService
    {
        private const string Message =
            "Autentificarea prin Active Directory nu este activată (LDAP_ENABLED=false).";

        public bool Enabled => false;

        public Task<DirectoryAuthResult> AuthenticateAsync(string username, string password, CancellationToken ct) =>
            Task.FromResult(DirectoryAuthResult.Fail(DirectoryAuthStatus.ServerUnavailable, Message));

        public Task<bool> VerifyPasswordAsync(string bindName, string password, CancellationToken ct) =>
            Task.FromResult(false);

        public Task<DirectoryUser?> FindUserAsync(string username, CancellationToken ct) =>
            Task.FromResult<DirectoryUser?>(null);

        public Task<IReadOnlyList<DirectoryOrgUnit>> GetOrganizationalUnitsAsync(CancellationToken ct) =>
            throw new InvalidOperationException(Message);

        public Task<DirectoryProbe> TestConnectionAsync(CancellationToken ct) =>
            Task.FromResult(new DirectoryProbe(false, false, 0, 0, null, null, Message));
    }
}
