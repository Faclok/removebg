using System.Net;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using RemoveBg.Api.Configuration;
using SmtpClient = MailKit.Net.Smtp.SmtpClient;

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
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(settings.FromName, settings.FromAddress));
            message.To.Add(MailboxAddress.Parse(recipientEmail));
            message.Subject = "Пароль от аккаунта Удалифон";
            message.Body = new BodyBuilder
            {
                TextBody = BuildTextBody(recipientName, password, settings.LoginUrl),
                HtmlBody = BuildHtmlBody(recipientName, password, settings.LoginUrl)
            }.ToMessageBody();

            using var client = new SmtpClient { Timeout = 15_000 };
            var socketOptions = settings.EnableSsl
                ? settings.Port == 465
                    ? SecureSocketOptions.SslOnConnect
                    : SecureSocketOptions.StartTls
                : SecureSocketOptions.None;
            await client.ConnectAsync(settings.Host, settings.Port, socketOptions, cancellationToken);
            if (!string.IsNullOrWhiteSpace(settings.Username))
            {
                await client.AuthenticateAsync(settings.Username, settings.Password, cancellationToken);
            }
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Failed to send registration password to {Email}", recipientEmail);
            throw new EmailDeliveryException("Не удалось отправить письмо", exception);
        }
    }

    private static string BuildTextBody(string recipientName, string password, string loginUrl) =>
        $"""
        {recipientName}, ваш аккаунт Удалифон создан.

        Пароль для входа:
        {password}

        Войти: {loginUrl}

        Храните пароль в надёжном месте и никому его не передавайте.
        """;

    private static string BuildHtmlBody(string recipientName, string password, string loginUrl)
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
