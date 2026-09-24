using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Options;
using RemoveBg.Api.Configuration;

namespace RemoveBg.Api.Services;

public interface IAccountEmailSender
{
    Task SendRegistrationPasswordAsync(
        string recipientName,
        string recipientEmail,
        string password,
        CancellationToken cancellationToken);
}

public sealed class EmailDeliveryException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public sealed class SmtpAccountEmailSender(
    IOptions<EmailOptions> options,
    ILogger<SmtpAccountEmailSender> logger) : IAccountEmailSender
{
    public async Task SendRegistrationPasswordAsync(
        string recipientName,
        string recipientEmail,
        string password,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Host)
            || string.IsNullOrWhiteSpace(settings.FromAddress))
        {
            throw new EmailDeliveryException("SMTP не настроен");
        }

        try
        {
            using var message = new MailMessage
            {
                From = new MailAddress(settings.FromAddress, settings.FromName, Encoding.UTF8),
                Subject = "Пароль от аккаунта Удалифон",
                SubjectEncoding = Encoding.UTF8,
                BodyEncoding = Encoding.UTF8,
                IsBodyHtml = true,
                Body = BuildBody(recipientName, password, settings.LoginUrl)
            };
            message.To.Add(new MailAddress(recipientEmail));

            using var client = new SmtpClient(settings.Host, settings.Port)
            {
                EnableSsl = settings.EnableSsl,
                UseDefaultCredentials = false,
                Credentials = string.IsNullOrWhiteSpace(settings.Username)
                    ? null
                    : new NetworkCredential(settings.Username, settings.Password)
            };
            await client.SendMailAsync(message, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Failed to send registration password to {Email}", recipientEmail);
            throw new EmailDeliveryException("Не удалось отправить письмо", exception);
        }
    }

    private static string BuildBody(string recipientName, string password, string loginUrl)
    {
        var safeName = WebUtility.HtmlEncode(recipientName);
        var safePassword = WebUtility.HtmlEncode(password);
        var safeLoginUrl = WebUtility.HtmlEncode(loginUrl);
        return $$"""
            <!doctype html>
            <html lang="ru">
              <body style="font-family:Arial,sans-serif;color:#17211f;line-height:1.5">
                <h2>Добро пожаловать в Удалифон</h2>
                <p>{{safeName}}, ваш аккаунт создан.</p>
                <p>Пароль для входа:</p>
                <p><code style="font-size:16px;padding:10px;background:#f1f5f4">{{safePassword}}</code></p>
                <p><a href="{{safeLoginUrl}}">Перейти ко входу</a></p>
                <p style="color:#64706d">Храните пароль в надёжном месте и никому его не передавайте.</p>
              </body>
            </html>
            """;
    }
}
