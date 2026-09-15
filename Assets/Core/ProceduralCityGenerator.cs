using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural city street-graph generator (Phase 2).
/// Strategy: perturbed grid with road hierarchy (Local / Avenue / Highway).
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

    [Header("Road Hierarchy")]
    [Tooltip("Every Nth grid line (row or column) becomes an Avenue")]
    public int avenueInterval = 4;
    [Tooltip("Every Nth grid line (row or column) becomes a Highway. Should be a multiple of avenueInterval.")]
    public int highwayInterval = 8;

    [Header("Irregularity (Local roads only)")]
    [Tooltip("Probability [0-1] that a LOCAL grid edge is removed to create irregular blocks. Avenues and Highways are never removed.")]
    [Range(0f, 0.9f)]
    public float edgeRemovalChance = 0.15f;
    [Tooltip("Probability [0-1] that a diagonal 'shortcut' edge is added between adjacent local cells")]
    [Range(0f, 0.5f)]
    public float diagonalAddChance = 0.05f;

    [Header("Street Width")]
    public float localWidth = 6f;
    public float avenueWidth = 12f;
    public float highwayWidth = 20f;
    [Tooltip("Random variance applied on top of the base width for each edge (+/- meters)")]
    public float widthJitter = 1.5f;

    [Header("Gizmos")]
    public bool drawGizmos = true;
    public Color nodeColor = new Color(1f, 0.6f, 0f);
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
        GenerateEdges();
        RemoveRandomLocalEdges();
        AddDiagonalShortcuts();
        PruneIsolatedNodes();

        int highwayCount = Edges.FindAll(e => e.Type == RoadType.Highway).Count;
        int avenueCount = Edges.FindAll(e => e.Type == RoadType.Avenue).Count;
        int localCount = Edges.Count - highwayCount - avenueCount;

        Debug.Log($"[ProceduralCityGenerator] Seed {seed}: {Nodes.Count} nodes, " +
                  $"{Edges.Count} edges ({highwayCount} highway, {avenueCount} avenue, {localCount} local).");
    }

    // Classify a grid line index by hierarchy. Highway takes precedence over Avenue.
    private RoadType ClassifyLine(int index)
    {
        if (highwayInterval > 0 && index % highwayInterval == 0) return RoadType.Highway;
        if (avenueInterval > 0 && index % avenueInterval == 0) return RoadType.Avenue;
        return RoadType.Local;
    }

    // A node's "importance" is the highest-ranked line it sits on (row or column)
    private RoadType ClassifyNode(int x, int z)
    {
        RoadType rowType = ClassifyLine(z);
        RoadType colType = ClassifyLine(x);
        return (RoadType)Mathf.Max((int)rowType, (int)colType);
    }

    // 1. Base grid of nodes, perturbed by seeded noise (less perturbation for higher hierarchy)
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
                    Position = new Vector2(baseX + offsetX, baseZ + offsetZ)
                };

                Nodes.Add(node);
                id++;
            }
        }
    }

    // 2. Connect each node to its right and bottom grid neighbor.
    //    Edge type = the higher-ranked type of the line it travels along.
    private void GenerateEdges()
    {
        int edgeId = 0;
        for (int x = 0; x < gridWidth; x++)
        {
            for (int z = 0; z < gridHeight; z++)
            {
                int currentId = GridIndex(x, z);

                if (x + 1 < gridWidth)
                {
                    int rightId = GridIndex(x + 1, z);
                    // horizontal edge runs along row z
                    RoadType type = ClassifyLine(z);
                    AddEdge(edgeId++, currentId, rightId, type);
                }

                if (z + 1 < gridHeight)
                {
                    int downId = GridIndex(x, z + 1);
                    // vertical edge runs along column x
                    RoadType type = ClassifyLine(x);
                    AddEdge(edgeId++, currentId, downId, type);
                }
            }
        }
    }

    // 3. Randomly remove LOCAL edges only, to create irregular block shapes.
    //    Avenues and Highways are structural and never removed.
    //    Never remove if it would fully isolate a node.
    private void RemoveRandomLocalEdges()
    {
        var candidates = Edges.FindAll(e => e.Type == RoadType.Local);
        foreach (var edge in candidates)
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

    // 4. Occasionally add a diagonal shortcut inside a grid cell (always Local)
    private void AddDiagonalShortcuts()
    {
        int edgeId = Edges.Count > 0 ? Edges[Edges.Count - 1].Id + 1 : 0;

        for (int x = 0; x < gridWidth - 1; x++)
        {
            for (int z = 0; z < gridHeight - 1; z++)
            {
                if (NextFloat(0f, 1f) > diagonalAddChance) continue;

                int a = GridIndex(x, z);
                int b = GridIndex(x + 1, z + 1);
                AddEdge(edgeId++, a, b, RoadType.Local);
            }
        }
    }

    // 5. Remove nodes that ended up with zero connections
    private void PruneIsolatedNodes()
    {
        Nodes.RemoveAll(n => n.ConnectedEdgeIds.Count == 0);
    }

    private void AddEdge(int edgeId, int nodeAId, int nodeBId, RoadType type)
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

        Gizmos.color = nodeColor;
        foreach (var node in Nodes)
        {
            Gizmos.DrawSphere(ToWorld(node.Position), nodeGizmoRadius);
        }
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