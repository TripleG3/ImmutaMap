namespace ImmutaMap.Builders;

public class AsyncTargetBuilder
{
    private const BindingFlags PropertyBindingFlag = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly ITypeFormatter typeFormatter;
    // Removed always-on transformedValues; allocate only when async transformers present.

    /// <summary>
    /// Initializes the Mapper with an ITypeFormatter.
    /// </summary>
    /// <param name="typeFormatter">The ITypeFormatter is used to instantiate all types during the Build method.</param>
    AsyncTargetBuilder(ITypeFormatter typeFormatter)
    {
        this.typeFormatter = typeFormatter;
    }

    /// <summary>
    /// A simpler instantiation that allows for quick fluent designing.
    /// </summary>
    /// <returns>A new Mapper used to map and instantiate the maps target.</returns>
    public static AsyncTargetBuilder GetNewInstance() => new(ITypeFormatter.Default);

    /// <summary>
    /// A simpler instantiation that allows for quick fluent designing.
    /// </summary>
    /// <param name="typeFormatter">The ITypeFormatter is used to instantiate all types during the Build method.</param>
    /// <returns>A new Mapper used to map and instantiate the maps target.</returns>
    public static AsyncTargetBuilder GetNewInstance(ITypeFormatter typeFormatter) => new(typeFormatter);

    /// <summary>
    /// Builds the target value from the source value using the default mappings and any custom mappings put in place.
    /// </summary>
    /// <typeparam name="TSource">The source type mapped from.</typeparam>
    /// <typeparam name="TTarget">The target type mapped to.</typeparam>
    /// <param name="mapper">The Map used to build.</param>
    /// <returns>An instance of the target type with values mapped from the source instance.</returns>
    public async Task<TTarget?> BuildAsync<TSource, TTarget>(IConfiguration<TSource, TTarget> configuration, TSource source)
    {
        if (source == null)
        {
            return default;
        }
        var target = typeFormatter.GetInstance<TTarget>();
        target = await CopyAsync(configuration, source, target);
        return target;
    }

    /// <summary>
    /// Builds the target value from the source value using the default mappings and any custom mappings put in place.
    /// </summary>
    /// <typeparam name="TSource">The source type mapped from.</typeparam>
    /// <typeparam name="TTarget">The target type mapped to.</typeparam>
    /// <param name="configuration">The Map used to build.</param>
    /// <param name="source">The source used during the mapping.</param>
    /// <param name="getArgs">Optional parameters that may be used to instantiate the target.</param>
    /// <returns>An instance of the target type with values mapped from the source instance.</returns>
    public async Task<TTarget> BuildAsync<TSource, TTarget>(IConfiguration<TSource, TTarget> configuration, TSource source, Func<object[]> getArgs)
    {
        var target = typeFormatter.GetInstance<TTarget>(getArgs);
        target = await CopyAsync(configuration, source, target);
        return target;
    }

    public async Task ReverseCopyAsync<TSource, TTarget>(IConfiguration<TSource, TTarget> configuration, TSource source, TTarget target)
    {
        await CopyAsync(configuration, source, target);
    }

