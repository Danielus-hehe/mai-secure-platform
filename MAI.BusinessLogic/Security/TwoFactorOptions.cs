namespace MAI.BusinessLogic.Security
{
    /// <summary>
    /// Configurarea autentificarii in doi pasi (TOTP, RFC 6238).
    /// Sectiunea "TwoFactor" din appsettings.json.
    /// </summary>
    public class TwoFactorOptions
    {
        /// <summary>
        /// Numele emitentului afisat in aplicatia de autentificare
        /// (Google Authenticator, Aegis, 1Password). Apare deasupra codului.
        /// </summary>
        public string Issuer { get; set; } = "SGDM MAI";

        /// <summary>Numarul de cifre ale codului. 6 este ce asteapta orice aplicatie standard.</summary>
        public int Digits { get; set; } = 6;

        /// <summary>Durata unui interval, in secunde. 30 este standardul de facto.</summary>
        public int PeriodSeconds { get; set; } = 30;

        /// <summary>
        /// Cate intervale inainte si dupa cel curent se accepta.
        ///
        /// 1 inseamna o toleranta de ±30 secunde, adica o fereastra totala de 90.
        /// Acopera ceasul desincronizat al telefonului si timpul de tastare.
        /// Valori mai mari maresc proportional sansa unui atacator care ghiceste:
        /// fiecare interval in plus adauga inca un cod valid simultan.
        /// </summary>
        public int WindowSteps { get; set; } = 1;

        /// <summary>
        /// Cate coduri de recuperare se genereaza la activare. Se afiseaza o
        /// singura data si se stocheaza doar ca hash.
        /// </summary>
        public int RecoveryCodeCount { get; set; } = 10;

        /// <summary>
        /// Cat timp e valabila provocarea dintre parola corecta si codul TOTP.
        ///
        /// Scurt intentionat. In fereastra asta, cine a furat provocarea din
        /// retea are nevoie doar de cod; cu 5 minute, atacul are un buget de
        /// timp de zece ori mai mare degeaba.
        /// </summary>
        public int ChallengeLifetimeSeconds { get; set; } = 180;

        /// <summary>
        /// Cate coduri gresite se accepta pe o provocare inainte sa fie anulata.
        ///
        /// Fara asta, provocarea devine un oracol: atacatorul care are parola dar
        /// nu are telefonul poate incerca coduri de 6 cifre la nesfarsit in
        /// fereastra de valabilitate. Cu 5, sansa de ghicire ramane sub 1/200.000.
        /// </summary>
        public int MaxChallengeAttempts { get; set; } = 5;

        /// <summary>
        /// Obliga rolurile privilegiate sa aiba 2FA activ. Cu true, un
        /// Administrator sau SefDirectie fara 2FA primeste la login un raspuns
        /// care il obliga sa-l configureze inainte sa poata lucra.
        /// </summary>
        public bool RequiredForPrivilegedRoles { get; set; } = false;

        /// <summary>
        /// Cheia AES-256 (base64, 32 de octeti) cu care se cifreaza secretele TOTP
        /// in baza de date. NU in appsettings.json — din variabila de mediu
        /// MAI_TWOFACTOR_KEY.
        /// </summary>
        public string? EncryptionKey { get; set; }

        public void Validate()
        {
            if (Digits is < 6 or > 8)
                throw new InvalidOperationException("TwoFactor:Digits trebuie sa fie intre 6 si 8.");

            if (PeriodSeconds is < 15 or > 120)
                throw new InvalidOperationException("TwoFactor:PeriodSeconds trebuie sa fie intre 15 si 120.");

            if (WindowSteps is < 0 or > 5)
                throw new InvalidOperationException("TwoFactor:WindowSteps trebuie sa fie intre 0 si 5.");

            if (RecoveryCodeCount is < 4 or > 32)
                throw new InvalidOperationException("TwoFactor:RecoveryCodeCount trebuie sa fie intre 4 si 32.");

            if (ChallengeLifetimeSeconds is < 30 or > 900)
                throw new InvalidOperationException("TwoFactor:ChallengeLifetimeSeconds trebuie sa fie intre 30 si 900.");

            if (MaxChallengeAttempts is < 1 or > 20)
                throw new InvalidOperationException("TwoFactor:MaxChallengeAttempts trebuie sa fie intre 1 si 20.");
        }
    }
}