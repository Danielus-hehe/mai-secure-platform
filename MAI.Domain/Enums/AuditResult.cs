namespace MAI.Domain.Enums
{
    /// <summary>
    /// Rezultatul unei operații consemnate în jurnalul de audit.
    ///
    /// Înainte, rezultatul era codificat ca prefix de text în <c>Details</c>
    /// ("ESEC:", "SUCCES:", "ATENTIE:"). Funcționa, dar filtrarea în baza de date
    /// se făcea cu StartsWith pe un câmp liber, iar o greșeală de scriere
    /// ("ESEK:") trecea de compilator și dispărea tăcut din rapoarte.
    ///
    /// Valorile numerice sunt fixate explicit: coloana e persistată ca int, iar
    /// o reordonare accidentală a membrilor ar reinterpreta istoricul deja scris.
    /// </summary>
    public enum AuditResult
    {
        /// <summary>Operația s-a încheiat cu succes.</summary>
        Success = 0,

        /// <summary>Operația a eșuat sau a fost refuzată.</summary>
        Failure = 1,

        /// <summary>
        /// Operația a reușit, dar merită atenția unui supervizor: dezactivarea 2FA,
        /// resetare administrativă, semnătură invalidă la descărcare.
        /// </summary>
        Warning = 2,
    }
}
