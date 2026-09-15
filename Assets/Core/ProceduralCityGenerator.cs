using System;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Procedural city street-graph generator (Phase 2).
/// Highways and Avenues are generated as organic paths: a seeded, biased
/// random walk across the grid where each step can be straight or diagonal,
/// chosen randomly but weighted toward the path's target — producing bends
/// and irregular routes instead of a perfect mathematical grid.
/// Streets (local roads) fill in the remaining orthogonal grid.
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
    [Tooltip("Generate organic Highway paths")]
    public bool generateHighways = true;
    [Tooltip("Generate organic Avenue paths")]
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

    [Header("Perturbation (Local streets only)")]
    [Tooltip("Max random offset applied to a LOCAL node, as a fraction of cellSize")]
    [Range(0f, 0.5f)]
    public float perturbationStrength = 0.25f;

    [Header("Highways (organic path)")]
    [Tooltip("How many highway paths to grow across the city")]
    public int highwayCount = 3;
    [Tooltip("0 = wanders freely (very organic), 1 = always continues straight/diagonal once picked (very rigid)")]
    [Range(0f, 1f)]
    public float highwayStraightBias = 0.8f;

    [Header("Avenues (organic path)")]
    [Tooltip("How many avenue paths to grow across the city")]
    public int avenueCount = 6;
    [Tooltip("0 = wanders freely (very organic), 1 = always continues straight/diagonal once picked (very rigid)")]
    [Range(0f, 1f)]
    public float avenueStraightBias = 0.5f;

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
    private int _nextEdgeId;
    private readonly HashSet<(int a, int b)> _usedPairs = new HashSet<(int, int)>();
    private readonly Dictionary<(int x, int z), RoadType> _intendedType = new Dictionary<(int, int), RoadType>();

    private static readonly (int dx, int dz)[] EightDirections =
    {
        (1, 0), (-1, 0), (0, 1), (0, -1),
        (1, 1), (1, -1), (-1, 1), (-1, -1)
    };

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
        _nextEdgeId = 0;
        _usedPairs.Clear();
        _intendedType.Clear();

        Nodes.Clear();
        Edges.Clear();

        // 1. Plan organic paths first (index space only) so node perturbation
        //    can already take hierarchy into account.
        List<List<(int x, int z)>> highwayPaths = generateHighways
            ? PlanPaths(highwayCount, RoadType.Highway, highwayStraightBias)
            : new List<List<(int x, int z)>>();

        List<List<(int x, int z)>> avenuePaths = generateAvenues
            ? PlanPaths(avenueCount, RoadType.Avenue, avenueStraightBias)
            : new List<List<(int x, int z)>>();

        // 2. Create node positions
        GenerateNodes();

        // 3. Build edges: highways and avenues first (higher priority),
        //    streets fill the gaps and never overwrite an existing pair.
        if (generateHighways) BuildEdgesFromPaths(highwayPaths, RoadType.Highway, highwayWidth);
        if (generateAvenues) BuildEdgesFromPaths(avenuePaths, RoadType.Avenue, avenueWidth);
        if (generateStreets) GenerateStreets();

        PruneIsolatedNodes();
        RecomputeNodeTypes();

        int highwayEdgeCount = Edges.FindAll(e => e.Type == RoadType.Highway).Count;
        int avenueEdgeCount = Edges.FindAll(e => e.Type == RoadType.Avenue).Count;
        int localEdgeCount = Edges.Count - highwayEdgeCount - avenueEdgeCount;

        Debug.Log($"[ProceduralCityGenerator] Seed {seed}: {Nodes.Count} nodes, " +
                  $"{Edges.Count} edges ({highwayEdgeCount} highway, {avenueEdgeCount} avenue, {localEdgeCount} local).");
    }

    // ---------- Organic path planning ----------

    // Grows `count` paths from a random border cell toward another random
    // border cell, using a biased random walk over the 8 grid directions.
    // Each step can be orthogonal (straight) or diagonal, chosen randomly
    // and weighted toward the target, so the resulting road bends organically
    // instead of following a fixed formula.
    private List<List<(int x, int z)>> PlanPaths(int count, RoadType type, float straightBias)
    {
        var paths = new List<List<(int x, int z)>>();
        int maxSteps = (gridWidth + gridHeight) * 2;

        for (int i = 0; i < count; i++)
        {
            var start = RandomBorderCell();
            var target = RandomBorderCell();

            int guard = 0;
            while (target == start && guard < 5)
            {
                target = RandomBorderCell();
                guard++;
            }

            var path = WalkPath(start, target, straightBias, maxSteps);
            if (path.Count < 2) continue;

            paths.Add(path);
            foreach (var cell in path) MarkIntendedType(cell.x, cell.z, type);
        }

        return paths;
    }

    private (int x, int z) RandomBorderCell()
    {
        int side = _rng.Next(4); // 0=left, 1=right, 2=top, 3=bottom
        switch (side)
        {
            case 0: return (0, _rng.Next(gridHeight));
            case 1: return (gridWidth - 1, _rng.Next(gridHeight));
            case 2: return (_rng.Next(gridWidth), 0);
            default: return (_rng.Next(gridWidth), gridHeight - 1);
        }
    }

    private List<(int x, int z)> WalkPath((int x, int z) start, (int x, int z) target, float straightBias, int maxSteps)
    {
        var path = new List<(int x, int z)> { start };
        var visited = new HashSet<(int x, int z)> { start };
        var current = start;
        (int dx, int dz)? prevDir = null;

        for (int step = 0; step < maxSteps; step++)
        {
            if (current == target) break;

            var candidates = new List<(int dx, int dz)>();
            foreach (var dir in EightDirections)
            {
                var next = (x: current.x + dir.dx, z: current.z + dir.dz);
                if (next.x < 0 || next.x >= gridWidth || next.z < 0 || next.z >= gridHeight) continue;
                if (visited.Contains(next)) continue;
                candidates.Add(dir);
            }

            if (candidates.Count == 0) break; // dead end, stop the path here

            (int dx, int dz) chosen;
            if (prevDir.HasValue && candidates.Contains(prevDir.Value) && NextFloat(0f, 1f) < straightBias)
            {
                // Keep going the same way (straight or diagonal, whichever it already was)
                chosen = prevDir.Value;
            }
            else
            {
                // Randomly pick a new direction, weighted toward the target —
                // this is what makes each step randomly straight or diagonal.
                chosen = WeightedTowardTarget(candidates, current, target);
            }

            current = (current.x + chosen.dx, current.z + chosen.dz);
            path.Add(current);
            visited.Add(current);
            prevDir = chosen;
        }

        return path;
    }

    private (int dx, int dz) WeightedTowardTarget(List<(int dx, int dz)> candidates, (int x, int z) current, (int x, int z) target)
    {
        float toTargetX = target.x - current.x;
        float toTargetZ = target.z - current.z;
        float len = Mathf.Sqrt(toTargetX * toTargetX + toTargetZ * toTargetZ);
        if (len < 0.001f) len = 1f;
        toTargetX /= len;
        toTargetZ /= len;

        var weights = new List<float>(candidates.Count);
        float totalWeight = 0f;

        foreach (var c in candidates)
        {
            float dlen = Mathf.Sqrt(c.dx * c.dx + c.dz * c.dz);
            float dot = (c.dx / dlen) * toTargetX + (c.dz / dlen) * toTargetZ; // -1..1
            // Every direction keeps some chance (min 0.05) so the walk can still wiggle away from target;
            // directions aligned with target get progressively more weight.
            float weight = Mathf.Max(0.05f, dot + 1.1f);
            weights.Add(weight);
            totalWeight += weight;
        }

        float roll = NextFloat(0f, totalWeight);
        float cumulative = 0f;
        for (int i = 0; i < candidates.Count; i++)
        {
            cumulative += weights[i];
            if (roll <= cumulative) return candidates[i];
        }

        return candidates[candidates.Count - 1];
    }

    private void MarkIntendedType(int x, int z, RoadType type)
    {
        var key = (x, z);
        if (!_intendedType.TryGetValue(key, out var existing) || type > existing)
        {
            _intendedType[key] = type;
        }
    }

    // ---------- Node / edge generation ----------

    // Base grid of node positions, perturbed by seeded noise. Nodes that a
    // highway/avenue path passes through get little to no perturbation so
    // the organic path reads clearly against the looser local grid.
    private void GenerateNodes()
    {
        int id = 0;
        for (int x = 0; x < gridWidth; x++)
        {
            for (int z = 0; z < gridHeight; z++)
            {
                float baseX = x * cellSize;
                float baseZ = z * cellSize;

                _intendedType.TryGetValue((x, z), out var intended);
                float factor = intended switch
                {
                    RoadType.Highway => 0.05f,
                    RoadType.Avenue => 0.3f,
                    _ => 1f
                };

                float maxOffset = cellSize * perturbationStrength * factor;
                float offsetX = NextFloat(-maxOffset, maxOffset);
                float offsetZ = NextFloat(-maxOffset, maxOffset);

                var node = new CityNode
                {
                    Id = id,
                    Position = new Vector2(baseX + offsetX, baseZ + offsetZ),
                    Type = intended
                };

                Nodes.Add(node);
                id++;
            }
        }
    }

    private void BuildEdgesFromPaths(List<List<(int x, int z)>> paths, RoadType type, float baseWidth)
    {
        foreach (var path in paths)
        {
            for (int i = 0; i < path.Count - 1; i++)
            {
                int a = GridIndex(path[i].x, path[i].z);
                int b = GridIndex(path[i + 1].x, path[i + 1].z);
                AddEdgeIfNew(a, b, type, baseWidth);
            }
        }
    }

    // Orthogonal local grid, filling in whatever highways/avenues didn't
    // already claim. Randomly removes some edges for irregular block shapes,
    // never isolating a node.
    private void GenerateStreets()
    {
        var localEdges = new List<CityEdge>();

        for (int x = 0; x < gridWidth; x++)
        {
            for (int z = 0; z < gridHeight; z++)
            {
                int currentId = GridIndex(x, z);

                if (x + 1 < gridWidth)
                {
                    var e = AddEdgeIfNew(currentId, GridIndex(x + 1, z), RoadType.Local, localWidth);
                    if (e != null) localEdges.Add(e);
                }

                if (z + 1 < gridHeight)
                {
                    var e = AddEdgeIfNew(currentId, GridIndex(x, z + 1), RoadType.Local, localWidth);
                    if (e != null) localEdges.Add(e);
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
    // (e.g. a node only reachable through a road type that was toggled off)
    private void PruneIsolatedNodes()
    {
        Nodes.RemoveAll(n => n.ConnectedEdgeIds.Count == 0);
    }

    // Final pass: a node's Type reflects the highest-priority edge actually
    // touching it after pruning/removal, so gizmos and downstream systems
    // read the real graph rather than the original plan.
    private void RecomputeNodeTypes()
    {
        var lookup = new Dictionary<int, CityEdge>();
        foreach (var e in Edges) lookup[e.Id] = e;

        foreach (var node in Nodes)
        {
            RoadType best = RoadType.Local;
            foreach (var eid in node.ConnectedEdgeIds)
            {
                if (lookup.TryGetValue(eid, out var edge) && edge.Type > best)
                    best = edge.Type;
            }
            node.Type = best;
        }
    }

    private CityEdge AddEdgeIfNew(int nodeAId, int nodeBId, RoadType type, float baseWidth)
    {
        var pair = nodeAId < nodeBId ? (nodeAId, nodeBId) : (nodeBId, nodeAId);
        if (_usedPairs.Contains(pair)) return null; // a higher-priority road already claimed this segment

        _usedPairs.Add(pair);

        float width = baseWidth + NextFloat(-widthJitter, widthJitter);
        var edge = new CityEdge { Id = _nextEdgeId++, NodeA = nodeAId, NodeB = nodeBId, Width = width, Type = type };

        Edges.Add(edge);
        Nodes[nodeAId].ConnectedEdgeIds.Add(edge.Id);
        Nodes[nodeBId].ConnectedEdgeIds.Add(edge.Id);
        return edge;
    }

    private int GridIndex(int x, int z) => x * gridHeight + z;

    private float NextFloat(float min, float max)
    {
        return (float)(_rng.NextDouble() * (max - min) + min);
    }

    // ---------- Gizmos ----------

    private void OnDrawGizmos()
    {
        if (!drawGizmos || Nodes == null || Edges == null) return;

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