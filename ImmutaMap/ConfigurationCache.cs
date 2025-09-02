namespace ImmutaMap;

using System.Collections.Concurrent;

/// <summary>
/// Thread-safe cache for configurations keyed by (sourceType, targetType)
/// </summary>
internal static class ConfigurationCache
{
    private static readonly ConcurrentDictionary<(Type SourceType, Type TargetType), object> Cache = new();

    public static bool TryGetConfiguration<TSource, TTarget>(out IConfiguration<TSource, TTarget>? configuration)
    {
        if (Cache.TryGetValue((typeof(TSource), typeof(TTarget)), out var value))
        {
            configuration = (IConfiguration<TSource, TTarget>)value;
            return true;
        }
        configuration = null;
        return false;
    }

    public static void Add<TSource, TTarget>(IConfiguration<TSource, TTarget> configuration)
    {
        Cache.TryAdd((typeof(TSource), typeof(TTarget)), configuration);
    }
}
