using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural city street-graph generator (Phase 2).
/// Highways and Avenues are generated as diagonal lines crossing the grid;
/// Streets (local roads) are generated as the orthogonal grid.
/// Each road type can be toggled independently for isolated testing.
/// Deterministic via System.Random + seed.
/// Attach to an empty GameObject and press Play or use the context menu
/// "Generate City" to regenerate in the editor.
/// </summary>
public class ProceduralCityGenerator : MonoBehaviour
{
    public enum RoadType { Local, Avenue, Highway }

    [Header("Seed")]
    public int seed = 12345;
    public bool randomizeSeedOnGenerate = false;

    [Header("Generation Toggles (for isolated testing)")]
    [Tooltip("Generate diagonal Highway lines")]
    public bool generateHighways = true;
    [Tooltip("Generate diagonal Avenue lines")]
    public bool generateAvenues = true;
    [Tooltip("Generate orthogonal local Streets")]
    public bool generateStreets = true;

    [Header("Grid Parameters")]
    [Tooltip("Number of nodes along X")]
    public int gridWidth = 12;
    [Tooltip("Number of nodes along Z")]
    public int gridHeight = 12;
    [Tooltip("Distance between adjacent grid nodes before perturbation")]
    public float cellSize = 20f;

    [Header("Perturbation")]
    [Tooltip("Max random offset applied to a LOCAL node, as a fraction of cellSize")]
    [Range(0f, 0.5f)]
    public float perturbationStrength = 0.25f;
    [Tooltip("Perturbation multiplier applied to Avenue nodes (0 = perfectly straight)")]
    [Range(0f, 1f)]
    public float avenuePerturbationFactor = 0.35f;
    [Tooltip("Perturbation multiplier applied to Highway nodes (0 = perfectly straight)")]
    [Range(0f, 1f)]
    public float highwayPerturbationFactor = 0.05f;

    [Header("Road Hierarchy (diagonal spacing)")]
    [Tooltip("Highway diagonal lines run where (x + z) is a multiple of this")]
    public int highwayInterval = 8;
    [Tooltip("Avenue diagonal lines run where (x - z) is a multiple of this")]
    public int avenueInterval = 4;

    [Header("Irregularity (Local streets only)")]
    [Tooltip("Probability [0-1] that a LOCAL grid edge is removed to create irregular blocks. Avenues and Highways are never removed.")]
    [Range(0f, 0.9f)]
    public float edgeRemovalChance = 0.15f;

    [Header("Street Width")]
    public float localWidth = 6f;
    public float avenueWidth = 12f;
    public float highwayWidth = 20f;
    [Tooltip("Random variance applied on top of the base width for each edge (+/- meters)")]
    public float widthJitter = 1.5f;

    [Header("Gizmos")]
    public bool drawGizmos = true;
    public Color localColor = Color.white;
    public Color avenueColor = Color.yellow;
    public Color highwayColor = Color.red;
    public float nodeGizmoRadius = 0.6f;
    public float highwayGizmoWidth = 3f; // visual thickness hint, drawn as parallel lines

    // --- Data ---
    public List<CityNode> Nodes { get; private set; } = new List<CityNode>();
    public List<CityEdge> Edges { get; private set; } = new List<CityEdge>();

    private System.Random _rng;

    [Serializable]
    public class CityNode
    {
        public int Id;
        public Vector2 Position; // XZ plane
        public List<int> ConnectedEdgeIds = new List<int>();
        public RoadType Type; // highest-ranked road type this node belongs to
    }

    [Serializable]
    public class CityEdge
    {
        public int Id;
        public int NodeA;
        public int NodeB;
        public float Width;
        public RoadType Type;
    }

    private void Awake()
    {
        Generate();
    }

    [ContextMenu("Generate City")]
    public void Generate()
    {
        if (randomizeSeedOnGenerate)
        {
            seed = Guid.NewGuid().GetHashCode();
        }

        _rng = new System.Random(seed);

        Nodes.Clear();
        Edges.Clear();

        GenerateNodes();

        // Each road type has its own independent generation pass.
        if (generateHighways) GenerateHighways();
        if (generateAvenues) GenerateAvenues();
        if (generateStreets) GenerateStreets();

        PruneIsolatedNodes();

        int highwayCount = Edges.FindAll(e => e.Type == RoadType.Highway).Count;
        int avenueCount = Edges.FindAll(e => e.Type == RoadType.Avenue).Count;
        int localCount = Edges.Count - highwayCount - avenueCount;

        Debug.Log($"[ProceduralCityGenerator] Seed {seed}: {Nodes.Count} nodes, " +
                  $"{Edges.Count} edges ({highwayCount} highway, {avenueCount} avenue, {localCount} local).");
    }

