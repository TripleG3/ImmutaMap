namespace ImmutaMap.Builders;

public class AsyncTargetBuilder
{
    private const BindingFlags PropertyBindingFlag = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly ITypeFormatter typeFormatter;
    private readonly IDictionary<(Type, PropertyInfo), object> transformedValues = new Dictionary<(Type, PropertyInfo), object>();

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
        MappingPlanCache.MappingPlan plan;
        var dynamicObjectSource = typeof(TSource) == typeof(object);
        if (!dynamicObjectSource)
        {
            plan = MappingPlanCache.GetOrAddPlan(configuration);
        }
        else
        {
            plan = default;
        }
        var asyncTransformers = (configuration as ITransformAsync)?.AsyncTransformers;

        if (!dynamicObjectSource)
        {
            var pairs = plan.Pairs;
            var getters = plan.SourceGetters;
            var setters = plan.TargetSetters;
            for (int i = 0; i < pairs.Length; i++)
            {
                var (sourcePropertyInfo, targetPropertyInfo) = pairs[i];
                object? finalValue = null;
                var hasValue = false;

            if (asyncTransformers != null && asyncTransformers.Count > 0)
            {
                foreach (var transformer in asyncTransformers)
                {
                    if (transformedValues.TryGetValue((typeof(TSource), sourcePropertyInfo), out var prev))
                    {
                        var boolItem = await transformer.GetValueAsync(source, sourcePropertyInfo, targetPropertyInfo, prev);
                        if (boolItem.BooleanValue)
                        {
                            finalValue = boolItem.Item;
                            transformedValues[(typeof(TSource), sourcePropertyInfo)] = boolItem.Item;
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
                            transformedValues[(typeof(TSource), sourcePropertyInfo)] = boolItem.Item;
                            hasValue = true;
                            break;
                        }
                    }
                }
            }

                if (!hasValue)
                {
                    if (transformedValues.TryGetValue((typeof(TSource), sourcePropertyInfo), out var prev))
                    {
                        finalValue = prev;
                    }
                    else
                    {
                        if (typeof(TSource) != typeof(TTarget)
                            && sourcePropertyInfo.PropertyType == typeof(TSource)
                            && targetPropertyInfo.PropertyType == typeof(TTarget))
                        {
                            finalValue = await GetNewInstance().BuildAsync(configuration, source);
                        }
                        else
                        {
                            var getter = (Func<TSource, object?>)getters[i];
                            finalValue = getter(source);
                        }
                    }
                }

                if (finalValue == null && sourcePropertyInfo.CanRead)
                {
                    var getter = (Func<TSource, object?>)getters[i];
                    var fallback = getter(source);
                    if (fallback != null) finalValue = fallback;
                }

                if (setters[i] is Action<TTarget, object?> setter && targetPropertyInfo.CanWrite)
                {
                    if (finalValue != null && !targetPropertyInfo.PropertyType.IsAssignableFrom(finalValue.GetType()))
                    {
                        if (!configuration.WillNotThrowExceptions)
                            throw new BuildException(finalValue.GetType(), targetPropertyInfo);
                        continue;
                    }
                    setter(target, finalValue);
                    transformedValues[(typeof(TTarget), targetPropertyInfo)] = finalValue!;
                }
                else
                {
                    SetTargetValue(target, targetPropertyInfo, finalValue, configuration);
                }
            }
            return target;
        }

