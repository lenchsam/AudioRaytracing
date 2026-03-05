using UnityEngine;

[RequireComponent(typeof(AudioSource))]
[RequireComponent(typeof(AudioLowPassFilter))]
public class AudioOcclusion : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Transform _listener;

    [Header("Audio Settings")]
    [SerializeField] private LayerMask _occlusionLayer;
    [Tooltip("How fast the audio transitions between muffled and clear.")]
    [SerializeField] private float _transitionSpeed = 8f;

    [Header("Open Path")]
    [SerializeField] private float _openVolume = 1f;
    [SerializeField] private float _openCutoff = 22000f; //clear audio

    [Header("Occluded Path")]
    [SerializeField] private float _occludedVolume = 0.5f;
    [SerializeField] private float _occludedCutoff = 1200f; //muffled audio

    private AudioSource _audioSource;
    private AudioLowPassFilter _lowPassFilter;

    private float _targetVolume;
    private float _targetCutoff;

    void Start()
    {
        _audioSource = GetComponent<AudioSource>();
        _lowPassFilter = GetComponent<AudioLowPassFilter>();

        _targetVolume = _openVolume;
        _targetCutoff = _openCutoff;
    }

    void Update()
    {
        CheckOcclusion();
        ApplyAudioChanges();
    }

    void CheckOcclusion()
    {
        if (_listener == null) return;

        Vector3 dir = _listener.position - transform.position;
        float dist = dir.magnitude;

        //detect if something is blocking the audio path to the listener
        if (Physics.Raycast(transform.position, dir.normalized, out RaycastHit hit, dist, _occlusionLayer))
        {
            if (hit.transform != _listener)
            {
                //audio is now occluded
                _targetVolume = _occludedVolume;
                _targetCutoff = _occludedCutoff;
                Debug.DrawLine(transform.position, hit.point, Color.red);
                return;
            }
        }

        //audio has a clear path
        _targetVolume = _openVolume;
        _targetCutoff = _openCutoff;
        Debug.DrawLine(transform.position, _listener.position, Color.green);
    }

    void ApplyAudioChanges()
    {
        _audioSource.volume = Mathf.Lerp(_audioSource.volume, _targetVolume, Time.deltaTime * _transitionSpeed);
        _lowPassFilter.cutoffFrequency = Mathf.Lerp(_lowPassFilter.cutoffFrequency, _targetCutoff, Time.deltaTime * _transitionSpeed);
    }
}
