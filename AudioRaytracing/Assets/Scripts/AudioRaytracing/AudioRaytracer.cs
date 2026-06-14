using System.Collections.Generic;
using System.Linq;
using UnityEngine;

public class AudioRaytracer : MonoBehaviour
{
    [SerializeField] private ComputeShader _computeShader;

    [Header("Raytracing Settings")]
    [SerializeField] private int _rayCount = 64;    //must be a multiple of 64
    [SerializeField] private int _maxBounces = 3;
    [SerializeField] private float _receiverSensitivity = 15f;
    [SerializeField] private float _directSensitivity = 6f;
    [SerializeField] private float _checkInterval = 0.1f;

    [Header("Audio Sources")]
    [SerializeField] private AudioSource[] _audioSources;
    [SerializeField] private bool _autoFindSources = false;

    private int _kernelIndex;
    private float _timeSinceLastCheck;
    private int _pathStride; //maxBoundes + 1

    private ComputeBuffer _cubesBuffer;
    private ComputeBuffer _directionsBuffer;
    private ComputeBuffer _rayVolumesBuffer;
    private ComputeBuffer _rayPathBuffer;
    private ComputeBuffer _rayArrivalDirsBuffer;
    private ComputeBuffer _rayEnergyBinsBuffer;

    private float[] _rayVolumesReadback;
    private Vector3[] _rayArrivalDirsReadback;
    private Vector2[] _rayEnergyBinsReadback;

    [Header("Volume Smoothing")]
    [SerializeField] private float _smoothingSpeed = 8f;
    [SerializeField] private float _panSmoothingSpeed = 5f;

    [Header("Line of Sight")]
    [SerializeField] private LayerMask _occlusionMask = ~0;
    [SerializeField] private float _probeRadius = 0.35f;
    [SerializeField] private float _directPathThreshold = 0.2f;

    private Vector3[] _probeOffsets = new Vector3[5];
    private Vector3[] _probeFroms = new Vector3[5];  //for gizmos

    [Header("Reverb")]
    [SerializeField] private bool _enableReverb = true;
    [SerializeField] private int _reverbBinCount = 64;
    [SerializeField] private float _reverbMaxTime = 1.5f;        //length of the impulse-response window in seconds
    [SerializeField] private float _speedOfSound = 343f;
    [SerializeField] private float _reverbSensitivity = 50f;     //reflected energy -> wet level
    [SerializeField] private float _reverbSmoothingSpeed = 4f;
    [SerializeField] private float _maxDecayTime = 12f;

    [Header("Frequency Bands")]
    [SerializeField, Range(0f, 1f)] private float _lowAbsorption = 0.9f;   //bass survives bounces
    [SerializeField, Range(0f, 1f)] private float _highAbsorption = 0.6f;  //treble is absorbed faster

    [Header("Occlusion Muffling")]
    [SerializeField] private bool _enableOcclusionMuffling = true;
    [SerializeField] private float _openCutoff = 22000f;     //clear line of sight
    [SerializeField] private float _occludedCutoff = 700f;   //deep in the acoustic shadow
    [SerializeField] private float _diffractionRange = 3f;   //metres of sidestep before fully muffled
    [SerializeField] private int _diffractionSteps = 6;      //shadow-depth search resolution

    private float[] _echogramLow;
    private float[] _echogramHigh;
    private float _binDuration;

    private readonly List<TracedSource> _sources = new List<TracedSource>();

    class TracedSource
    {
        public AudioSource source;
        public AudioReverbFilter reverbFilter;
        public AudioLowPassFilter lowPassFilter;

        public float currentVolume, targetVolume;
        public float currentPan, targetPan;

        public float currentDecayTime = 0.1f, targetDecayTime = 0.1f;
        public float currentDecayHFRatio = 1f, targetDecayHFRatio = 1f;
        public float currentReverbLevel = -10000f, targetReverbLevel = -10000f;
        public float currentRoomHF = 0f, targetRoomHF = 0f;
        public float currentReflectionsLevel = -10000f, targetReflectionsLevel = -10000f;
        public float currentReflectionsDelay, targetReflectionsDelay;
        public float currentReverbDelay, targetReverbDelay;
        public float currentCutoff = 22000f, targetCutoff = 22000f;

