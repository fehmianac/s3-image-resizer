using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.Core;
using Amazon.S3;
using Amazon.S3.Model;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.Json.JsonSerializer))]

namespace Resizer;

public class Entrypoint
{
    private readonly IAmazonS3 _s3Client = new AmazonS3Client();

    public async Task<APIGatewayProxyResponse> Handler(APIGatewayProxyRequest request, ILambdaContext context)
    {
        try
        {
            var key = request.QueryStringParameters["path"];
            var match = System.Text.RegularExpressions.Regex.Match(key, @"((\d+)x(\d+))\/(.*)");

            if (match.Groups.Count < 4)
            {
                return new APIGatewayProxyResponse
                {
                    StatusCode = 403,
                    Headers = new Dictionary<string, string>(),
                    Body = string.Empty
                };
            }

            var allowedResolutions = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ALLOWED_RESOLUTIONS"))
                ? Environment.GetEnvironmentVariable("ALLOWED_RESOLUTIONS").Split(',').Select(res => res.Trim()).ToArray()
                : Array.Empty<string>();

            if (allowedResolutions.Length > 0 && !allowedResolutions.Contains(match.Groups[1].Value))
            {
                return new APIGatewayProxyResponse
                {
                    StatusCode = 403,
                    Headers = new Dictionary<string, string>(),
                    Body = string.Empty
                };
            }

            var width = int.Parse(match.Groups[2].Value);
            var height = int.Parse(match.Groups[3].Value);
            var originalKey = match.Groups[4].Value;

            var prefix = Environment.GetEnvironmentVariable("PREFIX");
            if (!string.IsNullOrEmpty(prefix))
                originalKey = prefix + "/" + originalKey;

            var imageExtension = originalKey.Split('.').Last();
            var validExtensions = new Dictionary<string, ImageEncoder>
            {
                {"jpg", new JpegEncoder()},
                {"jpeg", new JpegEncoder()},
                {"png", new PngEncoder()},
            };

            if (!validExtensions.ContainsKey(imageExtension))
            {
                return new APIGatewayProxyResponse
                {
                    StatusCode = 301,
                    Headers = new Dictionary<string, string>
                    {
                        {"location", originalKey}
                    },
                    Body = string.Empty
                };
            }


            Console.WriteLine(originalKey);
            var getObjectResponse = await _s3Client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = Environment.GetEnvironmentVariable("BUCKET"),
                Key = originalKey
            });

            await using (var originalStream = getObjectResponse.ResponseStream)
            using (var outputMemoryStream = new MemoryStream())
            {
                using (var image = await Image.LoadAsync(originalStream))
                {
                    image.Mutate(x =>
                        x.AutoOrient().Resize(new ResizeOptions
                        {
                            Size = new Size(width, height),
                            Mode = ResizeMode.Max
                        }));

                    var encoder = validExtensions[imageExtension];

                    await image.SaveAsync(outputMemoryStream, encoder);
                }

                await _s3Client.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = Environment.GetEnvironmentVariable("BUCKET"),
                    Key = key,
                    ContentType = $"image/{imageExtension}",
                    InputStream = outputMemoryStream,
                    TagSet = new List<Tag>
                    {
                        new Tag
                        {
                            Key = "lifetime",
                            Value = "transient"
                        }
                    }
                });
            }

            return new APIGatewayProxyResponse
            {
                StatusCode = 301,
                Headers = new Dictionary<string, string>
                {
                    {"location", $"{Environment.GetEnvironmentVariable("URL")}/{key}"}
                },
                Body = string.Empty
            };
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error: {ex.Message}");
            throw;
        }
    }
}