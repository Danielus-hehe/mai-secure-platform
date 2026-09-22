using System.Collections.Concurrent;
using MAI.BusinessLogic.Interfaces;

namespace MAI.IntegrationTests;

/// <summary>
/// Serviciu email care nu trimite nimic, dar inregistreaza fiecare apel.
///
/// Testele pot verifica: "s-a trimis o notificare?" fara sa configureze un
/// server SMTP. Thread-safe prin ConcurrentBag.
/// </summary>
public sealed class FakeEmailService : IEmailService
{
    public ConcurrentBag<EmailRecord> Sent { get; } = [];

    public bool IsConfigured => true;

    public Task<bool> SendTransferNotificationAsync(
        string toEmail, string toName, string senderName,
        string fileName, DateTime? expiresAt, CancellationToken ct = default)
    {
        Sent.Add(new("TransferNotification", toEmail, toName));
        return Task.FromResult(true);
    }

    public Task<bool> SendInvitationEmailAsync(
        string toEmail, string toName, string invitationLink, CancellationToken ct = default)
    {
        Sent.Add(new("Invitation", toEmail, toName));
        return Task.FromResult(true);
    }

    public Task<bool> SendPasswordResetEmailAsync(
        string toEmail, string toName, string link, TimeSpan validity, bool hasKeys,
        CancellationToken ct = default)
    {
        Sent.Add(new("PasswordReset", toEmail, toName) { Link = link });
        return Task.FromResult(true);
    }

    public Task<bool> SendPasswordChangedEmailAsync(
        string toEmail, string toName, DateTime changedAt, CancellationToken ct = default)
    {
        Sent.Add(new("PasswordChanged", toEmail, toName));
        return Task.FromResult(true);
    }

    public record EmailRecord(string Type, string ToEmail, string ToName)
    {
        public string? Link { get; init; }
    }
}
