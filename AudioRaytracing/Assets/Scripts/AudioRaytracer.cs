using Unity.Collections;
using Unity.Jobs;
using UnityEngine;

public class AudioRaytracer : MonoBehaviour
{
    const float SPEED_OF_SOUND = 343f; //meters per second

    [SerializeField] private int _rayCount = 32;
    [SerializeField] private int _maxBounces = 3;

    [SerializeField] private float _receiverSensitivity = 15f;

    [SerializeField]  private float _checkInterval = 0.1f; //in seconds
    private float _timeSinceLastCheck = 0f;

    //native arrays as they dont create garbage
    //can also be used with jobs
    private NativeArray<RaycastCommand> _commands;
    private NativeArray<RaycastHit> _results;

    private NativeArray<RaycastCommand> _shadowCommands;
    private NativeArray<RaycastHit> _shadowResults;

    private Vector3[] _baseDirections;

    [SerializeField] private Transform _audioSource;

    void Start()
    {
        _commands = new NativeArray<RaycastCommand>(_rayCount, Allocator.Persistent);
        _results = new NativeArray<RaycastHit>(_rayCount, Allocator.Persistent);

        _shadowCommands = new NativeArray<RaycastCommand>(_rayCount, Allocator.Persistent);
        _shadowResults = new NativeArray<RaycastHit>(_rayCount, Allocator.Persistent);

        _baseDirections = FibonacciSphere.GenerateDirections(_rayCount);
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

    void TraceAcoustics()
    {
        NativeArray<Vector3> currentOrigins = new NativeArray<Vector3>(_rayCount, Allocator.Temp);
        NativeArray<Vector3> currentDirections = new NativeArray<Vector3>(_rayCount, Allocator.Temp);
        NativeArray<bool> rayAlive = new NativeArray<bool>(_rayCount, Allocator.Temp);

        //initialize rays for first bounce
        NativeArray<float> totalDistances = new NativeArray<float>(_rayCount, Allocator.Temp);
        NativeArray<float> rayEnergies = new NativeArray<float>(_rayCount, Allocator.Temp);

        for (int i = 0; i < _rayCount; i++)
        {
            currentOrigins[i] = transform.position;
            currentDirections[i] = _baseDirections[i];
            rayAlive[i] = true;
            totalDistances[i] = 0f;
            rayEnergies[i] = 1.0f;
        }

        float totalVolume = 0f;

        for (int bounce = 0; bounce < _maxBounces; bounce++)
        {
            //schedule raycast batches for all alive rays
            // schedule raycast batches for all alive rays
            for (int i = 0; i < _rayCount; i++)
            {
                if (rayAlive[i])
                {
                    _commands[i] = new RaycastCommand(currentOrigins[i], currentDirections[i], 100f);
                }
                else
                {
                    //if a ray is dead fill with blank raycast
                    _commands[i] = new RaycastCommand(Vector3.zero, Vector3.up, 0f);
                }
            }

            JobHandle handle = RaycastCommand.ScheduleBatch(_commands, _results, 1, default);
            handle.Complete(); //wait until done

            //process results and prepare for next bounce + shadow rays
            for (int i = 0; i < _rayCount; i++)
            {
                if (!rayAlive[i]) continue;

                if (_results[i].collider != null)
                {
                    //hit a wall
                    Vector3 hitPoint = _results[i].point;
                    Vector3 normal = _results[i].normal;

                    totalDistances[i] += _results[i].distance;
                    rayEnergies[i] *= 0.8f;

                    Debug.DrawLine(currentOrigins[i], hitPoint, Color.green, _checkInterval);

                    //reflection for next bounce
                    Vector3 incomingDir = currentDirections[i];
                    Vector3 reflectDir = Vector3.Reflect(incomingDir, normal);

                    //update states for the next iteration of the bounce loop
                    currentOrigins[i] = hitPoint + normal * 0.01f;
                    currentDirections[i] = reflectDir;

                    Vector3 dirToSource = (_audioSource.position - hitPoint).normalized;

                    float distToSource = Vector3.Distance(hitPoint, _audioSource.position) - 0.1f;
                    _shadowCommands[i] = new RaycastCommand(hitPoint + normal * 0.01f, dirToSource, Mathf.Max(0f, distToSource));
                }
                else
                {
                    //ray escaped
                    Debug.DrawRay(currentOrigins[i], currentDirections[i] * 100f, Color.red, _checkInterval);
                    rayAlive[i] = false;
                    _shadowCommands[i] = new RaycastCommand(Vector3.zero, Vector3.up, 0f);
                }
            }

            //evaluate shadow rays
            //this is where we check if the reflection point can see the audio source or not
            JobHandle shadowHandle = RaycastCommand.ScheduleBatch(_shadowCommands, _shadowResults, 1, default);
            shadowHandle.Complete();

            for (int i = 0; i < _rayCount; i++)
            {
                if (!rayAlive[i]) continue;

                bool pathIsClear = _shadowResults[i].collider == null || _shadowResults[i].collider.CompareTag("AudioSource");

                if (pathIsClear)
                {
                    float finalDistance = totalDistances[i] + Vector3.Distance(currentOrigins[i], _audioSource.position);

                    float delay = finalDistance / SPEED_OF_SOUND;

                    float distanceAttenuation = 1.0f / Mathf.Max(1.0f, finalDistance);
                    float finalVolume = rayEnergies[i] * distanceAttenuation;

                    totalVolume += finalVolume;

                    Debug.DrawLine(currentOrigins[i], _audioSource.position, Color.yellow, _checkInterval);
                }
            }
        }

        //clean up temp arrays
        if (_audioSource.TryGetComponent<AudioSource>(out var audioComponent))
        {
            audioComponent.volume = Mathf.Clamp01((totalVolume / _rayCount) * _receiverSensitivity);
        }

        currentOrigins.Dispose();
        currentDirections.Dispose();
        rayAlive.Dispose();
        totalDistances.Dispose();
        rayEnergies.Dispose();
    }

    void OnDestroy()
    {
        if (_commands.IsCreated) _commands.Dispose();
        if (_results.IsCreated) _results.Dispose();
        if (_shadowCommands.IsCreated) _shadowCommands.Dispose();
        if (_shadowResults.IsCreated) _shadowResults.Dispose();
    }
}
