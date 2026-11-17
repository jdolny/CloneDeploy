using System.Net;
using System.Net.Mail;
using CloneDeploy_Server.Backend.Data.Models;
namespace CloneDeploy_Server.Backend.Services;

public enum EmailSendOutcome { Accepted, Rejected, Uncertain }
public interface IEmailTransport
{
    Task<EmailSendOutcome> SendAsync(EmailServerSettings settings, string recipient, string subject, string body, string messageId, CancellationToken ct);
}
public sealed class SmtpEmailTransport(ISecretProtector secrets) : IEmailTransport
{
    public async Task<EmailSendOutcome> SendAsync(EmailServerSettings settings, string recipient, string subject, string body, string messageId, CancellationToken ct)
    {
        using var client = new SmtpClient(settings.Host, settings.Port) { EnableSsl = settings.StartTls, UseDefaultCredentials = false };
        if (!string.IsNullOrWhiteSpace(settings.UserName))
            client.Credentials = new NetworkCredential(settings.UserName, secrets.Unprotect(settings.PasswordEncrypted));
        using var message = new MailMessage(new MailAddress(settings.FromAddress, settings.FromName), new MailAddress(recipient))
            { Subject = subject, Body = body, IsBodyHtml = false, BodyEncoding = System.Text.Encoding.UTF8, SubjectEncoding = System.Text.Encoding.UTF8 };
        message.Headers.Add("Message-ID", $"<{messageId}@clonedeploy.local>");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try { await client.SendMailAsync(message, deadline.Token); return EmailSendOutcome.Accepted; }
        catch (SmtpFailedRecipientException) { return EmailSendOutcome.Rejected; }
        catch (SmtpException ex) when (ex.StatusCode != SmtpStatusCode.GeneralFailure) { return EmailSendOutcome.Rejected; }
        catch (SmtpException) { return EmailSendOutcome.Uncertain; }
        catch (OperationCanceledException) { return EmailSendOutcome.Uncertain; }
    }
    public static bool ValidAddress(string value) => value.Length <= 254 && !value.Contains('\r') && !value.Contains('\n')
        && MailAddress.TryCreate(value, out var address) && address.Address == value && value.Contains('@');
}
