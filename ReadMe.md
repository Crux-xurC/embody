# embody

Helper modules that evaluate expressions in a language's REPL and print each result as one finite line between fixed markers — so a
program driving the REPL can read values, errors and timeouts without parsing the REPL's own output.

- **Target-agnostic**: the caller sends the expression as one line; embody uses only the language's standard environment
- **A result for every evaluation**: timeouts, exceptions and type errors are records too; a value that cannot be shown whole (a
  function, an infinite structure) gets a finite display and `repr=unsupported`
- **Giving values a body**: `repr=exact` says the display, read back as source, gives the same value — it can be recorded and compared

It is a component of [Xipe Totec](https://github.com/Crux-xurC/xipe_totec), which evaluates a project's functions as calc rows; other
tools can use it by the output contract. The whole ReadMe — loading each language, its protocol, the versions checked, the limits — is
[ReadMe.full.md](ReadMe.full.md).

What it looks like: [Xipe Totec's README](https://github.com/Crux-xurC/xipe_totec#readme) has a screenshot and a 3.5-minute video of an AI agent trying a library's functions over MCP, with tezcatl as the window.

## Files

| File | Language / REPL |
|---|---|
| `embody.ml` | OCaml (the `ocaml` toplevel) |
| `embody.hs` | Haskell (GHCi) |
| `embody.py` | Python (`python -i`) |
| `embody.fsx` | F# (F# Interactive, `dotnet fsi`) |
| `embody.scala` | Scala 3 (Scala's REPL) |
| `embody.cs` | C#, through F# Interactive: a seam for `embody.fsx` |
| `embody.java` | Java, through Scala's REPL: a seam for `embody.scala` |

Each file is loaded on its own; there is no build configuration. Xipe Totec finds this repository next to its own (`../embody/`, or
`XIPE_TOTEC_EMBODY_DIR`). Update the two together: Xipe Totec checks the protocol of `embody.scala`, `embody.cs` and `embody.java` and
names an older embody instead of waiting.

## The output contract

```
XT_BEGIN_<id>
repr=<exact|binary|unsupported>
type=<type>
compare=<none|same|changed|skipped>
display=<single-line value display>
XT_END_<id>
```

or `XT_ERROR_<id> timeout`, `XT_ERROR_<id> crash <one-line reason>`. Each record is one write. The markers, the field names and the
`key=value` lines are the protocol; everything else the REPL prints can be ignored. The fields in detail, when `compare` is
`skipped` in each language, and the auxiliary markers (`XT_CALLED_<id>`, `XT_READY` …): [ReadMe.full.md](ReadMe.full.md#shared-output-contract).

## License

MIT ([LICENSE](LICENSE)).
