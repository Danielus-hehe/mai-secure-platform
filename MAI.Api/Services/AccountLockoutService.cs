using MAI.BusinessLogic.Security;
using MAI.Domain.Entities;

namespace MAI.Api.Services
{
    /// <summary>
    /// Politica de blocare a contului după autentificări eșuate.
    ///
    /// Extrasă din AuthController pentru că e o regulă de business cu o formulă
    /// în ea (creștere exponențială plafonată), iar o formulă îngropată într-o
    /// metodă privată de controller nu se poate testa fără să ridici un server.
    ///
    /// Nu salvează în baza de date și nu scrie în audit: modifică doar entitatea.
    /// Apelantul decide când face SaveChanges și ce text de audit potrivește
    /// contextului — <see cref="LockoutOutcome"/> îi dă tot ce-i trebuie.
    /// </summary>
    public interface IAccountLockoutService
    {
        /// <summary>Înregistrează o încercare eșuată și, dacă e cazul, blochează contul.</summary>
        LockoutOutcome RegisterFailedAttempt(User user);

        /// <summary>
        /// Șterge contorul după o parolă corectă. Se apelează imediat ce parola e
        /// validată, chiar dacă mai urmează pasul doi — altfel un utilizator cu 2FA
        /// activ ar rămâne cu eșecuri vechi neșterse și s-ar bloca aparent din senin
        /// la o greșeală ulterioară.
        /// </summary>
        void ResetCounters(User user);

        /// <summary>Deblocare administrativă.</summary>
        void Unlock(User user);

        /// <summary>Secundele rămase până la expirarea blocării. 0 dacă nu e blocat.</summary>
        int RemainingLockoutSeconds(User user);
    }

    /// <summary>Ce s-a întâmplat la ultima încercare eșuată.</summary>
    /// <param name="AttemptCount">Al câtelea eșec consecutiv, în fereastra curentă.</param>
    /// <param name="MaxAttempts">Pragul configurat, pentru mesaje și audit.</param>
    /// <param name="LockedOut">True dacă acest eșec a declanșat blocarea.</param>
    /// <param name="LockoutMinutes">Durata blocării, 0 dacă nu s-a blocat.</param>
    public readonly record struct LockoutOutcome(
        int AttemptCount,
        int MaxAttempts,
        bool LockedOut,
        double LockoutMinutes)
    {
        /// <summary>Textul de audit corespunzător, ca formularea să fie identică peste tot.</summary>
        public string AuditDetails => LockedOut
            ? $"Cont blocat {LockoutMinutes:0} minute dupa {AttemptCount} incercari"
            : $"Parola incorecta ({AttemptCount}/{MaxAttempts})";
    }

    public sealed class AccountLockoutService : IAccountLockoutService
    {
        private readonly LockoutOptions _options;

        /// <summary>
        /// Plafon pe exponent înainte de plafonul pe minute. Fără el, 2^40 depășește
        /// domeniul lui double înainte să apuce Math.Min să taie rezultatul.
        /// </summary>
        private const int MaxExponent = 10;

        public AccountLockoutService(LockoutOptions options) => _options = options;

        public LockoutOutcome RegisterFailedAttempt(User user)
        {
            // Fereastră glisantă: dacă ultima greșeală e veche, pornim de la zero.
            // Fără asta, o greșeală de tastare de acum trei săptămâni ar contribui
            // la blocarea de azi.
            if (user.LastFailedLoginAt is { } last &&
                (DateTime.UtcNow - last).TotalMinutes > _options.AttemptWindowMinutes)
            {
                user.FailedLoginAttempts = 0;
            }

            user.FailedLoginAttempts++;
            user.LastFailedLoginAt = DateTime.UtcNow;

            if (user.FailedLoginAttempts < _options.MaxFailedAttempts)
            {
                return new LockoutOutcome(
                    user.FailedLoginAttempts, _options.MaxFailedAttempts, false, 0);
            }

            // Creștere exponențială: fiecare eșec peste prag dublează pedeapsa.
            // Un atac prin forță brută devine impracticabil după câteva runde,
            // iar un utilizator care doar și-a uitat parola așteaptă cinci minute.
            var over    = user.FailedLoginAttempts - _options.MaxFailedAttempts;
            var minutes = _options.BaseLockoutMinutes * Math.Pow(2, Math.Min(over, MaxExponent));
            var capped  = Math.Min(minutes, _options.MaxLockoutMinutes);

            user.LockoutEndsAt = DateTime.UtcNow.AddMinutes(capped);

            return new LockoutOutcome(
                user.FailedLoginAttempts, _options.MaxFailedAttempts, true, capped);
        }

        public void ResetCounters(User user)
        {
            user.FailedLoginAttempts = 0;
            user.LockoutEndsAt       = null;
        }

        public void Unlock(User user)
        {
            ResetCounters(user);
            user.LastFailedLoginAt = null;
        }

        public int RemainingLockoutSeconds(User user)
        {
            if (user.LockoutEndsAt is not { } ends) return 0;

            var seconds = (ends - DateTime.UtcNow).TotalSeconds;
            return seconds <= 0 ? 0 : (int)Math.Ceiling(seconds);
        }
    }
}
