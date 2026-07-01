using Actio.Engine.Actions;
using Actio.Engine.Caching;

namespace Actio.Web.Models;

public sealed record CacheResult(
    string CacheRoot,
    IReadOnlyList<ActionCacheEntry> Entries,
    string DependencyCacheRoot,
    IReadOnlyList<DependencyCacheEntry> DependencyEntries);
