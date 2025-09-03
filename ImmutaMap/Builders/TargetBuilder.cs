namespace ImmutaMap.Builders;

public class TargetBuilder
{
    private const BindingFlags PropertyBindingFlag = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly ITypeFormatter typeFormatter;
    private readonly IDictionary<(Type, PropertyInfo), object> transformedValues = new Dictionary<(Type, PropertyInfo), object>();

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
        var target = typeFormatter.GetInstance<TTarget>();
        Copy(configuration, source, target);
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
        MappingPlanCache.MappingPlan plan;
        var dynamicObjectSource = typeof(TSource) == typeof(object);
        if (!dynamicObjectSource)
        {
            plan = MappingPlanCache.GetOrAddPlan(configuration);
        }
        else
        {
            // We'll emulate original behavior using runtime source & target reflection each call.
            plan = default;
        }
        var transformers = (configuration as ITransform)?.Transformers;

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

            if (transformers != null && transformers.Count > 0)
            {
                foreach (var transformer in transformers)
                {
                    if (transformedValues.TryGetValue((typeof(TSource), sourcePropertyInfo), out var prev))
                    {
                        if (transformer.TryGetValue(source, sourcePropertyInfo, targetPropertyInfo, prev, out var transformedValue))
                        {
                            finalValue = transformedValue;
                            transformedValues[(typeof(TSource), sourcePropertyInfo)] = transformedValue;
                            hasValue = true;
                            break; // first transformer wins
                        }
                    }
                    else if (transformer.TryGetValue(source, sourcePropertyInfo, targetPropertyInfo, out var transformedValue))
                    {
                        finalValue = transformedValue;
                        transformedValues[(typeof(TSource), sourcePropertyInfo)] = transformedValue;
                        hasValue = true;
                        break;
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
                            finalValue = GetNewInstance().Build(configuration, source);
                        }
                        else
                        {
                            // Use compiled getter
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

                // Use compiled setter if available, otherwise fallback to SetTargetValue (handles backing field)
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
            return;
        }

        // Dynamic object source path (TSource == object) with runtime plan caching
        if (source == null) return;
    var runtimePlan = MappingPlanCache.GetOrAddRuntimePlan(source.GetType(), (IConfiguration<object, TTarget>)configuration);
        var runtimePairs = runtimePlan.Pairs;
        var rGetters = runtimePlan.SourceGetters;
        var rSetters = runtimePlan.TargetSetters;
        var transformersDyn = (configuration as ITransform)?.Transformers;
        for (int i = 0; i < runtimePairs.Length; i++)
        {
            var (sourcePropertyInfo, targetPropertyInfo) = runtimePairs[i];
            object? finalValue = null;
            var transformed = false;
            if (transformersDyn != null && transformersDyn.Count > 0)
            {
                foreach (var transformer in transformersDyn)
                {
                    if (transformedValues.TryGetValue((typeof(TSource), sourcePropertyInfo), out var prevVal))
                    {
                        if (transformer.TryGetValue(source, sourcePropertyInfo, targetPropertyInfo, prevVal, out var tv))
                        {
                            finalValue = tv;
                            transformedValues[(typeof(TSource), sourcePropertyInfo)] = tv!;
                            transformed = true;
                            break;
                        }
                    }
                    else if (transformer.TryGetValue(source, sourcePropertyInfo, targetPropertyInfo, out var tv))
                    {
                        finalValue = tv;
                        transformedValues[(typeof(TSource), sourcePropertyInfo)] = tv!;
                        transformed = true;
                        break;
                    }
                }
            }
            if (!transformed)
            {
                if (transformedValues.TryGetValue((typeof(TSource), sourcePropertyInfo), out var prev))
                {
                    finalValue = prev;
                }
                else
                {
                    finalValue = rGetters[i](source!);
                }
            }
            if (rSetters[i] != null && targetPropertyInfo.CanWrite)
            {
                var setter = rSetters[i];
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

        transformedValues[(typeof(TTarget), targetPropertyInfo)] = targetValue!;
    }

    // Removed older helper methods relying on repeated ToLowerInvariant calls; dictionary approach used instead.
}