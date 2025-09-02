namespace ImmutaMap.Builders;

public class TargetBuilder
{
    private const BindingFlags PropertyBindingFlag = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly ITypeFormatter typeFormatter;
    // Removed persistent transformedValues dictionary to avoid per-map overhead when not needed.

    /// <summary>
    /// Initializes the Mapper with an ITypeFormatter.
    /// </summary>
    /// <param name="typeFormatter">The ITypeFormatter is used to instantiate all types during the Build method.</param>
    TargetBuilder(ITypeFormatter typeFormatter)
    {
        this.typeFormatter = typeFormatter;
    }

    /// <summary>
    /// A simpler instantiation that allows for quick fluent designing.
    /// </summary>
    /// <returns>A new Mapper used to map and instantiate the maps target.</returns>
    public static TargetBuilder GetNewInstance() => new(ITypeFormatter.Default);

    /// <summary>
    /// A simpler instantiation that allows for quick fluent designing.
    /// </summary>
    /// <param name="typeFormatter">The ITypeFormatter is used to instantiate all types during the Build method.</param>
    /// <returns>A new Mapper used to map and instantiate the maps target.</returns>
    public static TargetBuilder GetNewInstance(ITypeFormatter typeFormatter) => new(typeFormatter);

    /// <summary>
    /// Builds the target value from the source value using the default mappings and any custom mappings put in place.
    /// </summary>
    /// <typeparam name="TSource">The source type mapped from.</typeparam>
    /// <typeparam name="TTarget">The target type mapped to.</typeparam>
    /// <param name="mapper">The Map used to build.</param>
    /// <returns>An instance of the target type with values mapped from the source instance.</returns>
    public TTarget? Build<TSource, TTarget>(IConfiguration<TSource, TTarget> configuration, TSource source)
    {
        if (source == null)
        {
            return default;
        }
        // Defer instantiation until we know whether a constructor fast path exists
        var plan = MappingPlanCache.GetOrAddPlan(configuration);
        var transformers = (configuration as ITransform)?.Transformers;
        var hasTransformers = transformers != null && transformers.Count > 0;
        if (!hasTransformers && plan.ConstructorFactory != null)
        {
            // Use compiled constructor directly
            return (TTarget?)plan.ConstructorFactory(source!);
        }
        var target = typeFormatter.GetInstance<TTarget>();
        CopyInternal(configuration, source, target, plan, hasTransformers, transformers);
        return target;
    }

    /// <summary>
    /// Builds the target value from the source value using the default mappings and any custom mappings put in place.
    /// </summary>
    /// <typeparam name="TSource">The source type mapped from.</typeparam>
    /// <typeparam name="TTarget">The target type mapped to.</typeparam>
    /// <param name="configuration">The Map used to build.</param>
    /// <param name="source">The source used during the mapping.</param>
    /// <param name="args">Optional parameters that may be used to instantiate the target.</param>
    /// <returns>An instance of the target type with values mapped from the source instance.</returns>
    public TTarget Build<TSource, TTarget>(IConfiguration<TSource, TTarget> configuration, TSource source, Func<object[]> args)
    {
        var target = typeFormatter.GetInstance<TTarget>(args);
        Copy(configuration, source, target);
        return target;
    }

    public void ReverseCopy<TSource, TTarget>(IConfiguration<TSource, TTarget> configuration, TSource source, TTarget target)
    {
        Copy(configuration, source, target);
    }

    private void Copy<TSource, TTarget>(IConfiguration<TSource, TTarget> configuration, TSource source, TTarget target)
    {
        // legacy entry still used by ReverseCopy or explicit builds with args
        var plan = MappingPlanCache.GetOrAddPlan(configuration);
        var transformers = (configuration as ITransform)?.Transformers;
        var hasTransformers = transformers != null && transformers.Count > 0;
        CopyInternal(configuration, source, target, plan, hasTransformers, transformers);
    }

