namespace ImmutaMap;

using System.Collections.Concurrent;

/// <summary>
/// Caches property mapping plans and attribute lookups to reduce reflection cost per mapping invocation.
/// </summary>
internal static class MappingPlanCache
{
	private static readonly ConcurrentDictionary<MappingPlanKey, MappingPlan> PlanCache = new();
	private static readonly ConcurrentDictionary<(PropertyInfo Property, Type AttributeType), Attribute?> AttributeCache = new();
	// Cache for dynamic runtime source type (when generic TSource == object at compile time)
	private static readonly ConcurrentDictionary<RuntimeMappingPlanKey, RuntimeMappingPlan> RuntimePlanCache = new();

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
		var pairs = list.ToArray();
		// Compile getters & setters (strongly typed) to remove reflection during mapping.
		var sourceGetters = new Delegate[pairs.Length];
		var targetSetters = new Delegate[pairs.Length];
		for (int i = 0; i < pairs.Length; i++)
		{
			var (sourceProp, targetProp) = pairs[i];
			// Getter: (TSource s) => (object?)s.Prop
			try
			{
				var srcParam = Expression.Parameter(typeof(TSource), "s");
				Expression access = Expression.Property(srcParam, sourceProp);
				Expression boxed = Expression.Convert(access, typeof(object));
				var getterLambda = Expression.Lambda<Func<TSource, object?>>(boxed, srcParam);
				sourceGetters[i] = getterLambda.Compile();
			}
			catch
			{
				// Fallback: reflection wrapper
				sourceGetters[i] = new Func<TSource, object?>(s => sourceProp.GetValue(s));
			}

			// Setter (only if writable)
			if (targetProp.CanWrite)
			{
				try
				{
					var tgtParam = Expression.Parameter(typeof(TTarget), "t");
					var valParam = Expression.Parameter(typeof(object), "v");
					Expression valueCast = Expression.Convert(valParam, targetProp.PropertyType);
					Expression assign = Expression.Assign(Expression.Property(tgtParam, targetProp), valueCast);
					var setterLambda = Expression.Lambda<Action<TTarget, object?>>(assign, tgtParam, valParam);
					targetSetters[i] = setterLambda.Compile();
				}
				catch
				{
					// Fallback reflection wrapper
					targetSetters[i] = new Action<TTarget, object?>((t, v) => targetProp.SetValue(t, v));
				}
			}
		}

		return new MappingPlan(pairs, sourceGetters, targetSetters);
	}

	public static RuntimeMappingPlan GetOrAddRuntimePlan<TTarget>(Type runtimeSourceType, IConfiguration<object, TTarget> config)
	{
		var key = new RuntimeMappingPlanKey(runtimeSourceType, typeof(TTarget), config.IgnoreCase, config.PropertyNameMaps.Count, config.SkipPropertyNames.Count);
		return RuntimePlanCache.GetOrAdd(key, _ => BuildRuntimePlan(runtimeSourceType, config));
	}

	private static RuntimeMappingPlan BuildRuntimePlan<TTarget>(Type runtimeSourceType, IConfiguration<object, TTarget> config)
	{
		var comparer = config.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		var skip = config.SkipPropertyNames;
		var sourceProps = runtimeSourceType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
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
		var pairs = list.ToArray();
		var sourceGetters = new Func<object, object?>[pairs.Length];
		var targetSetters = new Action<object, object?>[pairs.Length];
		for (int i = 0; i < pairs.Length; i++)
		{
			var (sourceProp, targetProp) = pairs[i];
			try
			{
				// (object s) => (object?) ((TRuntime)s).Prop
				var objParam = Expression.Parameter(typeof(object), "s");
				var cast = Expression.Convert(objParam, runtimeSourceType);
				Expression access = Expression.Property(cast, sourceProp);
				var boxed = Expression.Convert(access, typeof(object));
				var lambda = Expression.Lambda<Func<object, object?>>(boxed, objParam);
				sourceGetters[i] = lambda.Compile();
			}
			catch
			{
				sourceGetters[i] = s => sourceProp.GetValue(s);
			}

			if (targetProp.CanWrite)
			{
				try
				{
					var tgtParam = Expression.Parameter(typeof(object), "t");
					var valParam = Expression.Parameter(typeof(object), "v");
					var tgtCast = Expression.Convert(tgtParam, typeof(TTarget));
					var valCast = Expression.Convert(valParam, targetProp.PropertyType);
					var assign = Expression.Assign(Expression.Property(tgtCast, targetProp), valCast);
					var lambda = Expression.Lambda<Action<object, object?>>(assign, tgtParam, valParam);
					targetSetters[i] = lambda.Compile();
				}
				catch
				{
					targetSetters[i] = (t, v) => targetProp.SetValue(t, v);
				}
			}
			else
			{
				// leave null; dynamic builder will fallback to backing field logic
				targetSetters[i] = null!;
			}
		}
		return new RuntimeMappingPlan(pairs, sourceGetters, targetSetters);
	}

	public static Attribute? GetCachedAttribute(PropertyInfo prop, Type attributeType)
		=> AttributeCache.GetOrAdd((prop, attributeType), static key => key.Property.GetCustomAttribute(key.AttributeType));

	private readonly record struct MappingPlanKey(Type Source, Type Target, bool IgnoreCase, int MapCount, int SkipCount);
	internal readonly record struct MappingPlan((PropertyInfo Source, PropertyInfo Target)[] Pairs, Delegate[] SourceGetters, Delegate[] TargetSetters);

	private readonly record struct RuntimeMappingPlanKey(Type RuntimeSource, Type Target, bool IgnoreCase, int MapCount, int SkipCount);
	internal readonly record struct RuntimeMappingPlan((PropertyInfo Source, PropertyInfo Target)[] Pairs, Func<object, object?>[] SourceGetters, Action<object, object?>[] TargetSetters);
}
