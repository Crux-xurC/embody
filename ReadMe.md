# embody

Language-specific helper modules that evaluate expressions in an interactive environment (a REPL or toplevel) and print each result as a finite, single-line record surrounded by fixed markers.

- **Target-agnostic:** The caller chooses the expression by sending one line to the REPL. embody does not depend on the target module; it uses only the language's standard environment (the OCaml toplevel itself, Haskell's `base`, Python's standard library, or what comes with the .NET SDK).
- **A result for each evaluation:** Timeouts, exceptions, and type errors are returned as marker-delimited records. Values that cannot be fully displayed, such as infinite structures and functions, receive a finite display and `repr=unsupported`.
- **Giving values a body:** A value may be useful to inspect on screen but still need a representation to be recorded in a database. The name refers to temporarily giving such otherwise invisible values a body.

embody was created as a component used by Xipe Totec, a tool that evaluates and records functions from functional-language projects as calculation rows. Other tools can use it by following the output contract below.

---

## Files

| File | Language / REPL | Status |
|---|---|---|
| `embody.ml` | OCaml (the standard `ocaml` toplevel) | Implemented; checked with OCaml 5.5.0 and dune 3.23.1 |
| `embody.hs` | Haskell (GHCi) | Implemented; checked with GHC 9.10.3 |
| `embody.py` | Python (`python -i`) | Implemented; checked with Python 3.13.12 |
| `embody.fsx` | F# (F# Interactive, `dotnet fsi`) | Implemented; checked with the .NET 8 SDK 8.0.419 |
| `embody.scala` | Scala 3 (Scala's REPL, `dotty.tools.repl.Main`) | Implemented; checked with Scala 3.3.6 on JDK 21 and 27 |
| `embody.cs` | C#, through F# Interactive: a seam for `embody.fsx` | Implemented (the seam alone; Xipe Totec's C# adapter to come); checked with the .NET 8 SDK 8.0.419 |

Each file can be loaded on its own. This repository does not provide build configuration (flake, dune, cabal, and so on); the caller decides how to load the module.

---

## Shared output contract

Each evaluation prints one of the following records in a single write, so other output cannot interleave with it. The caller chooses `<id>`.

**Success**

```
XT_BEGIN_<id>
repr=<exact|binary|unsupported>
type=<type>
compare=<none|same|changed|skipped>
display=<single-line value display>
XT_END_<id>
```

| Field | Meaning |
|---|---|
| `repr` | `exact`: evaluating `display` as source in the same language reconstructs the value, so it can be embedded in another expression and compared. `binary`: the value cannot be reconstructed from source text but can be stored as bytes; a `bin_base64=` field is included. `unsupported`: it cannot be reconstructed (for example, a function, cycle, or abstract value); only its display is recorded. |
| `type` | The type at the language's own level of detail (for example, OCaml's `Geometry.shape list`). |
| `compare` | Comparison with a previous value, when supplied. It is `none` when no previous value was supplied and `skipped` when either value is not `exact`. |
| `display` | A one-line display of the value. Newlines are represented by the two characters `\n`. |

**Failure**

```
XT_ERROR_<id> timeout
XT_ERROR_<id> crash <one-line reason>
```

A `crash` reason is the language's own text, such as an exception, type error, or syntax error.

The marker prefixes (`XT_BEGIN_`, `XT_END_`, `XT_ERROR_`), field names, and one-line `key=value` format are protocol and must not change. A caller can ignore other REPL output, such as prompts, and parse only the markers for its own `<id>`.

---

## OCaml (`embody.ml`)

Load this into the standard `ocaml` toplevel; `utop` is not supported. It uses the toplevel's own `Toploop`. Loading `ocamlcommon.cma` would introduce a separate compiler instance and prevent errors from being reported with the active toplevel's diagnostics.

**Loading** (place these commands in an `ocaml -init <init-file>` file):

```ocaml
#directory "+compiler-libs";;
#directory "+unix";;
#load "unix.cma";;
#mod_use "<path-to-embody>/embody.ml";;
Embody.dune_deps "<project-root>";; (* dune projects only *)
Embody.load_all ~aliases:[("Mylib", ["Stats"; "Words"])] "<working-directory>" ["<a.ml>"; "<b.ml>"];;
```