    private void CopyInternal<TSource, TTarget>(IConfiguration<TSource, TTarget> configuration, TSource source, TTarget target, MappingPlanCache.MappingPlan plan, bool hasTransformers, ICollection<ITransformer>? transformers)
    {
        var dynamicObjectSource = typeof(TSource) == typeof(object);
        if (dynamicObjectSource) plan = default; // dynamic path rebuilds every call


        if (!dynamicObjectSource)
        {
            // Fast path: no transformers => straight compiled getter->setter copy without dictionary overhead.
            if (!hasTransformers)
            {
                // If a constructor factory was used earlier we would have returned; here we just copy into existing instance.
                foreach (var pair in plan.Pairs)
                {
                    var value = pair.Getter(source!);
                    // Value type safety & null handling identical to previous fallback logic.
                    pair.Setter(target!, value);
                }
                return;
            }

            // Only allocate dictionary if transformers are present (captures previous transformedValues behavior).
            var transformedValues = new Dictionary<PropertyInfo, object?>(plan.Pairs.Length);
            foreach (var pair in plan.Pairs)
            {
                var sourcePropertyInfo = pair.Source;
                var targetPropertyInfo = pair.Target;
                object? finalValue = null;
                var hasValue = false;
                if (hasTransformers)
                {
            foreach (var transformer in transformers!)
                    {
                        if (transformedValues.TryGetValue(sourcePropertyInfo, out var prev))
                        {
                if (prev != null && transformer.TryGetValue(source, sourcePropertyInfo, targetPropertyInfo, prev, out var transformedValue))
                            {
                                finalValue = transformedValue;
                                transformedValues[sourcePropertyInfo] = transformedValue;
                                hasValue = true;
                                break;
                            }
                        }
                        else if (transformer.TryGetValue(source, sourcePropertyInfo, targetPropertyInfo, out var transformedValue))
                        {
                            finalValue = transformedValue;
                            transformedValues[sourcePropertyInfo] = transformedValue;
                            hasValue = true;
                            break;
                        }
                    }
                }
                if (!hasValue)
                {
                    if (transformedValues.TryGetValue(sourcePropertyInfo, out var prev))
                    {
                        finalValue = prev;
                    }
                    else if (typeof(TSource) != typeof(TTarget) && sourcePropertyInfo.PropertyType == typeof(TSource) && targetPropertyInfo.PropertyType == typeof(TTarget))
                    {
                        finalValue = GetNewInstance().Build(configuration, source);
                    }
                    else
                    {
                        finalValue = pair.Getter(source!);
                    }
                }
                if (finalValue == null)
                {
                    var fallback = pair.Getter(source!);
                    if (fallback != null) finalValue = fallback;
                }
                // Use compiled setter; fallback SetTargetValue logic for type safety.
                if (finalValue != null && !targetPropertyInfo.PropertyType.IsAssignableFrom(finalValue.GetType()))
                {
                    SetTargetValue(target, targetPropertyInfo, finalValue, configuration);
                }
                else
                {
                    pair.Setter(target!, finalValue);
                }
                transformedValues[targetPropertyInfo] = finalValue!;
            }
            return;
        }

        // Dynamic object source path (TSource == object)
        if (source == null) return;
        var skipSet = configuration.SkipPropertyNames;
        var sourceProps = source.GetType().GetProperties(PropertyBindingFlag).Where(p => !skipSet.Contains(p.Name)).ToList();
        var targetProps = typeof(TTarget).GetProperties(PropertyBindingFlag).Where(p => !skipSet.Contains(p.Name)).ToList();
        var joinedPropertyInfos = GetSourceResultProperties(sourceProps, targetProps, configuration);
        AddPropertyNameMaps(configuration, sourceProps, targetProps, joinedPropertyInfos);

        Dictionary<PropertyInfo, object?>? transformedValuesDyn = hasTransformers ? new() : null;

        foreach (var (sourcePropertyInfo, targetPropertyInfo) in joinedPropertyInfos)
        {
            var isTransformed = false;
            var transform = configuration as ITransform;
            if (hasTransformers)
            {
                foreach (var transformer in transform!.Transformers)
                {
            if (transformedValuesDyn!.TryGetValue(sourcePropertyInfo, out var prevVal) && prevVal != null)
                    {
                        if (transformer.TryGetValue(source, sourcePropertyInfo, targetPropertyInfo, prevVal, out var transformedValue))
                        {
                transformedValuesDyn[sourcePropertyInfo] = transformedValue;
                            SetTargetValue(target, targetPropertyInfo, transformedValue, configuration);
                            isTransformed = true;
                            break;
                        }
                    }
                    else if (transformer.TryGetValue(source, sourcePropertyInfo, targetPropertyInfo, out var transformedValue))
                    {
            transformedValuesDyn[sourcePropertyInfo] = transformedValue;
                        SetTargetValue(target, targetPropertyInfo, transformedValue, configuration);
                        isTransformed = true;
                        break;
                    }
                }
            }
            if (!isTransformed)
            {
        if (hasTransformers && transformedValuesDyn!.TryGetValue(sourcePropertyInfo, out var prev))
                {
                    SetTargetValue(target, targetPropertyInfo, prev, configuration);
                }
                else
                {
                    var targetValue = sourcePropertyInfo.GetValue(source);
                    SetTargetValue(target, targetPropertyInfo, targetValue, configuration);
                }
            }
        }
    }

