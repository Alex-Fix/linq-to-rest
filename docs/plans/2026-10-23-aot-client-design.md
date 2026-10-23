# Trim- and AOT-safe .NET client — design
Status: approved, implemented (see Implementation notes) · Date: 2026-10-23

Milestone 2 of [`2026-10-20-roadmap.md`](2026-10-20-roadmap.md): "Trim/AOT-safe .NET client".

## Problem

The .NET client depends on reflection and runtime code generation:

- Results are read with `JsonSerializer.Deserialize<List<T>>` and reflection-based metadata. Full rows are generated
  classes; projections can be anonymous types or records declared at the query site, which the System.Text.Json
  source generator can't describe.
- Tracking reads and writes members with `PropertyInfo.GetValue`/`SetValue` and builds getters with
  `Expression.Compile()`; saved rows are copied back by `PropertyInfo`.
- `ClientModel.Bind` discovers members with `GetProperties()`, so the trimmer can remove properties it doesn't see used.
- Smaller spots: `MakeGenericType` in the non-generic `CreateQuery`, `Enum.ToObject`, the captured-variable evaluator.

So the Blazor sample needs a `TrimmerRootAssembly` stopgap, and apps with reflection-based JSON turned off (Native AOT,
file-based `dotnet run app.cs`) can't read results at all.

## Goals / Non-goals

**Goals**
- The client publishes trimmed and with Native AOT with zero trim/AOT warnings, and works with reflection-based JSON
  turned off.
- Generated contexts bind through compile-time delegates; the client materializes full rows and `Select` projections
  itself.
- Hand-written classes keep working through the existing `Type`-based `Bind`, annotated as trim/AOT-unsafe.
- Reflection can't creep back: the trim/AOT analyzers fail the build, and a Native AOT smoke app runs in CI.
- No change to the wire, translation or golden request bodies; no slower materialization or change detection.

**Non-goals**
- A Roslyn source generator (backlog).
- AOT support for the server (EF Core and ASP.NET Core).
- Removing the `Type`-based `Bind` or `SerializerOptions` now (obsolete first, removal in the next breaking release).

## Constraints check

| Constraint | This feature |
|---|---|
| **Translatable** | Unchanged: translation and the wire are untouched. |
| **Untrusted input** | Responses come from the server the client chose, but the materializer stays strict: values are read with `JsonValues` (strict types, enums by name) and mismatches throw. Unknown keys are ignored, as today. |
| **No dynamic code** | The client's AOT path adopts the server rule: no `Compile()`, `MakeGenericType` or `Activator` outside annotated methods. The captured-variable evaluator keeps `Compile(preferInterpretation: true)` (interpreted, AOT-legal). Members referenced by the developer's own expression trees may be invoked through their `ConstructorInfo`/`MemberInfo`. |
| **Wire contract** | Unchanged. |
| **Public API** | Additive: `SetBinding<T>`, `ClientModel.Bind(RestModel, params SetBinding[])`. The `Type`-based `Bind` gets `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`; `RestDataContextOptions.SerializerOptions` becomes `[Obsolete]` (unused for reading). Generated C# changes shape (regenerated, not hand-written). |
| **Dependencies** | Client and Abstractions stay BCL-only (`System.Diagnostics.CodeAnalysis` attributes). The smoke app is a test project. |
| **Errors** | Same exception types. A materialization mismatch throws `RestQueryException`-style errors naming the set and member, not serializer exceptions. |

## Chosen approach

**Generated accessors plus the client's own materializer.** Generated code registers, per set, a factory and typed
get/set delegates per member; the client reads JSON itself (rows from bindings, projections from the `Select`
expression) and tracks through the same delegates.

- *Not a System.Text.Json source-generated context for rows:* two materialization mechanisms, it relies on the STJ
  generator running in every consumer project, enums by name need converter configuration, and projections still need
  their own materializer.
- *Not trim-safe annotations only:* Native AOT and reflection-off JSON would stay broken.
- *Not AOT for full rows only:* projections into anonymous types are the common case in real apps.

## Design

### Generated bindings and `ClientModel`

```csharp
public static ClientModel Model { get; } = ClientModel.Bind(
    RestModel.Parse("""{"v":1,...}"""),
    new SetBinding<Product>("products", static () => new Product())
        .Property("id", static p => p.Id, static (p, v) => p.Id = v)
        .Property("name", static p => p.Name, static (p, v) => p.Name = v)
        .Values("tags", static p => p.Tags, static (p, v) => p.Tags = v)
        .Reference("category", static p => p.Category, static (p, v) => p.Category = v)
        .Collection("images", static p => p.Images, static (p, v) => p.Images = v),
    new SetBinding<Category>("categories", static () => new Category())
        .Property("id", static c => c.Id, static (c, v) => c.Id = v));
```

