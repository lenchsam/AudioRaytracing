using System.Linq;
using UnityEngine;

public class AudioRaytracer : MonoBehaviour
{
    [SerializeField] private ComputeShader _computeShader;

    [Header("Raytracing Settings")]
    [SerializeField] private int _rayCount = 64;    //must be a multiple of 64
    [SerializeField] private int _maxBounces = 3;
    [SerializeField] private float _receiverSensitivity = 15f;
    [SerializeField] private float _checkInterval = 0.1f;

    [SerializeField] private AudioSource _audioSource;

    private int _kernelIndex;
    private float _timeSinceLastCheck;
    private int _pathStride; //maxBoundes + 1

    private ComputeBuffer _cubesBuffer;
    private ComputeBuffer _directionsBuffer;
    private ComputeBuffer _rayVolumesBuffer;

    private float[] _rayVolumesReadback;

    private ComputeBuffer _rayPathBuffer;
    private Vector3[] _rayPathsReadback;

    private ComputeBuffer _rayArrivalDirsBuffer;
    private Vector3[] _rayArrivalDirsReadback;
    [Header("Volume Smoothing")]
    [SerializeField] private float _smoothingSpeed = 8f;
    private float _currentVolume;
    private float _targetVolume;

    [SerializeField] private float _panSmoothingSpeed = 5f;
    private float _currentPan;
    private float _targetPan;

    [Header("Line of Sight")]
    [SerializeField] private LayerMask _occlusionMask = ~0;
    [SerializeField] private float _probeRadius = 0.35f;
    [SerializeField] private float _directPathThreshold = 0.2f;

    private Vector3[] _probeOffsets = new Vector3[5];
    private Vector3[] _probeFroms = new Vector3[5];  //for gizmos
    private bool[] _probeBlocked = new bool[5];      //for gizmos
    private float _lastVisibility;

    [Header("Reverb")]
    [SerializeField] private bool _enableReverb = true;
    [SerializeField] private AudioReverbFilter _reverbFilter;
    [SerializeField] private int _reverbBinCount = 64;
    [SerializeField] private float _reverbMaxTime = 1.5f;        //length of the impulse-response window in seconds
    [SerializeField] private float _speedOfSound = 343f;
    [SerializeField] private float _reverbSensitivity = 50f;     //reflected energy -> wet level
    [SerializeField] private float _reverbSmoothingSpeed = 4f;
    [SerializeField] private float _maxDecayTime = 12f;

    private ComputeBuffer _rayEnergyBinsBuffer;
    private float[] _rayEnergyBinsReadback;
    private float[] _echogram;          //per-bin energy summed across all rays
    private float _binDuration;

    private float _currentDecayTime = 0.1f, _targetDecayTime = 0.1f;
    private float _currentReverbLevel = -10000f, _targetReverbLevel = -10000f;
    private float _currentReflectionsLevel = -10000f, _targetReflectionsLevel = -10000f;
    private float _currentReflectionsDelay, _targetReflectionsDelay;
    private float _currentReverbDelay, _targetReverbDelay;

    //needs to match the struct layout in the AudioRaytracing compute shader (2 × float3 = 24 bytes)
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
        _rayPathsReadback = new Vector3[_rayCount * _pathStride];

        _rayArrivalDirsBuffer = new ComputeBuffer(_rayCount, sizeof(float) * 3);
        _rayArrivalDirsReadback = new Vector3[_rayCount];

        //reverb
        _reverbBinCount = Mathf.Max(1, _reverbBinCount);
        _reverbMaxTime = Mathf.Max(0.01f, _reverbMaxTime);
        _binDuration = _reverbMaxTime / _reverbBinCount;

        _rayEnergyBinsBuffer = new ComputeBuffer(_rayCount * _reverbBinCount, sizeof(float));
        _rayEnergyBinsReadback = new float[_rayCount * _reverbBinCount];
        _echogram = new float[_reverbBinCount];

        if (_reverbFilter == null && _audioSource != null)
            _reverbFilter = _audioSource.GetComponent<AudioReverbFilter>();
        if (_reverbFilter == null && _audioSource != null && _enableReverb)
            _reverbFilter = _audioSource.gameObject.AddComponent<AudioReverbFilter>();
        if (_reverbFilter != null)
            _reverbFilter.reverbPreset = AudioReverbPreset.User;
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
        _rayPathsReadback = null;

