namespace ImmutaMap;

using System.Collections.Concurrent;

/// <summary>
/// Caches property mapping plans and attribute lookups to reduce reflection cost per mapping invocation.
/// </summary>
internal static class MappingPlanCache
{
	private static readonly ConcurrentDictionary<MappingPlanKey, MappingPlan> PlanCache = new();
	private static readonly ConcurrentDictionary<RuntimePlanKey, MappingPlan> RuntimePlanCache = new();
	private static readonly ConcurrentDictionary<(PropertyInfo Property, Type AttributeType), Attribute?> AttributeCache = new();

	internal readonly record struct MappingPair(PropertyInfo Source, PropertyInfo Target, Func<object, object?> Getter, Action<object, object?> Setter);
	internal readonly record struct MappingPlan(MappingPair[] Pairs, Func<object, object?>? ConstructorFactory = null);

	public static MappingPlan GetOrAddPlan<TSource, TTarget>(IConfiguration<TSource, TTarget> config)
	{
		var key = new MappingPlanKey(typeof(TSource), typeof(TTarget), config.IgnoreCase, config.PropertyNameMaps.Count, config.SkipPropertyNames.Count);
		return PlanCache.GetOrAdd(key, _ => BuildPlan(typeof(TSource), typeof(TTarget), config));
	}

	public static MappingPlan GetOrAddRuntimePlan<TTarget>(Type runtimeSourceType, IConfiguration<object, TTarget> config)
	{
		var key = new RuntimePlanKey(runtimeSourceType, typeof(TTarget), config.IgnoreCase, config.PropertyNameMaps.Count, config.SkipPropertyNames.Count);
		return RuntimePlanCache.GetOrAdd(key, _ => BuildPlan(runtimeSourceType, typeof(TTarget), config));
	}

