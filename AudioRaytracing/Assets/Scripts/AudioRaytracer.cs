using System.Linq;
using UnityEngine;

public class AudioRaytracer : MonoBehaviour
{
    [SerializeField] private ComputeShader _computeShader;

    [SerializeField] private int _rayCount = 64;    //must be a multiple of 64
    [SerializeField] private int _maxBounces = 3;
    [SerializeField] private float _receiverSensitivity = 15f;
    [SerializeField] private float _checkInterval = 0.1f;

    [SerializeField] private Transform _audioSource;

    private int _kernelIndex;
    private float _timeSinceLastCheck;

    private ComputeBuffer _cubesBuffer;
    private ComputeBuffer _directionsBuffer;
    private ComputeBuffer _rayVolumesBuffer;

    private float[] _rayVolumesReadback;

    private ComputeBuffer _rayPathBuffer;
    private Vector3[] _rayPathsReadback;

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

        //geometry baking
        RebuildCubeBuffer();

        //ray setup
        Vector3[] directions = FibonacciSphere.GenerateDirections(_rayCount);
        _directionsBuffer = new ComputeBuffer(_rayCount, sizeof(float) * 3);
        _directionsBuffer.SetData(directions);

        //output buffers
        _rayVolumesBuffer = new ComputeBuffer(_rayCount, sizeof(float));
        _rayVolumesReadback = new float[_rayCount];

        _rayPathBuffer = new ComputeBuffer(_rayCount * 4, sizeof(float) * 3);
        _rayPathsReadback = new Vector3[_rayCount * 4];
    }

    void Update()
    {
        _timeSinceLastCheck += Time.deltaTime;
        if (_timeSinceLastCheck >= _checkInterval)
        {
            TraceAcoustics();
            _timeSinceLastCheck = 0f;
        }
    }

    void OnDisable()
    {
        // Release existing buffers
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
        //inputs
        _computeShader.SetBuffer(_kernelIndex, "_Cubes", _cubesBuffer);
        _computeShader.SetInt("_CubeCount", _cubesBuffer.count);

        _computeShader.SetVector("_ListenerPos", transform.position);
        _computeShader.SetVector("_SourcePos", _audioSource.position);

        _computeShader.SetBuffer(_kernelIndex, "_Directions", _directionsBuffer);
        _computeShader.SetInt("_RayCount", _rayCount);
        _computeShader.SetInt("_MaxBounces", _maxBounces);

        //output
        _computeShader.SetBuffer(_kernelIndex, "_RayVolumes", _rayVolumesBuffer);

        _computeShader.SetBuffer(_kernelIndex, "_RayPathBuffer", _rayPathBuffer);

        int threadGroups = Mathf.CeilToInt(_rayCount / 64.0f);
        _computeShader.Dispatch(_kernelIndex, threadGroups, 1, 1);

        _rayPathBuffer.GetData(_rayPathsReadback);

        _rayVolumesBuffer.GetData(_rayVolumesReadback);

        float totalVolume = 0f;
        for (int i = 0; i < _rayCount; i++)
            totalVolume += _rayVolumesReadback[i];

        if (_audioSource.TryGetComponent<AudioSource>(out var audioComponent))
        {
            audioComponent.volume =
                Mathf.Clamp01((totalVolume / _rayCount) * _receiverSensitivity);
        }
    }
    private void OnDrawGizmos()
    {
        if (_rayPathsReadback == null || _rayPathsReadback.Length == 0) return;

        Gizmos.color = Color.cyan;
        for (int i = 0; i < _rayCount; i++)
        {
            int offset = i * 4;
            for (int b = 0; b < 3; b++)
            {
                Vector3 start = _rayPathsReadback[offset + b];
                Vector3 end = _rayPathsReadback[offset + b + 1];

                if (end == Vector3.zero) break;

                Gizmos.DrawLine(start, end);
                Gizmos.DrawSphere(end, 0.05f);
            }
        }
    }
}