- `SetBinding<T>`: set name and factory; `Property<TValue>` (scalars, enums), `Values<TElement>` (primitive
  collections), `Reference<TTarget>`, `Collection<TTarget>` (navigations). Member types come from generic arguments
  (`typeof(TValue)`); typed delegates are wrapped into untyped ones internally.
- `Bind(RestModel, params SetBinding[])` validates like today (every set and member bound exactly once; types,
  collections, nullability and enum members fit) with the same messages, without reflection.
- `Bind(RestModel, IReadOnlyDictionary<string, Type>)` stays for hand-written classes, annotated
  `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`, and builds the same internal accessors from `PropertyInfo`.
  Both overloads produce one internal representation, so the rest of the client has one code path.
- Abstractions becomes `IsAotCompatible`; `JsonValues` gets generic, AOT-safe readers for lists
  (`ReadList<TElement>`), used by `Values<TElement>`; the server keeps its existing call.
- `linqtorest-gen cs` emits the binding chain instead of the type dictionary.

### Materialization

**Full rows:** a row reader per set, built at bind time, walks each JSON object, reads known members with their typed
readers and sets them on a new instance from the factory; included navigations are read recursively; unknown keys are
ignored; a mismatched value throws naming the set and member. It serves queries of full rows, includes, and reading
saved rows back after `SaveChangesAsync` (replacing `Deserialize` plus the `PropertyInfo` copy).

**Projections:** a materializer built from the `Select` lambda, sharing the translator's key naming:

| Expression in `Select` | Materialized as |
|---|---|
| scalar member or computed value | typed scalar read |
| `new { ... }` / record constructor (`NewExpression`) | the constructor, with materialized arguments |
| `new T { A = ..., B = ... }` (`MemberInit`) | the constructor, then member assignments |
| a navigation | the target set's row reader (not tracked) |
| `p.Lines`, `p.Lines.Select(...).ToList()` | a list of the element's materialization |

Constructors, members and `ToList` come from the developer's expression tree, which roots them, so invoking them via
`ConstructorInfo`/`MemberInfo` is AOT-safe. Creating a `List<T>` for a runtime element type is the one place the
analyzers can't follow; it lives in one helper with a justified `[UnconditionalSuppressMessage]`, and the smoke app
covers it (lists of a value type, of anonymous objects, nested). If that proves unreliable, `ToList()` element types
in projections are limited to reference types and primitives, with a clear local error.

### Tracking

`Accessors` (compiled getters) and every `PropertyInfo.GetValue`/`SetValue` give way to the binding's delegates:
snapshots, change detection, identity keys, foreign-key sync, writing saved values back and reading batch values.

### Guards

- `<IsAotCompatible>true</IsAotCompatible>` on Abstractions and Client: trim, AOT and single-file analyzers; with
  warnings as errors, new unannotated reflection fails the build.
- `tests/LinqToRest.AotSmoke`: a console app (`PublishAot=true`, `JsonSerializerIsReflectionEnabledByDefault=false`)
  with a context generated from `tests/golden/model.json`, answering HTTP from canned JSON through a stub handler. It
  runs filters and sorting, full rows with includes, every projection shape, cursor pages, and tracking with a save,
  and exits non-zero on any mismatch. CI publishes it with Native AOT on Linux and runs it.
- The Blazor sample drops its `TrimmerRootAssembly` stopgap; its trimmed publish stays in CI.

## Public API changes

- **Client (added):** `SetBinding<T>` (`Property`, `Values`, `Reference`, `Collection`), abstract `SetBinding`,
  `ClientModel.Bind(RestModel, params SetBinding[])`.
- **Client (annotated):** `ClientModel.Bind(RestModel, IReadOnlyDictionary<string, Type>)` with
  `[RequiresUnreferencedCode]` and `[RequiresDynamicCode]`.
- **Client (obsolete):** `RestDataContextOptions.SerializerOptions`.
- **Abstractions (added):** AOT-safe generic readers in `JsonValues`: `ReadList<TElement>` and `ReadScalar<T>`.
- **Generated C#:** binding chain instead of the type dictionary.

## Security notes

- The materializer stays strict (types, enums by name, nullability) and never instantiates types named by the
  response: only types from the binding or the developer's expression tree.
- No new inputs reach the server; the wire is unchanged.

## Testing

| Where | Covers |
|---|---|
| Client.UnitTests | `SetBinding` validation (missing, extra, mistyped bindings, enum mismatch) with the same messages as the `Type`-based `Bind`; row reader (unknown keys ignored, mismatches naming set and member, includes); projection materializer (anonymous, record constructor, `MemberInit`, scalar select, navigation, lists of values/rows/projections, nesting); parity tests over both `Bind` overloads |
| Existing client tests, golden bodies | Run on the generated `ShopContext`, exercising the new path; bodies byte-identical |
| CodeGen.UnitTests | C# snapshot of the binding chain |
| EndToEnd.Tests, Samples.Tests | Unchanged; generated clients use the new path |
| AotSmoke (CI) | Native AOT publish with zero warnings; scenarios with reflection JSON off |
| Benchmarks | Before/after `ClientMaterialization`, `ChangeDetection`, `ClientTranslation` (alternating runs) |

