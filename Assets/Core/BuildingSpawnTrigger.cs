using UnityEngine;

/// <summary>
/// Debug/testing helper: check the "spawn" box in the Inspector (or set it
/// from another script) and this fires BuildingSpawner.Spawn() once on the
/// next Update, then resets itself back to false.
///
/// Useful for quickly re-triggering building generation in Play Mode without
/// hunting for the context menu each time.
/// </summary>
public class BuildingSpawnTrigger : MonoBehaviour
{
    [Header("Target")]
    public BuildingSpawner buildingSpawner;

    [Header("Trigger")]
    [Tooltip("Tick this in the Inspector (or set it via code) to fire a spawn. Resets to false automatically after firing.")]
    public bool spawn = false;

    [Tooltip("If true, clears existing buildings before spawning new ones each time")]
    public bool clearBeforeSpawn = true;

    private void Update()
    {
        if (!spawn) return;

        if (buildingSpawner == null)
        {
            Debug.LogWarning("[BuildingSpawnTrigger] No BuildingSpawner assigned.");
            spawn = false;
            return;
        }

        if (clearBeforeSpawn) buildingSpawner.ClearExisting();
        buildingSpawner.Spawn();

        spawn = false; // consume the trigger so it only fires once per tick-on
    }
}