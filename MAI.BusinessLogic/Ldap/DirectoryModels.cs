using System;
using System.Collections.Generic;

namespace MAI.BusinessLogic.Ldap
{
    /// <summary>
    /// Un cont, așa cum îl vede Active Directory. Strict citire: aplicația nu
    /// scrie niciodată în AD, deci obiectul acesta este o fotografie, nu o
    /// entitate.
    /// </summary>
    /// <param name="ObjectId">objectGUID, singurul identificator stabil la redenumire.</param>
    /// <param name="SamAccountName">Numele de utilizator scurt („ion.popescu”).</param>
    /// <param name="DistinguishedName">DN-ul complet, folosit la bind.</param>
    /// <param name="DisplayName">Numele afișat (displayName sau cn).</param>
    /// <param name="Email">Adresa din atributul mail. Poate lipsi.</param>
    /// <param name="Department">Valoarea atributului configurat (implicit „department”).</param>
    /// <param name="UserPrincipalName">UPN-ul, ex. „ion.popescu@sgdm.local”.</param>
    /// <param name="Groups">DN-urile grupurilor din memberOf.</param>
    /// <param name="Enabled">False dacă în userAccountControl e setat bitul ACCOUNTDISABLE.</param>
    /// <param name="PasswordSetAt">pwdLastSet, convertit din FILETIME. Null dacă lipsește.</param>
    /// <param name="MustChangePasswordAtNextLogon">pwdLastSet = 0.</param>
    public sealed record DirectoryUser(
        string ObjectId,
        string SamAccountName,
        string DistinguishedName,
        string? DisplayName,
        string? Email,
        string? Department,
        string? UserPrincipalName,
        IReadOnlyList<string> Groups,
        bool Enabled,
        DateTime? PasswordSetAt,
        bool MustChangePasswordAtNextLogon);

    /// <summary>De ce a eșuat (sau a reușit) o autentificare în domeniu.</summary>
    public enum DirectoryAuthStatus
    {
        Success = 0,

        /// <summary>Contul nu există în AD sub numele dat.</summary>
        UserNotFound = 1,

        /// <summary>Numele există, parola nu e bună (AD: data 52e).</summary>
        InvalidCredentials = 2,

        /// <summary>Cont dezactivat sau expirat în AD (data 533, 701).</summary>
        AccountDisabled = 3,

        /// <summary>Cont blocat în AD după prea multe încercări (data 775).</summary>
        AccountLocked = 4,

        /// <summary>Parola a expirat sau trebuie schimbată la următorul logon (data 532, 773).</summary>
        PasswordExpired = 5,

        /// <summary>Serverul nu a răspuns, certificatul nu e acceptat, timeout.</summary>
        ServerUnavailable = 6,

        /// <summary>Contul de serviciu nu se poate autentifica la DC.</summary>
        ServiceAccountRejected = 7,
    }

    /// <summary>Rezultatul unei autentificări în domeniu.</summary>
    /// <param name="Status">Reușită sau motivul eșecului.</param>
    /// <param name="User">Contul citit din AD. Prezent doar la succes.</param>
    /// <param name="Detail">Text scurt pentru jurnalul de audit. NU se arată utilizatorului.</param>
    public sealed record DirectoryAuthResult(
        DirectoryAuthStatus Status,
        DirectoryUser? User,
        string Detail)
    {
        public bool Success => Status == DirectoryAuthStatus.Success && User is not null;

        public static DirectoryAuthResult Ok(DirectoryUser user) =>
            new(DirectoryAuthStatus.Success, user, "Bind LDAP reusit");

        public static DirectoryAuthResult Fail(DirectoryAuthStatus status, string detail) =>
            new(status, null, detail);
    }

    /// <summary>O unitate organizatorică din AD, pentru importul structurii.</summary>
    /// <param name="DistinguishedName">DN-ul OU-ului, cheia de identificare la reimport.</param>
    /// <param name="Name">Denumirea (atributul ou sau name).</param>
    /// <param name="ParentDn">DN-ul OU-ului părinte, dacă părintele e tot un OU din import.</param>
    /// <param name="Depth">Adâncimea în arbore, 0 pentru nivelul de vârf al importului.</param>
    /// <param name="Description">Descrierea din AD, folosită ca prescurtare dacă e scurtă.</param>
    public sealed record DirectoryOrgUnit(
        string DistinguishedName,
        string Name,
        string? ParentDn,
        int Depth,
        string? Description);

    /// <summary>Rezultatul unui test de conexiune, afișat în pagina de administrare.</summary>
    /// <param name="Reachable">DC-ul a răspuns și TLS-ul a fost acceptat.</param>
    /// <param name="ServiceAccountBound">Contul de serviciu s-a autentificat.</param>
    /// <param name="UserCount">Câte conturi s-au găsit sub baza de căutare (maxim 1000).</param>
    /// <param name="OrgUnitCount">Câte unități organizatorice s-au găsit.</param>
    /// <param name="ServerCertificateSubject">Subiectul certificatului prezentat de DC.</param>
    /// <param name="ServerCertificateThumbprint">Amprenta SHA-256, de pus în configurare.</param>
    /// <param name="Error">Mesajul erorii, dacă ceva a eșuat.</param>
    public sealed record DirectoryProbe(
        bool Reachable,
        bool ServiceAccountBound,
        int UserCount,
        int OrgUnitCount,
        string? ServerCertificateSubject,
        string? ServerCertificateThumbprint,
        string? Error);
}