## Implementation notes

Where the implementation differs from the sections above, this list wins.

1. **Bindings name the CLR member too:** `Property("id", nameof(Product.Id), get, set)`. Queries name members in
   lambdas and validation messages name the property, and neither can be recovered from a delegate.
2. **Projections:** collections need `ToList()` or `ToArray()` (an `IEnumerable<T>`-typed `Select` has no concrete
   type to build) and get a local `NotSupportedException` otherwise. Lists are built by invoking the developer's own
   closed `Enumerable.ToList<T>` on an array of the elements, not by `Activator`: under Native AOT, `List<T>`'s
   constructor isn't kept for an anonymous `T`. That array comes from `MakeArrayType`, the one justified
   suppression (reference-type arrays share code; value-type arrays exist wherever `List<T>` does). The open
   question is closed: lists of anonymous objects, `int` and `string` work under Native AOT.
3. **`decimal` operators:** C# builds `decimal` comparisons and arithmetic in lambdas without naming the operator
   method, so System.Linq.Expressions looks it up by reflection, and Native AOT trims it with no warning. The client
   keeps `decimal`'s public methods (`[DynamicDependency]` on `CreateQuery<T>`). `DateTime`, `DateOnly` and `Guid`
   didn't need it. The smoke app found this and the list problem above; the analyzers can't see either.
4. **Trim warnings at the query site:** the compiler builds anonymous types with `Expression.New(…, members)` and
   object initializers with `Expression.Bind(MethodInfo, …)`, which System.Linq.Expressions marks
   `[RequiresUnreferencedCode]`. A trimmed app gets IL2026 where it writes such a projection; the smoke app suppresses
   it in its projection scenario and shows they work. Record constructors don't warn.
5. **Smaller changes:** the untyped `IQueryProvider.CreateQuery(Expression)` throws `NotSupportedException` (it
   needed `MakeGenericType`; the `Queryable` operators never call it). `AddRestDataContext<TContext>` annotates
   `TContext`'s public constructors. The evaluator compiles an object-returning lambda and keeps `new T { … }`
   intact (it used to fail on a parameterless initializer). A conversion to a nullable type in a projection
   accepts `null`. Reading saved values back only sets the members present in the result.
6. **Performance.** `main` and the branch alternated twice (DefaultJob), with a new `ClientMaterialization.Project`
   (anonymous projection) benchmark. Translation and change detection are unchanged (within ±3%). The first row
   reader was 45% slower than `JsonSerializer` and allocated almost twice as much: it built a JSON pointer string per
   member, allocated each property name, boxed every value and allocated in the enum reader. Members are now matched
   as UTF-8 (in model order first), pointers are built only for errors, `SetBinding.Property<TValue>` reads through
   the new `JsonValues.ReadScalar<T>` (no boxing for the common value types, checked against the `Type`-based reader
   by a parity test) straight into the typed setter, and a single enum name is checked with `Enum.IsDefined`. Full
   rows now take the same time as `main` (3.3/79/321 µs vs 3.4/80/320 µs for 1/50/200 rows) with 4% more
   allocation (enum values are still boxed). Projections are 7–16% faster from 50 rows and allocate 25% more; a
   one-row projection is about 11% slower (0.7 µs), because its reader is built from the `Select` lambda per query.

## Open questions

- ~~Are projections into `List<T>` of a value type reliable under Native AOT?~~ Yes (Implementation notes 2).
- A Roslyn source generator for the .NET client (backlog).

## Implementation steps

1. Abstractions: `IsAotCompatible`; generic AOT-safe `JsonValues` list readers; tests.
2. Client: `SetBinding<T>`, reflection-free `Bind`, shared internal binding representation, annotations on the
   `Type`-based `Bind`; tests.
3. Client: tracking through binding accessors (remove `Accessors` and `PropertyInfo` use); change-detection benchmark.
4. Client: row reader for full rows, includes and save write-back; tests; materialization benchmark.
5. Client: projection materializer; tests; `SerializerOptions` obsolete.
6. Client: `IsAotCompatible`; resolve remaining findings (non-generic `CreateQuery`, `Enum.ToObject`, evaluator).
7. CodeGen: emit the binding chain; snapshot; regenerate every generated C# file.
8. `tests/LinqToRest.AotSmoke` and its CI job; remove the Blazor stopgap.
9. Docs (README "Trimming and Native AOT", `dotnet-conventions` skill rule), roadmap; benchmark comparison.
