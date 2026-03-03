using System.Collections.Generic;
using UnityEngine;

public class AudioRay
{
    public Vector3 Origin;
    public Vector3 Direction;
    public float Intensity; //range of 0 to 1. 1 is full volume
    public List<Vector3> PathPoints; //needed to visualise the audio ray

    public AudioRay(Vector3 origin, Vector3 direction, float intensity)
    {
        Origin = origin;
        Direction = direction.normalized;
        Intensity = intensity;
        PathPoints = new List<Vector3> { origin };
    }
}
