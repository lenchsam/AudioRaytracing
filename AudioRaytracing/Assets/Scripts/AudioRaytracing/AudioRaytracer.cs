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
    private ComputeBuffer _raySourceHitsBuffer;

    private float[] _rayVolumesReadback;
    private Vector3[] _rayArrivalDirsReadback;
    private Vector2[] _rayEnergyBinsReadback;
    private int[] _raySourceHitsReadback;

    private Vector3[] _rayDirections;
    private Vector3[] _reflectionDirs;    //direction-bucket centres, one per reflection emitter
    private float[] _bucketLow, _bucketHigh, _bucketTime;
    private Vector3[] _bucketDir;

    [Header("Volume Smoothing")]
    [SerializeField] private float _smoothingSpeed = 8f;

    [Header("Directional Spatialization")]
    [SerializeField] private bool _enableDirectionalRendering = true;
    [SerializeField] private float _spatialSmoothingSpeed = 6f;
    [SerializeField] private Transform _listener;

    [Header("Early Reflections")]
    [SerializeField] private bool _enableReflectionEmitters = true;     //needs directional rendering
    [SerializeField] private int _reflectionEmitterCount = 6;
    [SerializeField] private float _earlyWindow = 0.08f;                //seconds after the first arrival treated as early reflections
    [SerializeField] private float _reflectionSensitivity = 12f;
    [SerializeField] private float _reflectionStagger = 0.007f;         //seconds each copy trails the previous one; kills comb-filtering between the sample-locked copies
    [SerializeField] private float _reflectionMinCutoff = 4000f;        //darkest a reflection emitter can get, keeps their tone close to the dry signal

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

    [Header("Surfaces & Air")]
    [SerializeField, Range(0f, 1f)] private float _scattering = 0.4f;      //0 = mirror walls, 1 = fully diffuse
    [SerializeField] private float _airAbsorptionLow = 0.0002f;            //energy lost to the air per metre travelled
    [SerializeField] private float _airAbsorptionHigh = 0.008f;

    [Header("Occlusion Muffling")]
    [SerializeField] private bool _enableOcclusionMuffling = true;
    [SerializeField] private bool _propagationMuffle = true;    //derive muffle from the traced shadow rays instead of the sidestep probe
    [SerializeField] private float _muffleInterpolation = 6f;   //log-frequency cutoff smoothing speed
    [SerializeField] private float _openCutoff = 22000f;        //clear line of sight
    [SerializeField] private float _occludedCutoff = 700f;      //deep in the acoustic shadow
    [SerializeField] private float _diffractionRange = 3f;      //metres of detour around an edge (or probe sidestep) before fully muffled
    [SerializeField] private int _diffractionSteps = 6;         //shadow-depth search resolution, only used for the probe
    [SerializeField, Range(0f, 1f)] private float _wallTransmission = 0.1f;

    [Header("Dynamic Geometry")]
    [SerializeField] private bool _autoUpdateGeometry = true;
    [SerializeField] private float _geometryMoveThreshold = 0.001f;

    private BoxCollider[] _trackedColliders;
    private Cube[] _cubes;

    [Header("Debug")]
    [SerializeField] private bool _drawDebugRays = false;

    private float[] _echogramLow;
    private float[] _echogramHigh;
    private float _binDuration;

    private Transform Listener => _listener != null ? _listener : transform;
    private const string EmitterPrefix = "[AR]";
    private const string EmitterName = "[AR] SpatialEmitter";

    //irregular spacing multipliers so the staggered copies never form a periodic comb,
    //which rings metallically like a flutter echo between parallel walls
    private static readonly float[] StaggerPattern = { 0.7f, 1.6f, 2.4f, 4.1f, 5.3f, 6.1f, 7.6f, 8.7f, 9.7f, 11.3f, 12.7f, 14.1f };

    //corner index pairs forming the 12 edges of a box (corner bit 0 = x, bit 1 = y, bit 2 = z)
    private static readonly int[] CubeEdgePairs =
    {
        0,1, 2,3, 4,5, 6,7,   //x aligned
        0,2, 1,3, 4,6, 5,7,   //y aligned
        0,4, 1,5, 2,6, 3,7    //z aligned
    };
    private readonly Vector3[] _cubeCorners = new Vector3[8];

    private readonly List<TracedSource> _sources = new List<TracedSource>();

    class TracedSource
    {
        public AudioSource source;
        public AudioSource originalSource;
        public Transform anchorTransform;
        public GameObject ownedEmitter;

        public AudioReverbFilter reverbFilter;
        public AudioLowPassFilter lowPassFilter;

        //non-spatialized wet only source so the reverb tail surrounds the listener
        public AudioSource wetSource;
        public AudioLowPassFilter wetLowPass;
        public bool separateWet;

        //one emitter per direction bucket for the early reflections
        public AudioSource[] reflEmitters;
        public AudioLowPassFilter[] reflLowPass;
        public float[] reflCurrentVolume, reflTargetVolume;
        public Vector3[] reflCurrentDir, reflTargetDir;
        public float[] reflCurrentDist, reflTargetDist;
        public float[] reflCurrentCutoff, reflTargetCutoff;

        public Vector3 currentApparentDir, targetApparentDir;
        public float apparentDistance;

        public float currentVolume, targetVolume;

        public float currentDecayTime = 0.1f, targetDecayTime = 0.1f;
        public float currentDecayHFRatio = 1f, targetDecayHFRatio = 1f;
        public float currentReverbLevel = -10000f, targetReverbLevel = -10000f;
        public float currentRoomHF = 0f, targetRoomHF = 0f;
        public float currentReflectionsLevel = -10000f, targetReflectionsLevel = -10000f;
        public float currentReflectionsDelay = 0.02f, targetReflectionsDelay = 0.02f;
        public float currentReverbDelay = 0.01f, targetReverbDelay = 0.01f;
        public float currentCutoff = 22000f, targetCutoff = 22000f;

        public float lastVisibility;

        //edge diffraction
        //the point the sound bends around when occluded
        public bool hasBend;
        public Vector3 bendPoint;

        //gizmo data
        public Vector3[] rayPaths;
        public readonly bool[] probeBlocked = new bool[5];
    }
    
    //needs to match the struct layout in the AudioRaytracing compute shader (5 x float3 = 60 bytes)
    struct Cube
    {
        public Vector3 center;
        public Vector3 halfExtents;
        public Vector3 axisX;
        public Vector3 axisY;
        public Vector3 axisZ;
    }

    void OnEnable()
    {
        if (_computeShader == null) return;

        _kernelIndex = _computeShader.FindKernel("CSMain");

        _pathStride = _maxBounces + 1;

        //geometry baking
        RebuildCubeBuffer();

        //ray setup
        _rayDirections = FibonacciSphere.GenerateDirections(_rayCount);
        _directionsBuffer = new ComputeBuffer(_rayCount, sizeof(float) * 3);
        _directionsBuffer.SetData(_rayDirections);

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

        _raySourceHitsBuffer = new ComputeBuffer(_rayCount, sizeof(int));
        _raySourceHitsReadback = new int[_rayCount];

        if (_listener == null)
        {
            AudioListener al = FindObjectOfType<AudioListener>();
            _listener = (al != null) ? al.transform : transform;
        }

        //direction buckets for the early reflection emitters
        _reflectionEmitterCount = Mathf.Max(2, _reflectionEmitterCount);
        _reflectionDirs = FibonacciSphere.GenerateDirections(_reflectionEmitterCount);
        _bucketLow = new float[_reflectionEmitterCount];
        _bucketHigh = new float[_reflectionEmitterCount];
        _bucketTime = new float[_reflectionEmitterCount];
        _bucketDir = new Vector3[_reflectionEmitterCount];

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

        _raySourceHitsBuffer?.Release();
        _raySourceHitsBuffer = null;
        _raySourceHitsReadback = null;

        foreach (TracedSource ts in _sources)
        {
            if (ts.originalSource != null) ts.originalSource.enabled = true;
            if (ts.ownedEmitter != null) Destroy(ts.ownedEmitter);
        }

        _sources.Clear();
    }

    void Update()
    {
        _timeSinceLastCheck += Time.deltaTime;
        if (_timeSinceLastCheck >= _checkInterval)
        {
            if (_autoUpdateGeometry)
                RefreshCubeBuffer();

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

        foreach (AudioSource src in candidates.Where(s => s != null && !s.gameObject.name.StartsWith(EmitterPrefix)).Distinct())
            _sources.Add(CreateTracedSource(src));
    }

    //wraps an AudioSource in per source state and makes sure it has the filters it needs
    TracedSource CreateTracedSource(AudioSource orig)
    {
        var ts = new TracedSource { originalSource = orig, anchorTransform = orig.transform };

        AudioSource emitter = orig;
        bool startPlaying = false;
        int startSamples = 0;
        if (_enableDirectionalRendering)
        {
            var go = new GameObject(EmitterName);
            go.transform.SetParent(orig.transform, false);
            emitter = go.AddComponent<AudioSource>();

            emitter.clip = orig.clip;
            emitter.outputAudioMixerGroup = orig.outputAudioMixerGroup;
            emitter.loop = orig.loop;
            emitter.pitch = orig.pitch;
            emitter.priority = orig.priority;
            emitter.mute = orig.mute;
            emitter.minDistance = orig.minDistance;
            emitter.maxDistance = orig.maxDistance;
            emitter.bypassEffects = orig.bypassEffects;
            emitter.bypassListenerEffects = orig.bypassListenerEffects;
            emitter.bypassReverbZones = orig.bypassReverbZones;
            emitter.playOnAwake = false;

            startPlaying = orig.isPlaying || orig.playOnAwake;
            startSamples = (orig.clip != null) ? orig.timeSamples : 0;
            orig.Stop();
            orig.enabled = false;

            ts.ownedEmitter = go;
        }

        ts.source = emitter;

        emitter.spatialBlend = 1f;            //full 3D
        emitter.spatialize = true;            //route through Microsoft Spatializer
        emitter.spatializePostEffects = true;
        emitter.dopplerLevel = 0f;

        emitter.rolloffMode = AudioRolloffMode.Custom;
        emitter.SetCustomCurve(AudioSourceCurveType.CustomRolloff, AnimationCurve.Constant(0f, 1f, 1f));

        //with directional rendering the wet signal gets its own nonspatialized source
        //so the reverb tail surrounds the listener instead of coming from a point
        ts.separateWet = _enableDirectionalRendering && _enableReverb;
        if (ts.separateWet)
        {
            AudioSource wet = CreateChildEmitter("[AR] ReverbEmitter", emitter, false);
            wet.volume = 1f;
            ts.wetSource = wet;
            ts.reverbFilter = wet.gameObject.AddComponent<AudioReverbFilter>();
            if (_enableOcclusionMuffling)
                ts.wetLowPass = wet.gameObject.AddComponent<AudioLowPassFilter>();
        }
        else
        {
            ts.reverbFilter = emitter.GetComponent<AudioReverbFilter>();
            if (ts.reverbFilter == null && _enableReverb)
                ts.reverbFilter = emitter.gameObject.AddComponent<AudioReverbFilter>();
        }
        if (ts.reverbFilter != null)
        {
            ts.reverbFilter.reverbPreset = AudioReverbPreset.User;
            ts.reverbFilter.reflectionsDelay = 0.02f;
            ts.reverbFilter.reverbDelay = 0.01f;
        }

        ts.lowPassFilter = emitter.GetComponent<AudioLowPassFilter>();
        if (ts.lowPassFilter == null && _enableOcclusionMuffling)
            ts.lowPassFilter = emitter.gameObject.AddComponent<AudioLowPassFilter>();

        if (_enableDirectionalRendering && _enableReflectionEmitters && _reflectionDirs != null)
        {
            int k = _reflectionDirs.Length;
            ts.reflEmitters = new AudioSource[k];
            ts.reflLowPass = new AudioLowPassFilter[k];
            ts.reflCurrentVolume = new float[k];
            ts.reflTargetVolume = new float[k];
            ts.reflCurrentDir = new Vector3[k];
            ts.reflTargetDir = new Vector3[k];
            ts.reflCurrentDist = new float[k];
            ts.reflTargetDist = new float[k];
            ts.reflCurrentCutoff = new float[k];
            ts.reflTargetCutoff = new float[k];

            for (int i = 0; i < k; i++)
            {
                AudioSource refl = CreateChildEmitter("[AR] ReflectionEmitter", emitter, true);
                ts.reflEmitters[i] = refl;
                ts.reflLowPass[i] = refl.gameObject.AddComponent<AudioLowPassFilter>();
                ts.reflLowPass[i].cutoffFrequency = _openCutoff;
                ts.reflCurrentDir[i] = ts.reflTargetDir[i] = _reflectionDirs[i];
                ts.reflCurrentDist[i] = ts.reflTargetDist[i] = 2f;
                ts.reflCurrentCutoff[i] = ts.reflTargetCutoff[i] = _openCutoff;
            }
        }

        //start every owned emitter on the same sample so they stay in sync
        if (startPlaying)
            PlaySynced(ts, startSamples);

        Vector3 toSource = orig.transform.position - Listener.position;
        ts.currentApparentDir = ts.targetApparentDir =
            (toSource.sqrMagnitude > 1e-8f) ? toSource.normalized : Listener.forward;
        ts.apparentDistance = toSource.magnitude;

        return ts;
    }

    //extra AudioSource that plays the same clip in sample sync with the main emitter
    AudioSource CreateChildEmitter(string name, AudioSource template, bool spatialized)
    {
        var go = new GameObject(name);
        go.transform.SetParent(template.transform, false);
        var src = go.AddComponent<AudioSource>();

        src.clip = template.clip;
        src.outputAudioMixerGroup = template.outputAudioMixerGroup;
        src.loop = template.loop;
        src.pitch = template.pitch;
        src.priority = template.priority;
        src.mute = template.mute;
        src.bypassEffects = template.bypassEffects;
        src.bypassListenerEffects = template.bypassListenerEffects;
        src.bypassReverbZones = template.bypassReverbZones;
        src.playOnAwake = false;
        src.dopplerLevel = 0f;
        src.volume = 0f;

        if (spatialized)
        {
            src.spatialBlend = 1f;
            src.spatialize = true;
            src.spatializePostEffects = true;
            src.minDistance = template.minDistance;
            src.maxDistance = template.maxDistance;
            src.rolloffMode = AudioRolloffMode.Custom;
            src.SetCustomCurve(AudioSourceCurveType.CustomRolloff, AnimationCurve.Constant(0f, 1f, 1f));
        }
        else
        {
            src.spatialBlend = 0f;
            src.spatialize = false;
        }
        return src;
    }

    void PlaySynced(TracedSource ts, int timeSamples)
    {
        double startTime = AudioSettings.dspTime + 0.1;

        Schedule(ts.source, timeSamples, startTime);
        Schedule(ts.wetSource, timeSamples, startTime);
        if (ts.reflEmitters != null)
        {
            for (int i = 0; i < ts.reflEmitters.Length; i++)
            {
                float delay = _reflectionStagger * StaggerPattern[i % StaggerPattern.Length]
                              * (1 + i / StaggerPattern.Length);
                Schedule(ts.reflEmitters[i], timeSamples, startTime + delay);
            }
        }
    }

    static void Schedule(AudioSource src, int timeSamples, double dspTime)
    {
        if (src == null) return;
        src.Stop();
        if (src.clip == null) return;
        src.timeSamples = Mathf.Clamp(timeSamples, 0, src.clip.samples - 1);
        src.PlayScheduled(dspTime);
    }

    public void SetSourceClip(AudioSource original, AudioClip clip, bool play = true)
    {
        foreach (TracedSource ts in _sources)
        {
            if (ts.originalSource != original && ts.source != original) continue;

            //keep the disabled original in step so a raytracer re-enable picks up the right clip
            if (ts.originalSource != null) ts.originalSource.clip = clip;

            if (ts.source != null) ts.source.clip = clip;
            if (ts.wetSource != null) ts.wetSource.clip = clip;
            if (ts.reflEmitters != null)
                foreach (AudioSource refl in ts.reflEmitters)
                    if (refl != null) refl.clip = clip;

            if (play)
                PlaySynced(ts, 0);
            return;
        }

        if (original != null)
        {
            original.clip = clip;
            if (play) original.Play();
        }
    }

    public void RegisterSource(AudioSource src)
    {
        if (src == null || src.gameObject.name.StartsWith(EmitterPrefix) ||
            _sources.Any(s => s.originalSource == src || s.source == src)) return;
        _sources.Add(CreateTracedSource(src));
    }

    public void UnregisterSource(AudioSource src)
    {
        for (int i = _sources.Count - 1; i >= 0; i--)
        {
            TracedSource ts = _sources[i];
            if (ts.originalSource != src && ts.source != src) continue;

            if (ts.originalSource != null) ts.originalSource.enabled = true;
            if (ts.ownedEmitter != null) Destroy(ts.ownedEmitter);
            _sources.RemoveAt(i);
        }
    }

    //needs to be called when geometry changes during runtime
    public void RebuildCubeBuffer()
    {
        _trackedColliders = FindObjectsOfType<BoxCollider>().Where(bc => bc.enabled).ToArray();

        _cubes = _trackedColliders.Select(CubeFromCollider).ToArray();

        //compute buffers complain when they have 0 elements
        if (_cubes.Length == 0)
            _cubes = new[] { new Cube { axisX = Vector3.right, axisY = Vector3.up, axisZ = Vector3.forward } };

        //only reallocate when the count changes, otherwise reuse the existing buffer
        if (_cubesBuffer == null || _cubesBuffer.count != _cubes.Length)
        {
            _cubesBuffer?.Release();
            _cubesBuffer = new ComputeBuffer(_cubes.Length, sizeof(float) * 15); // 5 x float3
        }
        _cubesBuffer.SetData(_cubes);
    }

    //oriented box in world space so rotated colliders keep their true shape
    static Cube CubeFromCollider(BoxCollider bc)
    {
        Transform t = bc.transform;
        Vector3 scale = t.lossyScale;
        return new Cube
        {
            center = t.TransformPoint(bc.center),
            halfExtents = new Vector3(
                Mathf.Abs(bc.size.x * scale.x),
                Mathf.Abs(bc.size.y * scale.y),
                Mathf.Abs(bc.size.z * scale.z)) * 0.5f,
            axisX = t.rotation * Vector3.right,
            axisY = t.rotation * Vector3.up,
            axisZ = t.rotation * Vector3.forward
        };
    }

    void RefreshCubeBuffer()
    {
        if (_trackedColliders == null || _cubesBuffer == null) return;

        float threshold = _geometryMoveThreshold * _geometryMoveThreshold;
        bool changed = false;

        for (int i = 0; i < _trackedColliders.Length; i++)
        {
            BoxCollider bc = _trackedColliders[i];

            //a collider was destroyed or disabled -> topology changed, do a full rescan
            if (bc == null || !bc.enabled)
            {
                RebuildCubeBuffer();
                return;
            }

            Cube fresh = CubeFromCollider(bc);
            Cube cube = _cubes[i];
            if ((cube.center - fresh.center).sqrMagnitude > threshold ||
                (cube.halfExtents - fresh.halfExtents).sqrMagnitude > threshold ||
                (cube.axisX - fresh.axisX).sqrMagnitude > threshold ||
                (cube.axisY - fresh.axisY).sqrMagnitude > threshold ||
                (cube.axisZ - fresh.axisZ).sqrMagnitude > threshold)
            {
                _cubes[i] = fresh;
                changed = true;
            }
        }

        if (changed)
            _cubesBuffer.SetData(_cubes);

    }
    //run compute shader for a single source and get back results
    void TraceAcoustics(TracedSource ts)
    {
        if (_computeShader == null || _cubesBuffer == null || _directionsBuffer == null || ts.source == null)
            return;

        //make sure this source has somewhere to store its ray paths for gizmos
#if UNITY_EDITOR
        if (_drawDebugRays && (ts.rayPaths == null || ts.rayPaths.Length != _rayCount * _pathStride))
            ts.rayPaths = new Vector3[_rayCount * _pathStride];
#endif

        //check if the audio source is visible from the listener's position
        Vector3 listenerPos = Listener.position;
        Vector3 sourcePos = ts.anchorTransform != null ? ts.anchorTransform.position : ts.source.transform.position;
        Vector3 toSource = sourcePos - listenerPos;
        float dist = toSource.magnitude;

        ts.lastVisibility = ComputeVisibility(listenerPos, sourcePos, ts.probeBlocked);

        bool useReflEmitters = _enableReflectionEmitters && ts.reflEmitters != null;

        if (!_enableReverb && !useReflEmitters && ts.lastVisibility > _directPathThreshold)
        {
            ts.hasBend = false;
            float directVolumeOnly = Mathf.Clamp01(_directSensitivity / (dist + 1e-3f) * ts.lastVisibility);
            ts.targetVolume = directVolumeOnly;
            ts.targetApparentDir = (toSource.sqrMagnitude > 1e-8f) ? toSource.normalized : ts.currentApparentDir;
            ts.apparentDistance = dist;
            ts.targetCutoff = ComputeMuffleTarget(ts, listenerPos, sourcePos, 0f);
            return;
        }

        //compute shader dispatch
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
        _computeShader.SetBuffer(_kernelIndex, "_RaySourceHits", _raySourceHitsBuffer);
        _computeShader.SetBuffer(_kernelIndex, "_RayEnergyBins", _rayEnergyBinsBuffer);
        _computeShader.SetInt("_BinCount", _reverbBinCount);
        _computeShader.SetFloat("_BinDuration", _binDuration);
        _computeShader.SetFloat("_SpeedOfSound", _speedOfSound);
        _computeShader.SetFloat("_AbsorbLow", _lowAbsorption);
        _computeShader.SetFloat("_AbsorbHigh", _highAbsorption);
        _computeShader.SetFloat("_ScatterCoeff", _scattering);
        _computeShader.SetFloat("_AirAbsorbLow", _airAbsorptionLow);
        _computeShader.SetFloat("_AirAbsorbHigh", _airAbsorptionHigh);

        //send compute shader to GPU
        int threadGroups = Mathf.CeilToInt(_rayCount / 64.0f);
        _computeShader.Dispatch(_kernelIndex, threadGroups, 1, 1);

        //get results back from GPU
        _rayVolumesBuffer.GetData(_rayVolumesReadback);
        _rayArrivalDirsBuffer.GetData(_rayArrivalDirsReadback);
        _raySourceHitsBuffer.GetData(_raySourceHitsReadback);
        if (_enableReverb)
            _rayEnergyBinsBuffer.GetData(_rayEnergyBinsReadback);

#if UNITY_EDITOR
        if (_drawDebugRays && ts.rayPaths != null)
            _rayPathBuffer.GetData(ts.rayPaths);
#endif

        int raysReachingSource = 0;
        for (int i = 0; i < _rayCount; i++)
            if (_raySourceHitsReadback[i] > 0) raysReachingSource++;
        float reflectedRatio = raysReachingSource / (float)_rayCount;

        //edge diffraction: shortest first-order path around whatever blocks the direct line
        ts.hasBend = false;
        float detourOpenness = 0f;
        float bendPathLen = dist;
        if (ts.lastVisibility < 0.999f && FindDiffractionPath(listenerPos, sourcePos, out Vector3 bend, out float bendLen))
        {
            ts.hasBend = true;
            ts.bendPoint = bend;
            bendPathLen = bendLen;
            float detour = Mathf.Max(0f, bendLen - dist);
            detourOpenness = Mathf.Clamp01(1f - detour / Mathf.Max(_diffractionRange, 0.01f));
        }

        //with a real bend path the shadow depth drives the muffle
        //otherwise fall back to the ratio of this source's rays that reached it
        ts.targetCutoff = ComputeMuffleTarget(ts, listenerPos, sourcePos, ts.hasBend ? detourOpenness : reflectedRatio);

        //heuristic openness only backs up the cases where no bend path exists
        float heuristicOpenness = 0f;
        if (!ts.hasBend)
            heuristicOpenness = _propagationMuffle ? Mathf.Clamp01(reflectedRatio * 4f) : ComputeOpenness(listenerPos, sourcePos);

        if (useReflEmitters)
            UpdateReflectionEmitters(ts, dist);

        //indirect volume
        float totalVolume = 0f;
        int activeRays = 0;
        for (int i = 0; i < _rayCount; i++)
        {
            if (_rayVolumesReadback[i] > 0f)
            {
                totalVolume += _rayVolumesReadback[i];
                activeRays++;
            }
        }

        //blend the global ray average with an average of only the rays that hit something
        float averageEnergy = totalVolume / _rayCount;
        if (activeRays > 0)
        {
            float activeEnergyAverage = totalVolume / activeRays;
            averageEnergy = Mathf.Lerp(averageEnergy, activeEnergyAverage, 0.25f);
        }

        float raytracedVolume = Mathf.Clamp01(Mathf.Sqrt(averageEnergy) * _receiverSensitivity);

        //direct and diffracted volume
        float directVolume = Mathf.Clamp01(_directSensitivity / (dist + 1e-3f));

        float effectiveVisibility = ts.hasBend ? ts.lastVisibility : Mathf.Max(ts.lastVisibility, heuristicOpenness * 0.35f);

        //sound bending around the edge
        //attenuated by the longer path and by how deep
        //into the acoustic shadow the listener stands
        float diffractedVolume = ts.hasBend ? Mathf.Clamp01(_directSensitivity / (bendPathLen + 1e-3f)) * detourOpenness * (1f - ts.lastVisibility) : 0f;

        float transmittedVolume = directVolume * _wallTransmission * (1f - Mathf.Max(effectiveVisibility, detourOpenness));

        //combine indirect reflections with the direct, diffracted and transmitted paths
        //when the reflection emitters are active they carry the reflected energy themselves
        float indirectVolume = useReflEmitters ? 0f : raytracedVolume;
        ts.targetVolume = Mathf.Clamp01(indirectVolume + (directVolume * effectiveVisibility) + diffractedVolume + transmittedVolume);

        //directional positioning
        Vector3 trueDir = (toSource.sqrMagnitude > 1e-8f) ? toSource.normalized : ts.currentApparentDir;
        Vector3 reflectedDir = ComputeArrivalDirection(trueDir);

        //an occluded source is heard from its bend point (the doorway), not through the wall
        Vector3 occludedDir = ts.hasBend ? (ts.bendPoint - listenerPos).normalized : reflectedDir;
        ts.targetApparentDir = Vector3.Slerp(occludedDir, trueDir, Mathf.Clamp01(effectiveVisibility)).normalized;
        ts.apparentDistance = Mathf.Lerp(ts.hasBend ? bendPathLen : dist, dist, Mathf.Clamp01(effectiveVisibility));

        if (_enableReverb)
            UpdateReverb(ts);
    }

    Vector3 ComputeArrivalDirection(Vector3 fallback)
    {
        if (_enableReverb && _rayEnergyBinsReadback != null)
        {
            int firstBin = int.MaxValue;
            for (int i = 0; i < _rayCount; i++)
            {
                if (_rayArrivalDirsReadback[i].sqrMagnitude < 1e-12f) continue;
                int baseIdx = i * _reverbBinCount;
                for (int b = 0; b < _reverbBinCount; b++)
                {
                    Vector2 e = _rayEnergyBinsReadback[baseIdx + b];
                    if (e.x + e.y > 0f)
                    {
                        if (b < firstBin) firstBin = b;
                        break;
                    }
                }
            }

            if (firstBin != int.MaxValue)
            {
                int window = Mathf.Max(1, Mathf.CeilToInt(0.06f / Mathf.Max(_binDuration, 1e-4f)));
                int windowEnd = Mathf.Min(_reverbBinCount, firstBin + window);

                Vector3 early = Vector3.zero;
                for (int i = 0; i < _rayCount; i++)
                {
                    Vector3 d = _rayArrivalDirsReadback[i];
                    if (d.sqrMagnitude < 1e-12f) continue;
                    Vector3 launchDir = d.normalized;

                    int baseIdx = i * _reverbBinCount;
                    float energy = 0f;
                    for (int b = firstBin; b < windowEnd; b++)
                    {
                        Vector2 e = _rayEnergyBinsReadback[baseIdx + b];
                        energy += e.x + e.y;
                    }
                    early += launchDir * energy;
                }

                if (early.sqrMagnitude > 1e-12f) return early.normalized;
            }
        }

        //fallback
        Vector3 sum = Vector3.zero;
        for (int i = 0; i < _rayCount; i++)
            sum += _rayArrivalDirsReadback[i];
        return (sum.sqrMagnitude > 1e-8f) ? sum.normalized : fallback;
    }

    //split the traced early energy into direction buckets and hand each bucket to an emitter
    void UpdateReflectionEmitters(TracedSource ts, float sourceDist)
    {
        int bucketCount = ts.reflEmitters.Length;
        System.Array.Clear(_bucketLow, 0, bucketCount);
        System.Array.Clear(_bucketHigh, 0, bucketCount);
        System.Array.Clear(_bucketTime, 0, bucketCount);
        System.Array.Clear(_bucketDir, 0, bucketCount);

        if (_enableReverb)
        {
            //first arrival across all rays defines the start of the early window
            int firstBin = int.MaxValue;
            for (int i = 0; i < _rayCount; i++)
            {
                int baseIdx = i * _reverbBinCount;
                for (int b = 0; b < _reverbBinCount; b++)
                {
                    Vector2 e = _rayEnergyBinsReadback[baseIdx + b];
                    if (e.x + e.y > 0f)
                    {
                        if (b < firstBin) firstBin = b;
                        break;
                    }
                }
            }

            if (firstBin != int.MaxValue)
            {
                int window = Mathf.Max(1, Mathf.CeilToInt(_earlyWindow / Mathf.Max(_binDuration, 1e-4f)));
                int windowEnd = Mathf.Min(_reverbBinCount, firstBin + window);

                for (int i = 0; i < _rayCount; i++)
                {
                    int baseIdx = i * _reverbBinCount;
                    float low = 0f, high = 0f, time = 0f;
                    for (int b = firstBin; b < windowEnd; b++)
                    {
                        Vector2 e = _rayEnergyBinsReadback[baseIdx + b];
                        low += e.x;
                        high += e.y;
                        time += (e.x + e.y) * (b * _binDuration);
                    }
                    if (low + high <= 0f) continue;

                    int bucket = NearestReflectionBucket(_rayDirections[i]);
                    _bucketLow[bucket] += low;
                    _bucketHigh[bucket] += high;
                    _bucketTime[bucket] += time;
                    _bucketDir[bucket] += _rayDirections[i] * (low + high);
                }
            }
        }
        else
        {
            //no echogram available -> bucket each rays total reflected energy instead
            //skip ray 0 as its volume includes the direct path
            float fallbackTime = sourceDist / Mathf.Max(_speedOfSound, 1f);
            for (int i = 1; i < _rayCount; i++)
            {
                float e = _rayVolumesReadback[i];
                if (e <= 0f) continue;
                int bucket = NearestReflectionBucket(_rayDirections[i]);
                _bucketLow[bucket] += e * 0.5f;
                _bucketHigh[bucket] += e * 0.5f;
                _bucketTime[bucket] += e * fallbackTime;
                _bucketDir[bucket] += _rayDirections[i] * e;
            }
        }

        for (int b = 0; b < bucketCount; b++)
        {
            float total = _bucketLow[b] + _bucketHigh[b];
            if (total <= 1e-12f)
            {
                //keep direction and distance so the emitter fades out in place
                ts.reflTargetVolume[b] = 0f;
                continue;
            }

            ts.reflTargetVolume[b] = Mathf.Clamp01(Mathf.Sqrt(total / _rayCount) * _reflectionSensitivity);
            ts.reflTargetDir[b] = (_bucketDir[b].sqrMagnitude > 1e-12f) ? _bucketDir[b].normalized : _reflectionDirs[b];
            ts.reflTargetDist[b] = Mathf.Max((_bucketTime[b] / total) * _speedOfSound, 0.5f);

            //reflections that lost their treble to absorption arrive darker,
            //but never below the reflection floor so their tone stays close to the dry signal
            float hfRatio = Mathf.Clamp01(_bucketHigh[b] / Mathf.Max(_bucketLow[b], 1e-9f));
            float logMin = Mathf.Log(Mathf.Max(_reflectionMinCutoff, 1f), 2f);
            float logMax = Mathf.Log(Mathf.Max(_openCutoff, 1f), 2f);
            ts.reflTargetCutoff[b] = Mathf.Pow(2f, Mathf.Lerp(logMin, logMax, hfRatio));
        }
    }

    int NearestReflectionBucket(Vector3 dir)
    {
        int best = 0;
        float bestDot = float.MinValue;
        for (int b = 0; b < _reflectionDirs.Length; b++)
        {
            float d = Vector3.Dot(dir, _reflectionDirs[b]);
            if (d > bestDot)
            {
                bestDot = d;
                best = b;
            }
        }
        return best;
    }

    //helper functions 
    void ApplySmoothing(TracedSource ts)
    {
        if (ts.source == null) return;

        ts.currentVolume = Mathf.Lerp(ts.currentVolume, ts.targetVolume, Time.deltaTime * _smoothingSpeed);
        ts.source.volume = ts.currentVolume;

        ts.source.panStereo = 0f;

        if (_enableDirectionalRendering && ts.ownedEmitter != null)
        {
            ts.currentApparentDir = Vector3.Slerp(
                ts.currentApparentDir, ts.targetApparentDir, Time.deltaTime * _spatialSmoothingSpeed);
            if (ts.currentApparentDir.sqrMagnitude > 1e-8f)
                ts.currentApparentDir.Normalize();

            ts.source.transform.position =
                Listener.position + ts.currentApparentDir * Mathf.Max(ts.apparentDistance, 0.01f);
        }

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
            //a dedicated wet source must not leak the dry signal a second time
            ts.reverbFilter.dryLevel = ts.separateWet ? -10000f : 0f;
        }

        if (_enableOcclusionMuffling && ts.lowPassFilter != null)
        {
            float k = 1f - Mathf.Exp(-Time.deltaTime * _muffleInterpolation);
            float curLog = Mathf.Log(Mathf.Max(ts.currentCutoff, 1f), 2f);
            float tgtLog = Mathf.Log(Mathf.Max(ts.targetCutoff, 1f), 2f);
            ts.currentCutoff = Mathf.Pow(2f, Mathf.Lerp(curLog, tgtLog, k));
            ts.lowPassFilter.cutoffFrequency = ts.currentCutoff;
            if (ts.wetLowPass != null)
                ts.wetLowPass.cutoffFrequency = ts.currentCutoff;
        }

        if (ts.reflEmitters != null)
        {
            float kv = Time.deltaTime * _smoothingSpeed;
            float kd = Time.deltaTime * _spatialSmoothingSpeed;
            float kc = 1f - Mathf.Exp(-Time.deltaTime * _muffleInterpolation);
            Vector3 listenerPos = Listener.position;

            for (int i = 0; i < ts.reflEmitters.Length; i++)
            {
                AudioSource refl = ts.reflEmitters[i];
                if (refl == null) continue;

                ts.reflCurrentVolume[i] = Mathf.Lerp(ts.reflCurrentVolume[i], ts.reflTargetVolume[i], kv);
                ts.reflCurrentDir[i] = Vector3.Slerp(ts.reflCurrentDir[i], ts.reflTargetDir[i], kd);
                if (ts.reflCurrentDir[i].sqrMagnitude > 1e-8f)
                    ts.reflCurrentDir[i].Normalize();
                ts.reflCurrentDist[i] = Mathf.Lerp(ts.reflCurrentDist[i], ts.reflTargetDist[i], kd);

                refl.volume = ts.reflCurrentVolume[i];
                refl.transform.position = listenerPos + ts.reflCurrentDir[i] * Mathf.Max(ts.reflCurrentDist[i], 0.1f);

                if (ts.reflLowPass[i] != null)
                {
                    float curLog2 = Mathf.Log(Mathf.Max(ts.reflCurrentCutoff[i], 1f), 2f);
                    float tgtLog2 = Mathf.Log(Mathf.Max(ts.reflTargetCutoff[i], 1f), 2f);
                    ts.reflCurrentCutoff[i] = Mathf.Pow(2f, Mathf.Lerp(curLog2, tgtLog2, kc));
                    ts.reflLowPass[i].cutoffFrequency = ts.reflCurrentCutoff[i];
                }
            }
        }
    }

    //helper functions
    void UpdateProbeOffsets(Vector3 listenerPos)
    {
        _probeOffsets[0] = Vector3.zero;                    //center
        _probeOffsets[1] = Listener.right * _probeRadius;   //right ear
        _probeOffsets[2] = -Listener.right * _probeRadius;  //left ear
        _probeOffsets[3] = Listener.up * _probeRadius;      //top of head
        _probeOffsets[4] = -Listener.up * _probeRadius;     //chin

        for (int i = 0; i < _probeOffsets.Length; i++)
            _probeFroms[i] = listenerPos + _probeOffsets[i];
    }

    float ComputeVisibility(Vector3 listenerPos, Vector3 sourcePos, bool[] probeBlocked)
    {
        UpdateProbeOffsets(listenerPos);
        int clear = 0;
        for (int i = 0; i < _probeFroms.Length; i++)
        {
            probeBlocked[i] = Physics.Linecast(_probeFroms[i], sourcePos,
                _occlusionMask, QueryTriggerInteraction.Ignore);
            if (!probeBlocked[i]) clear++;
        }
        return clear / (float)_probeFroms.Length;
    }

    float ComputeOpenness(Vector3 listenerPos, Vector3 sourcePos)
    {
        // clear straight line -> fully open
        if (!Physics.Linecast(listenerPos, sourcePos, _occlusionMask, QueryTriggerInteraction.Ignore))
            return 1f;

        Vector3 dir = (sourcePos - listenerPos).normalized;
        Vector3 right = Vector3.Cross(dir, Vector3.up);
        if (right.sqrMagnitude < 1e-4f) right = Listener.right;   // source directly above/below
        right.Normalize();
        Vector3 up = Vector3.Cross(right, dir).normalized;

        float range = Mathf.Max(0.01f, _diffractionRange);
        int steps = Mathf.Max(2, _diffractionSteps);
        const int dirCount = 12;                 // ring of escape directions

        float bestClear = range;                 // smallest sidestep that regains line of sight

        for (int a = 0; a < dirCount; a++)
        {
            float ang = (a / (float)dirCount) * Mathf.PI * 2f;
            // bias toward horizontal so floor/ceiling samples don't dominate
            Vector3 axis = (right * Mathf.Cos(ang) + up * (0.4f * Mathf.Sin(ang))).normalized;

            float prev = 0f;
            for (int s = 1; s <= steps; s++)
            {
                float radius = range * s / steps;
                if (radius >= bestClear) break;   //cant beat the current best

                Vector3 probe = listenerPos + axis * radius;

                if (Physics.Linecast(listenerPos, probe, _occlusionMask, QueryTriggerInteraction.Ignore))
                    break;

                if (!Physics.Linecast(probe, sourcePos, _occlusionMask, QueryTriggerInteraction.Ignore))
                {
                    float lo = prev, hi = radius;
                    for (int it = 0; it < 4; it++)
                    {
                        float mid = 0.5f * (lo + hi);
                        Vector3 m = listenerPos + axis * mid;
                        bool reach = !Physics.Linecast(listenerPos, m, _occlusionMask, QueryTriggerInteraction.Ignore);
                        bool sees = reach && !Physics.Linecast(m, sourcePos, _occlusionMask, QueryTriggerInteraction.Ignore);
                        if (sees) hi = mid; else lo = mid;
                    }
                    bestClear = Mathf.Min(bestClear, hi);
                    break;
                }
                prev = radius;
            }
        }

        return Mathf.Clamp01(1f - bestClear / range);
    }

    float ComputeMuffleTarget(TracedSource ts, Vector3 listenerPos, Vector3 sourcePos, float pathOpenness)
    {
        if (_propagationMuffle)
        {
            float openness = Mathf.Max(ts.lastVisibility, pathOpenness);
            return MuffleCutoffFromOpenness(openness);
        }
        return MuffleCutoffFromOpenness(ComputeOpenness(listenerPos, sourcePos));
    }

    //shortest first order bend around the edges of whatever blocks the direct line
    //this is what makes an occluded source sound like it comes from the doorway
    bool FindDiffractionPath(Vector3 listenerPos, Vector3 sourcePos, out Vector3 bendPoint, out float pathLength)
    {
        bendPoint = Vector3.zero;
        pathLength = float.MaxValue;
        bool found = false;

        if (_cubes == null) return false;

        Vector3 toSource = sourcePos - listenerPos;
        float directDist = toSource.magnitude;
        if (directDist < 1e-4f) return false;
        Vector3 dir = toSource / directDist;

        for (int c = 0; c < _cubes.Length; c++)
        {
            //only the boxes actually blocking the direct line diffract it
            if (!RayHitsCube(_cubes[c], listenerPos, dir, directDist)) continue;

            FillCubeCorners(_cubes[c]);
            for (int e = 0; e < CubeEdgePairs.Length; e += 2)
            {
                Vector3 a = _cubeCorners[CubeEdgePairs[e]];
                Vector3 b = _cubeCorners[CubeEdgePairs[e + 1]];

                Vector3 p = ShortestBendOnEdge(listenerPos, sourcePos, a, b);

                //nudge the bend point off the surface so the validation rays dont hit the box
                p += (p - _cubes[c].center).normalized * 0.02f;

                float len = Vector3.Distance(listenerPos, p) + Vector3.Distance(p, sourcePos);
                if (len >= pathLength) continue;

                if (Physics.Linecast(listenerPos, p, _occlusionMask, QueryTriggerInteraction.Ignore)) continue;
                if (Physics.Linecast(p, sourcePos, _occlusionMask, QueryTriggerInteraction.Ignore)) continue;

                pathLength = len;
                bendPoint = p;
                found = true;
            }
        }
        return found;
    }

    static Vector3 ShortestBendOnEdge(Vector3 listenerPos, Vector3 sourcePos, Vector3 a, Vector3 b)
    {
        float lo = 0f, hi = 1f;
        for (int i = 0; i < 24; i++)
        {
            float t1 = Mathf.Lerp(lo, hi, 1f / 3f);
            float t2 = Mathf.Lerp(lo, hi, 2f / 3f);
            if (BendLength(listenerPos, sourcePos, a, b, t1) < BendLength(listenerPos, sourcePos, a, b, t2))
                hi = t2;
            else
                lo = t1;
        }
        return Vector3.Lerp(a, b, 0.5f * (lo + hi));
    }

    static float BendLength(Vector3 listenerPos, Vector3 sourcePos, Vector3 a, Vector3 b, float t)
    {
        Vector3 p = Vector3.Lerp(a, b, t);
        return Vector3.Distance(listenerPos, p) + Vector3.Distance(p, sourcePos);
    }

    //CPU twin of the shaders oriented box slab test
    static bool RayHitsCube(Cube c, Vector3 origin, Vector3 dir, float maxDist)
    {
        Vector3 rel = origin - c.center;
        Vector3 localOrigin = new Vector3(Vector3.Dot(rel, c.axisX), Vector3.Dot(rel, c.axisY), Vector3.Dot(rel, c.axisZ));
        Vector3 localDir = new Vector3(Vector3.Dot(dir, c.axisX), Vector3.Dot(dir, c.axisY), Vector3.Dot(dir, c.axisZ));

        float tNear = float.MinValue, tFar = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float o = localOrigin[i], d = localDir[i], h = c.halfExtents[i];
            if (Mathf.Abs(d) < 1e-9f)
            {
                if (Mathf.Abs(o) > h) return false;
                continue;
            }
            float t1 = (-h - o) / d;
            float t2 = (h - o) / d;
            if (t1 > t2) { float tmp = t1; t1 = t2; t2 = tmp; }
            tNear = Mathf.Max(tNear, t1);
            tFar = Mathf.Min(tFar, t2);
        }
        return tNear <= tFar && tFar > 0f && tNear < maxDist - 0.01f;
    }

    void FillCubeCorners(Cube c)
    {
        Vector3 ax = c.axisX * c.halfExtents.x;
        Vector3 ay = c.axisY * c.halfExtents.y;
        Vector3 az = c.axisZ * c.halfExtents.z;
        for (int i = 0; i < 8; i++)
        {
            Vector3 p = c.center;
            p += ((i & 1) == 0) ? -ax : ax;
            p += ((i & 2) == 0) ? -ay : ay;
            p += ((i & 4) == 0) ? -az : az;
            _cubeCorners[i] = p;
        }
    }

    float MuffleCutoffFromOpenness(float openness)
    {
        openness = Mathf.Clamp01(openness);
        float logMin = Mathf.Log(Mathf.Max(_occludedCutoff, 1f), 2f);
        float logMax = Mathf.Log(Mathf.Max(_openCutoff, 1f), 2f);
        return Mathf.Pow(2f, Mathf.Lerp(logMin, logMax, openness));
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
        float weightedTime = 0f;
        for (int b = 0; b < _reverbBinCount; b++)
        {
            float binEnergy = _echogramLow[b] + _echogramHigh[b];
            totalLow += _echogramLow[b];
            totalHigh += _echogramHigh[b];
            weightedTime += binEnergy * (b * _binDuration);
            if (firstBin < 0 && binEnergy > 0f)
                firstBin = b;
        }

        float totalAll = totalLow + totalHigh;

        //no reflections reached the source -> let the reverb fade out
        if (totalAll <= 1e-9f)
        {
            ts.targetReverbLevel = -2000f;
            ts.targetReflectionsLevel = -2000f;
            ts.targetRoomHF = 0f;
            ts.targetDecayTime = 0.1f;
            ts.targetDecayHFRatio = 1f;
            ts.targetReflectionsDelay = 0.3f;
            ts.targetReverbDelay = 0.1f;
            return;
        }

        float directTime = Mathf.Max(ts.apparentDistance, 0f) / Mathf.Max(_speedOfSound, 1f);
        float firstReflTime = Mathf.Max(0, firstBin) * _binDuration;
        float centroidTime = weightedTime / totalAll;

        float rt60Low = (totalLow > 1e-9f) ? EstimateRT60(_echogramLow, totalLow) : 0.1f;
        float rt60High = (totalHigh > 1e-9f) ? EstimateRT60(_echogramHigh, totalHigh) : 0.1f;

        ts.targetDecayTime = Mathf.Clamp(rt60Low, 0.1f, _maxDecayTime);

        //high band usually decays faster -> ratio < 1 darkens the tail over time
        ts.targetDecayHFRatio = Mathf.Clamp(rt60High / Mathf.Max(rt60Low, 1e-3f), 0.1f, 2f);

        //room size cues
        //how long after the direct sound the first reflection lands
        //and how far behind that the bulk of the reverberant energy arrives
        ts.targetReflectionsDelay = Mathf.Clamp(firstReflTime - directTime, 0f, 0.3f);
        ts.targetReverbDelay = Mathf.Clamp(centroidTime - firstReflTime, 0f, 0.1f);

        float invRayCount = 1f / _rayCount;

        if (_enableReflectionEmitters && ts.reflEmitters != null)
        {
            //the reflection emitters render the early energy, so the wet tail only carries the late part
            int cut = Mathf.Min(_reverbBinCount,
                Mathf.Max(0, firstBin) + Mathf.Max(1, Mathf.CeilToInt(_earlyWindow / Mathf.Max(_binDuration, 1e-4f))));
            float lateLow = 0f, lateHigh = 0f;
            for (int b = cut; b < _reverbBinCount; b++)
            {
                lateLow += _echogramLow[b];
                lateHigh += _echogramHigh[b];
            }
            ts.targetReverbLevel = Mathf.Lerp(-10000f, 0f, Mathf.Clamp01(lateLow * invRayCount * _reverbSensitivity));
            ts.targetRoomHF = Mathf.Lerp(-10000f, 0f, Mathf.Clamp01(lateHigh * invRayCount * _reverbSensitivity));
            ts.targetReflectionsLevel = -10000f;
        }
        else
        {
            ts.targetReverbLevel = Mathf.Lerp(-10000f, 0f, Mathf.Clamp01(totalLow * invRayCount * _reverbSensitivity));

            //roomHF attenuates the reverbs treble when little high frequency energy survives
            ts.targetRoomHF = Mathf.Lerp(-10000f, 0f, Mathf.Clamp01(totalHigh * invRayCount * _reverbSensitivity));

            //early reflections = energy arriving in the first about 20% of the window
            int earlyBins = Mathf.Max(1, _reverbBinCount / 5);
            float earlyEnergy = 0f;
            for (int b = 0; b < earlyBins; b++) earlyEnergy += _echogramLow[b] + _echogramHigh[b];
            ts.targetReflectionsLevel = Mathf.Lerp(-10000f, 0f, Mathf.Clamp01(earlyEnergy * invRayCount * _reverbSensitivity));
        }
    }

    //Schroeder backward integration of the echogram
    float EstimateRT60(float[] echo, float total)
    {
        float running = total;     //running = remaining energy from bin b to the end (Schroeder curve)
        float prevDb = 0f;
        float prevT = 0f;
        float t5 = -1f;
        float t25 = -1f;
        float lastDb = 0f;
        float lastT = 0f;

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
            lastDb = db;
            lastT = t;
            running -= echo[b];
        }

        if (t5 >= 0f && t25 > t5)
            return 3f * (t25 - t5);     //T20 extrapolated to a full 60 dB drop

        if (t5 >= 0f && lastT > t5 && lastDb < -5f)
        {
            float slope = (lastDb + 5f) / (lastT - t5);     //dB per second, negative
            if (slope < -1e-3f)
                return Mathf.Clamp(-60f / slope, _reverbMaxTime, _maxDecayTime);
        }
        return _maxDecayTime;
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
        if (_drawDebugRays)
        {
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
        }

        //line of sight probes from the listener to each source
        foreach (TracedSource ts in _sources)
        {
            Vector3 truePos = ts.anchorTransform != null ? ts.anchorTransform.position
                                                         : (ts.source != null ? ts.source.transform.position : Vector3.zero);
            if (ts.anchorTransform == null && ts.source == null) continue;

            for (int i = 0; i < _probeFroms.Length; i++)
            {
                Gizmos.color = ts.probeBlocked[i] ? Color.red : Color.green;
                Gizmos.DrawLine(_probeFroms[i], truePos);
            }
        }

        Gizmos.color = Color.magenta;
        foreach (TracedSource ts in _sources)
        {
            if (ts.source == null) continue;
            Gizmos.DrawLine(Listener.position, ts.source.transform.position);
        }

        Gizmos.color = Color.yellow;
        foreach (TracedSource ts in _sources)
        {
            if (!ts.hasBend || ts.anchorTransform == null) continue;
            Gizmos.DrawLine(Listener.position, ts.bendPoint);
            Gizmos.DrawLine(ts.bendPoint, ts.anchorTransform.position);
            Gizmos.DrawWireSphere(ts.bendPoint, 0.08f);
        }
    }
}