    // Legacy plan builder removed (dynamic path handled inline)

    private void SetTargetValue<TSource, TTarget>(TTarget target, PropertyInfo targetPropertyInfo, object? targetValue, IConfiguration<TSource, TTarget> configuration)
    {
        if (targetValue != null && !targetPropertyInfo.PropertyType.IsAssignableFrom(targetValue.GetType()))
        {
            if (configuration.WillNotThrowExceptions)
                return;
            else
                throw new BuildException(targetValue.GetType(), targetPropertyInfo);
        }

        if (targetPropertyInfo.CanWrite)
        {
            targetPropertyInfo.SetValue(target, targetValue);
        }
        else
        {
            var fields = typeof(TTarget).GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            var backingField = fields.FirstOrDefault(x => x.Name == $"<{targetPropertyInfo.Name}>k__BackingField");

            backingField?.SetValue(target, targetValue);
        }

    // No persistent dictionary anymore; per-call transformed tracking only when transformers present.
    }

    private static void AddPropertyNameMaps<TSource, TResult>(IConfiguration<TSource, TResult> configuration, List<PropertyInfo> sourceProperties, List<PropertyInfo> resultProperties, List<(PropertyInfo sourcePropertyInfo, PropertyInfo resultPropertyInfo)> joinedPropertyInfos)
    {
        foreach (var (sourcePropertyMapName, resultPropertyMapName) in configuration.PropertyNameMaps)
        {
            var sourcePropertyInfo = sourceProperties.FirstOrDefault(x => configuration.IgnoreCase ? x.Name.ToLowerInvariant() == sourcePropertyMapName.ToLowerInvariant() : x.Name == sourcePropertyMapName);
            if (sourcePropertyInfo == null) continue;
            var resultPropertyInfo = resultProperties.FirstOrDefault(x => configuration.IgnoreCase ? x.Name.ToLowerInvariant() == resultPropertyMapName.ToLowerInvariant() : x.Name == resultPropertyMapName);
            if (resultPropertyInfo == null) continue;
            if (joinedPropertyInfos.Any(x =>
                configuration.IgnoreCase
                ? x.sourcePropertyInfo.Name.ToLowerInvariant() == sourcePropertyMapName.ToLowerInvariant() && x.resultPropertyInfo.Name.ToLowerInvariant() == resultPropertyMapName.ToLowerInvariant()
                : x.sourcePropertyInfo.Name == sourcePropertyMapName && x.resultPropertyInfo.Name == resultPropertyMapName))

            {
                continue;
            }
            var existingJoinedPropertyInfo = joinedPropertyInfos
                .FirstOrDefault(x => x.sourcePropertyInfo.Name == sourcePropertyInfo.Name || x.resultPropertyInfo.Name == resultPropertyInfo.Name);
            if (existingJoinedPropertyInfo != default)
            {
                joinedPropertyInfos.Remove(existingJoinedPropertyInfo);
            }
            joinedPropertyInfos.Add((sourcePropertyInfo, resultPropertyInfo));
        }
    }

    private static List<(PropertyInfo sourceProperty, PropertyInfo resultProperty)>
        GetSourceResultProperties<TSource, TTarget>(List<PropertyInfo> sourceProperties,
                                                    List<PropertyInfo> targetProperties,
                                                    IConfiguration<TSource, TTarget> configuration)
    {
        return sourceProperties.Join(targetProperties,
            sourceProperty => configuration.IgnoreCase ? sourceProperty.Name.ToLowerInvariant() : sourceProperty.Name,
            resultProperty => configuration.IgnoreCase ? resultProperty.Name.ToLowerInvariant() : resultProperty.Name,
            (sourceProperty, resultProperty) => (sourceProperty, resultProperty))
            .ToList();
    }
}