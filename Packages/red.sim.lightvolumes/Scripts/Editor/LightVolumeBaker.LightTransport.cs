#if UNITY_6000_3_OR_NEWER
using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.LightTransport;
using UnityEngine.LightTransport.PostProcessing;
using UnityEngine.Rendering;

namespace VRCLightVolumes {
    // Unity 6 replaces the Progressive lightmapper's additional baked probes with LightTransport. Light Volume voxels are
    // integrated from a bake delegate, which runs as the last stage of the same Progressive bake.
    internal static partial class LightVolumeBaker {
        private const float LightTransportPushoff = 0.0001f;
        private const ulong SphericalHarmonicsL2Stride = 27 * sizeof(float);

        // Probe ranges follow _progressiveVolumes order inside one shared position buffer.
        private static readonly List<Vector3> _lightTransportPositions = new List<Vector3>();
        private static readonly List<Vector2Int> _lightTransportRanges = new List<Vector2Int>();
        private static LightTransportBake _lightTransportBake;
        private static int _lightTransportNextVolume;

        private sealed class LightTransportBake : IDisposable {
            internal RadeonRaysContext Context;
            internal RadeonRaysWorld World;
            internal BakeProgressState Progress;
            internal RadeonRaysProbeIntegrator Integrator;
            internal RadeonRaysProbePostProcessor PostProcessor;
            internal BufferID Positions;
            internal bool HasPositions;
            internal int DirectSampleCount;
            internal int IndirectSampleCount;
            internal float IndirectScale;

            public void Dispose() {
                if (HasPositions) Context.DestroyBuffer(Positions);
                HasPositions = false;
                Integrator?.Dispose();
                PostProcessor?.Dispose();
                World?.Dispose();
                Progress?.Dispose();
                Context?.Dispose();
            }
        }

        // Records one Light Volume's voxel centers for the LightTransport stage of the active bake.
        private static bool AddLightTransportProbes(LightVolumeInstance volume) {
            if (volume == null) return false;
            LightVolumeTools.Recalculate(volume);
            if (!LightVolumeTools.TryCalculateProbePositions(volume, volume.Resolution, out Vector3[] positions)) return false;
            _lightTransportRanges.Add(new Vector2Int(_lightTransportPositions.Count, positions.Length));
            _lightTransportPositions.AddRange(positions);
            return true;
        }

        // Integrates one Light Volume per call so Unity's progress bar keeps moving. Persistence still waits for bakeCompleted.
        private static void BakeLightTransportProbes(ref float progress, out bool done, InputExtraction.BakeInput bakeInput) {
            done = true;
            if (Application.isPlaying || _unityManager == null || !_progressiveCompletionProbeRegistered || _progressiveProbeResultsReady) return;
            try {
                if (_progressiveVolumes.Count > 0 && _lightTransportBake == null) {
                    _lightTransportNextVolume = 0;
                    _lightTransportBake = CreateLightTransportBake();
                }
                if (_lightTransportNextVolume < _progressiveVolumes.Count) {
                    int index = _lightTransportNextVolume++;
                    LightVolumeInstance volume = _progressiveVolumes[index];
                    try {
                        if (volume != null && volume.LightVolumeManager == _unityManager) {
                            ProgressiveProbeResult result = IntegrateLightTransportProbes(_lightTransportBake, volume, _lightTransportRanges[index]);
                            if (result != null) _progressiveProbeResults.Add(result);
                        }
                    } catch (Exception exception) {
                        Debug.LogError($"[LightVolumes] {exception}", volume);
                    }
                    progress = (float)_lightTransportNextVolume / _progressiveVolumes.Count;
                    done = _lightTransportNextVolume >= _progressiveVolumes.Count;
                    if (!done) return;
                }
            } catch (Exception exception) {
                // Results stay unready, so bakeCompleted reports an unfinished bake instead of finalizing unchanged data.
                Debug.LogError($"[LightVolumes] Can't bake Light Volumes with LightTransport. {exception}", _unityManager);
                ReleaseLightTransportProbes();
                EditorApplication.update += WatchUnityBake;
                return;
            }
            ReleaseLightTransportProbes();
            _progressiveProbeResultsReady = true;
            EditorApplication.update += WatchUnityBake;
        }

