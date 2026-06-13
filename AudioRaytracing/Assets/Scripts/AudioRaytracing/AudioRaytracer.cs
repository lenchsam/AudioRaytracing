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
        if (_computeShader == null || _cubesBuffer == null || _directionsBuffer == null) return;

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

        //send compute shader to GPU
        int threadGroups = Mathf.CeilToInt(_rayCount / 64.0f);
        _computeShader.Dispatch(_kernelIndex, threadGroups, 1, 1);

        //get results back from GPU
        _rayPathBuffer.GetData(_rayPathsReadback);
        _rayVolumesBuffer.GetData(_rayVolumesReadback);
        _rayArrivalDirsBuffer.GetData(_rayArrivalDirsReadback);

        //average volume
        float totalVolume = 0f;
        int contributingRays = 0;

        for (int i = 0; i < _rayCount; i++)
        {
            if (_rayVolumesReadback[i] > 0f)
            {
                totalVolume += _rayVolumesReadback[i];
                contributingRays++;
            }
        }

        //set audio source volume based on average energy of contributing rays
        if (_audioSource != null)
        {
            float averageEnergy = totalVolume / _rayCount;

            float targetVolume = Mathf.Clamp01(Mathf.Sqrt(averageEnergy) * _receiverSensitivity);
            _targetVolume = Mathf.Clamp01(Mathf.Sqrt(averageEnergy) * _receiverSensitivity);
        }

        Vector3 weightedDir = Vector3.zero;
        for (int i = 0; i < _rayCount; i++)
            weightedDir += _rayArrivalDirsReadback[i];

        _targetPan = weightedDir.sqrMagnitude > 1e-6f
                   ? Mathf.Clamp(Vector3.Dot(weightedDir.normalized, transform.right), -1f, 1f)
                   : 0f;
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
    }
}