using ImmutaMap.Builders;
using System.Collections.Concurrent;
using System.Reflection;

namespace ImmutaMap;

public static partial class TargetExtensions
{
    /// <summary>
    /// A quick update of a Type using an anonymous type to get the values to use.
    /// </summary>
    /// <typeparam name="T">The type this method works against.</typeparam>
    /// <param name="t">The instantiation of the type being mapped from.</param>
    /// <param name="a">The anonymous type used to make the mapping work.</param>
    /// <returns>Instantiated T target value.</returns>
    public static T? With<T>(this T t, dynamic a)
    {
        var properties = new List<(string Name, object Value)>();
        foreach (var prop in a.GetType().GetProperties())
        {
            var foundProp = typeof(T).GetProperty(prop.Name);
            if (foundProp != null) properties.Add((prop.Name, prop.GetValue(a, null)));
        }
        var configuration = Configuration<T, T>.Empty;
        foreach (var (Name, Value) in properties) configuration.Transformers.Add(new DynamicTransformer(Value.GetType(), Name, () => Value));
        return TargetBuilder.GetNewInstance().Build(configuration, t);
    }

    /// <summary>
    /// A quick update of a Type using an anonymous type to get the values to use.
    /// </summary>
    /// <typeparam name="T">The type this method works against.</typeparam>
    /// <param name="t">The instantiation of the type being mapped from.</param>
    /// <param name="a">The anonymous type used to make the mapping work.</param>
    /// <param name="Map">Map that can be supplied to mapping.</param>
    /// <param name="throwExceptions">Options value that determines if exceptions will be thrown or handled silently.  Default is true to throw exceptoipns.</param>
    /// <returns>Instantiated T target value.</returns>
    public static T? With<T>(this T t, dynamic a, Action<Configuration<T, T>> config)
    {
        var configuration = new Configuration<T, T>();
        config.Invoke(configuration);
        var properties = new List<(string Name, object Value)>();
        foreach (var prop in a.GetType().GetProperties())
        {
            var foundProp = typeof(T).GetProperty(prop.Name);
            if (foundProp != null) properties.Add((prop.Name, prop.GetValue(a, null)));
        }
        foreach (var (Name, Value) in properties) configuration.Transformers.Add(new DynamicTransformer(Value.GetType(), Name, () => Value));
        return TargetBuilder.GetNewInstance().Build(configuration, t);
    }

    public static T? With<T>(this T source, Action<Configuration<T, T>> config)
        where T : notnull
    {
        var configuration = new Configuration<T, T>();
        config.Invoke(configuration);
        return TargetBuilder.GetNewInstance().Build(configuration, source);
    }

    /// <summary>
    /// Maps a type to itself where an expression binding the property to a map and another function is used to perform the mapping logic.
    /// </summary>
    /// <typeparam name="T">The source type being mapped.</typeparam>
    /// <typeparam name="TSourcePropertyType">The source property type being mapped.</typeparam>
    /// <param name="t">The source object this method sets the mapping against.</param>
    /// <param name="sourceExpression">The expression used to get the source property.</param>
    /// <param name="valueFunc">The function used to get the target value from the source property.</param>
    /// <returns></returns>
    public static T? With<T, TSourcePropertyType>(this T t,
                                                 Expression<Func<T, TSourcePropertyType>> sourceExpression,
                                                 Func<TSourcePropertyType, TSourcePropertyType> valueFunc)
    {
        if (t == null) return default;
        // Fast path: single property update without full mapping pipeline.
        if (sourceExpression.Body is MemberExpression me && me.Member is PropertyInfo pi)
        {
            var access = AccessorCache<T>.GetOrAdd(pi);
            if (access.Setter != null && access.Getter != null)
            {
                var originalValueObj = access.Getter(t!);
                // If getter failed just fallback
                if (originalValueObj is TSourcePropertyType originalValue)
                {
                    var newValue = valueFunc(originalValue);
                    // If unchanged, return original to avoid clone cost.
                    if (EqualityComparer<TSourcePropertyType>.Default.Equals(originalValue, newValue))
                        return t;
                    var clone = ClonerCache<T>.Clone(t!);
                    try
                    {
                        access.Setter(clone!, newValue!);
                        return clone;
                    }
                    catch
                    {
                        // Fallback on any assignment issue
                    }
                }
            }
        }
        // Fallback to legacy mapping path if expression not simple property or any cache issue.
        var configuration = new Configuration<T, T>();
        var compiled = sourceExpression.Compile();
        configuration.MapPropertyType(sourceExpression, _ => valueFunc(compiled(t!))!);
        return TargetBuilder.GetNewInstance().Build(configuration, t);
    }

    /// <summary>
    /// Maps a type to itself where an expression binding the property to a map and another function is used to perform the mapping logic.
    /// </summary>
    /// <typeparam name="T">The source type being mapped.</typeparam>
    /// <typeparam name="TSourcePropertyType">The source property type being mapped.</typeparam>
    /// <param name="t">The source object this method sets the mapping against.</param>
    /// <param name="sourceExpression">The expression used to get the source property.</param>
    /// <param name="value">The function used to get the target value from the source property.</param>
    /// <returns></returns>
    public static T? With<T, TSourcePropertyType>(this T t,
                                                 Expression<Func<T, TSourcePropertyType>> sourceExpression,
                                                 TSourcePropertyType value)
    {
        return t.With(sourceExpression, propertyValue => value);
    }

