using Actio.Engine.Actions;

namespace Actio.Web.Models;

public sealed record CacheResult(
    string CacheRoot,
    IReadOnlyList<ActionCacheEntry> Entries);