| Function | Purpose |
|---|---|
| `Embody.run id var timeout_ms text prev` | Evaluates `text` (for example, `let <var> = <call>;;`) in the toplevel and converts its printed `val <var> : <type> = <value>` line to the output contract. `prev` is the previous value as source (`Some "…"` or `None`). |
| `Embody.load_all ?aliases dir paths` | Loads each target `.ml` file as a module named after the file. Top-level expressions and `let ()` / `let _` are blanked in sanitized copies under `dir` (newlines are preserved for source locations), so those effects do not run. Files that fail to load are retried after their dependencies. `aliases` defines wrapped dune library aliases (for example, `module Mylib = struct module Words = Words … end`) once their members are available. Failed files produce `XT_LOAD_FAILED <path>` and a reason. |
| `Embody.dune_deps root` | Runs `dune ocaml top` at `root` and loads only dependency `#directory` / `#load` directives, excluding the project's own library directives. The target's source is loaded by `load_all`. It uses the toplevel environment, including `OCAMLPATH` and dune; dune builds the project. |

**Example**

```ocaml
Embody.run "c1" "xt_c1" 5000 "let xt_c1 = Geometry.area (Geometry.Circle 2.);;" None;;
```

```
XT_BEGIN_c1
repr=exact
type=float
compare=none
display=12.
XT_END_c1
```

- `repr` is `exact` when evaluating the displayed value again with `=` produces the same value; otherwise it is `unsupported` (for example, `<fun>`, `<cycle>`, `<abstr>`, or inline-record displays). OCaml does not yet produce `binary`.
- The time limit uses `Unix.setitimer` and SIGALRM. There is no memory limit; a hard hang requires stopping the toplevel process.
- Warnings printed before the value line (for example, an unlabelled call to a labelled argument or a deprecated function) are ignored while the `val <var> : …` line is read. (2026-10-16; previously, such a successful evaluation could be reported as `crash`.)
- SIGINT interrupts the current phrase (`Interrupted.`) and leaves the toplevel available for more input.
- Later evaluations in the same REPL session can refer to an earlier result by its bound variable (for example, `xt_c1`).

**Known limitations:** Files that require ppx preprocessing; projects with duplicate source filenames; and a single `dune` file declaring multiple libraries (aliases are not generated for that case).

