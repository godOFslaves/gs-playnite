using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Playnite.SDK;
using Playnite.SDK.Plugins;
using GsPlugin.Infrastructure;
using Sentry;

namespace GsPlugin.Services {
    /// <summary>
    /// Retrieves per-game achievement data from the SuccessStory plugin via reflection.
    /// All methods return null if SuccessStory is not installed or an error occurs.
    /// </summary>
    public class GsSuccessStoryHelper : IAchievementProvider {
        private static readonly Guid SuccessStoryId = new Guid(
            "cebe6d32-8c46-4459-b993-5a5189d60788"
        );

        private readonly IPlayniteAPI _api;
        private Plugin _cachedPlugin;
        private bool _pluginSearched;

        // Cached reflection members — resolved once per plugin lifetime, not per game.
        private PropertyInfo _dbPropInfo;
        private MethodInfo _getMethodInfo;
        private object _cachedDb;
        private bool _reflectionResolved;

        public GsSuccessStoryHelper(IPlayniteAPI api) {
            _api = api;
        }

        public string ProviderName => "SuccessStory";

        public (int unlocked, int total)? GetCounts(Guid gameId) => GetAchievementCounts(gameId);

        public int? GetUnlockedCount(Guid gameId) => GetAchievementCounts(gameId)?.unlocked;

        public int? GetTotalCount(Guid gameId) => GetAchievementCounts(gameId)?.total;

        public bool IsInstalled => GetSuccessStoryPlugin() != null;

        /// <summary>
        /// Returns per-achievement details for a game, or null if SuccessStory is absent or the game has no data.
        /// </summary>
        public List<AchievementItem> GetAchievements(Guid gameId) {
            try {
                var db = ResolveDatabase();
                if (db == null || _getMethodInfo == null) {
                    return null;
                }

                var ga = _getMethodInfo.Invoke(db, new object[] { gameId, true, false });
                if (ga == null) {
                    return null;
                }

                var gaType = ga.GetType();
                var items = gaType.GetProperty("Items")?.GetValue(ga) as IEnumerable;
                if (items == null) {
                    return null;
                }

                var result = new List<AchievementItem>();
                foreach (var item in items) {
                    var itemType = item.GetType();
                    var name = itemType.GetProperty("Name")?.GetValue(item) as string;
                    var description = itemType.GetProperty("Description")?.GetValue(item) as string;
                    var dateUnlocked = itemType.GetProperty("DateUnlocked")?.GetValue(item) as DateTime?;
                    var percent = itemType.GetProperty("Percent")?.GetValue(item);

                    // SuccessStory stores DateUnlocked as DateTime.MinValue (or default) for locked achievements
                    bool isUnlocked = dateUnlocked.HasValue
                        && dateUnlocked.Value > DateTime.MinValue
                        && dateUnlocked.Value.Year > 1;

                    result.Add(new AchievementItem {
                        Name = name,
                        Description = description,
                        DateUnlocked = isUnlocked ? dateUnlocked : null,
                        IsUnlocked = isUnlocked,
                        RarityPercent = percent != null ? Convert.ToSingle(percent) : (float?)null
                    });
                }

                return result.Count > 0 ? result : null;
            }
            catch (TargetInvocationException ex) {
                GsSentry.AddBreadcrumb(
                    message: $"[GsSuccessStoryHelper] TargetInvocationException in GetAchievements for game {gameId}: {ex.InnerException?.Message ?? ex.Message}",
                    category: "achievement",
                    level: BreadcrumbLevel.Warning);
                GsLogger.Warn($"[GsSuccessStoryHelper] Achievement details lookup failed for game {gameId}: {ex.InnerException?.Message ?? ex.Message}");
                return null;
            }
            catch (InvalidOperationException ex) {
                // Catches Newtonsoft.Json JValue access errors from Playnite SDK internals
                GsLogger.Warn($"[GsSuccessStoryHelper] JValue/InvalidOp in GetAchievements for game {gameId}: {ex.Message}");
                return null;
            }
            catch (Exception ex) {
                GsLogger.Warn(
                    $"[GsSuccessStoryHelper] Achievement details lookup failed for game {gameId}: {ex.Message}"
                );
                return null;
            }
        }

        public string GetVersion() {
            try {
                var plugin = GetSuccessStoryPlugin();
                if (plugin == null)
                    return null;
                return plugin.GetType().Assembly.GetName().Version?.ToString(3);
            }
            catch (Exception ex) {
                GsLogger.Warn(
                    $"[GsSuccessStoryHelper] Version lookup failed: {ex.Message}"
                );
                return null;
            }
        }

        private (int unlocked, int total)? GetAchievementCounts(Guid gameId) {
            try {
                var db = ResolveDatabase();
                if (db == null || _getMethodInfo == null) {
                    return null;
                }

                var ga = _getMethodInfo.Invoke(db, new object[] { gameId, true, false });
                if (ga == null) {
                    return null;
                }

                var gaType = ga.GetType();
                var unlocked = (int?)gaType.GetProperty("Unlocked")?.GetValue(ga) ?? 0;
                var items = gaType.GetProperty("Items")?.GetValue(ga) as ICollection;
                var total = items?.Count ?? 0;
                return total > 0 ? (unlocked, total) : ((int, int)?)null;
            }
            catch (TargetInvocationException ex) {
                // Reflection call succeeded but the method threw — likely an API change in SuccessStory.
                GsSentry.AddBreadcrumb(
                    message: $"[GsSuccessStoryHelper] TargetInvocationException in GetAchievementCounts for game {gameId}: {ex.InnerException?.Message ?? ex.Message}",
                    category: "achievement",
                    level: BreadcrumbLevel.Warning);
                GsLogger.Warn($"[GsSuccessStoryHelper] Achievement lookup failed for game {gameId}: {ex.InnerException?.Message ?? ex.Message}");
                return null;
            }
            catch (InvalidOperationException ex) {
                GsLogger.Warn($"[GsSuccessStoryHelper] JValue/InvalidOp in GetCounts for game {gameId}: {ex.Message}");
                return null;
            }
            catch (Exception ex) {
                GsLogger.Warn(
                    $"[GsSuccessStoryHelper] Achievement lookup failed for game {gameId}: {ex.Message}"
                );
                return null;
            }
        }

        private Plugin GetSuccessStoryPlugin() {
            if (_pluginSearched) {
                return _cachedPlugin;
            }

            _pluginSearched = true;
            _cachedPlugin = _api.Addons.Plugins.FirstOrDefault(p => p.Id == SuccessStoryId);
            return _cachedPlugin;
        }

        /// <summary>
        /// Resolves and caches PluginDatabase property and Get method via reflection.
        /// Called once; subsequent calls return the cached members.
        /// Returns the database object, or null if resolution fails.
        /// </summary>
        private object ResolveDatabase() {
            if (_reflectionResolved) return _cachedDb;
            _reflectionResolved = true;

            var plugin = GetSuccessStoryPlugin();
            if (plugin == null) return null;

            _dbPropInfo = plugin.GetType()
                .GetProperty("PluginDatabase", BindingFlags.Public | BindingFlags.Instance);
            _cachedDb = _dbPropInfo?.GetValue(plugin);
            if (_cachedDb == null) return null;

            _getMethodInfo = _cachedDb.GetType()
                .GetMethod("Get", BindingFlags.Public | BindingFlags.Instance, null,
                    new[] { typeof(Guid), typeof(bool), typeof(bool) }, null);

            return _cachedDb;
        }
    }
}
