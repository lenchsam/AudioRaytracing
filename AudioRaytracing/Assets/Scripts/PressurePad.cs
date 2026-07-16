using UnityEngine;
using UnityEngine.SceneManagement;

public class PressurePad : MonoBehaviour
{
    private enum PadType
    {
        ChangeScene
    }

    [SerializeField] private PadType m_padType;
    [SerializeField] private string m_sceneName;

    [SerializeField] private bool m_isAudioRaytraced = true;

    private void OnTriggerEnter(Collider other)
    {
        if (other.CompareTag("Player"))
        {
            switch (m_padType)
            {
                case PadType.ChangeScene:
                    ChangeScene();
                    break;
            }
        }
    }

    private void ChangeScene()
    {
        GameManager.Instance.SetRaytracerActive(m_isAudioRaytraced);
        SceneManager.LoadScene(m_sceneName);

    }
}
