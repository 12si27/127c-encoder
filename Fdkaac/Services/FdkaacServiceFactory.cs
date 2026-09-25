using Encoder127c.Fdkaac.Validation;

namespace Encoder127c.Fdkaac.Services;

internal static class FdkaacServiceFactory
{
    public static IFdkaacManager CreateDefault()
    {
        return new FdkaacManager(new FdkaacValidator());
    }
}