    // Positive modulo helper (C#'s % can return negative results)
    private int Mod(int a, int b) => b <= 0 ? 0 : ((a % b) + b) % b;

    // A node belongs to a Highway diagonal line where (x + z) % highwayInterval == 0,
    // and/or an Avenue diagonal line where (x - z) % avenueInterval == 0.
    // Highway takes precedence over Avenue over Local. Disabled types are ignored.
    private RoadType ClassifyNode(int x, int z)
    {
        bool onHighway = generateHighways && highwayInterval > 0 && Mod(x + z, highwayInterval) == 0;
        if (onHighway) return RoadType.Highway;

        bool onAvenue = generateAvenues && avenueInterval > 0 && Mod(x - z, avenueInterval) == 0;
        if (onAvenue) return RoadType.Avenue;

        return RoadType.Local;
    }

    // 1. Base grid of node positions, perturbed by seeded noise
    //    (less perturbation for nodes that sit on a higher-hierarchy diagonal)
    private void GenerateNodes()
    {
        int id = 0;
        for (int x = 0; x < gridWidth; x++)
        {
            for (int z = 0; z < gridHeight; z++)
            {
                float baseX = x * cellSize;
                float baseZ = z * cellSize;

                RoadType nodeType = ClassifyNode(x, z);
                float factor = nodeType switch
                {
                    RoadType.Highway => highwayPerturbationFactor,
                    RoadType.Avenue => avenuePerturbationFactor,
                    _ => 1f
                };

                float maxOffset = cellSize * perturbationStrength * factor;
                float offsetX = NextFloat(-maxOffset, maxOffset);
                float offsetZ = NextFloat(-maxOffset, maxOffset);

                var node = new CityNode
                {
                    Id = id,
                    Position = new Vector2(baseX + offsetX, baseZ + offsetZ),
                    Type = nodeType
                };

                Nodes.Add(node);
                id++;
            }
        }
    }

    // Highways: diagonal lines along direction (1, -1). The line id (x + z) is
    // invariant along this direction, so consecutive nodes on the same line
    // automatically share the same id — no extra lookup needed.
    private void GenerateHighways()
    {
        if (highwayInterval <= 0) return;

        int edgeId = NextEdgeId();
        for (int x = 0; x < gridWidth - 1; x++)
        {
            for (int z = 1; z < gridHeight; z++)
            {
                if (Mod(x + z, highwayInterval) != 0) continue;

                int a = GridIndex(x, z);
                int b = GridIndex(x + 1, z - 1);
                AddEdge(edgeId++, a, b, RoadType.Highway);
            }
        }
    }

    // Avenues: diagonal lines along direction (1, 1). The line id (x - z) is
    // invariant along this direction.
    private void GenerateAvenues()
    {
        if (avenueInterval <= 0) return;

        int edgeId = NextEdgeId();
        for (int x = 0; x < gridWidth - 1; x++)
        {
            for (int z = 0; z < gridHeight - 1; z++)
            {
                if (Mod(x - z, avenueInterval) != 0) continue;

                int a = GridIndex(x, z);
                int b = GridIndex(x + 1, z + 1);
                AddEdge(edgeId++, a, b, RoadType.Avenue);
            }
        }
    }

