namespace Encoder127c.Encoding.Arguments;

/// <summary>Keep gain measurement and the PCM encoding path in the same sample domain.</summary>
internal static class AudioFilterDefaults
{
    public const int SampleRate = 48000;
    public const string Downmix = "aformat=sample_fmts=flt:sample_rates=48000:channel_layouts=stereo";
}
