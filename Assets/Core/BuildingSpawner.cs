using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Spawns simple placeholder buildings (white cubes) inside the city blocks
/// formed by ProceduralCityGenerator's street graph.
///
/// Strategy: iterate the same grid cells the generator used. A cell is
/// skipped if any of its 4 corner nodes belongs to a Highway or Avenue
/// (keeps a clearance around structural roads instead of building on top
/// of them). Every remaining cell gets 1-N buildings: random rectangular
/// footprint, random height, placed with a margin (setback) from the cell
/// edges so buildings don't touch the street.
///
/// Deterministic: uses its own System.Random seeded from the same seed as
/// the city generator (offset by +1) so results are reproducible without
/// depending on generation order or Unity's global RNG.
/// </summary>
[RequireComponent(typeof(Transform))]
public class BuildingSpawner : MonoBehaviour
{
    [Header("Source")]
    [Tooltip("The generator whose Nodes/Edges define the street grid")]
    public ProceduralCityGenerator cityGenerator;

    [Header("Seed")]
    [Tooltip("If true, derives the seed from cityGenerator.seed (+1) instead of using seedOverride")]
    public bool useGeneratorSeed = true;
    public int seedOverride = 54321;

    [Header("Density")]
    [Tooltip("Minimum buildings spawned per free cell")]
    public int minBuildingsPerCell = 1;
    [Tooltip("Maximum buildings spawned per free cell")]
    public int maxBuildingsPerCell = 3;
    [Range(0f, 1f)]
    [Tooltip("Chance a given free cell gets any building at all (0 = empty lots allowed, 1 = every free cell is built)")]
    public float cellOccupancyChance = 0.85f;

    [Header("Footprint")]
    [Tooltip("Margin kept clear from the cell's edges (simulates sidewalk/plot boundary), in world units")]
    public float setback = 2f;
    public float minFootprint = 4f;
    public float maxFootprint = 8f;

    [Header("Height")]
    public float minHeight = 3f;
    public float maxHeight = 18f;

    [Header("Appearance")]
    public Color buildingColor = Color.white;

    [Header("Output")]
    [Tooltip("All spawned buildings are parented under this container (auto-created if empty)")]
    public Transform container;

    private System.Random _rng;
    private Material _sharedMaterial;

    [ContextMenu("Spawn Buildings")]
    public void Spawn()
    {
        if (cityGenerator == null)
        {
            Debug.LogError("[BuildingSpawner] No ProceduralCityGenerator assigned.");
            return;
        }

        ClearExisting();

        int seed = useGeneratorSeed ? cityGenerator.seed + 1 : seedOverride;
        _rng = new System.Random(seed);

        EnsureContainer();
        EnsureMaterial();

        // Index node types by grid coordinate for quick corner lookups.
        // Node.Id was assigned as x * gridHeight + z by the generator, so we can
        // invert it directly instead of re-deriving positions.
        var nodeById = new Dictionary<int, ProceduralCityGenerator.CityNode>();
        foreach (var n in cityGenerator.Nodes) nodeById[n.Id] = n;

        int gridWidth = cityGenerator.gridWidth;
        int gridHeight = cityGenerator.gridHeight;
        float cellSize = cityGenerator.cellSize;

        int spawned = 0;

        for (int x = 0; x < gridWidth - 1; x++)
        {
            for (int z = 0; z < gridHeight - 1; z++)
            {
                if (IsCellBlockedByStructuralRoad(x, z, gridHeight, nodeById)) continue;
                if (NextFloat(0f, 1f) > cellOccupancyChance) continue;

                float cellMinX = x * cellSize;
                float cellMinZ = z * cellSize;

                int count = _rng.Next(minBuildingsPerCell, maxBuildingsPerCell + 1);
                for (int i = 0; i < count; i++)
                {
                    SpawnBuildingInCell(cellMinX, cellMinZ, cellSize);
                    spawned++;
                }
            }
        }

        Debug.Log($"[BuildingSpawner] Spawned {spawned} buildings.");
    }

    // A cell is "blocked" (left clear) if any of its 4 corner nodes is part of
    // a Highway or Avenue — keeps buildings from crowding structural roads.
    private bool IsCellBlockedByStructuralRoad(int x, int z, int gridHeight, Dictionary<int, ProceduralCityGenerator.CityNode> nodeById)
    {
        int[] cornerIds =
        {
            GridIndex(x, z, gridHeight),
            GridIndex(x + 1, z, gridHeight),
            GridIndex(x, z + 1, gridHeight),
            GridIndex(x + 1, z + 1, gridHeight)
        };

        foreach (var id in cornerIds)
        {
            if (nodeById.TryGetValue(id, out var node) && node.Type != ProceduralCityGenerator.RoadType.Local)
                return true;
        }

        return false;
    }

    private void SpawnBuildingInCell(float cellMinX, float cellMinZ, float cellSize)
    {
        float usableSize = cellSize - setback * 2f;
        if (usableSize <= minFootprint)
        {
            // Cell too small for the configured setback/footprint, skip safely.
            return;
        }

        float footprintX = NextFloat(minFootprint, Mathf.Min(maxFootprint, usableSize));
        float footprintZ = NextFloat(minFootprint, Mathf.Min(maxFootprint, usableSize));
        float height = NextFloat(minHeight, maxHeight);

        // Random position for the building's footprint within the usable area of the cell.
        float posX = cellMinX + setback + NextFloat(0f, usableSize - footprintX);
        float posZ = cellMinZ + setback + NextFloat(0f, usableSize - footprintZ);

        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = "Building";
        go.transform.SetParent(container, worldPositionStays: false);

        // Cube pivot is centered, so lift it by half the height and center the footprint.
        Vector3 worldBase = transform.position + new Vector3(posX + footprintX * 0.5f, 0f, posZ + footprintZ * 0.5f);
        go.transform.position = worldBase + Vector3.up * (height * 0.5f);
        go.transform.localScale = new Vector3(footprintX, height, footprintZ);

        go.GetComponent<MeshRenderer>().sharedMaterial = _sharedMaterial;
    }

    private void EnsureContainer()
    {
        if (container != null) return;

        var existing = transform.Find("Buildings");
        if (existing != null)
        {
            container = existing;
            return;
        }

        var go = new GameObject("Buildings");
        go.transform.SetParent(transform, worldPositionStays: false);
        container = go.transform;
    }

    private void EnsureMaterial()
    {
        // Uses the built-in Standard/URP-Lit shader if available, falls back to Unlit.
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Standard");
        if (shader == null) shader = Shader.Find("Unlit/Color");

        _sharedMaterial = new Material(shader) { color = buildingColor };
    }

    [ContextMenu("Clear Buildings")]
    public void ClearExisting()
    {
        if (container == null)
        {
            var existing = transform.Find("Buildings");
            if (existing == null) return;
            container = existing;
        }

        for (int i = container.childCount - 1; i >= 0; i--)
        {
            var child = container.GetChild(i);
            if (Application.isPlaying) Destroy(child.gameObject);
            else DestroyImmediate(child.gameObject);
        }
    }

    private static int GridIndex(int x, int z, int gridHeight) => x * gridHeight + z;

    private float NextFloat(float min, float max) => (float)(_rng.NextDouble() * (max - min) + min);
}