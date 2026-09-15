using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural city street-graph generator (Phase 2).
/// Strategy: perturbed grid. Deterministic via System.Random + seed.
/// Attach to an empty GameObject and press Play or use the context menu
/// "Generate City" to regenerate in the editor.
/// </summary>
public class ProceduralCityGenerator : MonoBehaviour
{
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
    [Tooltip("Max random offset applied to each node, as a fraction of cellSize")]
    [Range(0f, 0.5f)]
    public float perturbationStrength = 0.25f;

    [Header("Irregularity")]
    [Tooltip("Probability [0-1] that a given grid edge is removed to create irregular blocks")]
    [Range(0f, 0.9f)]
    public float edgeRemovalChance = 0.15f;
    [Tooltip("Probability [0-1] that a diagonal 'shortcut' edge is added between adjacent cells")]
    [Range(0f, 0.5f)]
    public float diagonalAddChance = 0.05f;

    [Header("Street Width")]
    public float minStreetWidth = 4f;
    public float maxStreetWidth = 10f;

    [Header("Gizmos")]
    public bool drawGizmos = true;
    public Color nodeColor = new Color(1f, 0.6f, 0f);
    public Color edgeColor = Color.white;
    public float nodeGizmoRadius = 0.6f;

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
        RemoveRandomEdges();
        AddDiagonalShortcuts();
        PruneIsolatedNodes();

        Debug.Log($"[ProceduralCityGenerator] Seed {seed}: generated {Nodes.Count} nodes, {Edges.Count} edges.");
    }

    // 1. Base grid of nodes, perturbed by seeded noise
    private void GenerateNodes()
    {
        int id = 0;
        for (int x = 0; x < gridWidth; x++)
        {
            for (int z = 0; z < gridHeight; z++)
            {
                float baseX = x * cellSize;
                float baseZ = z * cellSize;

                float maxOffset = cellSize * perturbationStrength;
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

    // 2. Connect each node to its right and bottom grid neighbor
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
                    AddEdge(edgeId++, currentId, rightId);
                }

                if (z + 1 < gridHeight)
                {
                    int downId = GridIndex(x, z + 1);
                    AddEdge(edgeId++, currentId, downId);
                }
            }
        }
    }

    // 3. Randomly remove edges to create irregular block shapes
    //    (never remove if it would fully isolate a node)
    private void RemoveRandomEdges()
    {
        var candidates = new List<CityEdge>(Edges);
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

    // 4. Occasionally add a diagonal shortcut inside a grid cell
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
                AddEdge(edgeId++, a, b);
            }
        }
    }

    // 5. Remove nodes that ended up with zero connections
    private void PruneIsolatedNodes()
    {
        Nodes.RemoveAll(n => n.ConnectedEdgeIds.Count == 0);
    }

    private void AddEdge(int edgeId, int nodeAId, int nodeBId)
    {
        float width = NextFloat(minStreetWidth, maxStreetWidth);
        var edge = new CityEdge { Id = edgeId, NodeA = nodeAId, NodeB = nodeBId, Width = width };
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

        Gizmos.color = edgeColor;
        foreach (var edge in Edges)
        {
            if (edge.NodeA >= Nodes.Count || edge.NodeB >= Nodes.Count) continue;
            Vector3 a = ToWorld(Nodes[edge.NodeA].Position);
            Vector3 b = ToWorld(Nodes[edge.NodeB].Position);
            Gizmos.DrawLine(a, b);
        }

        Gizmos.color = nodeColor;
        foreach (var node in Nodes)
        {
            Gizmos.DrawSphere(ToWorld(node.Position), nodeGizmoRadius);
        }
    }

    private Vector3 ToWorld(Vector2 xz) => transform.position + new Vector3(xz.x, 0f, xz.y);
}