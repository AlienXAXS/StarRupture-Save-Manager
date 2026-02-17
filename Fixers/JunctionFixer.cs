using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using StarRuptureSaveFixer.Models;
using StarRuptureSaveFixer.Utils;

namespace StarRuptureSaveFixer.Fixers;

public class JunctionFixer : IFixer
{
    public string Name => "Junction Fixer";

    private record SplineData(long Id, string Key, JToken Entity, long StartId, long EndId,
        (double X, double Y)? StartPos, (double X, double Y)? EndPos);

    public bool ApplyFix(SaveFile saveFile)
    {
        ConsoleLogger.Info($"Applying fix: {Name}");

        try
        {
            JObject root = JObject.Parse(saveFile.JsonContent);

            JToken? itemData = root["itemData"];
            if (itemData == null) { ConsoleLogger.Warning("'itemData' not found in JSON."); return false; }
            JToken? mass = itemData["Mass"];
            if (mass == null) { ConsoleLogger.Warning("'Mass' not found in itemData."); return false; }
            JObject? entities = mass["entities"] as JObject;
            if (entities == null) { ConsoleLogger.Warning("'entities' not found in Mass."); return false; }

            // ── Phase 1: Discovery ──
            var junctionIds = new Dictionary<long, string>();   // eid -> "3-way"/"5-way"
            var splines = new List<SplineData>();
            var poleKeys = new List<string>();
            var droneKeys = new List<string>();

            var props = entities.Properties().ToList();
            ConsoleLogger.Progress($"Scanning {props.Count} entities...");

            foreach (var prop in props)
            {
                long? eid = ExtractId(prop.Name);
                if (eid == null || prop.Value is not JObject)
                    continue;

                string config = GetConfig(prop.Value);

                if (config.Contains("DroneLane_3"))
                    junctionIds[eid.Value] = "3-way";
                else if (config.Contains("DroneLane_5"))
                    junctionIds[eid.Value] = "5-way";

                if (config.Contains("DroneInvisiblePole"))
                    poleKeys.Add(prop.Name);

                if (config.Contains("RailDroneConfig"))
                    droneKeys.Add(prop.Name);

                var sd = GetSplineData(eid.Value, prop.Name, prop.Value);
                if (sd != null)
                    splines.Add(sd);
            }

            var junctionSet = new HashSet<long>(junctionIds.Keys);

            ConsoleLogger.Info($"Junctions: {junctionIds.Count}");
            ConsoleLogger.Info($"Splines: {splines.Count}");
            ConsoleLogger.Info($"Old poles: {poleKeys.Count}");
            ConsoleLogger.Info($"Drones: {droneKeys.Count}");

            if (junctionIds.Count == 0)
            {
                ConsoleLogger.Info("No junctions found. Nothing to fix.");
                return false;
            }

            // ── Phase 2: Analysis — build per-junction spline index ──
            // For each junction: list of (spline, field, neighbor, pos_at_junction)
            var junctionTouches = new Dictionary<long, List<(SplineData Spline, string Field, long Neighbor, (double X, double Y)? Pos)>>();

            foreach (var sp in splines)
            {
                if (junctionSet.Contains(sp.StartId))
                {
                    if (!junctionTouches.ContainsKey(sp.StartId))
                        junctionTouches[sp.StartId] = new();
                    junctionTouches[sp.StartId].Add((sp, "Start", sp.EndId, sp.StartPos));
                }
                if (junctionSet.Contains(sp.EndId))
                {
                    if (!junctionTouches.ContainsKey(sp.EndId))
                        junctionTouches[sp.EndId] = new();
                    junctionTouches[sp.EndId].Add((sp, "End", sp.StartId, sp.EndPos));
                }
            }

            // ── Phase 2 continued: detect lane axis & cluster per junction ──
            long nextId = GetMaxEntityId(entities) + 1;
            var newPoles = new Dictionary<long, JObject>();
            var allChanges = new List<(JToken SplineEntity, string Field, long JunctionId, long PoleId, long SplineId)>();
            int junctionsFixed = 0;
            int junctionsSkipped = 0;

            foreach (long jid in junctionIds.Keys.OrderBy(k => k))
            {
                if (!junctionTouches.TryGetValue(jid, out var touches) || touches.Count < 2)
                {
                    junctionsSkipped++;
                    continue;
                }

                // Group by neighbor to find the lane axis
                var byNeighbor = new Dictionary<long, List<(SplineData Spline, string Field, long Neighbor, (double X, double Y)? Pos)>>();
                foreach (var t in touches)
                {
                    if (!byNeighbor.ContainsKey(t.Neighbor))
                        byNeighbor[t.Neighbor] = new();
                    byNeighbor[t.Neighbor].Add(t);
                }

                // Find a neighbor group with 2+ splines to detect lane axis
                string? laneAxis = null;
                foreach (var group in byNeighbor.Values)
                {
                    if (group.Count >= 2)
                    {
                        var positions = group
                            .Where(t => t.Pos.HasValue)
                            .Select(t => t.Pos!.Value)
                            .ToList();
                        if (positions.Count >= 2)
                        {
                            laneAxis = DetectLaneAxis(positions);
                            break;
                        }
                    }
                }

                if (laneAxis == null)
                {
                    junctionsSkipped++;
                    continue;
                }

                // Cluster ALL touches by lane axis value
                var laneItems = new List<(double Value, (SplineData Spline, string Field, long Neighbor, (double X, double Y)? Pos) Touch)>();
                foreach (var t in touches)
                {
                    if (t.Pos.HasValue)
                    {
                        double val = laneAxis == "x" ? t.Pos.Value.X : t.Pos.Value.Y;
                        laneItems.Add((val, t));
                    }
                }

                var clusters = ClusterByValue(laneItems);

                if (clusters.Count <= 1)
                {
                    junctionsSkipped++;
                    continue;
                }

                ConsoleLogger.Info($"Junction {jid} ({junctionIds[jid]}): {touches.Count} splines -> {clusters.Count} lanes (axis={laneAxis})");

                // ── Phase 3: Fix — one pole per lane cluster ──
                foreach (var cluster in clusters)
                {
                    long poleId = nextId++;
                    newPoles[poleId] = MakePoleEntity();

                    foreach (var (val, touch) in cluster)
                    {
                        allChanges.Add((touch.Spline.Entity, touch.Field, jid, poleId, touch.Spline.Id));
                    }
                }

                junctionsFixed++;
            }

            // ── Summary ──
            ConsoleLogger.Info($"Junctions fixed: {junctionsFixed}");
            ConsoleLogger.Info($"Junctions skipped (single-lane or unconnected): {junctionsSkipped}");
            ConsoleLogger.Info($"Spline rewrites: {allChanges.Count}");
            ConsoleLogger.Info($"New poles: {newPoles.Count}");
            ConsoleLogger.Info($"Old poles to remove: {poleKeys.Count}");
            ConsoleLogger.Info($"Drones to remove: {droneKeys.Count}");

            if (junctionsFixed == 0)
            {
                ConsoleLogger.Info("No junctions needed fixing.");
                return false;
            }

            // ── Apply changes ──

            // Create new poles
            foreach (var (poleId, poleEntity) in newPoles)
                entities[$"(ID={poleId})"] = poleEntity;
            ConsoleLogger.Info($"Created {newPoles.Count} poles");

            // Rewrite splines (track to avoid duplicates)
            var done = new HashSet<(long, string)>();
            int okCount = 0, skipCount = 0, failCount = 0;
            foreach (var (spEntity, field, jid, poleId, spEid) in allChanges)
            {
                var key = (spEid, field);
                if (done.Contains(key)) { skipCount++; continue; }
                done.Add(key);

                if (RewriteSplineField(spEntity, jid, poleId, field))
                    okCount++;
                else
                    failCount++;
            }
            ConsoleLogger.Info($"Rewrote {okCount} endpoints ({skipCount} skipped, {failCount} failed)");

            // Remove old poles
            int removedPoles = 0;
            foreach (string key in poleKeys)
            {
                if (entities.Remove(key))
                    removedPoles++;
            }
            ConsoleLogger.Info($"Removed {removedPoles} old poles");

            // Remove drones
            int removedDrones = 0;
            foreach (string key in droneKeys)
            {
                if (entities.Remove(key))
                    removedDrones++;
            }
            ConsoleLogger.Info($"Removed {removedDrones} drones");

            saveFile.JsonContent = root.ToString(Newtonsoft.Json.Formatting.None);
            ConsoleLogger.Success($"Done! {junctionsFixed} junction(s) fixed.");
            return true;
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            ConsoleLogger.Error($"JSON parsing error: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error($"Error during junction fix: {ex.Message}");
            ConsoleLogger.Error($"Stack trace: {ex.StackTrace}");
            return false;
        }
    }

    private static long? ExtractId(string key)
    {
        var m = Regex.Match(key, @"\(ID=(\d+)\)");
        return m.Success ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    private static string GetConfig(JToken entity)
    {
        return entity["spawnData"]?["entityConfigDataPath"]?.ToString() ?? "";
    }

    private static SplineData? GetSplineData(long id, string key, JToken entity)
    {
        var fragments = entity["fragmentValues"] as JArray;
        if (fragments == null) return null;

        foreach (var frag in fragments)
        {
            string? fragStr = frag.Value<string>();
            if (fragStr == null || !fragStr.Contains("AuSplineConnectionFragment"))
                continue;

            var s = Regex.Match(fragStr, @"StartEntity=\(ID=(\d+)\)");
            var e = Regex.Match(fragStr, @"EndEntity=\(ID=(\d+)\)");
            if (!s.Success || !e.Success) continue;

            long startId = long.Parse(s.Groups[1].Value, CultureInfo.InvariantCulture);
            long endId = long.Parse(e.Groups[1].Value, CultureInfo.InvariantCulture);

            var positions = Regex.Matches(fragStr, @"Position=\(X=([\-\d.]+),Y=([\-\d.]+)");
            (double X, double Y)? startPos = null;
            (double X, double Y)? endPos = null;

            if (positions.Count > 0)
            {
                startPos = (
                    double.Parse(positions[0].Groups[1].Value, CultureInfo.InvariantCulture),
                    double.Parse(positions[0].Groups[2].Value, CultureInfo.InvariantCulture));
                endPos = (
                    double.Parse(positions[positions.Count - 1].Groups[1].Value, CultureInfo.InvariantCulture),
                    double.Parse(positions[positions.Count - 1].Groups[2].Value, CultureInfo.InvariantCulture));
            }

            return new SplineData(id, key, entity, startId, endId, startPos, endPos);
        }
        return null;
    }

    private static bool RewriteSplineField(JToken entity, long oldId, long newId, string field)
    {
        var fragments = entity["fragmentValues"] as JArray;
        if (fragments == null) return false;

        for (int i = 0; i < fragments.Count; i++)
        {
            string? fragStr = fragments[i].Value<string>();
            if (fragStr == null || !fragStr.Contains("AuSplineConnectionFragment"))
                continue;

            string pattern = $"{field}Entity=\\(ID={oldId}\\)";
            string replacement = $"{field}Entity=(ID={newId})";
            string newFrag = Regex.Replace(fragStr, pattern, replacement);
            if (newFrag != fragStr)
            {
                fragments[i] = newFrag;
                return true;
            }
        }
        return false;
    }

    private static JObject MakePoleEntity()
    {
        return new JObject
        {
            ["spawnData"] = new JObject
            {
                ["entityConfigDataPath"] = "/Game/Chimera/Buildings/DroneConnections/InvisibleConnection/DA_DroneInvisiblePole.DA_DroneInvisiblePole",
                ["transform"] = new JObject
                {
                    ["rotation"] = new JObject { ["x"] = 0, ["y"] = 0, ["z"] = 0, ["w"] = 1 },
                    ["translation"] = new JObject { ["x"] = 0, ["y"] = 0, ["z"] = 0 },
                    ["scale3D"] = new JObject { ["x"] = 1, ["y"] = 1, ["z"] = 1 }
                }
            },
            ["tags"] = new JArray(),
            ["fragmentValues"] = new JArray
            {
                "/Script/Chimera.CrElectricityFragment(ElectricityMultiplierLevel=1)"
            }
        };
    }

    private static long GetMaxEntityId(JObject entities)
    {
        long maxId = 0;
        foreach (var prop in entities.Properties())
        {
            long? eid = ExtractId(prop.Name);
            if (eid.HasValue && eid.Value < 4294967295)
                maxId = Math.Max(maxId, eid.Value);
        }
        return maxId;
    }

    private static string DetectLaneAxis(List<(double X, double Y)> positions)
    {
        if (positions.Count < 2) return "x";
        double xSpread = positions.Max(p => p.X) - positions.Min(p => p.X);
        double ySpread = positions.Max(p => p.Y) - positions.Min(p => p.Y);
        return xSpread > ySpread ? "x" : "y";
    }

    private static List<List<(double Value, T Item)>> ClusterByValue<T>(
        List<(double Value, T Item)> items, double tolerance = 15.0)
    {
        if (items.Count == 0) return new();

        var sorted = items.OrderBy(x => x.Value).ToList();
        var clusters = new List<List<(double Value, T Item)>> { new() { sorted[0] } };

        for (int i = 1; i < sorted.Count; i++)
        {
            if (Math.Abs(sorted[i].Value - sorted[i - 1].Value) <= tolerance)
                clusters[^1].Add(sorted[i]);
            else
                clusters.Add(new() { sorted[i] });
        }
        return clusters;
    }
}