    private async Task<TTarget> CopyAsync<TSource, TTarget>(IConfiguration<TSource, TTarget> configuration, TSource source, TTarget target)
    {
        var dynamicObjectSource = typeof(TSource) == typeof(object);
        var plan = dynamicObjectSource ? default : MappingPlanCache.GetOrAddPlan(configuration);
    var asyncTransformers = (configuration as ITransformAsync)?.AsyncTransformers;
    var hasAsyncTransformers = asyncTransformers != null && asyncTransformers.Count > 0;

        if (!dynamicObjectSource)
        {
            // Async fast path: no async transformers => use sync builder with compiled plan.
            if (!hasAsyncTransformers)
            {
                var syncBuilder = TargetBuilder.GetNewInstance();
                var syncResult = syncBuilder.Build(configuration, source);
                return syncResult == null ? target : syncResult;
            }
            var transformedValues = hasAsyncTransformers ? new Dictionary<PropertyInfo, object?>(plan.Pairs.Length) : null;
            foreach (var pair in plan.Pairs)
            {
                var sourcePropertyInfo = pair.Source;
                var targetPropertyInfo = pair.Target;
                object? finalValue = null;
                var hasValue = false;
                foreach (var transformer in asyncTransformers!)
                {
                    if (transformedValues!.TryGetValue(sourcePropertyInfo, out var prev) && prev != null)
                    {
                        var boolItem = await transformer.GetValueAsync(source, sourcePropertyInfo, targetPropertyInfo, prev);
                        if (boolItem.BooleanValue)
                        {
                            finalValue = boolItem.Item;
                            transformedValues[sourcePropertyInfo] = boolItem.Item;
                            hasValue = true;
                            break;
                        }
                    }
                    else
                    {
                        var boolItem = await transformer.GetValueAsync(source, sourcePropertyInfo, targetPropertyInfo);
                        if (boolItem.BooleanValue)
                        {
                            finalValue = boolItem.Item;
                transformedValues![sourcePropertyInfo] = boolItem.Item;
                            hasValue = true;
                            break;
                        }
                    }
                }
                if (!hasValue)
                {
                    if (transformedValues!.TryGetValue(sourcePropertyInfo, out var prev) && prev != null)
                    {
                        finalValue = prev;
                    }
                    else if (typeof(TSource) != typeof(TTarget) && sourcePropertyInfo.PropertyType == typeof(TSource) && targetPropertyInfo.PropertyType == typeof(TTarget))
                    {
                        finalValue = await GetNewInstance().BuildAsync(configuration, source);
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
                if (finalValue != null && !targetPropertyInfo.PropertyType.IsAssignableFrom(finalValue.GetType()))
                {
                    SetTargetValue(target, targetPropertyInfo, finalValue, configuration);
                }
                else
                {
                    pair.Setter(target!, finalValue);
                }
                transformedValues![targetPropertyInfo] = finalValue!;
            }
            return target;
        }

        // Dynamic path
        if (source == null) return target;
        var skipSet = configuration.SkipPropertyNames;
        var sourceProps = source.GetType().GetProperties(PropertyBindingFlag).Where(p => !skipSet.Contains(p.Name)).ToList();
        var targetProps = typeof(TTarget).GetProperties(PropertyBindingFlag).Where(p => !skipSet.Contains(p.Name)).ToList();
        var joinedPropertyInfos = GetSourceResultProperties(sourceProps, targetProps, configuration);
        AddPropertyNameMaps(configuration, sourceProps, targetProps, joinedPropertyInfos);

        var transformedValuesDyn = hasAsyncTransformers ? new Dictionary<PropertyInfo, object?>() : null;
        foreach (var (sourcePropertyInfo, targetPropertyInfo) in joinedPropertyInfos)
        {
            var isTransformed = false;
            var transform = configuration as ITransformAsync;
            if (hasAsyncTransformers)
            {
                foreach (var transformer in transform!.AsyncTransformers)
                {
            if (transformedValuesDyn!.TryGetValue(sourcePropertyInfo, out var prev) && prev != null)
                    {
                        var boolItem = await transformer.GetValueAsync(source, sourcePropertyInfo, targetPropertyInfo, prev);
                        if (boolItem.BooleanValue)
                        {
                transformedValuesDyn[sourcePropertyInfo] = boolItem.Item;
                            SetTargetValue(target, targetPropertyInfo, boolItem.Item, configuration);
                            isTransformed = true;
                            break;
                        }
                    }
                    else
                    {
                        var boolItem = await transformer.GetValueAsync(source, sourcePropertyInfo, targetPropertyInfo);
                        if (boolItem.BooleanValue)
                        {
                transformedValuesDyn[sourcePropertyInfo] = boolItem.Item;
                            SetTargetValue(target, targetPropertyInfo, boolItem.Item, configuration);
                            isTransformed = true;
                            break;
                        }
                    }
                }
            }
            if (!isTransformed)
            {
        if (hasAsyncTransformers && transformedValuesDyn!.TryGetValue(sourcePropertyInfo, out var prevVal))
                {
                    SetTargetValue(target, targetPropertyInfo, prevVal, configuration);
                }
                else
                {
                    var targetValue = sourcePropertyInfo.GetValue(source);
                    SetTargetValue(target, targetPropertyInfo, targetValue, configuration);
                }
            }
        }

        return target;
    }

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

    // per-call transformed values dictionary handled in calling code only when async transformers are present.
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