	private static MappingPlan BuildPlan(Type sourceType, Type targetType, dynamic config)
	{
		var comparer = config.IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
		var skip = (HashSet<string>)config.SkipPropertyNames;
		var sourceProps = sourceType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			.Where(p => !skip.Contains(p.Name)).ToArray();
		var targetProps = targetType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
			.Where(p => !skip.Contains(p.Name)).ToDictionary(p => p.Name, comparer);

		var list = new List<(PropertyInfo Source, PropertyInfo Target)>();
		foreach (var sp in sourceProps)
		{
			if (targetProps.TryGetValue(sp.Name, out var tp)) list.Add((sp, tp));
		}

		foreach (var mapEntry in (IEnumerable<(string SourcePropertyName,string TargetPropertyName)>)config.PropertyNameMaps)
		{
			var srcName = mapEntry.SourcePropertyName;
			var tgtName = mapEntry.TargetPropertyName;
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

		var pairs = new MappingPair[list.Count];
		for (int i = 0; i < list.Count; i++)
		{
			var (s, t) = list[i];
			var getter = CreateGetter(s);
			var setter = CreateSetter(t);
			pairs[i] = new MappingPair(s, t, getter, setter);
		}

		// Attempt to build a constructor factory fast path (no transformers, immutable target scenario).
		Func<object, object?>? ctorFactory = TryBuildConstructorFactory(sourceType, targetType, pairs, comparer);
		return new MappingPlan(pairs, ctorFactory);
	}

	/// <summary>
	/// Attempts to build a compiled constructor factory that directly constructs the target using a matching constructor.
	/// Conditions:
	/// * All constructor parameters have a corresponding target property.
	/// * Each target property in the plan has a matching constructor parameter by (case) name.
	/// * The number of constructor parameters equals the number of matched target properties (no partials needed).
	/// * Parameter types are assignable from the source->target property types.
	/// If multiple constructors qualify, the shortest parameter list that still covers all properties is chosen (prefer canonical record/primary constructor patterns).
	/// </summary>
	private static Func<object, object?>? TryBuildConstructorFactory(Type sourceType, Type targetType, MappingPair[] pairs, IEqualityComparer<string> nameComparer)
	{
		var ctors = targetType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		if (ctors.Length == 0) return null;

		// Build lookup of target property by name.
		var targetPropsByName = pairs.Select(p => p.Target).Distinct().ToDictionary(p => p.Name, p => p, nameComparer);
		Func<object, object?>? best = null;
		int bestParamCount = int.MaxValue;

		foreach (var ctor in ctors)
		{
			var parameters = ctor.GetParameters();
			if (parameters.Length == 0) continue; // parameterless already handled by existing instantiation path; not beneficial

			// Quick eliminate if more params than available distinct mapped properties.
			if (parameters.Length > targetPropsByName.Count) continue;

			var orderedPairs = new MappingPair[parameters.Length];
			var seenTargets = new HashSet<PropertyInfo>();
			bool invalid = false;
			for (int i = 0; i < parameters.Length; i++)
			{
				var param = parameters[i];
				if (!targetPropsByName.TryGetValue(param.Name!, out var targetProp)) { invalid = true; break; }
				// Find mapping pair whose Target == targetProp
				var mp = pairs.FirstOrDefault(pp => pp.Target == targetProp);
				if (mp.Target == null) { invalid = true; break; }
				// Type compatibility check
				if (!param.ParameterType.IsAssignableFrom(targetProp.PropertyType)) { invalid = true; break; }
				if (!seenTargets.Add(targetProp)) { invalid = true; break; }
				orderedPairs[i] = mp;
			}
			if (invalid) continue;
			// Require full coverage of constructor params; we don't require that all properties appear in ctor (records / primary ctors usually do). That's enough.
			// Prefer minimal param count that succeeds (gives most specific / primary constructor typically).
			if (parameters.Length < bestParamCount)
			{
				try
				{
					// Build expression: (object src) => (object)new TargetType( (TParam0)orderedPairs[0].Getter(src), ... )
					var srcParam = Expression.Parameter(typeof(object), "src");
					var args = new Expression[parameters.Length];
					for (int i = 0; i < parameters.Length; i++)
					{
						var getterConst = Expression.Constant(orderedPairs[i].Getter);
						var invoke = Expression.Invoke(getterConst, srcParam); // returns object
						var converted = Expression.Convert(invoke, parameters[i].ParameterType);
						args[i] = converted;
					}
					var newExpr = Expression.New(ctor, args);
					var box = Expression.Convert(newExpr, typeof(object));
					var lambda = Expression.Lambda<Func<object, object?>>(box, srcParam);
					best = lambda.Compile();
					bestParamCount = parameters.Length;
				}
				catch
				{
					// Ignore and keep searching
				}
			}
		}
		return best;
	}

	private static Func<object, object?> CreateGetter(PropertyInfo prop)
	{
		var objParam = Expression.Parameter(typeof(object), "o");
		var cast = Expression.Convert(objParam, prop.DeclaringType!);
		var access = Expression.Property(cast, prop);
		var box = Expression.Convert(access, typeof(object));
		return Expression.Lambda<Func<object, object?>>(box, objParam).Compile();
	}

	private static Action<object, object?> CreateSetter(PropertyInfo prop)
	{
		// Exclude indexers and init-only/private setters
		var setMethod = prop.SetMethod;
		var isIndexer = prop.GetIndexParameters().Length > 0;
		var isWritableProperty = setMethod != null && setMethod.IsPublic && !setMethod.IsStatic && !isIndexer && !IsInitOnly(prop);
		if (isWritableProperty)
		{
			try
			{
				var objParam = Expression.Parameter(typeof(object), "o");
				var valueParam = Expression.Parameter(typeof(object), "v");
				var castObj = Expression.Convert(objParam, prop.DeclaringType!);
				var castValue = Expression.Convert(valueParam, prop.PropertyType);
				var propAccess = Expression.Property(castObj, prop);
				var assign = Expression.Assign(propAccess, castValue);
				return Expression.Lambda<Action<object, object?>>(assign, objParam, valueParam).Compile();
			}
			catch
			{
				// Fallback to backing field path below
			}
		}
		// Backing field fallback for init-only / private / read-only properties => compile direct field assign if possible
		var backingField = prop.DeclaringType!
			.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
			.FirstOrDefault(f => f.Name == $"<{prop.Name}>k__BackingField");
		if (backingField != null)
		{
			try
			{
				var objParam = Expression.Parameter(typeof(object), "o");
				var valueParam = Expression.Parameter(typeof(object), "v");
				var castObj = Expression.Convert(objParam, prop.DeclaringType!);
				var castValue = Expression.Convert(valueParam, prop.PropertyType);
				var fieldExpr = Expression.Field(castObj, backingField);
				var assign = Expression.Assign(fieldExpr, castValue);
				return Expression.Lambda<Action<object, object?>>(assign, objParam, valueParam).Compile();
			}
			catch
			{
				return (o, v) => { try { backingField.SetValue(o, v); } catch { } };
			}
		}
		// No-op when truly immutable
		return (_, _) => { };
	}

	private static bool IsInitOnly(PropertyInfo prop)
	{
		var setMethod = prop.SetMethod;
		if (setMethod == null) return false;
		static bool HasInitModifier(ParameterInfo p) => p.GetRequiredCustomModifiers().Any(m => m.FullName == "System.Runtime.CompilerServices.IsExternalInit");
		return HasInitModifier(setMethod.ReturnParameter) || setMethod.GetParameters().Any(HasInitModifier);
	}

	public static Attribute? GetCachedAttribute(PropertyInfo prop, Type attributeType)
		=> AttributeCache.GetOrAdd((prop, attributeType), static key => key.Property.GetCustomAttribute(key.AttributeType));

	private readonly record struct MappingPlanKey(Type Source, Type Target, bool IgnoreCase, int MapCount, int SkipCount);
	private readonly record struct RuntimePlanKey(Type RuntimeSource, Type Target, bool IgnoreCase, int MapCount, int SkipCount);
}
