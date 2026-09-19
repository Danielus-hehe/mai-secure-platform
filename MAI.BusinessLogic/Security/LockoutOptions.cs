namespace MAI.BusinessLogic.Security
{
    /// <summary>
    /// Blocarea contului după încercări eșuate consecutive.
    ///
    /// De ce e nevoie și de asta, pe lângă rate limiting per IP: un atac de tip
    /// credential stuffing distribuie încercările pe mii de IP-uri, fiecare rămânând
    /// sub pragul per-IP. Contorul per cont prinde exact acest scenariu.
    ///
    /// Invers, blocarea per cont singură permite un atac de tip "account lockout DoS":
    /// cineva blochează intenționat conturile altora greșind parola. De aceea pragul
    /// nu e prea mic, iar blocarea e temporară și cu durată crescătoare, nu permanentă.
    /// </summary>
    public class LockoutOptions
    {
        /// <summary>Câte eșecuri consecutive până la prima blocare.</summary>
        public int MaxFailedAttempts { get; set; } = 5;

        /// <summary>Durata primei blocări, în minute. Se dublează la fiecare eșec ulterior.</summary>
        public double BaseLockoutMinutes { get; set; } = 5;

        /// <summary>Plafonul duratei de blocare, în minute (implicit 8 ore).</summary>
        public double MaxLockoutMinutes { get; set; } = 480;

        /// <summary>
        /// Fereastra în care se numără eșecurile. Dacă ultima greșeală e mai veche de
        /// atât, contorul repornește de la zero - un utilizator care greșește parola
        /// o dată pe lună nu trebuie să acumuleze blocări.
        /// </summary>
        public double AttemptWindowMinutes { get; set; } = 30;
    }
}