    /// <summary>
    /// For simple one to one mappings from type to type.
    /// </summary>
    /// <typeparam name="T">The type to map to.</typeparam>
    /// <param name="obj">The obejct this method works against.</param>
    /// <returns>Returns an instantiated T with the values from the object used as reference.</returns>
    public static T? To<T>(this object obj)
    {
    return TargetBuilder.GetNewInstance().Build(new Configuration<object, T>(), obj);
    }

    /// <summary>
    /// For simple one to one mappings from type to type.
    /// </summary>
    /// <typeparam name="T">The type to map to.</typeparam>
    /// <param name="source">The obejct this method works against.</param>
    /// <param name="config">Sets mapping configurations inline.</param>
    /// <returns>Returns an instantiated T with the values from the object used as reference.</returns>
    public static TTarget? To<TSource, TTarget>(this TSource source, Action<Configuration<TSource, TTarget>> config)
    {
        var configuration = new Configuration<TSource, TTarget>();
        config.Invoke(configuration);
        return TargetBuilder.GetNewInstance().Build(configuration, source);
    }

    public static TTarget? To<TSource, TTarget>(this TSource source)
    {
        return TargetBuilder.GetNewInstance().Build(Configuration<TSource, TTarget>.Empty, source);
    }

    public static dynamic ToDynamic<T>(this T t)
    {
        return AnonymousMapBuilder.Build(Configuration<T, dynamic>.Empty, t);
    }

    public static dynamic ToDynamic<T>(this T t, Action<Configuration<T, dynamic>> config)
    {
        var configuration = new Configuration<T, dynamic>();
        config.Invoke(configuration);
        return AnonymousMapBuilder.Build(configuration, t);
    }

    public static void Copy<TSource, TTarget>(this TTarget target, TSource source)
    {
    TargetBuilder.GetNewInstance().ReverseCopy(new Configuration<object, TTarget>(), source!, target);
    }

    public static void Copy<TSource, TTarget>(this TTarget target, TSource source, Action<Configuration<TSource, TTarget>> config)
    {
        var configuration = new Configuration<TSource, TTarget>();
        config.Invoke(configuration);
        TargetBuilder.GetNewInstance().ReverseCopy(configuration, source!, target);
    }
}

public static partial class TargetExtensions
{
    public static Task<T?> ToAsync<T>(this object obj)
    {
    return AsyncTargetBuilder.GetNewInstance().BuildAsync(new AsyncConfiguration<object, T>(), obj);
    }

    public static Task<TTarget?> ToAsync<TSource, TTarget>(this TSource source, Action<AsyncConfiguration<TSource, TTarget>> config)
    {
        var configuration = new AsyncConfiguration<TSource, TTarget>();
        config.Invoke(configuration);
        return AsyncTargetBuilder.GetNewInstance().BuildAsync(configuration, source);
    }

    public static Task CopyAsync<TSource, TTarget>(this TTarget target, TSource source)
    {
    return AsyncTargetBuilder.GetNewInstance().ReverseCopyAsync(new AsyncConfiguration<object, TTarget>(), source!, target);
    }

    public static Task CopyAsync<TSource, TTarget>(this TTarget target, TSource source, Action<AsyncConfiguration<TSource, TTarget>> config)
    {
        var configuration = new AsyncConfiguration<TSource, TTarget>();
        config.Invoke(configuration);
        return AsyncTargetBuilder.GetNewInstance().ReverseCopyAsync(configuration, source!, target);
    }
}

// Internal caches for fast With<> single property updates.
internal static class AccessorCache<T>
{
    private static readonly ConcurrentDictionary<string, (Func<T, object?> Getter, Action<T, object?>? Setter)> Cache = new();

    public static (Func<T, object?> Getter, Action<T, object?>? Setter) GetOrAdd(PropertyInfo pi)
    {
        return Cache.GetOrAdd(pi.Name, _ => Build(pi));
    }

    private static (Func<T, object?> Getter, Action<T, object?>? Setter) Build(PropertyInfo pi)
    {
        var instance = Expression.Parameter(typeof(T), "i");
        Expression body = Expression.Property(pi.GetMethod!.IsStatic ? null : instance, pi);
        var boxed = Expression.Convert(body, typeof(object));
        var getter = Expression.Lambda<Func<T, object?>>(boxed, instance).Compile();

        Action<T, object?>? setter = null;
        if (pi.CanWrite)
        {
            var valueParam = Expression.Parameter(typeof(object), "v");
            var cast = Expression.Convert(valueParam, pi.PropertyType);
            var assign = Expression.Assign(Expression.Property(instance, pi), cast);
            setter = Expression.Lambda<Action<T, object?>>(assign, instance, valueParam).Compile();
        }
        return (getter, setter);
    }
}

internal static class ClonerCache<T>
{
    private static readonly Func<T, T> Cloner = Build();
    public static T Clone(T source) => Cloner(source);

    private static Func<T, T> Build()
    {
        var method = typeof(object).GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var p = Expression.Parameter(typeof(T), "s");
        var call = Expression.Call(Expression.Convert(p, typeof(object)), method);
        var cast = Expression.Convert(call, typeof(T));
        return Expression.Lambda<Func<T, T>>(cast, p).Compile();
    }
}
