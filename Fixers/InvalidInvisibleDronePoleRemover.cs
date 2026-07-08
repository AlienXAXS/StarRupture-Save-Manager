using System.Globalization;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using StarRuptureSaveFixer.Models;
using StarRuptureSaveFixer.Utils;

namespace StarRuptureSaveFixer.Fixers;

/// <summary>
/// Removes invisible drone pole entities that are not referenced as a
/// SocketPairInvisibleConnector by any drone lane entity.
/// </summary>
public class InvalidInvisibleDronePoleRemover : IFixer
{
    public string Name => "Invalid InvisibleDronePole Remover";

    private static readonly Regex IdPattern = new(@"\(ID=(\d+)\)", RegexOptions.Compiled);
    private static readonly Regex SocketPairPattern = new(@"SocketPairInvisibleConnector=\(ID=(\d+)\)", RegexOptions.Compiled);

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

            // ── Step 1: Find all invisible drone pole IDs ──
            var poleKeysById = new Dictionary<long, string>();

            foreach (JProperty prop in entities.Properties())
            {
                long? id = ExtractId(prop.Name);
                if (id == null)
                    continue;

                string configPath = GetConfigPath(prop.Value);
                if (configPath.Contains("DroneConnections", StringComparison.Ordinal)
                    && configPath.Contains("DroneInvisiblePole", StringComparison.Ordinal))
                {
                    poleKeysById[id.Value] = prop.Name;
                }
            }

            ConsoleLogger.Info($"Invisible drone poles found: {poleKeysById.Count}");

            if (poleKeysById.Count == 0)
            {
                ConsoleLogger.Info("No invisible drone poles found. Nothing to fix.");
                return false;
            }

            // ── Step 2: Scan drone lane entities for referenced pole IDs ──
            var referencedPoleIds = new HashSet<long>();
            int droneLaneCount = 0;

            foreach (JProperty prop in entities.Properties())
            {
                string configPath = GetConfigPath(prop.Value);
                if (!configPath.Contains("DroneConnections", StringComparison.Ordinal)
                    || !configPath.Contains("DroneLane", StringComparison.Ordinal))
                    continue;

                droneLaneCount++;

                if (prop.Value["fragmentValues"] is not JArray fragments)
                    continue;

                foreach (JToken fragment in fragments)
                {
                    string? fragmentText = fragment.Value<string>();
                    if (fragmentText == null)
                        continue;

                    foreach (Match match in SocketPairPattern.Matches(fragmentText))
                    {
                        if (long.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long referencedId))
                        {
                            referencedPoleIds.Add(referencedId);
                        }
                    }
                }
            }

            ConsoleLogger.Info($"Drone lane entities scanned: {droneLaneCount}");
            ConsoleLogger.Info($"Referenced invisible pole IDs found: {referencedPoleIds.Count}");

            // ── Step 3: Determine which poles are unreferenced (invalid) ──
            List<KeyValuePair<long, string>> polesToRemove = poleKeysById
                .Where(kvp => !referencedPoleIds.Contains(kvp.Key))
                .ToList();

            ConsoleLogger.Info($"Invalid (unreferenced) invisible poles: {polesToRemove.Count}");
            ConsoleLogger.Info("Applying the fix, this can take multiple minutes if the save file is large!");

            if (polesToRemove.Count == 0)
            {
                ConsoleLogger.Info("No invalid invisible drone poles found. Save file is clean.");
                return false;
            }

            for (int i = 0; i < polesToRemove.Count; i++)
            {
                entities.Remove(polesToRemove[i].Value);
                ConsoleLogger.ReportProgress(i + 1, polesToRemove.Count);
            }

            saveFile.JsonContent = root.ToString(Newtonsoft.Json.Formatting.None);
            ConsoleLogger.Success($"Successfully removed {polesToRemove.Count} invalid invisible drone pole(s).");
            return true;
        }
        catch (Newtonsoft.Json.JsonException ex)
        {
            ConsoleLogger.Error($"JSON parsing error: {ex.Message}");
            return false;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error($"Error during invalid invisible drone pole removal: {ex.Message}");
            ConsoleLogger.Error($"Stack trace: {ex.StackTrace}");
            return false;
        }
    }

    private static string GetConfigPath(JToken entity)
    {
        return entity["spawnData"]?["entityConfigDataPath"]?.ToString() ?? "";
    }

    private static long? ExtractId(string key)
    {
        Match match = IdPattern.Match(key);
        return match.Success ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }
}
