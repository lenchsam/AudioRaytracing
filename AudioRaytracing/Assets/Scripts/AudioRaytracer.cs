using UnityEngine;

public class AudioRaytracer : MonoBehaviour
{
    public int rayCount = 32;
    public int maxBounces = 3;
    public float absorption = 0.2f; //how much sound is lost per bounce

    void Update()
    {
        //this is just used for testing, the real implementation would be triggered elsewhere
        for (int i = 0; i < rayCount; i++)
        {
            //fire rays in a circle around the source
            float angle = i * (Mathf.PI * 2) / rayCount;
            Vector3 dir = new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle));

            AudioRay ray = new AudioRay(transform.position, dir, 1.0f);
            TraceRay(ray);
        }
    }

    void TraceRay(AudioRay ray)
    {
        Vector3 currentPos = ray.Origin;
        Vector3 currentDir = ray.Direction;

        for (int b = 0; b < maxBounces; b++)
        {
            if (Physics.Raycast(currentPos, currentDir, out RaycastHit hit, 50f))
            {
                //record the hit point
                ray.PathPoints.Add(hit.point);

                //calculate reflection
                currentDir = Vector3.Reflect(currentDir, hit.normal);
                currentPos = hit.point;

                //simulate absorption
                ray.Intensity -= absorption;

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
}
