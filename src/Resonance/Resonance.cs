using System.Reflection;
using Elements.Assets;
using FrooxEngine;
using HarmonyLib;
using ResoniteModLoader;

namespace Resonance;

public partial class Resonance : ResoniteMod
{
    public override string Name =>
        "<color=hero.cyan>🔊</color><color=hero.purple>🎶</color> Resonance";

    public override string Author => "Cyro";

    public override string Version =>
        typeof(Resonance)
            .Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? "";

    public override string Link => "https://github.com/SeyfertGames/Resonance";

    public static ModConfiguration? Config;

    public override void OnEngineInit()
    {
        Config = GetConfiguration();
        Config!.Save(true);
        Harmony harmony = new("net.Cyro.Resonance");
        harmony.PatchAll();

        var readMethod = typeof(VideoTextureProvider)
            .GetMethod("Read", BindingFlags.Instance | BindingFlags.Public)
            ?.MakeGenericMethod(typeof(StereoSample));
        if (readMethod != null)
            harmony.Patch(
                readMethod,
                postfix: new HarmonyMethod(
                    typeof(VideoTextureProviderPatcher),
                    nameof(VideoTextureProviderPatcher.Read_Postfix)
                )
            );

        HandleEvents();
    }

    [HarmonyPatch(typeof(VideoTextureProvider))]
    static class VideoTextureProviderPatcher
    {
        [HarmonyPostfix]
        [HarmonyPatch("OnAwake")]
        public static void OnAwake_Postfix(VideoTextureProvider __instance)
        {
            if (!Enabled)
                return;

            __instance.ReferenceID.ExtractIDs(out _, out byte user);
            if (__instance.LocalUser != __instance.World.GetUserByAllocationID(user))
                return;

            __instance.RunSynchronously(() =>
            {
                int sampleRate = __instance.AudioSystem.OutputSampleRate;

                FFTStreamSettings settings = new(
                    VisibleBins,
                    sampleRate,
                    (CSCore.DSP.FftSize)ConfigFftWidth,
                    NoiseFloor,
                    AutoGainSpeed,
                    Smoothing,
                    Gain,
                    Normalize_Fft,
                    AutoGain,
                    Quantize_Bins
                );

                VideoStreamHandler streamHandler = new(__instance, settings);
                streamHandler.Setup();

                __instance.Destroyed += VideoStreamHandler.Destroy;
            });
        }

        public static void Read_Postfix(VideoTextureProvider __instance, Span<StereoSample> buffer)
        {
            var world = __instance.World;
            if (!Enabled || world.Focus != World.WorldFocus.Focused)
                return;

            if (VideoStreamHandler.FFTDict.TryGetValue(__instance, out VideoStreamHandler? handler))
                handler.UpdateFFTData(buffer);
        }
    }

    [HarmonyPatch(typeof(UserAudioStream<StereoSample>))]
    static class UserAudioStreamPatcher
    {
        [HarmonyPostfix]
        [HarmonyPatch("OnAwake")]
        public static void OnAwake_Postfix(UserAudioStream<StereoSample> __instance)
        {
            if (!Enabled)
                return;

            __instance.ReferenceID.ExtractIDs(out _, out byte user);
            if (__instance.LocalUser != __instance.World.GetUserByAllocationID(user))
                return;

            __instance.RunSynchronously(() =>
            {
                int index = __instance.TargetDeviceIndex ?? -1;

                //44100;
                int sampleRate;

                // Check if we're using SDL. Either non-Windows platform or ForceSDLAudio launch argument
                // Filter out Steam Voice as a special case. Unless it's the only and Default option somehow.
                Type? audioInputType =
                    __instance.AudioSystem.AudioInputCount > 1
                        ? __instance
                            .AudioSystem.AudioInputs.First(ain => ain.DeviceID != "SteamVoice")
                            .GetType()
                        : __instance.AudioSystem.DefaultAudioInput.GetType();

                if (audioInputType.Name != "SDLRecordingDevice")
                {
                    sampleRate =
                        index > 0
                            ? __instance.AudioSystem.AudioInputs[index].SampleRate
                            : __instance.AudioSystem.DefaultAudioInput.SampleRate;
                }
                else
                {
                    // We need to cast AudioInput to SDLRecordingDevice to get Format.spec.Freq and set sampleRate.
                    Resonance.Msg(
                        "AudioInput is actually SDLRecordingDevice on Linux. Casting and getting sample rate..."
                    );

                    var selectedInput =
                        index > 0
                            ? __instance.AudioSystem.AudioInputs[index]
                            : __instance.AudioSystem.DefaultAudioInput;

                    ValueTuple<SDL3.SDL.AudioSpec, int> sdlRecordingDevice_Format =
                        (ValueTuple<SDL3.SDL.AudioSpec, int>)
                            audioInputType.GetProperty("Format")!.GetValue(selectedInput)!;

                    sampleRate = sdlRecordingDevice_Format.Item1.Freq;
                }

                FFTStreamSettings settings = new(
                    VisibleBins,
                    sampleRate,
                    (CSCore.DSP.FftSize)ConfigFftWidth,
                    NoiseFloor,
                    AutoGainSpeed,
                    Smoothing,
                    Gain,
                    Normalize_Fft,
                    AutoGain,
                    Quantize_Bins
                );

                FFTStreamHandler streamHandler = new(__instance, settings);
                streamHandler.Setup();
                streamHandler.PrintDebugInfo();

                var audioStream = __instance.Stream.Target;
                if (audioStream != null && LowLatencyAudio)
                {
                    audioStream.BufferSize.Value = 12000;
                    audioStream.MinimumBufferDelay.Value = 0.05f;
                }

                __instance.Destroyed += FFTStreamHandler.Destroy;
            });
        }

        [HarmonyPostfix]
        [HarmonyPatch("OnNewAudioData")]
        public static void OnNewAudioData_Postfix(
            UserAudioStream<StereoSample> __instance,
            Span<StereoSample> buffer,
            ref int ___lastDeviceIndex
        )
        {
            var world = __instance.World;
            if (
                !Enabled
                || world.Focus != World.WorldFocus.Focused
                || __instance.LocalUser.IsSilenced
                || (
                    ContactsDialog.RecordingVoiceMessage
                    && ___lastDeviceIndex == __instance.AudioSystem.DefaultAudioInputIndex
                )
            )
                return;

            if (FFTStreamHandler.FFTDict.TryGetValue(__instance, out FFTStreamHandler? handler))
                handler.UpdateFFTData(buffer);
        }
    }
}