        // Builds the ray tracing world from the open scenes and prepares every registered voxel center at once.
        private static LightTransportBake CreateLightTransportBake() {
            if (!Lightmapping.TryGetLightingSettings(out LightingSettings settings)) settings = Lightmapping.lightingSettingsDefaults;
            int sampleMultiplier = Mathf.Max(1, Mathf.RoundToInt(settings.lightProbeSampleCountMultiplier));
            LightTransportBake bake = new LightTransportBake {
                Context = new RadeonRaysContext(),
                World = new RadeonRaysWorld(),
                Progress = new BakeProgressState(),
                Integrator = new RadeonRaysProbeIntegrator(),
                PostProcessor = new RadeonRaysProbePostProcessor(),
                DirectSampleCount = Mathf.Max(1, settings.directSampleCount) * sampleMultiplier,
                IndirectSampleCount = Mathf.Max(1, Mathf.Max(settings.indirectSampleCount, settings.environmentSampleCount)) * sampleMultiplier,
                IndirectScale = settings.indirectScale
            };
            try {
                if (!bake.Context.Initialize()) throw new InvalidOperationException("The LightTransport device context could not be initialized.");
                // PopulateWorld never returns when given the BakeInput passed to the bake delegate (seen on Unity 6000.4), so extract a fresh copy.
                if (!InputExtraction.ExtractFromScene(out InputExtraction.BakeInput bakeInput)) throw new InvalidOperationException("The scene could not be extracted for LightTransport.");
                if (!InputExtraction.PopulateWorld(bakeInput, bake.Progress, bake.Context, bake.World)) throw new InvalidOperationException("The scene could not be loaded into the LightTransport world.");
                if (!bake.PostProcessor.Initialize(bake.Context)) throw new InvalidOperationException("The LightTransport probe post-processor could not be initialized.");

                bake.Positions = bake.Context.CreateBuffer((ulong)_lightTransportPositions.Count, 3 * sizeof(float));
                bake.HasPositions = true;
                BufferSlice<Vector3> positions = new BufferSlice<Vector3>(bake.Positions, 0);
                using (NativeArray<Vector3> positionData = new NativeArray<Vector3>(_lightTransportPositions.ToArray(), Allocator.Temp)) {
                    EventID writeEvent = bake.Context.CreateEvent();
                    bake.Context.WriteBuffer(positions, positionData, writeEvent);
                    bool written = bake.Context.Flush() && bake.Context.Wait(writeEvent);
                    bake.Context.DestroyEvent(writeEvent);
                    if (!written) throw new InvalidOperationException("Light Volume probe positions could not be uploaded.");
                }

                bake.Integrator.SetProgressReporter(bake.Progress);
                bake.Integrator.Prepare(bake.Context, bake.World, positions, LightTransportPushoff, settings.maxBounces);
                return bake;
            } catch {
                bake.Dispose();
                throw;
            }
        }