        public float lastVisibility;

        //gizmo data
        public Vector3[] rayPaths;
        public readonly bool[] probeBlocked = new bool[5];
    }

    //needs to match the struct layout in the AudioRaytracing compute shader (2 x float3 = 24 bytes)
    struct Cube
    {
        public Vector3 min;
        public Vector3 max;
    }

    void OnEnable()
    {
        if (_computeShader == null) return;

        _kernelIndex = _computeShader.FindKernel("CSMain");

        _pathStride = _maxBounces + 1;

        //geometry baking
        RebuildCubeBuffer();

        //ray setup
        Vector3[] directions = FibonacciSphere.GenerateDirections(_rayCount);
        _directionsBuffer = new ComputeBuffer(_rayCount, sizeof(float) * 3);
        _directionsBuffer.SetData(directions);

        //output buffers
        _rayVolumesBuffer = new ComputeBuffer(_rayCount, sizeof(float));
        _rayVolumesReadback = new float[_rayCount];

        _rayPathBuffer = new ComputeBuffer(_rayCount * _pathStride, sizeof(float) * 3);

        _rayArrivalDirsBuffer = new ComputeBuffer(_rayCount, sizeof(float) * 3);
        _rayArrivalDirsReadback = new Vector3[_rayCount];

        //reverb
        _reverbBinCount = Mathf.Max(1, _reverbBinCount);
        _reverbMaxTime = Mathf.Max(0.01f, _reverbMaxTime);
        _binDuration = _reverbMaxTime / _reverbBinCount;

        _rayEnergyBinsBuffer = new ComputeBuffer(_rayCount * _reverbBinCount, sizeof(float) * 2);
        _rayEnergyBinsReadback = new Vector2[_rayCount * _reverbBinCount];
        _echogramLow = new float[_reverbBinCount];
        _echogramHigh = new float[_reverbBinCount];

        BuildSourceList();
    }
    void OnDisable()
    {
        //release existing buffers
        _cubesBuffer?.Release();
        _cubesBuffer = null;

        _directionsBuffer?.Release();
        _directionsBuffer = null;

        _rayVolumesBuffer?.Release();
        _rayVolumesBuffer = null;

        _rayPathBuffer?.Release();
        _rayPathBuffer = null;

        _rayVolumesReadback = null;

        _rayArrivalDirsBuffer?.Release();
        _rayArrivalDirsBuffer = null;
        _rayArrivalDirsReadback = null;

        _rayEnergyBinsBuffer?.Release();
        _rayEnergyBinsBuffer = null;
        _rayEnergyBinsReadback = null;
        _echogramLow = null;
        _echogramHigh = null;

        _sources.Clear();
    }

    void Update()
    {
        _timeSinceLastCheck += Time.deltaTime;
        if (_timeSinceLastCheck >= _checkInterval)
        {
            for (int i = 0; i < _sources.Count; i++)
                TraceAcoustics(_sources[i]);
            _timeSinceLastCheck = 0f;
        }

        //smoothing runs every frame for every source
        for (int i = 0; i < _sources.Count; i++)
            ApplySmoothing(_sources[i]);
    }

    //collects the audio sources we should trace and prepares their filters
    void BuildSourceList()
    {
        _sources.Clear();

        AudioSource[] candidates = _autoFindSources ? FindObjectsOfType<AudioSource>() : _audioSources;
        if (candidates == null) return;

        foreach (AudioSource src in candidates.Where(s => s != null).Distinct())
            _sources.Add(CreateTracedSource(src));
    }

    //wraps an AudioSource in per source state and makes sure it has the filters it needs
    TracedSource CreateTracedSource(AudioSource src)
    {
        var ts = new TracedSource { source = src };

        //reverb & low-pass filters need to live on the sources own GameObject to affect its output
        ts.reverbFilter = src.GetComponent<AudioReverbFilter>();
        if (ts.reverbFilter == null && _enableReverb)
            ts.reverbFilter = src.gameObject.AddComponent<AudioReverbFilter>();
        if (ts.reverbFilter != null)
            ts.reverbFilter.reverbPreset = AudioReverbPreset.User;

        ts.lowPassFilter = src.GetComponent<AudioLowPassFilter>();
        if (ts.lowPassFilter == null && _enableOcclusionMuffling)
            ts.lowPassFilter = src.gameObject.AddComponent<AudioLowPassFilter>();

        return ts;
    }

