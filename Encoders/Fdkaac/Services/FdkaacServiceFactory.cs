using Encoder127c.Encoders.Fdkaac.Installation;
using Encoder127c.Encoders.Fdkaac.Validation;

namespace Encoder127c.Encoders.Fdkaac.Services;

internal static class FdkaacServiceFactory
{
    private static readonly HttpClient HttpClient = CreateHttpClient();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("127c-encoder");
        return client;
    }

    public static IFdkaacManager CreateDefault()
    {
        var validator = new FdkaacValidator();
        return new FdkaacManager(new FdkaacPackageInstaller(HttpClient, validator), validator);
    }
}
