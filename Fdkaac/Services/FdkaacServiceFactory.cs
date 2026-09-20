using Encoder127c.Fdkaac.Builds;
using Encoder127c.Fdkaac.Installation;
using Encoder127c.Fdkaac.Platform;
using Encoder127c.Fdkaac.Validation;

namespace Encoder127c.Fdkaac.Services;

internal static class FdkaacServiceFactory
{
    private static readonly HttpClient HttpClient = FdkaacBuildCatalog.CreateHttpClient();

    public static IFdkaacManager CreateDefault()
    {
        var validator = new FdkaacValidator();
        return new FdkaacManager(
            new FdkaacPlatformResolver(),
            new FdkaacBuildCatalog(HttpClient),
            new FdkaacPackageInstaller(HttpClient, validator),
            validator);
    }
}
