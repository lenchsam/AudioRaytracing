using UnityEngine;
using UnityEngine.InputSystem;

public class Button : MonoBehaviour
{
    private InputSystem_Actions controls;

    [SerializeField] private AudioRaytracer m_audioRaytracer;

    [SerializeField] private GameObject m_button;
    [SerializeField] private GameObject m_goToToggle;

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
            m_goToToggle.SetActive(!m_goToToggle.activeSelf);
            m_audioRaytracer.RebuildCubeBuffer();
        }
    }
}
