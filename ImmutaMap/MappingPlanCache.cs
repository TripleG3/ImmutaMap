namespace ImmutaMap;

using System.Collections.Concurrent;

/// <summary>
/// Caches property mapping plans and attribute lookups to reduce reflection cost per mapping invocation.
/// </summary>
internal static class MappingPlanCache
{
	private static readonly ConcurrentDictionary<MappingPlanKey, MappingPlan> PlanCache = new();
	private static readonly ConcurrentDictionary<(PropertyInfo Property, Type AttributeType), Attribute?> AttributeCache = new();

	public static MappingPlan GetOrAddPlan<TSource, TTarget>(IConfiguration<TSource, TTarget> config)
	{
		var key = new MappingPlanKey(typeof(TSource), typeof(TTarget), config.IgnoreCase, config.PropertyNameMaps.Count, config.SkipPropertyNames.Count);
		return PlanCache.GetOrAdd(key, _ => BuildPlan(config));
	}

	private static MappingPlan BuildPlan<TSource, TTarget>(IConfiguration<TSource, TTarget> config)
	{
		var comparer = config.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		var skip = config.SkipPropertyNames;
		var sourceProps = typeof(TSource).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			.Where(p => !skip.Contains(p.Name, comparer)).ToArray();
		var targetProps = typeof(TTarget).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			.Where(p => !skip.Contains(p.Name, comparer)).ToDictionary(p => p.Name, comparer);

		var list = new List<(PropertyInfo Source, PropertyInfo Target)>();
		foreach (var sp in sourceProps)
		{
			if (targetProps.TryGetValue(sp.Name, out var tp))
			{
				list.Add((sp, tp));
			}
		}

		foreach (var (srcName, tgtName) in config.PropertyNameMaps)
		{
			var sourceProp = sourceProps.FirstOrDefault(p => comparer.Equals(p.Name, srcName));
			if (sourceProp == null) continue;
			if (!targetProps.TryGetValue(tgtName, out var targetProp)) continue;
			for (var i = list.Count - 1; i >= 0; i--)
			{
				if (list[i].Source == sourceProp || list[i].Target == targetProp)
					list.RemoveAt(i);
			}
			list.Add((sourceProp, targetProp));
		}
		return new MappingPlan(list.ToArray());
	}

	public static Attribute? GetCachedAttribute(PropertyInfo prop, Type attributeType)
		=> AttributeCache.GetOrAdd((prop, attributeType), static key => key.Property.GetCustomAttribute(key.AttributeType));

	private readonly record struct MappingPlanKey(Type Source, Type Target, bool IgnoreCase, int MapCount, int SkipCount);
	internal readonly record struct MappingPlan((PropertyInfo Source, PropertyInfo Target)[] Pairs);
}
