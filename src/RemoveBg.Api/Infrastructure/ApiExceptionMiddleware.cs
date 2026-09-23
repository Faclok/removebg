using RemoveBg.Api.Contracts;

namespace RemoveBg.Api.Infrastructure;

public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (ApiException exception)
        {
            await WriteErrorAsync(context, exception.StatusCode, exception.Code, exception.Message);
        }
        catch (BadHttpRequestException exception)
        {
            await WriteErrorAsync(context, 400, "bad_request", exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unhandled request failure {RequestId}", context.GetRequestId());
            await WriteErrorAsync(
                context,
                500,
                "internal_error",
                "Внутренняя ошибка сервера");
        }
    }

    private static async Task WriteErrorAsync(
        HttpContext context,
        int statusCode,
        string code,
        string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(
            new ErrorEnvelope(new ErrorBody(code, message, context.GetRequestId())));
    }
}
