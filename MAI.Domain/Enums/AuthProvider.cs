namespace MAI.Domain.Enums
{
    /// <summary>
    /// Sursa care decide dacă parola introdusă la login este corectă.
    /// Persistat ca int; membrii nu se reordonează.
    ///
    /// Valoarea zero este <see cref="Local"/> intenționat: coloana se adaugă pe
    /// o tabelă cu conturi existente, iar toate acele conturi au parola în
    /// baza noastră. Un default diferit ar fi transformat, la migrare, fiecare
    /// cont local într-unul de domeniu, deci nimeni nu s-ar mai fi putut
    /// autentifica.
    /// </summary>
    public enum AuthProvider
    {
        /// <summary>Parola e verificată cu Argon2id, din coloana PasswordHash.</summary>
        Local = 0,

        /// <summary>
        /// Parola e verificată printr-un bind LDAPS la Active Directory. Contul
        /// local nu are hash de parolă (PasswordHash rămâne gol) - un cont de
        /// domeniu nu trebuie să poată fi deschis cu o parolă păstrată la noi,
        /// rămasă valabilă după dezactivarea contului în domeniu.
        /// </summary>
        Ldap = 1,
    }
}