**Out of scope** (these violate the “always returns a record” expectation, though they are unlikely in Xipe Totec's current test projects; crux, 2026-10-16):

- If `load_all` cannot open a target file or write its sanitized copy, it raises OCaml's `Sys_error` instead of printing `XT_LOAD_FAILED`. This happens during loading and appears to the caller as a startup failure.
- SIGALRM may arrive in the microseconds between evaluation finishing and the timer being stopped, causing `Timeout` to escape without a marker.
- If a type contains ` = ` (for example, a first-class module type such as `(module S with type t = int)`), the output parser splits at the first ` = `; `type=` and `display=` can be misaligned. Such values are already `unsupported`.

---

## Haskell (`embody.hs`)

Load this into GHCi. It depends only on `base` and was checked with GHC 9.10.3.

**Starting GHCi**

```
ghci -v0 -fdefer-type-errors -Wno-deferred-type-errors -Wno-deferred-out-of-scope-variables \
     <path-to-embody>/embody.hs <target .hs files ...> -i<source-directory>
:module + *Target ...
```

With `-v0`, GHCi prints neither a banner nor a prompt. Load the target sources and bring their modules into scope with `*Target`; even unexported functions can then be called as `Target.f`. `main` is not run.

**Evaluating one entry** (`-fdefer-type-errors` applies to `let` bindings, so each expression used for classification is bound with `let`):

```haskell
let xt_<id> = Target.f (x)
let xt_<id>_w = Embody.whnf xt_<id>
let xt_<id>_x = Embody.exact <previous: Nothing | Just "…"> xt_<id>
let xt_<id>_s = Embody.shown xt_<id>
let xt_<id>_t = Embody.typed xt_<id>
Embody.run "<id>" <timeout-in-microseconds> xt_<id>_w xt_<id>_x xt_<id>_s xt_<id>_t
```

- `run` tries, in order: `whnf` (whether the call runs), `exact` (the value has `Read`, `Show`, and `Eq`, and `show` can be read back to an equal value), `shown` (only `Show`; truncated to 2,000 characters, so infinite structures can still be displayed), then the type (`Typeable`). If an instance is missing, the corresponding `let` produces a deferred runtime type error and that stage is skipped.
- Exceptions and call type errors become `crash` with GHC's message. Timeouts use `System.Timeout.timeout`, which can interrupt interpreted loops.
- SIGINT interrupts the current evaluation (`Interrupted.`) and GHCi continues reading.
- There is no memory limit. GHC's `enableAllocationLimit` limits total allocation, not live memory, so it is not used here.
- `cut :: [a] -> [a]` takes the first 100 list elements. It can be used in an observation row; the resulting list has `Read`, can be `exact`, and can be compared with a previous result.
- Functions returning `IO` are not executed as IO actions; their type is displayed. Approximate floating-point comparison is also unavailable; comparison uses `==`.

**Out of scope:** An exception whose own message throws (for example, `error (show (1 `div` 0))`) and values whose `Show` implementation throws while producing text. An exception during error formatting can truncate the marker to `XT_ERROR_<id> crash `; the remaining output is GHCi's `*** Exception` message. The output is therefore not guaranteed to be a complete record in these cases (crux, 2026-10-16).

---

## Python (`embody.py`)

Load this into `python -i`. It uses only the standard library and was checked with Python 3.13.12.

**Starting Python** (an init script given to `python -i -q -u <init.py>`, with `PYTHONDONTWRITEBYTECODE=1` so nothing is written into the target):

```python
import sys
sys.path.insert(0, "<path-to-embody>")
import embody as _xt_embody
del sys.path[0]
_xt_embody.install({"calc": "/project/calc.py", "geo.shape": "/project/src/geo/shape.py"})
```

`install` puts an import hook first on `sys.meta_path` for the named modules. Importing one of them runs only its `def`, `async def`, `class`, `import` and `from … import` statements, assignments of literal values (`FACTOR = 3`), aliases (`comp = compose`, `join = os.path.join`: a name or a chain of attributes, no call), and `try` blocks made of those; other top-level statements (`print(…)`, `if __name__ == "__main__":`, `X = f()`) do not run. Imports between the target's own modules go through the same hook. Other packages import as usual.

**Evaluating one entry** (one line):

```python
_xt_embody.run("<id>", "xt_<id>", <timeout-in-milliseconds>, "<module>", "<call>", <previous: None | ("exact", "…") | ("binary", "<base64>")>)
```

- `run` imports `<module>`'s top-level package, then `<module>` (so a package whose `__init__.py` fails gives that error in every entry), binds its top-level package name in `__main__`, and evaluates `<call>` (text, compiled inside `run`, so an argument that does not parse is a `crash` with `SyntaxError` instead of the REPL waiting for more input). The value is bound to `xt_<id>` in `__main__` whatever its repr.
- `repr`: `repr()` read back with `ast.literal_eval` and equal → `exact`; otherwise a `pickle` that loads back equal → `binary` (`bin_base64=`); otherwise `unsupported`. Equality requires the same types, compares floats within `1e-9` and containers element by element; a value with a cycle is compared by its pickle bytes.
- Exceptions become `crash` with the exception's type and message. Timeouts use SIGALRM (`signal.setitimer`), which stops loops running Python bytecode.
- SIGINT raises `KeyboardInterrupt` in the current evaluation and the REPL continues reading.
- There is no memory limit (Python can only limit the whole process).

**Out of scope:** A loop inside C code (for example, `sum(itertools.count())`) is stopped neither by SIGALRM nor by SIGINT; the caller must end the process. The value is judged (`repr`, `pickle`) outside the time limit. An `async def` function returns its coroutine, which is not awaited (`unsupported`).

---

## F# (`embody.fsx`)

Load this into `dotnet fsi --quiet`. It uses only what comes with the .NET SDK (FSharp.Core, System.Text.Json, and FSharp.Compiler.Service for `Embody.Load`) and was checked with the .NET 8 SDK 8.0.419. A project is built first (for example `dotnet build -o <dir>`) and its assembly referenced; scripts are loaded through definitions-only copies (below).

**Starting F# Interactive** (an init script given to `dotnet fsi --quiet --use:<init.fsx>`; embody first, so the entries still end in records when the target is missing):

```fsharp
#load "<path-to-embody>/embody.fsx"
Embody.setup "<the build's log, or empty>";;
#r "<dir>/Target.dll";;
Embody.reference "<dir>/Target.dll";;
```

`Embody.reference` names a `#r`-ed assembly: fsi loads it only when a submission first uses it, so `finish` loads the named ones before it diagnoses a call that did not compile (the same assembly fsi loads), or a call failing before any other has run would find no module.

**Loading scripts without running their top level.** `#load` runs a script's top level. `Embody.Load.write "<root>" "<out dir>" ["scripts/tool.fsx"; …]` (run once, for example by `dotnet fsi --exec`) writes a copy of each script under the out dir, at the same relative path, in which only its definitions are left and every other top-level declaration is replaced by spaces (lines and columns kept): `let` functions; `let` values that run nothing (a literal, a name or a chain of names, a tuple, list or array of those, a lambda); types, exceptions, `open`, module abbreviations; nested modules and namespaces read the same way; hash directives except `#r "nuget: …"`. A script that does not parse prints `XT_LOAD_ERROR <path> <reason>`. Load each copy in a submission of its own (an init script stops at its first failing submission; stdin goes on):

```fsharp
Embody.trying "scripts/tool.fsx";;
#load "<out dir>/scripts/tool.fsx"
Embody.loaded "scripts/tool.fsx";;
```

`loaded` counts the script only when fsi compiled it (fsi does not compile a second time a file another script already `#load`-ed and failed on); the scripts tried and not loaded are named in the reason of a call to a module that is missing.

**Evaluating one entry** (three submissions, each ended by `;;`):

```fsharp
Embody.start "<id>" <timeout-in-milliseconds> <None | (Some ("exact", "…")) | (Some ("binary", "<base64>"))>;;
let xt_<id> = Embody.call (fun () -> Ns.Module.f (a) (b));;
Embody.finish "<id>" "Ns.Module" "f" <number-of-arguments>;;
```

A type's static member is called the same way, as a method: `let xt_<id> = Embody.call (fun () -> Ns.Type.M((a), (b)));;` and `Embody.finish "<id>" "Ns.Type" "M" <number-of-arguments>;;`.

- F# Interactive cannot evaluate a string from inside a script, so the call is compiled by F# Interactive itself. A call that does not compile leaves `xt_<id>` unbound; its diagnostic goes to F# Interactive's own output (it cannot be captured), and `finish` prints a `crash` with what reflection can tell, in F#'s terms: no such module (with the build log's first error, or the scripts that did not load), a `private` or `internal` function, a value and not a function, how many curried arguments it takes (`let f a b` 2, `let f (a, b)` and `let f ()` 1), or its signature (`add : int -> int -> int`); for a type's static member (no module of that name, a type of it): the member is missing, `private` or `internal`, a static property (a value), how many parameters its public overloads take, or their signatures (`Show(int) : string; Show(float, int) : string`, one parenthesized group per curried group). `;;` does not end an open string, comment or bracket: the caller must not send such an argument (Xipe Totec checks it and sends `Embody.fail "<id>" "<reason>";;` instead).
- `call` runs the call on a thread of its own under `ControlledExecution.Run` with the time limit: a running loop is aborted and F# Interactive reads on. A native wait (`Thread.Sleep`, I/O) is aborted only when it returns; `call` stops waiting at the limit plus 0.5 s and reports `timeout`, leaving that background thread. The value is bound to `xt_<id>` (the type's default value after a crash or a timeout). Because the call is stopped where it is, what it was changing may be left half done: the caller should start a new session after a `timeout`.
- `repr`: `exact` when `embody.fsx`'s own F# literal writer covers the value and its reader reads the text back to an equal value — bool, char, string, the integer types with their suffixes (`1L`, `1uy` …), float, float32, decimal, unit, enums by name (`Ns.Color.Red`), 1-dimensional arrays (`[|1; 2|]`), F# lists, `List<T>` (`ResizeArray [1; 2]`), `Dictionary<K, V>` with the default comparer (`System.Collections.Generic.Dictionary(dict [("a", 1)])`), options, tuples and struct tuples up to 7 elements, F# records (`{ Ns.Point.X = 1.0; Y = 2.0 }`) and unions (`Ns.Shape.Rect (2.0, 3.0)`, `Ns.Shape.Empty`) with public representations, and `null`. Otherwise `binary` through System.Text.Json (`IncludeFields`) when serialize → deserialize → serialize gives the same text and every instance field is public or a public property's backing field; `bin_base64=` is the type's assembly-qualified name, a newline and the JSON. Otherwise `unsupported`. `display` is the literal for `exact` values, else the type's own `ToString()` when it overrides it, else F#'s `%A`.
- `compare`: an `exact` previous value is read back as the new value's type (types equal, floats within `1e-9`, containers, records and unions element by element); a `binary` one compares by its type name and JSON text.
- `type=` is F#'s name: `int`, `int[]`, `int list`, `string option`, `struct (int * string)`, `int -> int` for a function, a union's own type (not its case's class), else the .NET name with its namespace (`Sample.Point`, `System.Collections.Generic.List<int>`); a type F# Interactive compiled loses its `FSI_0002.` prefix.
- SIGINT makes F# Interactive print `- Interrupt` and read on with its bindings. There is no memory limit.

**Helpers: another .NET language's values, through a seam.** `embody.fsx` stays F#'s alone; a small helper written for another language (for C#, `embody.cs`, below) registers itself after embody is loaded, with `Embody.register { Embody.Helper.Covers = …; Head = …; Parts = …; Split = …; Make = …; Diagnose = … }`, every field a `Func` over .NET's own types, so a helper in any .NET language can fill it; or, for a helper written as a class with public static methods of those names and types, `Embody.registerType typeof<Ns.Class>`. embody asks the helpers only where it has no answer of its own, the first registered first; a helper that throws gives no answer.

- `Covers(type)`: whether values of that type are the helper's
- `Head(type)`, `Parts(type)`, `Split(value)`, `Make(type, args)`: such a value is written as a call of its constructor, `Head(arg, …)` (`Sample.Point(2.0, 3.0)`), the arguments in the order of `Parts`, each written by embody's own writer (a number, a string, a `List` inside is the literal it always is; a covered value inside is written the same way), so the text is F# a later row takes as it is. It is read back by embody's reader (the head, then each argument as its type) and made with `Make`; `exact` and `compare` follow, two such values being equal argument by argument (a `List` inside element by element). A value `Split` gives `null` for is not written: `binary` or `unsupported` as before. A head must be a dotted name (no type arguments)
- `Diagnose(module, name, count)`: why a call that did not compile failed, in the helper's language's terms; `null` when the module is not its own (embody's F# diagnosis follows)

**Out of scope:** a native wait longer than the time limit keeps its thread running after `timeout`; the value is judged (`display`, JSON) outside the time limit; private state reached only through a nested object is not seen by the `binary` check; a script whose definitions use a value its top level computes does not load from its copy.

---

## C# through F# Interactive (`embody.cs`)

C# text cannot be evaluated in the .NET SDK (Roslyn's scripting is not in it), so C# calls are compiled by F# Interactive with `embody.fsx` (above), and `embody.cs` is the **seam**: C#'s meaning brought to F# on the side that receives the result. The arguments stay F# expressions. It uses only the framework, works by reflection over what is loaded (it does not reference the target), and was checked with the .NET 8 SDK 8.0.419.

**Building it**: a class library of its own, outside the target, compiling this one file, for example in a temp dir:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <AssemblyName>EmbodyCs</AssemblyName>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup><Compile Include="<path-to-embody>/embody.cs" /></ItemGroup>
</Project>
```

`dotnet build <dir> --source <an empty directory> -o <out>` builds it without fetching anything (about 2 s). `EmbodyCs.Seam.Protocol` (today `1`) is the version of what follows; a caller checks it before starting (`public const int Protocol = 1;` in the file).

**Starting** (after `embody.fsx`'s own lines, with the target built and referenced the same way):

```fsharp
#r "<out>/EmbodyCs.dll";;
Embody.registerType typeof<EmbodyCs.Seam>;;
```

**Evaluating one entry**: `embody.fsx`'s three submissions, the call being C#'s static method as F# calls it, `Ns.Type.Method((a), (b))`. A method returning `Task`, `Task<T>`, `ValueTask` or `ValueTask<T>` is called inside `EmbodyCs.Seam.Await(…)`, so the value is the `T` (a `Task` gives `()`); the wait is on the call's own thread, under embody's time limit.

- `repr`: a public, non-generic C# record whose public properties are exactly its primary constructor's parameters is `exact`, written as that constructor's call with F# literals inside (`Sample.Geo.Point(2.0, 3.0)`, `Sample.Bag("x", ResizeArray [0; 1])`): F# a later row takes as it is. It is read back by `embody.fsx`'s reader, and `compare` goes property by property (a `List` inside element by element, where C#'s own `==` compares the list by reference). Other records (state set outside the primary constructor, generic ones) and classes are `binary` or `unsupported` as `embody.fsx` judges them; an anonymous type is `unsupported` (`display` its own `{ N = 1, S = 1 }`)
- Failures in C#'s terms, for a type of a C# assembly (F#'s diagnosis for anything else): not public; no such method; a property, not a method; an instance method (`only public static methods are called`, with its signature); `private`, `internal` or `protected`; how many arguments the public static overloads take (`out` parameters left out, optional ones may be, `params` any), with their C# signatures (`static string Over(int x); static string Over(string s)`); else the arguments do not fit those signatures (fsi's message is in the REPL output)

**Out of scope:** C# text as an argument (the arguments are F#); a generic method whose type parameter appears in no parameter is called by F# as `obj` (the caller must not send it: Xipe Totec refuses such a row); `out` and `ref` parameters are F#'s (an `out` comes back in a tuple); records with type arguments, and positional records with extra properties set outside the constructor, are not written as calls.

---

## Scala (`embody.scala`)

Load this into Scala 3's REPL. It uses only what the REPL itself carries: the Scala 3 library, the JDK, and the compiler on the REPL's class path (its parser for `Embody.Load`, its `dotty.tools.repl.ScriptEngine` for reading a literal back). It was checked with Scala 3.3.6 on JDK 21 and 27. Start the REPL with the target on the JVM's class path too, so the ScriptEngine reads the target's values back:

```
java -cp "<compiler and library jars of the project's Scala version>:<the project's class path>" dotty.tools.repl.Main -usejavacp
```

**Starting** (REPL inputs, one per line; embody first, so the entries still end in records when the target is missing):

```scala
:load <path-to-embody>/embody.scala
Embody.setup("<the build's log, or empty>")
Embody.ready()            // prints XT_READY: a REPL without embody never looks ready
```

`Embody.protocol` (today `1`) is the version of these inputs; Xipe Totec checks it before starting the REPL, so an embody older than the adapter is named instead of never looking ready.

**Loading scripts without running their top level.** `:load` runs a script's top level. `Embody.Load.write("<root>", "<out dir>", List("scripts/tool.sc", …))` writes a copy of each script under the out dir, at the same relative path, holding only its definitions inside an object named by its path without `.sc` (`tool` for `tool.sc`, `` `scripts/tool` `` for `scripts/tool.sc`: scripts of one file name in different directories stay apart; called as `` `scripts/tool`.f(…) ``) and a last line `Embody.markLoaded("<path>")`: `def`s; `val`s that run nothing (a literal, a name or a chain of names, a tuple or a standard collection of those, a lambda); classes, traits, objects, enums, type aliases, givens, extensions; `import`s and `export`s. `println(…)`, `val n = f()` and `//> using` directives (comments: nothing is fetched) are left out. A script that does not parse prints `XT_LOAD_ERROR <path> <reason>`. Then, per script:

```scala
Embody.trying("scripts/tool.sc")
:load <out dir>/scripts/tool.sc
```

**Evaluating one entry** (three REPL inputs, each a complete line):

```scala
Embody.start("<id>", <timeout-in-milliseconds>, <None | Some(("exact", "…")) | Some(("binary", "<base64>"))>)
val xt_<id> = Embody.call(() => pkg.Obj.f((a), (b))((c)))
Embody.finish("<id>", "pkg.Obj", "f", List(2, 1))
```

- The REPL compiles the call. A call that does not compile leaves `xt_<id>` unbound and the compiler's message on the REPL's output, and `finish` prints a `crash` with what reflection can tell, in Scala's terms: no such object (with the build log's first `[error]`, `XT_BUILD_ERROR` or `XT_BUILD_NOTE` line, or the scripts that did not load), no such function, a `private` one, how many arguments it takes, its signature. The last argument of `finish` is the number of arguments per parameter list. The REPL waits silently for the rest of an unclosed bracket, string or comment: the caller must not send such an argument (Xipe Totec checks it and sends `Embody.fail("<id>", "<reason>")` instead).
- `call` runs the call, and the value's display, on a thread of its own and waits for the time limit. The JVM cannot stop a running thread: at the limit the thread is interrupted (a sleep or a wait ends) and left running, and the entry is a `timeout`; the caller should start a new session. The value is bound to `xt_<id>` (`null`, the type's default, after a crash or a timeout). When the call returns, `XT_CALLED_<id>` is printed before the value is judged.
- `repr`: `exact` when `embody.scala`'s own literal writer covers the value and the ScriptEngine evaluates the text back to an equal value — `Int`, `Long` (`1L`), `Short` and `Byte` (`(1: Short)`), `Double` (`Double.NaN` …), `Float` (`1.5f`), `Char`, `Boolean`, `Unit`, `String`, `BigInt`, `BigDecimal`, `Option`, `Either`, tuples, `List`, `Vector`, `Set`, `Map`, arrays (`Array(1, 2)`, `Array.empty[Int]`), mutable `ArrayBuffer`, `ListBuffer`, `HashMap`, `HashSet`, case classes with their package (`sample.Point(2.0, 3.0)`), Scala 3 and Java enums (`sample.Color.Red`), objects, and `null`. Otherwise `binary` through Java serialization when it reads back to an equal value (`bin_base64=` the serialized bytes); otherwise `unsupported` (functions, `LazyList`, a class without structural equality). `display` is the literal for `exact` values, else `toString` (`<function>` for a function).
- `compare`: an `exact` previous value is evaluated by the ScriptEngine and compared (floats within `1e-9`, containers and case classes element by element); a `binary` one is deserialized and compared the same way.
- `type=` is the running value's, as Scala writes it: `Int`, `List[Int]`, `Map[String, Int]`, `(Int, String)`, `Option[?]` for `None`, `sample.Point`; erasure leaves the static type unknown.
- SIGINT ends Scala's REPL. There is no memory limit.

**Out of scope:** the REPL's own echo of `val xt_<id> = …` runs `toString` once more outside the time limit (only on a value whose display finished within it); a value of a class defined in the REPL or a script cannot be read back by the ScriptEngine (it is `binary` or `unsupported`); a script whose definitions use a value its top level computes does not load from its copy; Scala 2.

---

## Adding support for another language

Add an embody module when the target REPL needs a way to return a finite one-line value representation and say whether it can be read back. The following have been validated against real OCaml and Haskell REPLs:

1. **Remain target-agnostic.** The caller sends the expression as text and the REPL interprets it. Do not add a compile-time dependency on target modules; that would require rebuilding for each target and still would not reveal arbitrary types.
2. **Use the language's standard environment.** External packages may not be installed in the target project.
3. **Return a marker record.** Exceptions, type and syntax errors, missing instances, and infinite loops should all produce one marker-delimited line. Prevent the REPL from waiting for the rest of an incomplete expression (OCaml receives the call as text; Haskell binds expressions with `let`).
4. **Print each record in one write** and flush it so other output cannot interleave.
5. **Apply a per-entry timeout** and verify that even a loop without allocation can be interrupted.
6. **Mark only reconstructible values as `exact`.** Evaluate the displayed source and compare it with the original. For values that cannot be fully displayed (infinite structures, functions, cycles), return a finite display or type with `repr=unsupported`.
7. **Bind previous results** at the REPL top level so later evaluations in the same session can refer to them as `xt_<id>`.

Before implementing an adapter, connect to the target REPL through a pipe and check startup, failures, timeouts, and whether SIGINT stops only the current evaluation. Xipe Totec's `language adapter guide` §2.2 describes the procedure and examples.

After adding a language, update this file's language table and the corresponding section with loading instructions, the evaluation protocol, tested versions, and limitations.

---

## Used by

- **Xipe Totec:** On every evaluation pass, it loads the language's embody module into the REPL and wraps each call with it. By default, Xipe Totec looks for embody in the sibling `../embody/` directory; `XIPE_TOTEC_EMBODY_DIR` overrides that location.

---

## License

MIT. See [LICENSE](LICENSE).
