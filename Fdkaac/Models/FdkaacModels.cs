namespace Encoder127c.Fdkaac.Models;

internal sealed record FdkaacPlatform(string Id, string ExecutableName);

internal sealed record FdkaacBuild(Uri DownloadUri, string Sha256);