    // Streets: orthogonal grid (right + bottom neighbor), with random removal
    // for irregular block shapes. Never removes an edge that would isolate a node.
    private void GenerateStreets()
    {
        int edgeId = NextEdgeId();
        var localEdges = new List<CityEdge>();

        for (int x = 0; x < gridWidth; x++)
        {
            for (int z = 0; z < gridHeight; z++)
            {
                int currentId = GridIndex(x, z);

                if (x + 1 < gridWidth)
                {
                    int rightId = GridIndex(x + 1, z);
                    var e = AddEdge(edgeId++, currentId, rightId, RoadType.Local);
                    localEdges.Add(e);
                }

                if (z + 1 < gridHeight)
                {
                    int downId = GridIndex(x, z + 1);
                    var e = AddEdge(edgeId++, currentId, downId, RoadType.Local);
                    localEdges.Add(e);
                }
            }
        }

        foreach (var edge in localEdges)
        {
            if (NextFloat(0f, 1f) > edgeRemovalChance) continue;

            var nodeA = Nodes[edge.NodeA];
            var nodeB = Nodes[edge.NodeB];

            if (nodeA.ConnectedEdgeIds.Count <= 1 || nodeB.ConnectedEdgeIds.Count <= 1)
                continue; // would isolate a node, skip

            nodeA.ConnectedEdgeIds.Remove(edge.Id);
            nodeB.ConnectedEdgeIds.Remove(edge.Id);
            Edges.Remove(edge);
        }
    }

    // Remove nodes that ended up with zero connections
    // (e.g. a node that only matched a road type which was toggled off)
    private void PruneIsolatedNodes()
    {
        Nodes.RemoveAll(n => n.ConnectedEdgeIds.Count == 0);
    }

    private int NextEdgeId() => Edges.Count > 0 ? Edges[Edges.Count - 1].Id + 1 : 0;

    private CityEdge AddEdge(int edgeId, int nodeAId, int nodeBId, RoadType type)
    {
        float baseWidth = type switch
        {
            RoadType.Highway => highwayWidth,
            RoadType.Avenue => avenueWidth,
            _ => localWidth
        };
        float width = baseWidth + NextFloat(-widthJitter, widthJitter);

        var edge = new CityEdge { Id = edgeId, NodeA = nodeAId, NodeB = nodeBId, Width = width, Type = type };
        Edges.Add(edge);
        Nodes[nodeAId].ConnectedEdgeIds.Add(edgeId);
        Nodes[nodeBId].ConnectedEdgeIds.Add(edgeId);
        return edge;
    }

    private int GridIndex(int x, int z) => x * gridHeight + z;

    private float NextFloat(float min, float max)
    {
        return (float)(_rng.NextDouble() * (max - min) + min);
    }

    private void OnDrawGizmos()
    {
        if (!drawGizmos || Nodes == null || Edges == null) return;

        // Draw local roads first, then avenues, then highways, so hierarchy is visually on top
        DrawEdgesOfType(RoadType.Local, localColor, 1);
        DrawEdgesOfType(RoadType.Avenue, avenueColor, 2);
        DrawEdgesOfType(RoadType.Highway, highwayColor, 3);

        foreach (var node in Nodes)
        {
            Gizmos.color = ColorForType(node.Type);
            Gizmos.DrawSphere(ToWorld(node.Position), nodeGizmoRadius);
        }
    }

    private Color ColorForType(RoadType type)
    {
        return type switch
        {
            RoadType.Highway => highwayColor,
            RoadType.Avenue => avenueColor,
            _ => localColor
        };
    }

    private void DrawEdgesOfType(RoadType type, Color color, int parallelLines)
    {
        Gizmos.color = color;
        foreach (var edge in Edges)
        {
            if (edge.Type != type) continue;
            if (edge.NodeA >= Nodes.Count || edge.NodeB >= Nodes.Count) continue;

            Vector3 a = ToWorld(Nodes[edge.NodeA].Position);
            Vector3 b = ToWorld(Nodes[edge.NodeB].Position);

            if (parallelLines <= 1)
            {
                Gizmos.DrawLine(a, b);
                continue;
            }

            // Draw a few parallel offset lines so wider roads read as thicker in the Scene View
            Vector3 dir = (b - a).normalized;
            Vector3 perpendicular = new Vector3(-dir.z, 0f, dir.x);
            float spread = Mathf.Clamp(edge.Width * 0.05f, 0.2f, highwayGizmoWidth);

            for (int i = 0; i < parallelLines; i++)
            {
                float t = parallelLines == 1 ? 0f : (i / (float)(parallelLines - 1) - 0.5f) * 2f;
                Vector3 offset = perpendicular * spread * t;
                Gizmos.DrawLine(a + offset, b + offset);
            }
        }
    }

    private Vector3 ToWorld(Vector2 xz) => transform.position + new Vector3(xz.x, 0f, xz.y);
}