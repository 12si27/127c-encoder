using Encoder127c.Fdkaac.Validation;

namespace Encoder127c.Fdkaac.Services;

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
        return new FdkaacManager(HttpClient, new FdkaacValidator());
    }
}