        _rayArrivalDirsBuffer?.Release();
        _rayArrivalDirsBuffer = null;
        _rayArrivalDirsReadback = null;

        _rayEnergyBinsBuffer?.Release();
        _rayEnergyBinsBuffer = null;
        _rayEnergyBinsReadback = null;
        _echogram = null;
    }

    void Update()
    {
        _timeSinceLastCheck += Time.deltaTime;
        if (_timeSinceLastCheck >= _checkInterval)
        {
            TraceAcoustics();
            _timeSinceLastCheck = 0f;
        }

        _currentVolume = Mathf.Lerp(_currentVolume, _targetVolume, Time.deltaTime * _smoothingSpeed);
        if (_audioSource != null)
            _audioSource.volume = _currentVolume;

        _currentPan = Mathf.Lerp(_currentPan, _targetPan, Time.deltaTime * _panSmoothingSpeed);
        if (_audioSource != null)
            _audioSource.panStereo = _currentPan;

        if (_enableReverb && _reverbFilter != null)
        {
            float k = Time.deltaTime * _reverbSmoothingSpeed;
            _currentDecayTime = Mathf.Lerp(_currentDecayTime, _targetDecayTime, k);
            _currentReverbLevel = Mathf.Lerp(_currentReverbLevel, _targetReverbLevel, k);
            _currentReflectionsLevel = Mathf.Lerp(_currentReflectionsLevel, _targetReflectionsLevel, k);
            _currentReflectionsDelay = Mathf.Lerp(_currentReflectionsDelay, _targetReflectionsDelay, k);
            _currentReverbDelay = Mathf.Lerp(_currentReverbDelay, _targetReverbDelay, k);

            _reverbFilter.decayTime = _currentDecayTime;
            _reverbFilter.reverbLevel = _currentReverbLevel;
            _reverbFilter.reflectionsLevel = _currentReflectionsLevel;
            _reverbFilter.reflectionsDelay = _currentReflectionsDelay;
            _reverbFilter.reverbDelay = _currentReverbDelay;
            _reverbFilter.dryLevel = 0f;
        }

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
        _cubesBuffer = new ComputeBuffer(cubes.Length, sizeof(float) * 6); // 2 × float3
        _cubesBuffer.SetData(cubes);
    }

    //run compute shader and get back results
    void TraceAcoustics()
    {
        if (_computeShader == null || _cubesBuffer == null || _directionsBuffer == null || _audioSource == null)
            return;

        //check if the audio source is visible from the listener's position
        Vector3 listenerPos = transform.position;
        Vector3 sourcePos = _audioSource.transform.position;
        Vector3 toSource = sourcePos - listenerPos;
        float dist = toSource.magnitude;

        _lastVisibility = ComputeVisibility(listenerPos, sourcePos);

        if (!_enableReverb && _lastVisibility > _directPathThreshold)
        {
            float directVolume = Mathf.Clamp01(
                Mathf.Sqrt(1f / (dist * dist + 1e-6f)) * _receiverSensitivity * _lastVisibility);

            _targetVolume = directVolume;
            _targetPan = Mathf.Clamp(Vector3.Dot(toSource.normalized, transform.right), -1f, 1f);
            return;
        }

        //settings parameters for compute shader
        _computeShader.SetBuffer(_kernelIndex, "_Cubes", _cubesBuffer);
        _computeShader.SetInt("_CubeCount", _cubesBuffer.count);

        _computeShader.SetVector("_ListenerPos", transform.position);
        _computeShader.SetVector("_SourcePos", _audioSource.transform.position);

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

        //send compute shader to GPU
        int threadGroups = Mathf.CeilToInt(_rayCount / 64.0f);
        _computeShader.Dispatch(_kernelIndex, threadGroups, 1, 1);

        //get results back from GPU
        _rayPathBuffer.GetData(_rayPathsReadback);
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
        _targetVolume = raytracedVolume;

        Vector3 weightedDir = Vector3.zero;
        for (int i = 0; i < _rayCount; i++)
            weightedDir += _rayArrivalDirsReadback[i];

        _targetPan = weightedDir.sqrMagnitude > 1e-6f
                   ? Mathf.Clamp(Vector3.Dot(weightedDir.normalized, transform.right), -1f, 1f)
                   : 0f;

        if (_lastVisibility > 0f)
        {
            float directVolume = Mathf.Clamp01(
                Mathf.Sqrt(1f / (dist * dist + 1e-6f)) * _receiverSensitivity);
            float directPan = Mathf.Clamp(
                Vector3.Dot(toSource.normalized, transform.right), -1f, 1f);

            float blend = _lastVisibility / _directPathThreshold;

            _targetVolume = Mathf.Clamp01(raytracedVolume + directVolume * _lastVisibility);
            _targetPan = Mathf.Lerp(_targetPan, directPan, blend);
        }
        if (_enableReverb)
            UpdateReverb();
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

    float ComputeVisibility(Vector3 listenerPos, Vector3 sourcePos)
    {
        UpdateProbeOffsets(listenerPos);
        int clear = 0;
        for (int i = 0; i < _probeFroms.Length; i++)
        {
            _probeBlocked[i] = Physics.Linecast(_probeFroms[i], sourcePos, _occlusionMask);
            if (!_probeBlocked[i]) clear++;
        }
        return clear / (float)_probeFroms.Length;
    }

    void UpdateReverb()
    {
        System.Array.Clear(_echogram, 0, _echogram.Length);
        for (int r = 0; r < _rayCount; r++)
        {
            int baseIdx = r * _reverbBinCount;
            for (int b = 0; b < _reverbBinCount; b++)
                _echogram[b] += _rayEnergyBinsReadback[baseIdx + b];
        }

        float total = 0f;
        int firstBin = -1;
        for (int b = 0; b < _reverbBinCount; b++)
        {
            if (_echogram[b] > 0f)
            {
                total += _echogram[b];
                if (firstBin < 0) firstBin = b;
            }
        }

        //no reflections reached the source -> let the reverb fade out
        if (total <= 1e-9f)
        {
            _targetReverbLevel = -10000f;
            _targetReflectionsLevel = -10000f;
            _targetDecayTime = 0.1f;
            return;
        }

        _targetDecayTime = Mathf.Clamp(EstimateRT60(total), 0.1f, _maxDecayTime);

        float wetGain = Mathf.Clamp01(total * _reverbSensitivity);
        _targetReverbLevel = Mathf.Lerp(-10000f, 0f, wetGain);

        //early reflections = energy arriving in the first ~20% of the window
        int earlyBins = Mathf.Max(1, _reverbBinCount / 5);
        float earlyEnergy = 0f;
        for (int b = 0; b < earlyBins; b++) earlyEnergy += _echogram[b];
        float earlyGain = Mathf.Clamp01(earlyEnergy * _reverbSensitivity);
        _targetReflectionsLevel = Mathf.Lerp(-10000f, 0f, earlyGain);

        float preDelay = (firstBin >= 0) ? firstBin * _binDuration : 0f;
        _targetReflectionsDelay = Mathf.Clamp(preDelay, 0f, 0.3f);
        _targetReverbDelay = Mathf.Clamp(preDelay, 0f, 0.1f);
    }

    //Schroeder backward integration of the echogram
    float EstimateRT60(float total)
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
            running -= _echogram[b];
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
        if (_rayPathsReadback == null || _rayPathsReadback.Length == 0) return;

        Gizmos.color = Color.cyan;
        for (int i = 0; i < _rayCount; i++)
        {
            int offset = i * _pathStride;
            for (int b = 0; b < _maxBounces; b++)
            {
                Vector3 start = _rayPathsReadback[offset + b];
                Vector3 end = _rayPathsReadback[offset + b + 1];

                if (end == Vector3.zero) break;

                Gizmos.DrawLine(start, end);
            }
        }

        if (_audioSource != null && _probeFroms != null)
        {
            for (int i = 0; i < _probeFroms.Length; i++)
            {
                Gizmos.color = _probeBlocked[i] ? Color.red : Color.green;
                Gizmos.DrawLine(_probeFroms[i], _audioSource.transform.position);
            }
        }
    }
}