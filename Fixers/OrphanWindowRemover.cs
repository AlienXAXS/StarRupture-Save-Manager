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

    private static readonly Regex IdPattern = new(@"\(ID=(\d+)\)", RegexOptions.Compiled);
    private static readonly Regex StrengthPattern = new(@"Strength=([-+]?\d+(?:\.\d+)?)", RegexOptions.Compiled);

    private sealed record EntityInfo(long Id, string Key, JToken Entity, string ConfigPath, bool IsWindow, Vector3 Position);

    private sealed record WindowCandidate(
        EntityInfo Window,
        double Strength,
        int NonWindowGraphConnections,
        int NonWindowCustomConnections,
        EntityInfo? NearestNonWindow,
        double NearestDistance3D);

    private readonly record struct Vector3(double X, double Y, double Z);

    private sealed class IdKeySet
    {
        private readonly HashSet<string> _exactKeys;
        private readonly string[] _substrings;

        public IdKeySet(HashSet<long> ids)
        {
            _exactKeys = new HashSet<string>(ids.Count * 2, StringComparer.Ordinal);
            var substrings = new List<string>(ids.Count);

            foreach (long id in ids)
            {
                string idKey = $"(ID={id})";
                _exactKeys.Add(idKey);
                _exactKeys.Add($"(UId={idKey})");
                substrings.Add(idKey);
            }

            _substrings = substrings.ToArray();
        }

        public bool Matches(string key)
        {
            if (_exactKeys.Contains(key))
            {
                return true;
            }

            foreach (string s in _substrings)
            {
                if (key.Contains(s, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class ConnectionIndex
    {
        private readonly Dictionary<long, HashSet<long>> _adj = new();

        public void AddEdge(long a, long b)
        {
            GetOrAdd(a).Add(b);
            GetOrAdd(b).Add(a);
        }

        public IReadOnlyCollection<long> GetConnected(long id)
        {
            return _adj.TryGetValue(id, out HashSet<long>? set) ? set : [];
        }

        private HashSet<long> GetOrAdd(long id)
        {
            if (!_adj.TryGetValue(id, out HashSet<long>? set))
            {
                set = new HashSet<long>();
                _adj[id] = set;
            }

            return set;
        }
    }

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

            JObject? stability = mass["stabilitySubsystemState"] as JObject;
            ConnectionIndex graphIndex = BuildGraphConnectionIndex(stability?["graphData"]?["neighbours"] as JObject);
            ConnectionIndex customIndex = BuildCustomConnectionIndex(stability?["customConnectionData"] as JObject);

            List<WindowCandidate> candidates = FindCandidates(
                entityInfos.Where(entity => entity.IsWindow),
                supportEntities,
                entitiesById,
                windowIds,
                graphIndex,
                customIndex);

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

            IdKeySet keySet = new(idsToRemove);

            int removedEntityCount = RemoveEntities(entities, keySet);
            int removedReferenceCount = RemoveMassReferences(mass, idsToRemove, keySet);
            int remainingReferences = CountTargetReferences(mass, keySet);

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

    private static ConnectionIndex BuildGraphConnectionIndex(JObject? neighbours)
    {
        var index = new ConnectionIndex();
        if (neighbours == null)
        {
            return index;
        }

        foreach (JProperty property in neighbours.Properties())
        {
            long? sourceId = ExtractId(property.Name);
            if (!sourceId.HasValue)
            {
                continue;
            }

            if (property.Value["values"] is not JArray values)
            {
                continue;
            }

            foreach (JToken value in values)
            {
                long? targetId = ReadIdObject(value);
                if (targetId.HasValue)
                {
                    index.AddEdge(sourceId.Value, targetId.Value);
                }
            }
        }

        return index;
    }

    private static ConnectionIndex BuildCustomConnectionIndex(JObject? customConnectionData)
    {
        var index = new ConnectionIndex();
        if (customConnectionData == null)
        {
            return index;
        }

        foreach (JProperty property in customConnectionData.Properties())
        {
            long? sourceId = ExtractId(property.Name);
            if (!sourceId.HasValue)
            {
                continue;
            }

            var connectedIds = new HashSet<long>();
            AddIdsFromToken(property.Value, connectedIds);

            foreach (long targetId in connectedIds)
            {
                index.AddEdge(sourceId.Value, targetId);
            }
        }

        return index;
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
        IEnumerable<EntityInfo> windows,
        IReadOnlyList<EntityInfo> nonWindows,
        IReadOnlyDictionary<long, EntityInfo> entitiesById,
        HashSet<long> windowIds,
        ConnectionIndex graphIndex,
        ConnectionIndex customIndex)
    {
        var candidates = new List<WindowCandidate>();

        foreach (EntityInfo window in windows)
        {
            EntityInfo? nearest = FindNearestNonWindow(window, nonWindows, out double nearestDistance3D);
            if (nearest == null || nearestDistance3D < DistanceThreshold3D)
            {
                continue;
            }

            double strength = GetStabilityStrength(window.Entity);
            if (strength > MaxStabilityStrength)
            {
                continue;
            }

            int graphConnections = CountNonWindowConnections(graphIndex, window.Id, entitiesById, windowIds);
            if (graphConnections > 0)
            {
                continue;
            }

            int customConnections = CountNonWindowConnections(customIndex, window.Id, entitiesById, windowIds);
            if (customConnections > 0)
            {
                continue;
            }

            candidates.Add(new WindowCandidate(window, strength, graphConnections, customConnections, nearest, nearestDistance3D));
        }

        return candidates;
    }

    private static int CountNonWindowConnections(
        ConnectionIndex index,
        long windowId,
        IReadOnlyDictionary<long, EntityInfo> entitiesById,
        HashSet<long> windowIds)
    {
        int count = 0;

        foreach (long connectedId in index.GetConnected(windowId))
        {
            if (IsKnownNonWindow(connectedId, entitiesById, windowIds))
            {
                count++;
            }
        }

        return count;
    }

    private static int RemoveEntities(JObject entities, IdKeySet keySet)
    {
        int removed = 0;

        foreach (JProperty property in entities.Properties().ToList())
        {
            if (keySet.Matches(property.Name))
            {
                property.Remove();
                removed++;
            }
        }

        return removed;
    }

    private static int RemoveMassReferences(JObject mass, HashSet<long> idsToRemove, IdKeySet keySet)
    {
        return RemoveReferencesRecursive(mass, idsToRemove, keySet);
    }

    private static int RemoveReferencesRecursive(JToken token, HashSet<long> idsToRemove, IdKeySet keySet)
    {
        int removed = 0;

        if (token is JObject obj)
        {
            foreach (JProperty property in obj.Properties().ToList())
            {
                if (keySet.Matches(property.Name))
                {
                    property.Remove();
                    removed++;
                }
                else
                {
                    removed += RemoveReferencesRecursive(property.Value, idsToRemove, keySet);
                }
            }
        }
        else if (token is JArray array)
        {
            for (int i = array.Count - 1; i >= 0; i--)
            {
                JToken item = array[i];
                if (TokenContainsAnyId(item, idsToRemove, keySet))
                {
                    item.Remove();
                    removed++;
                }
                else
                {
                    removed += RemoveReferencesRecursive(item, idsToRemove, keySet);
                }
            }
        }

        return removed;
    }

    private static int CountTargetReferences(JToken token, IdKeySet keySet)
    {
        int count = 0;

        if (token is JObject obj)
        {
            foreach (JProperty property in obj.Properties())
            {
                if (keySet.Matches(property.Name))
                {
                    count++;
                }

                count += CountTargetReferences(property.Value, keySet);
            }
        }
        else if (token is JArray array)
        {
            foreach (JToken item in array)
            {
                count += CountTargetReferences(item, keySet);
            }
        }
        else if (token is JValue value)
        {
            if (ValueMatchesAnyId(value, keySet))
            {
                count++;
            }
        }

        return count;
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
            if (fragment is JObject fragmentObj)
            {
                double? direct = fragmentObj["Strength"]?.Value<double>();
                if (direct.HasValue)
                {
                    return direct.Value;
                }
            }

            string fragmentText = fragment.ToString();
            if (!fragmentText.StartsWith("/Script/Chimera.CrMassBuildingStabilityData", StringComparison.Ordinal))
            {
                continue;
            }

            Match match = StrengthPattern.Match(fragmentText);
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

            if (distance < DistanceThreshold3D)
            {
                nearestDistance3D = distance;
                return other;
            }

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

    private static bool IsKnownNonWindow(long id, IReadOnlyDictionary<long, EntityInfo> entitiesById, HashSet<long> windowIds)
    {
        return entitiesById.ContainsKey(id) && !windowIds.Contains(id);
    }

    private static bool TokenContainsAnyId(JToken token, HashSet<long> idsToRemove, IdKeySet keySet)
    {
        if (token is JObject obj)
        {
            long? objectId = ReadIdObject(obj);
            if (objectId.HasValue && idsToRemove.Contains(objectId.Value))
            {
                return true;
            }

            foreach (JProperty property in obj.Properties())
            {
                if (keySet.Matches(property.Name) || TokenContainsAnyId(property.Value, idsToRemove, keySet))
                {
                    return true;
                }
            }

            return false;
        }

        if (token is JArray array)
        {
            return array.Any(item => TokenContainsAnyId(item, idsToRemove, keySet));
        }

        return token is JValue value && ValueMatchesAnyId(value, keySet);
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

    private static long? ReadIdObject(JToken token)
    {
        if (token is JObject obj
            && obj["iD"] != null
            && long.TryParse(obj["iD"]!.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
        {
            return id;
        }

        return null;
    }

    private static bool ValueMatchesAnyId(JValue value, IdKeySet keySet)
    {
        if (value.Type == JTokenType.String)
        {
            string? str = Convert.ToString(value.Value, CultureInfo.InvariantCulture);
            return str != null && keySet.Matches(str);
        }

        return false;
    }

    private static string FormatIdKey(long id)
    {
        return $"(ID={id})";
    }

    private static long? ExtractId(string key)
    {
        Match match = IdPattern.Match(key);
        return match.Success ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }
}