    public void RegisterSource(AudioSource src)
    {
        if (src == null || _sources.Any(s => s.source == src)) return;
        _sources.Add(CreateTracedSource(src));
    }

    public void UnregisterSource(AudioSource src)
    {
        _sources.RemoveAll(s => s.source == src);
    }

    //needs to be called when geometry changes during runtime
    public void RebuildCubeBuffer()
    {
        BoxCollider[] colliders = FindObjectsOfType<BoxCollider>();

        Cube[] cubes = colliders
            .Select(bc => new Cube { min = bc.bounds.min, max = bc.bounds.max })
            .ToArray();

        //compute buffers complain when they have 0 elements
        if (cubes.Length == 0)
            cubes = new[] { new Cube { min = Vector3.zero, max = Vector3.zero } };

        _cubesBuffer?.Release();
        _cubesBuffer = new ComputeBuffer(cubes.Length, sizeof(float) * 6); // 2 x float3
        _cubesBuffer.SetData(cubes);
    }

    //run compute shader for a single source and get back results
    void TraceAcoustics(TracedSource ts)
    {
        if (_computeShader == null || _cubesBuffer == null || _directionsBuffer == null || ts.source == null)
            return;

        //make sure this source has somewhere to store its ray paths for gizmos
        if (ts.rayPaths == null || ts.rayPaths.Length != _rayCount * _pathStride)
            ts.rayPaths = new Vector3[_rayCount * _pathStride];

        //check if the audio source is visible from the listener's position
        Vector3 listenerPos = transform.position;
        Vector3 sourcePos = ts.source.transform.position;
        Vector3 toSource = sourcePos - listenerPos;
        float dist = toSource.magnitude;

        ts.lastVisibility = ComputeVisibility(listenerPos, sourcePos, ts.probeBlocked);
        ts.targetCutoff = Mathf.Lerp(_occludedCutoff, _openCutoff, ComputeOpenness(listenerPos, sourcePos));

        if (!_enableReverb && ts.lastVisibility > _directPathThreshold)
        {
            float directVolumeOnly = Mathf.Clamp01(_directSensitivity / (dist + 1e-3f) * ts.lastVisibility);

            ts.targetVolume = directVolumeOnly;
            ts.targetPan = Mathf.Clamp(Vector3.Dot(toSource.normalized, transform.right), -1f, 1f);
            return;
        }

        //settings parameters for compute shader
        _computeShader.SetBuffer(_kernelIndex, "_Cubes", _cubesBuffer);
        _computeShader.SetInt("_CubeCount", _cubesBuffer.count);

        _computeShader.SetVector("_ListenerPos", listenerPos);
        _computeShader.SetVector("_SourcePos", sourcePos);

        _computeShader.SetBuffer(_kernelIndex, "_Directions", _directionsBuffer);
        _computeShader.SetInt("_RayCount", _rayCount);
        _computeShader.SetInt("_MaxBounces", _maxBounces);
        _computeShader.SetInt("_PathStride", _pathStride);

        _computeShader.SetBuffer(_kernelIndex, "_RayVolumes", _rayVolumesBuffer);
        _computeShader.SetBuffer(_kernelIndex, "_RayPathBuffer", _rayPathBuffer);
        _computeShader.SetBuffer(_kernelIndex, "_RayArrivalDirs", _rayArrivalDirsBuffer);

        _computeShader.SetBuffer(_kernelIndex, "_RayEnergyBins", _rayEnergyBinsBuffer);
        _computeShader.SetInt("_BinCount", _reverbBinCount);
        _computeShader.SetFloat("_BinDuration", _binDuration);
        _computeShader.SetFloat("_SpeedOfSound", _speedOfSound);
        _computeShader.SetFloat("_AbsorbLow", _lowAbsorption);
        _computeShader.SetFloat("_AbsorbHigh", _highAbsorption);

        //send compute shader to GPU
        int threadGroups = Mathf.CeilToInt(_rayCount / 64.0f);
        _computeShader.Dispatch(_kernelIndex, threadGroups, 1, 1);

        //get results back from GPU
        _rayPathBuffer.GetData(ts.rayPaths);
        _rayVolumesBuffer.GetData(_rayVolumesReadback);
        _rayArrivalDirsBuffer.GetData(_rayArrivalDirsReadback);
        if (_enableReverb)
            _rayEnergyBinsBuffer.GetData(_rayEnergyBinsReadback);

        //average volume
        float totalVolume = 0f;

        for (int i = 0; i < _rayCount; i++)
        {
            if (_rayVolumesReadback[i] > 0f)
            {
                totalVolume += _rayVolumesReadback[i];
            }
        }

        float averageEnergy = totalVolume / _rayCount;
        float raytracedVolume = Mathf.Clamp01(Mathf.Sqrt(averageEnergy) * _receiverSensitivity);
        ts.targetVolume = raytracedVolume;

        Vector3 weightedDir = Vector3.zero;
        for (int i = 0; i < _rayCount; i++)
            weightedDir += _rayArrivalDirsReadback[i];

        ts.targetPan = weightedDir.sqrMagnitude > 1e-6f
                   ? Mathf.Clamp(Vector3.Dot(weightedDir.normalized, transform.right), -1f, 1f)
                   : 0f;

        if (ts.lastVisibility > 0f)
        {
            float directVolume = Mathf.Clamp01(_directSensitivity / (dist + 1e-3f));
            float directPan = Mathf.Clamp(
                Vector3.Dot(toSource.normalized, transform.right), -1f, 1f);

            float blend = Mathf.Clamp01(ts.lastVisibility / _directPathThreshold);

            ts.targetVolume = Mathf.Clamp01(raytracedVolume + directVolume * ts.lastVisibility);
            ts.targetPan = Mathf.Lerp(ts.targetPan, directPan, blend);
        }
        if (_enableReverb)
            UpdateReverb(ts);
    }

