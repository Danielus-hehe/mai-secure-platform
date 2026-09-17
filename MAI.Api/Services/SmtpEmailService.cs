using MAI.Api.Options;
using MAI.BusinessLogic.Interfaces;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace MAI.Api.Services
{
    /// <summary>
    /// Trimitere email prin SMTP cu MailKit.
    ///
    /// Portul 465 folosește SSL implicit (SecureSocketOptions.SslOnConnect).
    /// Portul 587 folosește STARTTLS (SecureSocketOptions.StartTls).
    ///
    /// Toate erorile sunt prinse și logate: un server SMTP indisponibil sau
    /// credențiale greșite nu trebuie să blocheze un transfer de fișier.
    /// </summary>
    public class SmtpEmailService : IEmailService
    {
        private readonly SmtpOptions _opts;
        private readonly ILogger<SmtpEmailService> _logger;

        public SmtpEmailService(SmtpOptions opts, ILogger<SmtpEmailService> logger)
        {
            _opts   = opts;
            _logger = logger;
        }

        /// <inheritdoc />
        public bool IsConfigured => _opts.IsConfigured;

        /// <inheritdoc />
        public async Task<bool> SendTransferNotificationAsync(
            string toEmail,
            string toName,
            string senderName,
            string fileName,
            DateTime? expiresAt,
            CancellationToken ct = default)
        {
            if (!_opts.IsConfigured)
            {
                _logger.LogDebug("SMTP nu este configurat. Notificarea pentru {Email} a fost omisă.", toEmail);
                return false;
            }

            if (string.IsNullOrWhiteSpace(toEmail))
            {
                _logger.LogWarning("SendTransferNotification: adresa destinatarului este goală — notificarea a fost omisă.");
                return false;
            }

            try
            {
                var message = BuildTransferMessage(toEmail, toName, senderName, fileName, expiresAt);
                await SendAsync(message, ct);

                _logger.LogInformation(
                    "Notificare transfer trimisă la {Email} pentru fișierul '{File}'.",
                    toEmail, fileName);

                return true;
            }
            catch (Exception ex)
            {
                // Nu aruncăm mai departe: eșecul emailului nu afectează transferul.
                _logger.LogWarning(ex,
                    "Notificarea email pentru {Email} nu a putut fi trimisă. Fișier: '{File}'.",
                    toEmail, fileName);
                return false;
            }
        }

        // ── Construcție mesaj ────────────────────────────────────────────────

        private MimeMessage BuildTransferMessage(
            string toEmail, string toName, string senderName,
            string fileName, DateTime? expiresAt)
        {
            var msg = new MimeMessage();

            msg.From.Add(new MailboxAddress(_opts.DisplayName, _opts.From));
            msg.To.Add(new MailboxAddress(toName, toEmail));
            msg.Subject = $"[SGDM] Fișier criptat primit de la {senderName}";

            var expiryText = expiresAt.HasValue
                ? expiresAt.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
                : "nedefinit";

            var bodyText =
                $"Bună, {toName},\r\n\r\n" +
                $"Ați primit un fișier criptat în sistemul SGDM MAI:\r\n\r\n" +
                $"  Expeditor : {senderName}\r\n" +
                $"  Fișier    : {fileName}\r\n" +
                $"  Expiră la : {expiryText}\r\n\r\n" +
                "Conectați-vă la SGDM pentru a descărca și decripta fișierul.\r\n" +
                "Descărcarea se face în browser — serverul nu vede conținutul.\r\n\r\n" +
                "SGDM MAI — Sistem Securizat de Gestiune Documente";

            var bodyHtml =
                $"""
                <!DOCTYPE html>
                <html lang="ro">
                <head><meta charset="utf-8"></head>
                <body style="font-family:Arial,sans-serif;color:#222;max-width:600px;margin:auto;padding:24px">
                  <h2 style="color:#1a3a5c;border-bottom:2px solid #1a3a5c;padding-bottom:8px">
                    Fișier criptat primit
                  </h2>
                  <p>Bună, <strong>{System.Net.WebUtility.HtmlEncode(toName)}</strong>,</p>
                  <p>Ați primit un fișier criptat în sistemul SGDM MAI:</p>
                  <table style="border-collapse:collapse;width:100%;margin:16px 0">
                    <tr>
                      <td style="padding:8px 12px;background:#f4f6f8;font-weight:bold;width:130px;border:1px solid #dde2e8">Expeditor</td>
                      <td style="padding:8px 12px;border:1px solid #dde2e8">{System.Net.WebUtility.HtmlEncode(senderName)}</td>
                    </tr>
                    <tr>
                      <td style="padding:8px 12px;background:#f4f6f8;font-weight:bold;border:1px solid #dde2e8">Fișier</td>
                      <td style="padding:8px 12px;border:1px solid #dde2e8;font-family:monospace">{System.Net.WebUtility.HtmlEncode(fileName)}</td>
                    </tr>
                    <tr>
                      <td style="padding:8px 12px;background:#f4f6f8;font-weight:bold;border:1px solid #dde2e8">Expiră la</td>
                      <td style="padding:8px 12px;border:1px solid #dde2e8">{System.Net.WebUtility.HtmlEncode(expiryText)}</td>
                    </tr>
                  </table>
                  <p>
                    Conectați-vă la <strong>SGDM</strong> pentru a descărca și decripta fișierul.<br>
                    <small style="color:#666">Descărcarea se face în browser — serverul nu vede conținutul.</small>
                  </p>
                  <hr style="border:none;border-top:1px solid #dde2e8;margin:24px 0">
                  <p style="color:#888;font-size:12px">SGDM MAI — Sistem Securizat de Gestiune Documente</p>
                </body>
                </html>
                """;

            var builder = new BodyBuilder
            {
                TextBody = bodyText,
                HtmlBody = bodyHtml,
            };

            msg.Body = builder.ToMessageBody();
            return msg;
        }

        // ── Trimitere ────────────────────────────────────────────────────────

        private async Task SendAsync(MimeMessage message, CancellationToken ct)
        {
            using var client = new SmtpClient();

            // Port 465 → SSL implicit; port 587 → STARTTLS
            var socketOptions = _opts.UseSsl
                ? SecureSocketOptions.SslOnConnect
                : SecureSocketOptions.StartTls;

            await client.ConnectAsync(_opts.Host, _opts.Port, socketOptions, ct);
            await client.AuthenticateAsync(_opts.Username, _opts.Password, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(quit: true, ct);
        }
    }
}