        // Integrates direct, indirect and validity for one probe range and converts it to the Unity SH format Light Probes use.
        private static ProgressiveProbeResult IntegrateLightTransportProbes(LightTransportBake bake, LightVolumeInstance volume, Vector2Int range) {
            int voxelCount = LightVolumeTools.GetVoxelCount(volume);
            if (voxelCount != range.y) {
                Debug.LogError($"[LightVolumes] Can't save {volume.gameObject.name}. Its resolution changed during the bake.", volume);
                return null;
            }

            RadeonRaysContext context = bake.Context;
            ulong count = (ulong)range.y;
            BufferID direct = context.CreateBuffer(count, SphericalHarmonicsL2Stride);
            BufferID indirect = context.CreateBuffer(count, SphericalHarmonicsL2Stride);
            BufferID scratch0 = context.CreateBuffer(count, SphericalHarmonicsL2Stride);
            BufferID scratch1 = context.CreateBuffer(count, SphericalHarmonicsL2Stride);
            BufferID validity = context.CreateBuffer(count, sizeof(float));
            try {
                BufferSlice<SphericalHarmonicsL2> directSlice = new BufferSlice<SphericalHarmonicsL2>(direct, 0);
                BufferSlice<SphericalHarmonicsL2> indirectSlice = new BufferSlice<SphericalHarmonicsL2>(indirect, 0);
                BufferSlice<SphericalHarmonicsL2> scratch0Slice = new BufferSlice<SphericalHarmonicsL2>(scratch0, 0);
                BufferSlice<SphericalHarmonicsL2> scratch1Slice = new BufferSlice<SphericalHarmonicsL2>(scratch1, 0);
                BufferSlice<float> validitySlice = new BufferSlice<float>(validity, 0);

                CheckLightTransportResult(bake.Integrator.IntegrateDirectRadiance(context, range.x, range.y, bake.DirectSampleCount, false, directSlice));
                CheckLightTransportResult(bake.Integrator.IntegrateIndirectRadiance(context, range.x, range.y, bake.IndirectSampleCount, false, indirectSlice));
                CheckLightTransportResult(bake.Integrator.IntegrateValidity(context, range.x, range.y, bake.IndirectSampleCount, validitySlice));

                // Unity's additional baked probes are deringed by default. Deringing the Unity-format result is the closest LightTransport match.
                RadeonRaysProbePostProcessor postProcessor = bake.PostProcessor;
                if (!postProcessor.ScaleSphericalHarmonicsL2(context, indirectSlice, scratch0Slice, range.y, bake.IndirectScale)
                    || !postProcessor.AddSphericalHarmonicsL2(context, directSlice, scratch0Slice, scratch1Slice, range.y)
                    || !postProcessor.ConvolveRadianceToIrradiance(context, scratch1Slice, scratch0Slice, range.y)
                    || !postProcessor.ConvertToUnityFormat(context, scratch0Slice, scratch1Slice, range.y)
                    || !postProcessor.DeringSphericalHarmonicsL2(context, scratch1Slice, scratch0Slice, range.y)) {
                    throw new InvalidOperationException("LightTransport probe post-processing failed.");
                }

                using (NativeArray<SphericalHarmonicsL2> probeData = new NativeArray<SphericalHarmonicsL2>(range.y, Allocator.Temp))
                using (NativeArray<float> validityData = new NativeArray<float>(range.y, Allocator.Temp)) {
                    EventID probeRead = context.CreateEvent();
                    EventID validityRead = context.CreateEvent();
                    context.ReadBuffer(scratch0Slice, probeData, probeRead);
                    context.ReadBuffer(validitySlice, validityData, validityRead);
                    bool read = context.Flush() && context.Wait(probeRead) && context.Wait(validityRead);
                    context.DestroyEvent(probeRead);
                    context.DestroyEvent(validityRead);
                    if (!read) throw new InvalidOperationException("LightTransport probe results could not be read back.");
                    return CreateProgressiveProbeResult(volume, probeData, validityData);
                }
            } finally {
                context.DestroyBuffer(direct);
                context.DestroyBuffer(indirect);
                context.DestroyBuffer(scratch0);
                context.DestroyBuffer(scratch1);
                context.DestroyBuffer(validity);
            }
        }

        private static void CheckLightTransportResult(IProbeIntegrator.Result result) {
            if (result.type != IProbeIntegrator.ResultType.Success) throw new InvalidOperationException($"LightTransport probe integration failed. {result}");
        }

        // Releases native LightTransport objects and the probe ranges of the active bake.
        private static void ReleaseLightTransportProbes() {
            _lightTransportBake?.Dispose();
            _lightTransportBake = null;
            _lightTransportNextVolume = 0;
            _lightTransportPositions.Clear();
            _lightTransportRanges.Clear();
            _progressiveVolumes.Clear();
        }

        // Removes the bake delegate and every LightTransport resource still owned by this bake.
        private static void CleanupProgressiveProbeRegistrations() {
            ReleaseLightTransportProbes();
            if (!_progressiveCompletionProbeRegistered) return;
            _progressiveCompletionProbeRegistered = false;
            Lightmapping.RemoveBakeDelegate(BakeLightTransportProbes);
        }
    }
}
#endif