    //helper functions 
    void ApplySmoothing(TracedSource ts)
    {
        if (ts.source == null) return;

        ts.currentVolume = Mathf.Lerp(ts.currentVolume, ts.targetVolume, Time.deltaTime * _smoothingSpeed);
        ts.source.volume = ts.currentVolume;

        ts.currentPan = Mathf.Lerp(ts.currentPan, ts.targetPan, Time.deltaTime * _panSmoothingSpeed);
        ts.source.panStereo = ts.currentPan;

        if (_enableReverb && ts.reverbFilter != null)
        {
            float k = Time.deltaTime * _reverbSmoothingSpeed;
            ts.currentDecayTime = Mathf.Lerp(ts.currentDecayTime, ts.targetDecayTime, k);
            ts.currentDecayHFRatio = Mathf.Lerp(ts.currentDecayHFRatio, ts.targetDecayHFRatio, k);
            ts.currentReverbLevel = Mathf.Lerp(ts.currentReverbLevel, ts.targetReverbLevel, k);
            ts.currentRoomHF = Mathf.Lerp(ts.currentRoomHF, ts.targetRoomHF, k);
            ts.currentReflectionsLevel = Mathf.Lerp(ts.currentReflectionsLevel, ts.targetReflectionsLevel, k);
            ts.currentReflectionsDelay = Mathf.Lerp(ts.currentReflectionsDelay, ts.targetReflectionsDelay, k);
            ts.currentReverbDelay = Mathf.Lerp(ts.currentReverbDelay, ts.targetReverbDelay, k);

            ts.reverbFilter.decayTime = ts.currentDecayTime;
            ts.reverbFilter.decayHFRatio = ts.currentDecayHFRatio;
            ts.reverbFilter.reverbLevel = ts.currentReverbLevel;
            ts.reverbFilter.roomHF = ts.currentRoomHF;
            ts.reverbFilter.reflectionsLevel = ts.currentReflectionsLevel;
            ts.reverbFilter.reflectionsDelay = ts.currentReflectionsDelay;
            ts.reverbFilter.reverbDelay = ts.currentReverbDelay;
            ts.reverbFilter.dryLevel = 0f;
        }

        if (_enableOcclusionMuffling && ts.lowPassFilter != null)
        {
            ts.currentCutoff = Mathf.Lerp(ts.currentCutoff, ts.targetCutoff, Time.deltaTime * _reverbSmoothingSpeed);
            ts.lowPassFilter.cutoffFrequency = ts.currentCutoff;
        }
    }

