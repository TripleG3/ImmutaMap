# ImmutaMap

High‑performance, allocation‑aware mapping for immutable (and mutable) .NET types.

Seamlessly map between records, classes, anonymous types, and dynamic targets with inline configuration, attribute / type transforms, and async support.

## Why ImmutaMap?

Most mappers were designed around mutable POCOs. When you lean into C# records and immutable design, you either:

* Lose ergonomics (manual constructor plumbing everywhere), or
* Fall back to reflection (slow, alloc heavy), or
* Maintain piles of hand‑written projection code.

ImmutaMap focuses on immutable‑first scenarios while still supporting classic mutable types. It compiles mapping plans (constructor + property delegates), caches them, and applies only the transforms you ask for.

Key capabilities:

* Map record -> class, class -> record, class -> class, record -> record.
* Inline fluent configuration (rename, skip, transform property / type / attribute).
* Sync & async transforms (per property or per type) without changing your domain models.
* Update immutable instances using `With` (expression or anonymous type) – new instance every time.
* Copy into existing mutable targets (`Copy`, `CopyAsync`), including name remaps.
* Attribute‑driven transforms (source & target side) with full control.
* Dynamic mapping (`ToDynamic`) – shape an object on the fly.

> Full test coverage examples: [`TargetBuilderTests.cs`](./ImmutaMap.Test/TargetBuilderTests.cs)

## Quick Start

Install via source today (NuGet packaging WIP):

```bash
git clone https://github.com/TripleG3/ImmutaMap.git
cd ImmutaMap
dotnet build
```

Reference `ImmutaMap` from your project, then:

```csharp
var person = new PersonRecord("Mike", "Doe", 42);
var dto = person.To<PersonRecord, PersonDto>();
```

## Core API Surface

| Method | Purpose | Immutable Safe | Notes |
|--------|---------|----------------|-------|
| `To<TTarget>()` | Simple 1:1 map | ✅ | Uses plan cache & constructor fast path where possible. |
| `To<TSource,TTarget>(cfg => …)` | Configured map | ✅ | Rename, skip, transforms, etc. |
| `ToAsync<TSource,TTarget>(cfg => …)` | Async configured map | ✅ | Awaited property/type transforms. |
| `With(expr, value)` | Replace one property | ✅ | New instance (source untouched). |
| `With(expr, func)` | Compute new property from old | ✅ | Executes func lazily at build. |
| `With(anonymous)` | Apply multiple property values | ✅ | Only matching names considered. |
| `Copy(source)` | Copy into existing target | N/A | Mutates target in place. |
| `Copy(source, cfg => …)` | Configured in‑place copy | N/A | Rename + transforms allowed. |
| `CopyAsync(source, cfg => …)` | Async in‑place copy | N/A | Async transforms awaited. |
| `ToDynamic()` | Shape to dynamic | ✅ | Produces an `ExpandoObject`-like anonymous result. |
| `MapName` | Rename property mapping | ✅ | Source→Target pair registration. |
| `Skip(expr)` | Exclude property | ✅ | Name case handling via `IgnoreCase`. |
| `MapPropertyType(expr, func)` | Single property transform | ✅ | Adds a property transformer. |
| `MapSourceAttribute<TAttr>` | Source attribute transform | ✅ | Provide (attr,value) → new value. |
| `MapTargetAttribute<TAttr>` | Target attribute transform | ✅ | Same but runs targeting target property metadata. |
| `MapType<TType>` | Global type transform (sync) | ✅ | All matching source values run through delegate. |
| `MapTypeAsync<TType>` | Global type transform (async) | ✅ | ValueTask/Task path. |

## Examples

### 1. Simple Map (record → class)

```csharp
var record = new PersonRecord("Mike", "Doe", 42);
var cls = record.To<PersonRecord, PersonClass>(_ => { });
```

### 2. Rename a Property

```csharp
var employee = record.To<PersonRecord, Employee>(cfg =>
{
	cfg.MapName(r => r.FirstName, e => e.GivenName)
		.MapName(r => r.LastName,  e => e.Surname);
});
```

### 3. Skip & Case‑Insensitive Mapping

```csharp
var model = something.To<Source, Target>(cfg =>
{
	cfg.IgnoreCase = true; // 'firstname' -> 'FirstName'
	cfg.Skip(s => s.Secret);
});
```

### 4. Per‑Property Transform

