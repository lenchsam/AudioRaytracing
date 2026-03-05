using System.Collections.Generic;
using UnityEngine;

public class AudioRaytracer : MonoBehaviour
{
    [SerializeField] private int _rayCount = 32;
    [SerializeField] private int _maxBounces = 3;
    [SerializeField] private float _absorption = 0.2f; //how much sound is lost per bounce
    [SerializeField] private float _distanceFalloff = 0.01f;

    [SerializeField] Transform _listener;
    [SerializeField] float _listenerRadius = 1f;

    void Update()
    {
        List<Vector3> directions = FibonacciSphere.GenerateDirections(_rayCount);

        foreach (Vector3 dir in directions)
        {
            AudioRay ray = new AudioRay(transform.position, dir, 1.0f);
            TraceRay(ray);
        }
    }

    void TraceRay(AudioRay ray)
    {
        Vector3 currentPos = ray.Origin;
        Vector3 currentDir = ray.Direction;

        for (int b = 0; b < _maxBounces; b++)
        {
            if (Physics.Raycast(currentPos, currentDir, out RaycastHit hit, 100f))
            {
                //record the hit point
                ray.PathPoints.Add(hit.point);

                //simulate absorption
                ray.Intensity *= (1f - _absorption);

                //distance falloff
                float distance = Vector3.Distance(currentPos, hit.point);
                ray.Intensity *= Mathf.Exp(-distance * _distanceFalloff);

                //calculate reflection
                currentDir = Vector3.Reflect(currentDir, hit.normal);
                currentPos = hit.point;

                float distToListener = Vector3.Distance(_listener.position, hit.point);

                if (distToListener < _listenerRadius)
                {
                    RegisterSound(ray.Intensity);

                }
                //draw ray
                Debug.DrawLine(ray.PathPoints[ray.PathPoints.Count - 2], hit.point, Color.green);

                if (ray.Intensity <= 0) break;
            }
            else
            {
                //ray escaped
                ray.PathPoints.Add(currentPos + currentDir * 10f);
                Debug.DrawRay(currentPos, currentDir * 10f, Color.red);
                break;
            }
        }
    }

    void RegisterSound(float intensity)
    {
        Debug.Log("Sound reached listener: " + intensity);
    }
}
