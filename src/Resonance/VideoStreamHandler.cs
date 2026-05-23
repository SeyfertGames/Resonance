using Elements.Assets;
using Elements.Core;
using FrooxEngine;

namespace Resonance;

public partial class VideoStreamHandler
{
    public void Setup()
    {
        FFTDict.Add(Provider, this);

        WorkingSpace =
            Provider.Slot.FindSpace(null!) ?? provider.Slot.AttachComponent<DynamicVariableSpace>();
        VariableSlot = WorkingSpace.Slot.FindChildOrAdd(
            "<color=hero.green>Fft variable drivers</color>",
            false
        );

        SetupBins();
        SetupBands();

        VariableSlot.CreateVariable(WIDTH_VARIABLE, Settings.FftWidthCount, false);
        VariableSlot.CreateVariable(BINSIZE_VARIABLE, Settings.BinSize, false);
        VariableSlot.CreateVariable(NORMALIZED_VARIABLE, Settings.Normalized, false);

        freqLookup = Enumerable
            .Range(0, Settings.FftWidthCount)
            .Select(i => (float)i * settings.SamplingRate / Settings.FftWidthCount)
            .ToArray();

        gainLookup = freqLookup.Select(freq => (float)Math.Log10(freq + 1f)).ToArray();

        Initialized = true;
    }

    public void UpdateFFTData(Span<StereoSample> samples)
    {
        if (!Initialized)
            throw new FFTStreamNotInitializedException();

        foreach (var sample in samples)
            fftProvider.Add(sample[0], sample[1]);

        if (fftProvider.IsNewDataAvailable)
        {
            fftProvider.GetFftData(fftData);

            int samplesAdded = 0;
            int bandIndex = 0;
            float average = 0f;

            for (int i = 0; i < Settings.FftWidthCount / 2; i++)
            {
                if (i < Settings.BinSize && binStreams[i] != null && !binStreams[i].IsDestroyed)
                {
                    float db = 10 * MathX.Log10(fftData[i] * fftData[i]);

                    float normalizedDb = MathX.Clamp(
                        (db + Settings.DbNoiseFloor) / Settings.DbNoiseFloor,
                        0f,
                        1f
                    );

                    float binValue = Settings.Normalized
                        ? normalizedDb * normalizedDb * gainLookup![i]
                        : fftData[i] * fftData[i];

                    float smoothed = lastFftData[i] = MathX.LerpUnclamped(
                        binValue,
                        lastFftData[i],
                        MathX.Clamp(Settings.Smoothing, 0f, 1f)
                    );

                    if (smoothed > 1f)
                        autoGainFactor = Math.Min(1f / smoothed, autoGainFactor);

                    binStreams[i].Value = smoothed * AutoGain;
                    binStreams[i].ForceUpdate();
                }

                if (
                    bandIndex < BAND_RANGES.Length
                    && freqLookup![i] >= BAND_RANGES[bandIndex]
                    && bandStreams[bandIndex] != null
                    && !bandStreams[bandIndex].IsDestroyed
                )
                {
                    bandStreams[bandIndex].Value = average / samplesAdded;
                    bandStreams[bandIndex].ForceUpdate();
                    bandIndex++;
                    average = 0f;
                    samplesAdded = 0;
                }

                samplesAdded++;
                average += fftData[i] * fftData[i];
            }

            autoGainFactor = MathX.LerpUnclamped(autoGainFactor, 1f, Settings.AutoGain_Speed);
        }
    }

    private void SetStreamParams(ValueStream<float> stream, string name)
    {
        stream.Name = name;
        stream.SetInterpolation();
        stream.SetUpdatePeriod(0, 0);
        stream.Encoding = Settings.Quantized ? ValueEncoding.Quantized : ValueEncoding.Full;
        stream.FullFrameBits = 12;
        stream.FullFrameMin = 0f;
        stream.FullFrameMax = 1f;
    }

    private void SetBandStreamParams(ValueStream<float> stream, string name)
    {
        stream.Name = name;
        stream.SetInterpolation();
        stream.SetUpdatePeriod(0, 0);
        stream.Encoding = ValueEncoding.Full;
        stream.FullFrameMin = 0f;
        stream.FullFrameMax = 1f;
    }

    private void SetupBins()
    {
        User localUser = Provider.LocalUser;

        for (int i = 0; i < Settings.BinSize; i++)
        {
            string varName = $"fft_stream_bin_{i}";
            var binName = $"{Provider.ReferenceID}.bin.{i}";
            binStreams[i] = localUser.GetStreamOrAdd<ValueStream<float>>(
                binName,
                (s) => SetStreamParams(s, binName)
            );
            if (
                WorkingSpace?.TryReadValue(varName, out IValue<float> stream)
                ?? false && stream != null
            )
            {
                WorkingSpace.TryWriteValue(varName, binStreams[i]);
                continue;
            }
            VariableSlot?.CreateReferenceVariable<IValue<float>>(varName, binStreams[i], false);
        }
    }

    private void SetupBands()
    {
        User localUser = Provider.LocalUser;
        for (int i = 0; i < bandStreams.Length; i++)
        {
            string varName = $"fft_stream_band_{i}";
            var bandName = $"{Provider.ReferenceID}.band.{i}";
            bandStreams[i] = localUser.GetStreamOrAdd<ValueStream<float>>(
                bandName,
                (s) => SetBandStreamParams(s, bandName)
            );
            if (
                WorkingSpace?.TryReadValue(varName, out IValue<float> stream)
                ?? false && stream != null
            )
            {
                WorkingSpace.TryWriteValue(varName, bandStreams[i]);
                continue;
            }
            VariableSlot?.CreateReferenceVariable<IValue<float>>(varName, bandStreams[i], false);
        }
    }

    public void SetQuantized(bool state)
    {
        if (!Initialized)
            throw new FFTStreamNotInitializedException();

        foreach (var stream in binStreams)
        {
            stream.Encoding = Settings.Quantized ? ValueEncoding.Quantized : ValueEncoding.Full;
        }
        Settings.Quantized = state;
    }

    public void SetNormalized(bool state)
    {
        if (!Initialized)
            throw new FFTStreamNotInitializedException();

        WorkingSpace?.TryWriteValue(NORMALIZED_VARIABLE, state);
        Settings.Normalized = state;
    }

    private void DestroyStreams()
    {
        DestroyBins();
        DestroyBands();
    }

    private void DestroyBins()
    {
        foreach (var stream in binStreams)
        {
            stream.Destroy();
        }
    }

    private void DestroyBands()
    {
        foreach (var stream in bandStreams)
        {
            stream.Destroy();
        }
    }

    public void Destroy()
    {
        FFTDict.Remove(Provider);
        VariableSlot?.Destroy();
        DestroyStreams();
    }

    public static void Destroy(VideoTextureProvider? provider)
    {
        if (provider != null && FFTDict.TryGetValue(provider, out VideoStreamHandler? handler))
        {
            handler.Destroy();
        }
    }

    public static void Destroy(IDestroyable c)
    {
        Destroy(c as VideoTextureProvider);
    }
}
