using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using StarRuptureSaveFixer.Models;
using StarRuptureSaveFixer.Utils;

namespace StarRuptureSaveFixer.Fixers;

/// <summary>
/// Removes orphan viewport/window entities from Mass save data.
/// </summary>
public class OrphanWindowRemover : IFixer
{
    private const double DistanceThreshold3D = 300.0;
    private const double MaxStabilityStrength = 0.0;
    public string Name => "Orphan Window Remover";

    private sealed record EntityInfo(long Id, string Key, JToken Entity, string ConfigPath, bool IsWindow, Vector3 Position);

    private sealed record WindowCandidate(
        EntityInfo Window,
        double Strength,
        int NonWindowGraphConnections,
        int NonWindowCustomConnections,
        EntityInfo? NearestNonWindow,
        double NearestDistance3D);

    private readonly record struct Vector3(double X, double Y, double Z);

    public bool ApplyFix(SaveFile saveFile)
    {
        ConsoleLogger.Info($"Applying fix: {Name}");

        try
        {
            JObject root = JObject.Parse(saveFile.JsonContent);

            if (root["itemData"]?["Mass"] is not JObject mass)
            {
                ConsoleLogger.Warning("'itemData.Mass' not found in JSON.");
                return false;
            }

            if (mass["entities"] is not JObject entities)
            {
                ConsoleLogger.Warning("'itemData.Mass.entities' not found in JSON.");
                return false;
            }

            List<EntityInfo> entityInfos = ReadEntityInfos(entities);
            Dictionary<long, EntityInfo> entitiesById = entityInfos.ToDictionary(entity => entity.Id);
            HashSet<long> windowIds = entityInfos.Where(entity => entity.IsWindow).Select(entity => entity.Id).ToHashSet();
            List<EntityInfo> supportEntities = entityInfos.Where(entity => !entity.IsWindow && IsBuildingSupportConfig(entity.ConfigPath)).ToList();

            ConsoleLogger.Info($"Entities with positions: {entityInfos.Count}");
            ConsoleLogger.Info($"Viewport/window entities: {windowIds.Count}");
            ConsoleLogger.Info($"Non-window building/support entities: {supportEntities.Count}");

            List<WindowCandidate> candidates = FindCandidates(mass, entityInfos.Where(entity => entity.IsWindow), supportEntities, entitiesById, windowIds);

            ConsoleLogger.Info($"Probable orphan window candidates: {candidates.Count}");
            foreach (WindowCandidate candidate in candidates.Take(25))
            {
                string nearest = candidate.NearestNonWindow == null
                    ? "none"
                    : $"{candidate.NearestNonWindow.Key} dist3d={candidate.NearestDistance3D:F1}";
                ConsoleLogger.Info(
                    $"{candidate.Window.Key} {GetWindowType(candidate.Window.ConfigPath)} pos=({candidate.Window.Position.X:F1}, {candidate.Window.Position.Y:F1}, {candidate.Window.Position.Z:F1}) strength={candidate.Strength:F1} graph={candidate.NonWindowGraphConnections} custom={candidate.NonWindowCustomConnections} nearest={nearest}");
            }

            if (candidates.Count > 25)
            {
                ConsoleLogger.Info($"... {candidates.Count - 25} more candidate(s) not shown.");
            }

            if (candidates.Count == 0)
            {
                ConsoleLogger.Info("No probable orphan windows found. Save file is clean.");
                return false;
            }

            HashSet<long> idsToRemove = candidates.Select(candidate => candidate.Window.Id).ToHashSet();
            int removedEntityCount = RemoveEntities(entities, idsToRemove);
            int removedReferenceCount = RemoveKnownMassReferences(mass, idsToRemove);
            int remainingReferences = CountTargetReferences(mass, idsToRemove);

            ConsoleLogger.Info($"Removed window entities: {removedEntityCount}");
            ConsoleLogger.Info($"Removed Mass references: {removedReferenceCount}");
            ConsoleLogger.Info($"Remaining exact Mass references after cleanup: {remainingReferences}");

            if (remainingReferences > 0)
            {
                ConsoleLogger.Warning("Some exact references remain. The save was still updated, but inspect the JSON before using it in game.");
            }

            saveFile.JsonContent = root.ToString(Newtonsoft.Json.Formatting.None);
            ConsoleLogger.Success($"Successfully removed {removedEntityCount} orphan window(s).");
            return removedEntityCount > 0 || removedReferenceCount > 0;
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            ConsoleLogger.Error($"JSON parsing error: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error($"Error during orphan window removal: {ex.Message}");
            ConsoleLogger.Error($"Stack trace: {ex.StackTrace}");
            return false;
        }
    }

    private static List<EntityInfo> ReadEntityInfos(JObject entities)
    {
        var result = new List<EntityInfo>();

        foreach (JProperty property in entities.Properties())
        {
            long? id = ExtractId(property.Name);
            if (!id.HasValue)
            {
                continue;
            }

            string configPath = property.Value["spawnData"]?["entityConfigDataPath"]?.ToString() ?? "";
            if (!TryReadPosition(property.Value, out Vector3 position))
            {
                continue;
            }

            result.Add(new EntityInfo(id.Value, property.Name, property.Value, configPath, IsWindowConfig(configPath), position));
        }

        return result;
    }

    private static List<WindowCandidate> FindCandidates(
        JObject mass,
        IEnumerable<EntityInfo> windows,
        IReadOnlyList<EntityInfo> nonWindows,
        IReadOnlyDictionary<long, EntityInfo> entitiesById,
        HashSet<long> windowIds)
    {
        var candidates = new List<WindowCandidate>();

        foreach (EntityInfo window in windows)
        {
            double strength = GetStabilityStrength(window.Entity);
            int graphConnections = CountNonWindowGraphConnections(mass, window.Id, entitiesById, windowIds);
            int customConnections = CountNonWindowCustomConnections(mass, window.Id, entitiesById, windowIds);
            EntityInfo? nearest = FindNearestNonWindow(window, nonWindows, out double nearestDistance3D);

            if (nearest != null
                && nearestDistance3D >= DistanceThreshold3D
                && strength <= MaxStabilityStrength
                && graphConnections == 0
                && customConnections == 0)
            {
                candidates.Add(new WindowCandidate(window, strength, graphConnections, customConnections, nearest, nearestDistance3D));
            }
        }

        return candidates;
    }

    private static bool IsWindowConfig(string configPath)
    {
        return configPath.Contains("/Viewport/ViewportLeft/", StringComparison.OrdinalIgnoreCase)
            || configPath.Contains("/Viewport/ViewportMiddle/", StringComparison.OrdinalIgnoreCase)
            || configPath.Contains("/Viewport/ViewportRight/", StringComparison.OrdinalIgnoreCase)
            || configPath.Contains("/Viewport/ViewportSingle/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsBuildingSupportConfig(string configPath)
    {
        return configPath.Contains("/Buildings/", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWindowType(string configPath)
    {
        if (configPath.Contains("ViewportLeft", StringComparison.OrdinalIgnoreCase))
            return "ViewportLeft";
        if (configPath.Contains("ViewportMiddle", StringComparison.OrdinalIgnoreCase))
            return "ViewportMiddle";
        if (configPath.Contains("ViewportRight", StringComparison.OrdinalIgnoreCase))
            return "ViewportRight";
        if (configPath.Contains("ViewportSingle", StringComparison.OrdinalIgnoreCase))
            return "ViewportSingle";

        return "Viewport";
    }

    private static bool TryReadPosition(JToken entity, out Vector3 position)
    {
        position = default;
        JToken? translation = entity["spawnData"]?["transform"]?["translation"];
        if (translation == null)
        {
            return false;
        }

        double? x = translation["x"]?.Value<double>();
        double? y = translation["y"]?.Value<double>();
        double? z = translation["z"]?.Value<double>();
        if (!x.HasValue || !y.HasValue || !z.HasValue)
        {
            return false;
        }

        position = new Vector3(x.Value, y.Value, z.Value);
        return true;
    }

    private static double GetStabilityStrength(JToken entity)
    {
        if (entity["fragmentValues"] is not JArray fragments)
        {
            return 0.0;
        }

        foreach (JToken fragment in fragments)
        {
            string fragmentText = fragment.ToString();
            if (!fragmentText.StartsWith("/Script/Chimera.CrMassBuildingStabilityData", StringComparison.Ordinal))
            {
                continue;
            }

            Match match = Regex.Match(fragmentText, @"Strength=([-+]?\d+(?:\.\d+)?)");
            if (match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double strength))
            {
                return strength;
            }
        }

        return 0.0;
    }

    private static EntityInfo? FindNearestNonWindow(EntityInfo window, IReadOnlyList<EntityInfo> nonWindows, out double nearestDistance3D)
    {
        EntityInfo? nearest = null;
        nearestDistance3D = double.MaxValue;

        foreach (EntityInfo other in nonWindows)
        {
            double distance = Distance3D(window.Position, other.Position);
            if (distance < nearestDistance3D)
            {
                nearestDistance3D = distance;
                nearest = other;
            }
        }

        return nearest;
    }

    private static double Distance3D(Vector3 a, Vector3 b)
    {
        double dx = a.X - b.X;
        double dy = a.Y - b.Y;
        double dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static int CountNonWindowGraphConnections(
        JObject mass,
        long windowId,
        IReadOnlyDictionary<long, EntityInfo> entitiesById,
        HashSet<long> windowIds)
    {
        if (mass["stabilitySubsystemState"]?["graphData"]?["neighbours"] is not JObject neighbours)
        {
            return 0;
        }

        var connectedIds = new HashSet<long>();
        AddIdsFromValues(neighbours[FormatIdKey(windowId)]?["values"], connectedIds);

        foreach (JProperty property in neighbours.Properties())
        {
            long? sourceId = ExtractId(property.Name);
            if (!sourceId.HasValue || sourceId.Value == windowId)
            {
                continue;
            }

            if (TokenContainsId(property.Value, windowId))
            {
                connectedIds.Add(sourceId.Value);
            }
        }

        return connectedIds.Count(id => IsKnownNonWindow(id, entitiesById, windowIds));
    }

    private static int CountNonWindowCustomConnections(
        JObject mass,
        long windowId,
        IReadOnlyDictionary<long, EntityInfo> entitiesById,
        HashSet<long> windowIds)
    {
        if (mass["stabilitySubsystemState"]?["customConnectionData"] is not JObject customConnectionData)
        {
            return 0;
        }

        var connectedIds = new HashSet<long>();
        AddIdsFromToken(customConnectionData[FormatIdKey(windowId)], connectedIds);

        foreach (JProperty property in customConnectionData.Properties())
        {
            long? sourceId = ExtractId(property.Name);
            if (!sourceId.HasValue || sourceId.Value == windowId)
            {
                continue;
            }

            if (TokenContainsId(property.Value, windowId))
            {
                connectedIds.Add(sourceId.Value);
            }
        }

        return connectedIds.Count(id => IsKnownNonWindow(id, entitiesById, windowIds));
    }

    private static bool IsKnownNonWindow(long id, IReadOnlyDictionary<long, EntityInfo> entitiesById, HashSet<long> windowIds)
    {
        return entitiesById.ContainsKey(id) && !windowIds.Contains(id);
    }

    private static int RemoveEntities(JObject entities, HashSet<long> idsToRemove)
    {
        int removed = 0;

        foreach (long id in idsToRemove)
        {
            if (entities.Remove(FormatIdKey(id)))
            {
                removed++;
            }
        }

        return removed;
    }

    private static int RemoveKnownMassReferences(JObject mass, HashSet<long> idsToRemove)
    {
        int removed = 0;

        if (mass["stabilitySubsystemState"] is JObject stability)
        {
            removed += RemoveTargetKeyedProperties(stability["graphData"]?["neighbours"] as JObject, idsToRemove);
            removed += RemoveTargetKeyedProperties(stability["graphData"]?["nodeDatas"] as JObject, idsToRemove);
            removed += RemoveTargetKeyedProperties(stability["customConnectionData"] as JObject, idsToRemove);
            removed += RemoveTargetKeyedProperties(stability["rampConnectionData"] as JObject, idsToRemove);
            removed += RemoveTargetKeyedProperties(stability["buildingFoundationData"] as JObject, idsToRemove);
            removed += RemoveTargetKeyedProperties(stability["buildingDefaultFoundationData"] as JObject, idsToRemove);
            removed += RemoveArrayItemsReferencingIds(stability, idsToRemove);
        }

        if (mass["electricitySubsystemState"] is JObject electricity)
        {
            removed += RemoveTargetKeyedProperties(electricity["nodeData"] as JObject, idsToRemove);
            removed += RemoveEntriesContainingIds(electricity["connectorData"] as JObject, idsToRemove);
            removed += RemoveArrayItemsReferencingIds(electricity, idsToRemove);
        }

        removed += RemoveEntriesContainingIds(mass["foundableEntitiesSpawnData"] as JObject, idsToRemove);
        removed += RemoveEntriesContainingIds(mass["foundableEntityToPersistentIdMap"] as JObject, idsToRemove);
        removed += RemoveEntriesContainingIds(mass["buildingSpawnPointsSaveData"]?["spawnPointOwnerships"] as JObject, idsToRemove);
        removed += RemoveEntriesContainingIds(mass["logisticsRequestSubsystemState"]?["requestData"] as JObject, idsToRemove);
        removed += RemoveEntriesContainingIds(mass["logisticsRequestSubsystemState"]?["storageToItemsInTransfer"] as JObject, idsToRemove);
        removed += RemoveArrayItemsReferencingIds(mass, idsToRemove);
        removed += RemoveTargetKeyedProperties(mass, idsToRemove);

        return removed;
    }

    private static int RemoveTargetKeyedProperties(JObject? obj, HashSet<long> idsToRemove)
    {
        if (obj == null)
        {
            return 0;
        }

        int removed = 0;
        foreach (JProperty property in obj.Properties().ToList())
        {
            if (IsTargetKey(property.Name, idsToRemove))
            {
                property.Remove();
                removed++;
            }
        }

        return removed;
    }

    private static int RemoveEntriesContainingIds(JObject? obj, HashSet<long> idsToRemove)
    {
        if (obj == null)
        {
            return 0;
        }

        int removed = 0;
        foreach (JProperty property in obj.Properties().ToList())
        {
            if (IsTargetKey(property.Name, idsToRemove) || TokenContainsAnyId(property.Value, idsToRemove))
            {
                property.Remove();
                removed++;
            }
        }

        return removed;
    }

    private static int RemoveArrayItemsReferencingIds(JToken token, HashSet<long> idsToRemove)
    {
        int removed = 0;

        if (token is JArray array)
        {
            for (int index = array.Count - 1; index >= 0; index--)
            {
                JToken item = array[index];
                if (TokenContainsAnyId(item, idsToRemove))
                {
                    item.Remove();
                    removed++;
                }
                else
                {
                    removed += RemoveArrayItemsReferencingIds(item, idsToRemove);
                }
            }
        }
        else if (token is JObject obj)
        {
            foreach (JProperty property in obj.Properties().ToList())
            {
                if (IsTargetKey(property.Name, idsToRemove))
                {
                    property.Remove();
                    removed++;
                }
                else
                {
                    removed += RemoveArrayItemsReferencingIds(property.Value, idsToRemove);
                }
            }
        }

        return removed;
    }

    private static int CountTargetReferences(JToken token, HashSet<long> idsToRemove)
    {
        int count = 0;

        if (token is JObject obj)
        {
            foreach (JProperty property in obj.Properties())
            {
                if (IsTargetKey(property.Name, idsToRemove))
                {
                    count++;
                }

                count += CountTargetReferences(property.Value, idsToRemove);
            }
        }
        else if (token is JArray array)
        {
            foreach (JToken item in array)
            {
                count += CountTargetReferences(item, idsToRemove);
            }
        }
        else if (token is JValue value)
        {
            foreach (long id in idsToRemove)
            {
                if (ValueMatchesId(value, id))
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    private static void AddIdsFromValues(JToken? valuesToken, HashSet<long> ids)
    {
        if (valuesToken is not JArray values)
        {
            return;
        }

        foreach (JToken value in values)
        {
            long? id = ReadIdObject(value);
            if (id.HasValue)
            {
                ids.Add(id.Value);
            }
        }
    }

    private static void AddIdsFromToken(JToken? token, HashSet<long> ids)
    {
        if (token == null)
        {
            return;
        }

        long? id = ReadIdObject(token);
        if (id.HasValue)
        {
            ids.Add(id.Value);
        }

        foreach (JToken child in token.Children())
        {
            AddIdsFromToken(child, ids);
        }
    }

    private static bool TokenContainsAnyId(JToken token, HashSet<long> idsToRemove)
    {
        return idsToRemove.Any(id => TokenContainsId(token, id));
    }

    private static bool TokenContainsId(JToken token, long id)
    {
        if (token is JObject obj)
        {
            long? objectId = ReadIdObject(obj);
            if (objectId == id)
            {
                return true;
            }

            return obj.Properties().Any(property => IsTargetKey(property.Name, id) || TokenContainsId(property.Value, id));
        }

        if (token is JArray array)
        {
            return array.Any(item => TokenContainsId(item, id));
        }

        return token is JValue value && ValueMatchesId(value, id);
    }

    private static long? ReadIdObject(JToken token)
    {
        if (token is JObject obj && obj["iD"] != null && long.TryParse(obj["iD"]!.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
        {
            return id;
        }

        return null;
    }

    private static bool ValueMatchesId(JValue value, long id)
    {
        if (value.Type == JTokenType.Integer && Convert.ToInt64(value.Value, CultureInfo.InvariantCulture) == id)
        {
            return true;
        }

        return value.Type == JTokenType.String && Convert.ToString(value.Value, CultureInfo.InvariantCulture)?.Contains(FormatIdKey(id), StringComparison.Ordinal) == true;
    }

    private static bool IsTargetKey(string key, HashSet<long> idsToRemove)
    {
        return idsToRemove.Any(id => IsTargetKey(key, id));
    }

    private static bool IsTargetKey(string key, long id)
    {
        string idKey = FormatIdKey(id);
        return key == idKey || key == $"(UId={idKey})" || key.Contains(idKey, StringComparison.Ordinal);
    }

    private static string FormatIdKey(long id)
    {
        return $"(ID={id})";
    }

    private static long? ExtractId(string key)
    {
        Match match = Regex.Match(key, @"\(ID=(\d+)\)");
        return match.Success ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }
}
