# embody

Language-specific helper modules that evaluate expressions in an interactive environment (a REPL or toplevel) and print each result as a finite, single-line record surrounded by fixed markers.

- **Target-agnostic:** The caller chooses the expression by sending one line to the REPL. embody does not depend on the target module; it uses only the language's standard environment (the OCaml toplevel itself, or Haskell's `base`).
- **A result for each evaluation:** Timeouts, exceptions, and type errors are returned as marker-delimited records. Values that cannot be fully displayed, such as infinite structures and functions, receive a finite display and `repr=unsupported`.
- **Giving values a body:** A value may be useful to inspect on screen but still need a representation to be recorded in a database. The name refers to temporarily giving such otherwise invisible values a body.

embody was created as a component used by Xipe Totec, a tool that evaluates and records functions from functional-language projects as calculation rows. Other tools can use it by following the output contract below.

---

## Files

| File | Language / REPL | Status |
|---|---|---|
| `embody.ml` | OCaml (the standard `ocaml` toplevel) | Implemented; checked with OCaml 5.5.0 and dune 3.23.1 |
| `embody.hs` | Haskell (GHCi) | Implemented; checked with GHC 9.10.3 |

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