    //helper functions
    void UpdateProbeOffsets(Vector3 listenerPos)
    {
        _probeOffsets[0] = Vector3.zero;                     //center
        _probeOffsets[1] = transform.right * _probeRadius;   //right ear
        _probeOffsets[2] = -transform.right * _probeRadius;  //left ear
        _probeOffsets[3] = transform.up * _probeRadius;      //top of head
        _probeOffsets[4] = -transform.up * _probeRadius;     //chin

        for (int i = 0; i < _probeOffsets.Length; i++)
            _probeFroms[i] = listenerPos + _probeOffsets[i];
    }

    float ComputeVisibility(Vector3 listenerPos, Vector3 sourcePos, bool[] probeBlocked)
    {
        UpdateProbeOffsets(listenerPos);
        int clear = 0;
        for (int i = 0; i < _probeFroms.Length; i++)
        {
            probeBlocked[i] = Physics.Linecast(_probeFroms[i], sourcePos, _occlusionMask);
            if (!probeBlocked[i]) clear++;
        }
        return clear / (float)_probeFroms.Length;
    }

    //0 = deep in the acoustic shadow, 1 = clear line of sight.
    float ComputeOpenness(Vector3 listenerPos, Vector3 sourcePos)
    {
        if (!Physics.Linecast(listenerPos, sourcePos, _occlusionMask))
            return 1f;

        Vector3 dir = (sourcePos - listenerPos).normalized;
        Vector3 right = Vector3.Cross(dir, Vector3.up);
        if (right.sqrMagnitude < 1e-4f) right = transform.right;   //source directly above/below
        right.Normalize();
        Vector3 up = Vector3.Cross(right, dir).normalized;

        float range = Mathf.Max(0.01f, _diffractionRange);
        int steps = Mathf.Max(1, _diffractionSteps);
        for (int s = 1; s <= steps; s++)
        {
            float radius = range * s / steps;
            //the smallest sidestep that regains line of sight = how deep in shadow we are
            if (!Physics.Linecast(listenerPos + right * radius, sourcePos, _occlusionMask) ||
                !Physics.Linecast(listenerPos - right * radius, sourcePos, _occlusionMask) ||
                !Physics.Linecast(listenerPos + up * radius, sourcePos, _occlusionMask) ||
                !Physics.Linecast(listenerPos - up * radius, sourcePos, _occlusionMask))
            {
                return 1f - radius / range;
            }
        }
        return 0f;
    }