```csharp
var updated = order.To<Order, OrderDto>(cfg =>
{
	cfg.MapPropertyType(o => o.Total, total => Math.Round(total, 2));
});
```

### 5. Attribute‑Driven Transform (Source)

```csharp
var result = person.To<PersonRecord, PersonClass>(cfg =>
{
	cfg.MapSourceAttribute<FirstNameAttribute>((attr, value) => attr.RealName);
});
```

### 6. Global Type Transform

```csharp
var message = dto.To<MessageDto, Message>(cfg =>
{
	cfg.MapType<DateTime>(d => d.ToLocalTime());
});
```

### 7. Async Type Transform

```csharp
var msg = await dto.ToAsync<MessageDto, Message>(cfg =>
{
	cfg.MapTypeAsync<DateTime>(async d => d.ToLocalTime());
});
```

### 8. Updating Immutable Instance (`With`)

```csharp
var person = new PersonRecord("Mike", "Doe", 42);
var older = person.With(p => p.Age, age => age + 1);
var renamed = person.With(new { FirstName = "John" });
```

### 9. Copy Into Existing Mutable Target

```csharp
target.Copy(source); // shallow mapped properties by name

target.Copy(source, cfg =>
{
	cfg.MapName(s => s.Counter, t => t.Count)
		.MapName(s => s.Item_2,  t => t.Item2);
});
```

### 10. Dynamic Mapping

```csharp
dynamic shaped = person.ToDynamic(cfg =>
{
	cfg.Skip(p => p.Age);
});
Console.WriteLine(shaped.FirstName);
```

## Async Patterns
Async overloads mirror sync but use `AsyncConfiguration<,>` and accept:
* `MapTypeAsync<T>(Func<T, Task<object?>>)`
* `MapPropertyTypeAsync(expr, ValueTask<object>)`

They can be combined with sync transforms.

## Error Handling
Set `WillNotThrowExceptions = true` in a configuration to suppress mapper exceptions (they will be swallowed). Default is to throw mapping issues (e.g., access / type errors) to surface problems early.

## Performance Notes
Benchmarks (representative, will vary):

| Scenario | Library (µs/ns) | Manual (ns) | Notes |
|----------|-----------------|------------|-------|
| Record→Class (no transforms) | ~12 µs | ~6 ns | Dominated by property graph & delegate path; manual is raw ctor. |
| DTO→Record w/ ctor fast path | tens of ns | ~5–6 ns | Uses compiled constructor delegate. |
| DateTime global transform | Adds minor delegate overhead | N/A | Applied per matching property. |

Optimization features:
* Plan & constructor delegate caching.
* Compiled expression getters / setters (backing field for init‑only).
* Fast path when no transformers present.
* Async path defers allocation until needed.

## When to Use / When Not to Use
Use ImmutaMap when:
* You work heavily with records / immutable objects.
* You need inline, one‑off mapping logic without global profiles.
* You want attribute or type‑wide transforms without ceremony.

Avoid when:
* You require runtime code generation across assemblies (not exposed yet).
* You need precompiled static source generators (future consideration).

## Extension Points
Create custom transformers by implementing:
* `ITransformer` (sync)
* `IAsyncTransformer` (async)
Add them to `configuration.Transformers` / `configuration.AsyncTransformers`.

## Roadmap (Planned)
- Bulk compiled property copy delegate (remove per‑property loop cost).
- Specialized typed delegate paths (reduced boxing / indirection).
- Dynamic source fast‑plan caching.
- Optional source generator package.

## Contributing
1. Fork & branch from `main`.
2. Add/modify tests in `ImmutaMap.Test` for new behavior.
3. Run: `dotnet test` (all green) & `dotnet run --project ImmutaMap.Benchmarks` if perf related.
4. Submit PR with rationale + benchmark deltas (if perf change).

## Minimal End‑to‑End Sample
```csharp
var record = new PersonRecord("First", "Last", 30);
var enriched = record
	.To<PersonRecord, Employee>(cfg =>
	{
		cfg.MapName(r => r.FirstName, e => e.GivenName)
		   .MapType<DateTime>(d => d.ToLocalTime());
	})
	.With(e => e.GivenName, name => name.ToUpper());

// Copy into existing target
var target = new Employee();
target.Copy(enriched, cfg => cfg.MapName(e => e.GivenName, t => t.DisplayName));
```

## License
MIT. See [LICENSE](./LICENSE).

---
Questions / ideas? Open an issue or PR – feedback drives the roadmap.
```