        // Dynamic path with runtime plan caching
        if (source == null) return target;
        var runtimePlan = MappingPlanCache.GetOrAddRuntimePlan(source.GetType(), (IConfiguration<object, TTarget>)configuration);
        var pairsDyn = runtimePlan.Pairs;
        var gettersDyn = runtimePlan.SourceGetters;
        var settersDyn = runtimePlan.TargetSetters;
        var asyncTrans = (configuration as ITransformAsync)?.AsyncTransformers;
        for (int i = 0; i < pairsDyn.Length; i++)
        {
            var (sourcePropertyInfo, targetPropertyInfo) = pairsDyn[i];
            object? finalValue = null;
            var transformed = false;
            if (asyncTrans != null && asyncTrans.Count > 0)
            {
                foreach (var transformer in asyncTrans)
                {
                    if (transformedValues.TryGetValue((typeof(TSource), sourcePropertyInfo), out var prev))
                    {
                        var boolItem = await transformer.GetValueAsync(source, sourcePropertyInfo, targetPropertyInfo, prev);
                        if (boolItem.BooleanValue)
                        {
                            finalValue = boolItem.Item;
                            transformedValues[(typeof(TSource), sourcePropertyInfo)] = boolItem.Item;
                            transformed = true;
                            break;
                        }
                    }
                    else
                    {
                        var boolItem = await transformer.GetValueAsync(source, sourcePropertyInfo, targetPropertyInfo);
                        if (boolItem.BooleanValue)
                        {
                            finalValue = boolItem.Item;
                            transformedValues[(typeof(TSource), sourcePropertyInfo)] = boolItem.Item;
                            transformed = true;
                            break;
                        }
                    }
                }
            }
            if (!transformed)
            {
                if (transformedValues.TryGetValue((typeof(TSource), sourcePropertyInfo), out var prevVal))
                {
                    finalValue = prevVal;
                }
                else
                {
                    finalValue = gettersDyn[i](source!);
                }
            }
            if (settersDyn[i] != null && targetPropertyInfo.CanWrite)
            {
                var setter = settersDyn[i];
                if (finalValue != null && !targetPropertyInfo.PropertyType.IsAssignableFrom(finalValue.GetType()))
                {
                    if (!configuration.WillNotThrowExceptions)
                        throw new BuildException(finalValue.GetType(), targetPropertyInfo);
                    continue;
                }
                setter(target!, finalValue);
                transformedValues[(typeof(TTarget), targetPropertyInfo)] = finalValue!;
            }
            else
            {
                SetTargetValue(target, targetPropertyInfo, finalValue, configuration);
            }
        }
        return target;
    }

    private async Task<bool> RunAsyncTransformersAsync<TSource, TTarget>(IConfiguration<TSource, TTarget> configuration, TSource source, TTarget target, PropertyInfo sourcePropertyInfo, PropertyInfo targetPropertyInfo, bool isTransformed)
    {
        if (configuration is ITransformAsync transformAsync)
        {
            foreach (var transformer in transformAsync.AsyncTransformers)
            {
                var previouslyTransformedValue = transformedValues.ContainsKey((typeof(TSource), sourcePropertyInfo))
                    ? transformedValues[(typeof(TSource), sourcePropertyInfo)]
                    : default;

                if (transformedValues.ContainsKey((typeof(TSource), sourcePropertyInfo)))
                {
                    var boolItem = await transformer.GetValueAsync(source, sourcePropertyInfo, targetPropertyInfo, transformedValues[(typeof(TSource), sourcePropertyInfo)]);
                    if (boolItem.BooleanValue)
                    {
                        transformedValues[(typeof(TSource), sourcePropertyInfo)] = boolItem.Item;
                        SetTargetValue(target, targetPropertyInfo, boolItem.Item, configuration);
                        isTransformed = true;
                    }
                }
                else
                {
                    var boolItem = await transformer.GetValueAsync(source, sourcePropertyInfo, targetPropertyInfo);
                    if (boolItem.BooleanValue)
                    {
                        transformedValues[(typeof(TSource), sourcePropertyInfo)] = boolItem.Item;
                        SetTargetValue(target, targetPropertyInfo, boolItem.Item, configuration);
                        isTransformed = true;
                    }
                }
            }
        }

        if (!isTransformed)
        {
            var previouslyTransformedValue = transformedValues.ContainsKey((typeof(TSource), sourcePropertyInfo))
                ? transformedValues[(typeof(TSource), sourcePropertyInfo)]
                : default;

            if (previouslyTransformedValue != default)
            {
                SetTargetValue(target, targetPropertyInfo, previouslyTransformedValue, configuration);
            }
            else
            {
                object? targetValue;
                if (typeof(TSource) != typeof(TTarget)
                && sourcePropertyInfo.PropertyType == typeof(TSource)
                && targetPropertyInfo.PropertyType == typeof(TTarget))
                {
                    targetValue = await GetNewInstance().BuildAsync(configuration, source);
                }
                else
                {
                    targetValue = sourcePropertyInfo.GetValue(source)!;
                }
                SetTargetValue(target, targetPropertyInfo, targetValue, configuration);
            }
        }

        return isTransformed;
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

        transformedValues[(typeof(TTarget), targetPropertyInfo)] = targetValue!;
    }

    // Removed legacy helper methods (AddPropertyNameMaps / GetSourceResultProperties); replaced with dictionary-based logic in CopyAsync.
}