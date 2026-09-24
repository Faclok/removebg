using Microsoft.Extensions.Options;
using RemoveBg.Api.Configuration;
using RemoveBg.Api.Infrastructure;
using SkiaSharp;

namespace RemoveBg.Api.Services;

public sealed record ValidatedImage(string ContentType, int Width, int Height);

public sealed class ImageInputValidator(IOptions<RemovalOptions> options)
{
    private readonly RemovalOptions settings = options.Value;

    public ValidatedImage Validate(byte[] input, string declaredContentType)
    {
        using var stream = new SKMemoryStream(input);
        using var codec = SKCodec.Create(stream);
        if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
        {
            throw new ApiException(422, "invalid_image", "Файл не является корректным изображением");
        }

        var actualContentType = codec.EncodedFormat switch
        {
            SKEncodedImageFormat.Jpeg => "image/jpeg",
            SKEncodedImageFormat.Png => "image/png",
            SKEncodedImageFormat.Webp => "image/webp",
            _ => throw new ApiException(415, "unsupported_media_type", "Поддерживаются JPEG, PNG и WebP")
        };

        if (!string.Equals(actualContentType, declaredContentType, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(
                415,
                "content_type_mismatch",
                $"Содержимое файла не соответствует заявленному типу {declaredContentType}");
        }

        if (codec.Info.Width > settings.MaxImageWidth || codec.Info.Height > settings.MaxImageHeight)
        {
            throw new ApiException(
                413,
                "image_dimensions_too_large",
                $"Максимальное разрешение: {settings.MaxImageWidth}×{settings.MaxImageHeight}");
        }

        if ((long)codec.Info.Width * codec.Info.Height > settings.MaxImagePixels)
        {
            throw new ApiException(
                413,
                "image_too_large",
                $"Допустимо не более {settings.MaxImagePixels} пикселей");
        }

        return new ValidatedImage(actualContentType, codec.Info.Width, codec.Info.Height);
    }
}
