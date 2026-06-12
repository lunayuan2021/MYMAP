using UnityEngine;

public class MapElevationData : MonoBehaviour
{
    public int gridWidth = 80;
    public int gridHeight = 60;
    public Vector2 worldOrigin = new Vector2(-40f, -30f);
    public float cellSize = 1f;
    public float[] heightSamples;

    public float SampleWorldHeight(float worldX, float worldZ)
    {
        if (heightSamples == null || heightSamples.Length == 0) return transform.position.y;

        float gx = Mathf.Clamp((worldX - worldOrigin.x) / cellSize, 0f, gridWidth);
        // Terrain vertices are stored from north to south while worldOrigin is
        // the south-west corner, so convert world Z back into the stored row.
        float gz = Mathf.Clamp(
            gridHeight - (worldZ - worldOrigin.y) / cellSize,
            0f,
            gridHeight);
        int x0 = Mathf.FloorToInt(gx);
        int z0 = Mathf.FloorToInt(gz);
        int x1 = Mathf.Min(x0 + 1, gridWidth);
        int z1 = Mathf.Min(z0 + 1, gridHeight);
        float tx = gx - x0;
        float tz = gz - z0;

        float a = Mathf.Lerp(GetHeight(x0, z0), GetHeight(x1, z0), tx);
        float b = Mathf.Lerp(GetHeight(x0, z1), GetHeight(x1, z1), tx);
        return Mathf.Lerp(a, b, tz);
    }

    float GetHeight(int x, int z)
    {
        return heightSamples[z * (gridWidth + 1) + x];
    }
}
