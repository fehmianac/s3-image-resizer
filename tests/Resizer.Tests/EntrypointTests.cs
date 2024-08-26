using Amazon.Lambda.APIGatewayEvents;
using AutoFixture;
using Xunit;

namespace Resizer.Tests;

public class EntrypointTests
{
    private readonly IFixture _fixture;

    public EntrypointTests()
    {
        _fixture = new Fixture();
    }

    [Fact]
    public async Task ReplaceImageWithWebp()
    {
        Environment.SetEnvironmentVariable("BUCKET","cdn.kisshe.com");
        Environment.SetEnvironmentVariable("PREFIX","orginal");
        var entrypoint = new Entrypoint();
        var response = await entrypoint.Handler(new APIGatewayProxyRequest
        {
            QueryStringParameters = new Dictionary<string, string>
            {
                { "path", "961x1200/anonymous/B39C669B-2EFF-4DDC-B04F-1FC1D011111D.webp" }
            }
        }, null);
    }
}