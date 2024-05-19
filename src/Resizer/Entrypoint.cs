using System.Collections.Specialized;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using System.Web;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace Resizer;

public class Entrypoint
{
    private readonly IAmazonS3 _s3Client = new AmazonS3Client();

    public async Task<APIGatewayProxyResponse> Handler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        try
        {
            Console.WriteLine(JsonSerializer.Serialize(request));
            var key = request.QueryStringParameters["path"];
            var extension = request.QueryStringParameters.TryGetValue("extension", out var parameter) ? parameter : GetFileExtension(key);
            var originalExtension = extension;
            var match = Regex.Match(key, @"((\d+)x(\d+))\/(.*)");

            if (!IsValidMatch(match))
                return CreateResponse(403, string.Empty);

            var allowedResolutions = GetAllowedResolutions();
            if (!IsResolutionAllowed(match.Groups[1].Value, allowedResolutions))
                return CreateResponse(403, string.Empty);

            var (width, height, originalKey) = ExtractImageDimensionsAndKey(match, key);
            var prefixedKey = ApplyPrefix(originalKey);
            var imageExtension = GetFileExtension(prefixedKey);

            var validExtensions = GetValidExtensions();
            if (!validExtensions.ContainsKey(imageExtension))
                return CreateResponse(301, null, new Dictionary<string, string> { { "location", prefixedKey } });

            var fileKey = originalExtension != imageExtension ? prefixedKey.Replace(imageExtension, originalExtension) : prefixedKey;
            Console.WriteLine($"OrginalExtension : {originalExtension} ImageExtension : {imageExtension}");
            Console.WriteLine(fileKey);
            var getObjectResponse = await _s3Client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = Environment.GetEnvironmentVariable("BUCKET"),
                Key = fileKey
            });

            await ProcessAndSaveImage(getObjectResponse.ResponseStream, prefixedKey, width, height, validExtensions[imageExtension], imageExtension);
            return CreateRedirectResponse(prefixedKey);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error: {ex.Message}");
            throw;
        }
    }
    
    private static bool IsValidMatch(Match match)
    {
        return match.Groups.Count >= 4;
    }

    private static string[] GetAllowedResolutions()
    {
        var allowedResolutionsEnv = Environment.GetEnvironmentVariable("ALLOWED_RESOLUTIONS");
        return string.IsNullOrEmpty(allowedResolutionsEnv)
            ? Array.Empty<string>()
            : allowedResolutionsEnv.Split(',').Select(res => res.Trim()).ToArray();
    }

    private static bool IsResolutionAllowed(string resolution, string[] allowedResolutions)
    {
        return allowedResolutions.Length == 0 || allowedResolutions.Contains(resolution);
    }

    private static (int width, int height, string originalKey) ExtractImageDimensionsAndKey(Match match, string key)
    {
        var width = int.Parse(match.Groups[2].Value);
        var height = int.Parse(match.Groups[3].Value);
        var originalKey = match.Groups[4].Value;
        return (width, height, originalKey);
    }

    private static string ApplyPrefix(string originalKey)
    {
        var prefix = Environment.GetEnvironmentVariable("PREFIX");
        return string.IsNullOrEmpty(prefix) ? originalKey : $"{prefix}/{originalKey}";
    }

    private static string GetFileExtension(string key)
    {
        return key.Split('.').Last();
    }

    private static Dictionary<string, IImageEncoder> GetValidExtensions()
    {
        return new Dictionary<string, IImageEncoder>
        {
            { "jpg", new JpegEncoder() },
            { "jpeg", new JpegEncoder() },
            { "png", new PngEncoder() },
            { "webp", new WebpEncoder { Quality = 75 } }
        };
    }

    private async Task ProcessAndSaveImage(Stream originalStream, string key, int width, int height, IImageEncoder encoder, string imageExtension)
    {
        await using var outputMemoryStream = new MemoryStream();
        using (var image = await Image.LoadAsync(originalStream))
        {
            image.Mutate(x => x.AutoOrient().Resize(new ResizeOptions
            {
                Size = new Size(width, height),
                Mode = ResizeMode.Max
            }));
            await image.SaveAsync(outputMemoryStream, encoder);
        }

        await _s3Client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = Environment.GetEnvironmentVariable("BUCKET"),
            Key = key,
            ContentType = $"image/{imageExtension}",
            InputStream = outputMemoryStream,
            TagSet = new List<Tag> { new Tag { Key = "lifetime", Value = "transient" } }
        });
    }

    private static APIGatewayProxyResponse CreateResponse(int statusCode, string? body, Dictionary<string, string>? headers = null)
    {
        return new APIGatewayProxyResponse
        {
            StatusCode = statusCode,
            Headers = headers ?? new Dictionary<string, string>(),
            Body = body
        };
    }

    private static APIGatewayProxyResponse CreateRedirectResponse(string key)
    {
        return CreateResponse(301, string.Empty, new Dictionary<string, string>
        {
            { "location", $"{Environment.GetEnvironmentVariable("URL")}/{key}" }
        });
    }
}
