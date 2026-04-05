using System.Collections.Generic;
using UnityEngine;

public static class FibonacciSphere
{
    public static Vector3[] GenerateDirections(int numRays)
    {
        //this uses the fibonacci sphere algorithm to generate evenly distributed points on a sphere
        Vector3[] directions = new Vector3[numRays];
        float goldenAngle = Mathf.PI * (3f - Mathf.Sqrt(5f));

        for (int i = 0; i < numRays; i++)
        {
            //calculate y coordinate
            float y = 1f - (i / (float)(numRays - 1)) * 2f; //1 to -1

            //radius of horizontal circle at y level
            float radius = Mathf.Sqrt(1f - y * y);

            //calculate angle
            float theta = goldenAngle * i;

            //calculate x and z based on the angle and radius
            float x = Mathf.Cos(theta) * radius;
            float z = Mathf.Sin(theta) * radius;

            directions[i] = new Vector3(x, y, z); // already unit length
        }

        return directions;
    }
}