    void UpdateReverb(TracedSource ts)
    {
        System.Array.Clear(_echogramLow, 0, _echogramLow.Length);
        System.Array.Clear(_echogramHigh, 0, _echogramHigh.Length);
        for (int r = 0; r < _rayCount; r++)
        {
            int baseIdx = r * _reverbBinCount;
            for (int b = 0; b < _reverbBinCount; b++)
            {
                Vector2 v = _rayEnergyBinsReadback[baseIdx + b];
                _echogramLow[b] += v.x;
                _echogramHigh[b] += v.y;
            }
        }

        float totalLow = 0f, totalHigh = 0f;
        int firstBin = -1;
        for (int b = 0; b < _reverbBinCount; b++)
        {
            totalLow += _echogramLow[b];
            totalHigh += _echogramHigh[b];
            if (firstBin < 0 && (_echogramLow[b] + _echogramHigh[b]) > 0f)
                firstBin = b;
        }

        float totalAll = totalLow + totalHigh;

        //no reflections reached the source -> let the reverb fade out
        if (totalAll <= 1e-9f)
        {
            ts.targetReverbLevel = -10000f;
            ts.targetReflectionsLevel = -10000f;
            ts.targetRoomHF = 0f;
            ts.targetDecayTime = 0.1f;
            ts.targetDecayHFRatio = 1f;
            return;
        }

        float rt60Low = (totalLow > 1e-9f) ? EstimateRT60(_echogramLow, totalLow) : 0.1f;
        float rt60High = (totalHigh > 1e-9f) ? EstimateRT60(_echogramHigh, totalHigh) : 0.1f;

        ts.targetDecayTime = Mathf.Clamp(rt60Low, 0.1f, _maxDecayTime);

        //high band usually decays faster -> ratio < 1 darkens the tail over time
        ts.targetDecayHFRatio = Mathf.Clamp(rt60High / Mathf.Max(rt60Low, 1e-3f), 0.1f, 2f);

        ts.targetReverbLevel = Mathf.Lerp(-10000f, 0f, Mathf.Clamp01(totalLow * _reverbSensitivity));

        //roomHF attenuates the reverb's treble when little high-frequency energy survives
        ts.targetRoomHF = Mathf.Lerp(-10000f, 0f, Mathf.Clamp01(totalHigh * _reverbSensitivity));

        //early reflections = energy arriving in the first ~20% of the window
        int earlyBins = Mathf.Max(1, _reverbBinCount / 5);
        float earlyEnergy = 0f;
        for (int b = 0; b < earlyBins; b++) earlyEnergy += _echogramLow[b] + _echogramHigh[b];
        ts.targetReflectionsLevel = Mathf.Lerp(-10000f, 0f, Mathf.Clamp01(earlyEnergy * _reverbSensitivity));

        float preDelay = (firstBin >= 0) ? firstBin * _binDuration : 0f;
        ts.targetReflectionsDelay = Mathf.Clamp(preDelay, 0f, 0.3f);
        ts.targetReverbDelay = Mathf.Clamp(preDelay, 0f, 0.1f);
    }

    //Schroeder backward integration of the echogram
    float EstimateRT60(float[] echo, float total)
    {
        float running = total;     //running = remaining energy from bin b to the end (Schroeder curve)
        float prevDb = 0f;
        float prevT = 0f;
        float t5 = -1f;
        float t25 = -1f;

        for (int b = 0; b < _reverbBinCount; b++)
        {
            float db = 10f * Mathf.Log10(Mathf.Max(running, 1e-12f) / total);
            float t = b * _binDuration;

            if (t5 < 0f && db <= -5f)
                t5 = InterpTime(prevT, prevDb, t, db, -5f);
            if (t25 < 0f && db <= -25f)
            {
                t25 = InterpTime(prevT, prevDb, t, db, -25f);
                break;
            }

            prevDb = db;
            prevT = t;
            running -= echo[b];
        }

        if (t5 >= 0f && t25 > t5)
            return 3f * (t25 - t5);     //T20 extrapolated to a full 60 dB drop

        //decay never reached -25 dB inside the window -> reverb is at least this long
        return _reverbMaxTime;
    }

    //linearly interpolate the time at which the dB curve crosses a target level
    float InterpTime(float t0, float db0, float t1, float db1, float target)
    {
        if (Mathf.Abs(db1 - db0) < 1e-6f) return t1;
        float f = (target - db0) / (db1 - db0);
        return Mathf.Lerp(t0, t1, Mathf.Clamp01(f));

    }
    private void OnDrawGizmos()
    {
        if (_sources == null || _sources.Count == 0) return;

        //ray paths, one fan per source
        Gizmos.color = Color.cyan;
        foreach (TracedSource ts in _sources)
        {
            if (ts.rayPaths == null) continue;
            for (int i = 0; i < _rayCount; i++)
            {
                int offset = i * _pathStride;
                for (int b = 0; b < _maxBounces; b++)
                {
                    Vector3 start = ts.rayPaths[offset + b];
                    Vector3 end = ts.rayPaths[offset + b + 1];

                    if (end == Vector3.zero) break;

                    Gizmos.DrawLine(start, end);
                }
            }
        }

        //line of sight probes from the listener to each source
        foreach (TracedSource ts in _sources)
        {
            if (ts.source == null) continue;
            for (int i = 0; i < _probeFroms.Length; i++)
            {
                Gizmos.color = ts.probeBlocked[i] ? Color.red : Color.green;
                Gizmos.DrawLine(_probeFroms[i], ts.source.transform.position);
            }
        }
    }
}