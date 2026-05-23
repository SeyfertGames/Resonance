using CSCore.DSP;
using Elements.Assets;
using FrooxEngine;

namespace Resonance;

public partial class VideoStreamHandler(VideoTextureProvider provider, FFTStreamSettings settings)
{
    public static Dictionary<VideoTextureProvider, VideoStreamHandler> FFTDict = new();

    public readonly VideoTextureProvider Provider =
        provider
        ?? throw new NullReferenceException(
            "VideoStreamHandler REQUIRES a VideoTextureProvider instance!"
        );

    public bool Initialized { get; private set; }
    private float AutoGain => Settings.AutoGain_Enabled ? autoGainFactor : Settings.GraphGain;
    private float autoGainFactor = 1f;

    public FFTStreamSettings Settings => settings;

    private readonly float[] fftData = new float[settings.FftWidthCount];
    private readonly float[] lastFftData = new float[settings.FftWidthCount];

    public readonly ValueStream<float>[] binStreams = new ValueStream<float>[settings.BinSize];

    public readonly ValueStream<float>[] bandStreams = new ValueStream<float>[BAND_RANGES.Length];
    private float[]? gainLookup;
    private float[]? freqLookup;
    private readonly FftProvider fftProvider = new(2, settings.FftWidth)
    {
        WindowFunction = WindowFunctions.Hamming,
    };

    public DynamicVariableSpace? WorkingSpace { get; private set; }

    public Slot? VariableSlot { get; private set; }

    public const string WIDTH_VARIABLE = "fft_stream_width";

    public const string BINSIZE_VARIABLE = "fft_bin_size";

    public const string NORMALIZED_VARIABLE = "fft_data_normalized";

    public static readonly float[] BAND_RANGES = { 20, 60, 250, 500, 2000, 4000, 6000, 20000 };
}
