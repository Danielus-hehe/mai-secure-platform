using MAI.DataAccessLayer;
using MAI.Domain.Entities;
using MAI.Domain.Enums;

namespace MAI.Api.Services
{
    /// <summary>
    /// Trimite invitații de activare în fundal, fără să țină cererea HTTP deschisă.
    /// </summary>
    public interface IInvitationDispatcher
    {
        /// <summary>
        /// Programează trimiterea invitației pentru un cont deja salvat în bază.
        /// Metoda se întoarce imediat; rezultatul apare în log și, la eșec, în
        /// jurnalul de audit.
        /// </summary>
        void Enqueue(Guid userId, string username);
    }

    /// <summary>
    /// Implementare singleton care rulează fiecare trimitere în PROPRIUL scope DI.
    ///
    /// De ce există: UsersController.CreateUser pornea
    /// <c>_invitation.SendInvitationAsync(...)</c> fire-and-forget. InvitationService
    /// e Scoped și primea același AppDbContext ca cererea HTTP. Controllerul
    /// răspundea imediat, scope-ul cererii se închidea, contextul devenea disposed,
    /// iar trimiterea pica cu ObjectDisposedException (sau cu „a second operation
    /// was started on this context” dacă apuca să ruleze în paralel cu salvarea).
    /// Excepția era înghițită de ContinueWith, API-ul răspundea
    /// <c>invitationSent = true</c>, iar contul rămânea neactivat pentru totdeauna.
    ///
    /// Aici fiecare trimitere are scope-ul ei, deci AppDbContext propriu, care
    /// trăiește exact cât trimiterea.
    /// </summary>
    public sealed class InvitationDispatcher : IInvitationDispatcher
    {
        private readonly IServiceScopeFactory          _scopeFactory;
        private readonly IHostApplicationLifetime      _lifetime;
        private readonly ILogger<InvitationDispatcher> _logger;

        public InvitationDispatcher(
            IServiceScopeFactory          scopeFactory,
            IHostApplicationLifetime      lifetime,
            ILogger<InvitationDispatcher> logger)
        {
            _scopeFactory = scopeFactory;
            _lifetime     = lifetime;
            _logger       = logger;
        }

        /// <inheritdoc />
        public void Enqueue(Guid userId, string username)
        {
            // ApplicationStopping: la oprirea serverului trimiterea în curs se
            // anulează curat, nu rămâne un task orfan după dispose-ul containerului.
            var ct = _lifetime.ApplicationStopping;
            _ = Task.Run(() => SendAsync(userId, username, ct), CancellationToken.None);
        }

        private async Task SendAsync(Guid userId, string username, CancellationToken ct)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();

                var invitations = scope.ServiceProvider.GetRequiredService<IInvitationService>();
                var sent        = await invitations.SendInvitationAsync(userId, ct);

                if (sent) return;

                // Emailul nu a plecat. Administratorul trebuie să poată vedea asta
                // în aplicație, nu doar în logurile containerului.
                await WriteFailureAuditAsync(scope, userId, username,
                    "Invitatia de activare NU a fost trimisa (SMTP neconfigurat sau indisponibil). " +
                    "Poate fi retrimisa din pagina Utilizatori.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "Trimiterea invitației pentru {Username} a fost anulată la oprirea serverului.", username);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Trimiterea invitației pentru {Username} ({UserId}) a eșuat.", username, userId);

                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    await WriteFailureAuditAsync(scope, userId, username,
                        "Invitatia de activare a esuat cu eroare interna. Poate fi retrimisa din pagina Utilizatori.");
                }
                catch (Exception auditEx)
                {
                    // Ultima plasă: dacă nici baza nu răspunde, rămâne doar logul.
                    _logger.LogError(auditEx, "Nici înregistrarea eșecului invitației în audit nu a reușit.");
                }
            }
        }

        private static async Task WriteFailureAuditAsync(
            AsyncServiceScope scope, Guid userId, string username, string details)
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            db.AuditLogs.Add(new AuditLog
            {
                UserId    = userId,
                Username  = username,
                Action    = AuditAction.UserUpdated,
                Details   = details,
                Result    = AuditResult.Failure,
                IpAddress = "sistem",
                Timestamp = DateTime.UtcNow,
            });

            await db.SaveChangesAsync(CancellationToken.None);
        }
    }
}
