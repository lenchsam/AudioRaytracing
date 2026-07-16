using UnityEngine;
using UnityEngine.InputSystem;

public class Button : MonoBehaviour
{
    enum ButtonType
    {
        ToggleEnvironment,
        ChangeSong
    }

    private InputSystem_Actions controls;

    [SerializeField] private AudioRaytracer m_audioRaytracer;

    [SerializeField] private GameObject m_button;
    [SerializeField] private GameObject m_goToToggle;

    [SerializeField] private ButtonType m_buttonType;
    [SerializeField] private AudioClip[] m_audioClip;
    [SerializeField] private AudioSource m_audioSource;
    private int m_audioClipIndex = 0;

    private void Awake()
    {
        controls = new InputSystem_Actions();
    }

    private void OnEnable()
    {
        controls.Enable();
        controls.Player.Interact.started += OnInteract;
    }

    private void OnDisable()
    {
        controls.Player.Interact.started -= OnInteract;
        controls.Disable();
    }

    private void OnInteract(InputAction.CallbackContext ctx)
    {
        if (Camera.main == null || Mouse.current == null)
            return;

        Ray ray = Camera.main.ScreenPointToRay(Mouse.current.position.ReadValue());

        if (Physics.Raycast(ray, out RaycastHit hit) && hit.collider.gameObject == m_button)
        {
            switch(m_buttonType)
            {
                case ButtonType.ToggleEnvironment:
                    ToggleEnvironment();
                    break;
                case ButtonType.ChangeSong:
                    RotateSong();
                    break;
            }
        }
    }

    private void ToggleEnvironment()
    {
        m_goToToggle.SetActive(!m_goToToggle.activeSelf);
        m_audioRaytracer.RebuildCubeBuffer();
    }
    private void RotateSong()
    {
        AudioClip currentAudioClip = m_audioClip[m_audioClipIndex];

        m_audioSource.transform.GetChild(0).GetComponent<AudioSource>().clip = currentAudioClip;
        m_audioSource.transform.GetChild(0).GetComponent<AudioSource>().Play();
        m_audioClipIndex = (m_audioClipIndex + 1) % m_audioClip.Length;
    }
}
