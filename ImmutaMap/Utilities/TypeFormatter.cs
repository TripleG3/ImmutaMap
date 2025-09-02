namespace ImmutaMap.Utilities;

/// <summary>
/// Can get an instance of T using the default empty constructor
/// </summary>
public class TypeFormatter : ITypeFormatter
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Func<object>> FactoryCache = new();

    private static Func<object> CreateFactory(Type t)
    {
        // Prefer parameterless (public or non-public) ctor
        var ctor = t.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, Type.EmptyTypes, modifiers: null);
        if (ctor != null)
        {
            var newExpr = Expression.New(ctor);
            var lambda = Expression.Lambda<Func<object>>(Expression.Convert(newExpr, typeof(object)));
            return lambda.Compile();
        }
        // Fallback: Activator with non-public allowed
        return () =>
        {
            try
            {
                return Activator.CreateInstance(t, true)!;
            }
            catch
            {
                // Fallback for types without accessible constructors.
#pragma warning disable SYSLIB0050 // Formatter-based serialization is obsolete
                return FormatterServices.GetUninitializedObject(t);
#pragma warning restore SYSLIB0050
            }
        };
    }

    private static Func<object> GetFactory(Type t) => FactoryCache.GetOrAdd(t, static tt => CreateFactory(tt));

    /// <inheritdoc />
    public T GetInstance<T>()
    {
        try
        {
            return (T)GetFactory(typeof(T)).Invoke();
        }
        catch
        {
            // Ultimate fallback
#pragma warning disable SYSLIB0050
            return (T)FormatterServices.GetUninitializedObject(typeof(T));
#pragma warning restore SYSLIB0050
        }
    }

    /// <inheritdoc />
    public T GetInstance<T>(Func<object[]> getArgs)
    {
        // If there is a parameterless factory use it; otherwise attempt to construct with args.
        var ctor = typeof(T).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, Type.EmptyTypes, modifiers: null);
        if (ctor != null)
        {
            return GetInstance<T>();
        }
        try
        {
            return (T)Activator.CreateInstance(typeof(T), true, getArgs.Invoke())!;
        }
        catch
        {
            // Ultimate fallback
#pragma warning disable SYSLIB0050
            return (T)FormatterServices.GetUninitializedObject(typeof(T));
#pragma warning restore SYSLIB0050
        }
    }
}
