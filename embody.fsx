// embody.fsx -- evaluate one F# call inside F# Interactive (`dotnet fsi`) and print one marker record.
//
// Loaded into `dotnet fsi --quiet` by the caller (Xipe Totec's F# adapter) from an init script, before
// the target (its built assembly, or its scripts with only their definitions -- `Embody.Load` below):
//
//     #load "<path-to-embody>/embody.fsx"
//     Embody.setup "<build log, or empty>";;
//     #r "<the project's built assembly>.dll"
//     Embody.reference "<the same path>";;
//
// and then, three submissions per evaluation:
//
//     Embody.start "<id>" <timeout ms> <None | Some ("exact", "...") | Some ("binary", "<base64>")>;;
//     let xt_<id> = Embody.call (fun () -> Ns.Module.f (a) (b));;
//     Embody.finish "<id>" "Ns.Module" "f" <number of arguments>;;
//
// A seam for another .NET language (`embody.cs` for C#) registers a `Helper` after this file is loaded
// (`Embody.register`): its values are written as constructor calls and its failures told in its terms.
//
// The call is compiled by fsi itself; when it does not compile, `xt_<id>` stays unbound and `finish`
// reports a crash (fsi's own diagnostic goes to the REPL's output, outside the markers). Only what comes
// with the .NET SDK is used (FSharp.Core, System.Text.Json, FSharp.Compiler.Service for `Load`). See
// ReadMe.md, "F# through F# Interactive (`embody.fsx`)".

module Embody

#r "FSharp.Compiler.Service"
#nowarn "44"   // ControlledExecution is marked obsolete; it is what fsi itself uses to interrupt

open System
open System.Collections
open System.Collections.Generic
open System.Globalization
open System.Reflection
open System.Runtime.CompilerServices
open System.Text
open System.Text.Json
open System.Threading
open Microsoft.FSharp.Reflection

// --- state of the entry under way ----------------------------------------------------------------

type private Outcome =
    | NotRun
    | Value of obj * Type
    | Crash of string
    | TimedOut

type private Entry =
    { Id: string
      TimeoutMs: int
      Basis: (string * string) option
      mutable Outcome: Outcome }

let private out = Console.Out
let mutable private current: Entry option = None
let mutable private buildLog = ""

let private write (text: string) =
    lock out (fun () ->
        out.Write text
        out.Flush())

let private oneLine (text: string) =
    text.Replace("\r", "\\r").Replace("\n", "\\n")

/// The build's log (read only to explain a call that did not compile); "" for none.
let setup (log: string) = buildLog <- log

let private references = Generic.List<string>()

/// The caller `#r`-ed this assembly. fsi loads a referenced assembly only when a submission first uses it,
/// so a call that does not compile before any other has run would find no type in it; `finish` loads the
/// references first (the same assembly fsi loads: the default load context, by path).
let reference (path: string) = references.Add path

let private loadReferences () =
    for p in references do
        try Assembly.LoadFrom p |> ignore with _ -> ()

let private tried = Generic.List<string>()
let private done' = Generic.HashSet<string>()

/// A script about to be loaded (`#load` of its definitions-only copy, `Load` below) ...
let trying (script: string) = tried.Add script
/// ... and loaded: the same submission as its `#load`, so it does not run when the script fails to
/// compile. fsi does not compile a file a second time (a script another one `#load`-ed and failed on), so
/// what counts is the start-up type fsi made for it (`<StartupCode$FSI_0002>.$FSI_0002_Tool$fsx`).
let loaded (script: string) =
    let stem = IO.Path.GetFileNameWithoutExtension script
    let mark = "_" + (if stem = "" then stem else string (Char.ToUpperInvariant stem.[0]) + stem.Substring 1) + "$fsx"
    let made =
        AppDomain.CurrentDomain.GetAssemblies()
        |> Seq.filter (fun a -> a.GetName().Name.StartsWith "FSI")
        |> Seq.exists (fun a ->
            (try a.GetTypes() with :? ReflectionTypeLoadException as e -> e.Types |> Array.filter (isNull >> not))
            |> Array.exists (fun t -> t.Name.EndsWith mark))
    if made then done'.Add script |> ignore

// --- type names --------------------------------------------------------------------------------------

let private primitiveNames =
    dict
        [ typeof<bool>, "bool"; typeof<char>, "char"; typeof<string>, "string"; typeof<sbyte>, "sbyte"
          typeof<byte>, "byte"; typeof<int16>, "int16"; typeof<uint16>, "uint16"; typeof<int>, "int"
          typeof<uint32>, "uint32"; typeof<int64>, "int64"; typeof<uint64>, "uint64"; typeof<float32>, "float32"
          typeof<float>, "float"; typeof<decimal>, "decimal"; typeof<unit>, "unit"; typeof<obj>, "obj" ]

// a type fsi compiled from a submission or a #load-ed script: `FSI_0002.Tool+Point` is `Tool.Point`
let private sourceName (name: string) =
    let n = Text.RegularExpressions.Regex.Replace(name, @"^FSI_[0-9]+\.", "")
    n.Replace('+', '.')

let private generic (d: Type) (t: Type) = t.IsGenericType && t.GetGenericTypeDefinition() = d

// a union's case is a subclass of the union (`Shape+Rect`); a closure one of `FSharpFunc<_, _>`
let rec private declared (t: Type) =
    if isNull t then t
    elif not (isNull t.BaseType) && t.BaseType <> typeof<obj> && FSharpType.IsUnion(t.BaseType) && FSharpType.IsUnion(t) then t.BaseType
    elif not (isNull t.BaseType) && t.BaseType.IsGenericType && t.BaseType.GetGenericTypeDefinition() = typedefof<FSharpFunc<_, _>> then t.BaseType
    else t

let rec private typeName (t: Type) : string =
    let t = declared t
    if isNull t then "null"
    elif primitiveNames.ContainsKey t then primitiveNames.[t]
    elif t.IsArray then typeName (t.GetElementType()) + "[]"
    elif t.IsGenericType then
        let args = t.GetGenericArguments() |> Array.map typeName
        let d = t.GetGenericTypeDefinition()
        if d = typedefof<FSharpFunc<_, _>> then
            // `(int -> int) -> int`: a function argument in parentheses
            let a0 = t.GetGenericArguments().[0]
            (if generic typedefof<FSharpFunc<_, _>> a0 then "(" + args.[0] + ")" else args.[0]) + " -> " + args.[1]
        elif d = typedefof<list<_>> then args.[0] + " list"
        elif d = typedefof<option<_>> then args.[0] + " option"
        elif t.FullName <> null && t.FullName.StartsWith "System.Tuple`" then "(" + String.Join(" * ", args) + ")"
        elif t.FullName <> null && t.FullName.StartsWith "System.ValueTuple`" then "struct (" + String.Join(" * ", args) + ")"
        else
            let n = sourceName (if isNull d.FullName then d.Name else d.FullName)
            let n = match n.IndexOf '`' with -1 -> n | i -> n.Substring(0, i)
            n + "<" + String.Join(", ", args) + ">"
    else sourceName (if isNull t.FullName then t.Name else t.FullName)

// a type's name without its type arguments, for a record's first field or a union's case
let private bareName (t: Type) =
    let n = typeName t
    match n.IndexOf '<' with -1 -> n | i -> n.Substring(0, i)

let private isRecord (t: Type) = FSharpType.IsRecord(t)
// lists and options are unions too, written their own way above them
let private isUnion (t: Type) =
    FSharpType.IsUnion(t) && not (t.IsGenericType && (t.GetGenericTypeDefinition() = typedefof<list<_>> || t.GetGenericTypeDefinition() = typedefof<option<_>>))

// --- helpers: another language's values, through a seam --------------------------------------------
//
// A seam (another .NET language's own helper, such as `embody.cs` for C#) registers itself at the start
// with `register`. embody.fsx knows nothing of that language: it asks the helpers only where its own
// writer, reader, equality and diagnosis have no answer. A helper describes a value it covers as a call
// of a constructor, `Head(arg, …)`: embody.fsx writes the arguments with its own writer (so a number, a
// string or a list inside is the F# literal it always is, and a covered value inside another one is
// written the same way), reads them back with its own reader, and compares two such values argument by
// argument. Only .NET's own types cross, so a helper written in any .NET language can fill it.

/// What a seam gives embody.fsx. Every function may throw; a throw is taken as "no answer".
type Helper =
    { /// Whether values of this type are the helper's
      Covers: Func<Type, bool>
      /// The constructor as written in a call: `Sample.Point`
      Head: Func<Type, string>
      /// The constructor's parameter types, in order
      Parts: Func<Type, Type[]>
      /// A value's constructor arguments, in order; null when it cannot be written as a call
      Split: Func<obj, obj[]>
      /// The value the constructor makes from these arguments
      Make: Func<Type, obj[], obj>
      /// Why `module.name` with this many arguments did not compile, in the helper's language's terms;
      /// null when the module is not the helper's
      Diagnose: Func<string, string, int, string> }

let private helpers = Generic.List<Helper>()

/// Register a helper; the ones registered first are asked first.
let register (h: Helper) = helpers.Add h

/// Register the helper a type's public static methods make, one per field of `Helper` with the same name,
/// parameters and result (`Covers(Type) : bool`, …): a helper written as a class in its own language
/// (`Embody.registerType typeof<EmbodyCs.Seam>`).
let private staticDelegate<'D when 'D :> Delegate> (t: Type) (name: string) : 'D =
    match t.GetMethod(name, BindingFlags.Public ||| BindingFlags.Static) with
    | null -> failwithf "%s has no public static %s" t.FullName name
    | m -> Delegate.CreateDelegate(typeof<'D>, m) :?> 'D

let registerType (t: Type) =
    let f name = staticDelegate t name
    register
        { Covers = f "Covers"; Head = f "Head"; Parts = f "Parts"; Split = f "Split"; Make = f "Make"
          Diagnose = f "Diagnose" }

let private covering (t: Type) =
    helpers |> Seq.tryFind (fun h -> try h.Covers.Invoke t with _ -> false)

// --- F# literals: the values `exact` covers ----------------------------------------------------------
//
// A closed set of types, written as F# source and read back by `read` below: bool, char, string, the
// integer types, float, float32, decimal, unit, enums (by name), arrays, F# lists, List<T> (`ResizeArray
// [...]`), Dictionary<K, V> with the default comparer, options, tuples and struct tuples (up to 7), F#
// records (`{ Ns.Point.X = 1.0; Y = 2.0 }`) and unions (`Ns.Shape.Circle (1.0)`) with public
// representations, and null. `exact` is claimed only when the text reads back to an equal value.

let private isTuple (t: Type) =
    t.IsGenericType && t.FullName <> null
    && (t.FullName.StartsWith "System.Tuple`" || t.FullName.StartsWith "System.ValueTuple`")
    && t.GetGenericArguments().Length <= 7

let private isStructTuple (t: Type) = t.FullName <> null && t.FullName.StartsWith "System.ValueTuple`"


let private fsString (s: string) =
    let b = StringBuilder("\"")
    for c in s do
        match c with
        | '"' -> b.Append "\\\"" |> ignore
        | '\\' -> b.Append "\\\\" |> ignore
        | '\n' -> b.Append "\\n" |> ignore
        | '\r' -> b.Append "\\r" |> ignore
        | '\t' -> b.Append "\\t" |> ignore
        | c when Char.IsControl c || Char.IsSurrogate c -> b.AppendFormat("\\u{0:x4}", int c) |> ignore
        | c -> b.Append c |> ignore
    b.Append('"').ToString()

let private fsChar (c: char) =
    match c with
    | '\'' -> "'\\''"
    | '\\' -> "'\\\\'"
    | '\n' -> "'\\n'"
    | '\r' -> "'\\r'"
    | '\t' -> "'\\t'"
    | c when Char.IsControl c || Char.IsSurrogate c -> sprintf "'\\u%04x'" (int c)
    | c -> sprintf "'%c'" c

let private floatText (x: float) (suffix: string) =
    if Double.IsNaN x then "nan" + suffix
    elif Double.IsPositiveInfinity x then "infinity" + suffix
    elif Double.IsNegativeInfinity x then "-infinity" + suffix
    else
        let s = if suffix = "f" then (float32 x).ToString("R", CultureInfo.InvariantCulture) else x.ToString("R", CultureInfo.InvariantCulture)
        let s = if s.Contains "." || s.Contains "E" then s else s + ".0"
        s + suffix

let rec private literal (t: Type) (v: obj) : string option =
    let seqOf (items: obj seq) (elem: Type) (o: string) (c: string) =
        let parts = items |> Seq.map (literal elem) |> Seq.toList
        if parts |> List.forall Option.isSome then Some(o + String.Join("; ", parts |> List.map Option.get) + c) else None

    if isNull v then
        if t = typeof<unit> then Some "()"
        elif generic typedefof<option<_>> t then Some "None"
        elif t.IsValueType then None
        else Some "null"
    else
        let rt = declared (v.GetType())
        match v with
        | :? bool as b -> Some(if b then "true" else "false")
        | :? string as s -> Some(fsString s)
        | :? char as c -> Some(fsChar c)
        | :? int as i -> Some(string i)
        | :? int64 as i -> Some(string i + "L")
        | :? int16 as i -> Some(string i + "s")
        | :? sbyte as i -> Some(string i + "y")
        | :? byte as i -> Some(string i + "uy")
        | :? uint16 as i -> Some(string i + "us")
        | :? uint32 as i -> Some(string i + "u")
        | :? uint64 as i -> Some(string i + "UL")
        | :? float as x -> Some(floatText x "")
        | :? float32 as x -> Some(floatText (float x) "f")
        | :? decimal as d -> Some(d.ToString(CultureInfo.InvariantCulture) + "M")
        | _ when rt.IsEnum ->
            let name = Enum.GetName(rt, v)
            if isNull name then None else Some(typeName rt + "." + name)
        | _ when rt.IsArray && rt.GetArrayRank() = 1 ->
            seqOf (v :?> IEnumerable |> Seq.cast<obj>) (rt.GetElementType()) "[|" "|]"
        | _ when generic typedefof<list<_>> rt ->
            seqOf (v :?> IEnumerable |> Seq.cast<obj>) (rt.GetGenericArguments().[0]) "[" "]"
        | _ when generic typedefof<List<_>> rt ->
            seqOf (v :?> IEnumerable |> Seq.cast<obj>) (rt.GetGenericArguments().[0]) "ResizeArray [" "]"
        | _ when generic typedefof<option<_>> rt ->
            let inner = rt.GetProperty("Value").GetValue v
            literal (rt.GetGenericArguments().[0]) inner |> Option.map (fun s -> "Some (" + s + ")")
        | _ when generic typedefof<Dictionary<_, _>> rt ->
            let comparer = rt.GetProperty("Comparer").GetValue v
            let ka = rt.GetGenericArguments()
            let defaultComparer = typedefof<EqualityComparer<_>>.MakeGenericType(ka.[0]).GetProperty("Default").GetValue null
            if not (obj.ReferenceEquals(comparer, defaultComparer)) && not (comparer.Equals defaultComparer) then None
            else
                let d = v :?> IDictionary
                let pairs =
                    [ for k in d.Keys |> Seq.cast<obj> ->
                          match literal ka.[0] k, literal ka.[1] d.[k] with
                          | Some k, Some x -> Some("(" + k + ", " + x + ")")
                          | _ -> None ]
                if pairs |> List.forall Option.isSome then
                    Some("System.Collections.Generic.Dictionary(dict [" + String.Join("; ", pairs |> List.map Option.get) + "])")
                else None
        | :? ITuple as tup when isTuple rt ->
            let ts = rt.GetGenericArguments()
            let parts = [ for i in 0 .. tup.Length - 1 -> literal ts.[i] tup.[i] ]
            if parts |> List.forall Option.isSome then
                let body = "(" + String.Join(", ", parts |> List.map Option.get) + ")"
                Some(if isStructTuple rt then "struct " + body else body)
            else None
        | _ when isRecord rt ->
            let fields = FSharpType.GetRecordFields rt
            let parts = fields |> Array.map (fun f -> literal f.PropertyType (f.GetValue v)) |> Array.toList
            if parts |> List.forall Option.isSome then
                let body =
                    List.zip (List.ofArray fields) (parts |> List.map Option.get)
                    |> List.mapi (fun k (f, x) -> (if k = 0 then bareName rt + "." else "") + f.Name + " = " + x)
                if body.IsEmpty then None else Some("{ " + String.Join("; ", body) + " }")
            else None
        | _ when isUnion rt ->
            let case, values = FSharpValue.GetUnionFields(v, rt)
            let fields = case.GetFields()
            let parts = Array.map2 (fun (f: PropertyInfo) x -> literal f.PropertyType x) fields values |> Array.toList
            let name = (if rt.IsGenericType then "" else bareName rt + ".") + case.Name
            if parts.IsEmpty then Some name
            elif parts |> List.forall Option.isSome then Some(name + " (" + String.Join(", ", parts |> List.map Option.get) + ")")
            else None
        | _ ->
            // not F#'s own: a helper's value, written as its constructor's call
            match covering rt with
            | None -> None
            | Some h ->
                try
                    let args = h.Split.Invoke v
                    let types = h.Parts.Invoke rt
                    if isNull args || args.Length <> types.Length then None
                    else
                        let parts = Array.map2 literal types args |> Array.toList
                        if parts |> List.forall Option.isSome then
                            Some(h.Head.Invoke rt + "(" + String.Join(", ", parts |> List.map Option.get) + ")")
                        else None
                with _ -> None

// --- reading a literal back (for the exact check and for compare) -----------------------------------

exception private Unreadable of string

type private Reader(text: string) =
    let mutable i = 0
    member _.Pos = i
    member _.Skip() = while i < text.Length && Char.IsWhiteSpace text.[i] do i <- i + 1
    member r.Peek(s: string) = r.Skip(); String.CompareOrdinal(text, i, s, 0, s.Length) = 0
    member r.Eat(s: string) = if r.Peek s then i <- i + s.Length else raise (Unreadable(sprintf "expected %s at %d" s i))
    member r.TryEat(s: string) = if r.Peek s then i <- i + s.Length; true else false
    member r.AtEnd = r.Skip(); i >= text.Length
    member r.Token() =
        r.Skip()
        let start = i
        while i < text.Length && (Char.IsLetterOrDigit text.[i] || "._-+".IndexOf text.[i] >= 0) do i <- i + 1
        text.Substring(start, i - start)
    member r.Quoted(q: char) =
        r.Skip()
        if i >= text.Length || text.[i] <> q then raise (Unreadable "expected a quote")
        i <- i + 1
        let b = StringBuilder()
        let mutable fin = false
        while not fin do
            if i >= text.Length then raise (Unreadable "unterminated")
            let c = text.[i]
            if c = q then i <- i + 1; fin <- true
            elif c = '\\' then
                let e = text.[i + 1]
                i <- i + 2
                match e with
                | 'n' -> b.Append '\n' |> ignore
                | 'r' -> b.Append '\r' |> ignore
                | 't' -> b.Append '\t' |> ignore
                | 'u' ->
                    b.Append(char (Convert.ToInt32(text.Substring(i, 4), 16))) |> ignore
                    i <- i + 4
                | c -> b.Append c |> ignore
            else b.Append c |> ignore; i <- i + 1
        b.ToString()

let private parseNumber (tok: string) (suffix: string) =
    if suffix = "" then tok
    elif tok.EndsWith suffix then tok.Substring(0, tok.Length - suffix.Length)
    else raise (Unreadable("expected the suffix " + suffix))

let private inv = CultureInfo.InvariantCulture

let private readFloat (tok: string) =
    match tok with
    | "nan" -> Double.NaN
    | "infinity" -> Double.PositiveInfinity
    | "-infinity" -> Double.NegativeInfinity
    | s -> Double.Parse(s, NumberStyles.Float, inv)

let rec private read (r: Reader) (t: Type) : obj =
    let items (o: string) (c: string) (elem: Type) =
        r.Eat o
        let xs = ResizeArray<obj>()
        if not (r.TryEat c) then
            xs.Add(read r elem)
            while r.TryEat ";" do xs.Add(read r elem)
            r.Eat c
        xs

    if r.TryEat "null" then
        if t.IsValueType then raise (Unreadable "null for a value type") else null
    elif t = typeof<unit> then r.Eat "("; r.Eat ")"; box ()
    elif t = typeof<bool> then
        match r.Token() with
        | "true" -> box true
        | "false" -> box false
        | s -> raise (Unreadable s)
    elif t = typeof<string> then box (r.Quoted '"')
    elif t = typeof<char> then
        let s = r.Quoted '\''
        if s.Length = 1 then box s.[0] else raise (Unreadable "char")
    elif t = typeof<int> then box (Int32.Parse(r.Token(), inv))
    elif t = typeof<int64> then box (Int64.Parse(parseNumber (r.Token()) "L", inv))
    elif t = typeof<int16> then box (Int16.Parse(parseNumber (r.Token()) "s", inv))
    elif t = typeof<sbyte> then box (SByte.Parse(parseNumber (r.Token()) "y", inv))
    elif t = typeof<byte> then box (Byte.Parse(parseNumber (r.Token()) "uy", inv))
    elif t = typeof<uint16> then box (UInt16.Parse(parseNumber (r.Token()) "us", inv))
    elif t = typeof<uint32> then box (UInt32.Parse(parseNumber (r.Token()) "u", inv))
    elif t = typeof<uint64> then box (UInt64.Parse(parseNumber (r.Token()) "UL", inv))
    elif t = typeof<float> then box (readFloat (r.Token()))
    elif t = typeof<float32> then box (float32 (readFloat (parseNumber (r.Token()) "f")))
    elif t = typeof<decimal> then box (Decimal.Parse(parseNumber (r.Token()) "M", NumberStyles.Number, inv))
    elif t.IsEnum then
        let tok = r.Token()
        let prefix = typeName t + "."
        if tok.StartsWith prefix then Enum.Parse(t, tok.Substring prefix.Length) else raise (Unreadable tok)
    elif t.IsArray then
        let elem = t.GetElementType()
        let xs = items "[|" "|]" elem
        let a = Array.CreateInstance(elem, xs.Count)
        xs |> Seq.iteri (fun k x -> a.SetValue(x, k))
        box a
    elif generic typedefof<list<_>> t then
        let elem = t.GetGenericArguments().[0]
        let xs = items "[" "]" elem
        let empty = t.GetProperty("Empty").GetValue null
        let cons = t.GetMethod "Cons"
        Seq.foldBack (fun x acc -> cons.Invoke(null, [| x; acc |])) xs empty
    elif generic typedefof<List<_>> t then
        r.Eat "ResizeArray"
        let xs = items "[" "]" (t.GetGenericArguments().[0])
        let l = Activator.CreateInstance t :?> IList
        for x in xs do l.Add x |> ignore
        box l
    elif generic typedefof<option<_>> t then
        if r.TryEat "None" then null
        else
            r.Eat "Some"
            r.Eat "("
            let x = read r (t.GetGenericArguments().[0])
            r.Eat ")"
            t.GetMethod("Some").Invoke(null, [| x |])
    elif generic typedefof<Dictionary<_, _>> t then
        r.Eat "System.Collections.Generic.Dictionary"
        r.Eat "("
        r.Eat "dict"
        let ka = t.GetGenericArguments()
        let d = Activator.CreateInstance t :?> IDictionary
        r.Eat "["
        let pair () =
            r.Eat "("
            let k = read r ka.[0]
            r.Eat ","
            let x = read r ka.[1]
            r.Eat ")"
            d.Add(k, x)
        if not (r.TryEat "]") then
            pair ()
            while r.TryEat ";" do pair ()
            r.Eat "]"
        r.Eat ")"
        box d
    elif isTuple t then
        if isStructTuple t then r.Eat "struct"
        r.Eat "("
        let ts = t.GetGenericArguments()
        let xs = Array.zeroCreate ts.Length
        for k in 0 .. ts.Length - 1 do
            if k > 0 then r.Eat ","
            xs.[k] <- read r ts.[k]
        r.Eat ")"
        Activator.CreateInstance(t, xs)
    elif isRecord t then
        let fields = FSharpType.GetRecordFields t
        r.Eat "{"
        let xs =
            fields
            |> Array.mapi (fun k f ->
                if k > 0 then r.Eat ";"
                let name = r.Token()
                if not (name = f.Name || name.EndsWith("." + f.Name)) then raise (Unreadable("expected the field " + f.Name))
                r.Eat "="
                read r f.PropertyType)
        r.Eat "}"
        FSharpValue.MakeRecord(t, xs)
    elif isUnion t then
        let tok = r.Token()
        let caseName = match tok.LastIndexOf '.' with -1 -> tok | i -> tok.Substring(i + 1)
        match FSharpType.GetUnionCases t |> Array.tryFind (fun c -> c.Name = caseName) with
        | None -> raise (Unreadable("no case " + tok))
        | Some case ->
            let fields = case.GetFields()
            let xs =
                if fields.Length = 0 then [||]
                else
                    r.Eat "("
                    let xs = fields |> Array.mapi (fun k f -> (if k > 0 then r.Eat ","); read r f.PropertyType)
                    r.Eat ")"
                    xs
            FSharpValue.MakeUnion(case, xs)
    else
        match covering t with
        | None -> raise (Unreadable("no literal for " + typeName t))
        | Some h ->
            let head = h.Head.Invoke t
            let tok = r.Token()
            if tok <> head then raise (Unreadable("expected " + head))
            let types = h.Parts.Invoke t
            r.Eat "("
            let xs = types |> Array.mapi (fun k pt -> (if k > 0 then r.Eat ","); read r pt)
            r.Eat ")"
            h.Make.Invoke(t, xs)

let private readAll (text: string) (t: Type) : obj =
    let r = Reader text
    let v = read r t
    if r.AtEnd then v else raise (Unreadable "text left over")

// --- equality: types must match, floats within 1e-9, containers element by element ------------------

let rec private equal (a: obj) (b: obj) : bool =
    match a, b with
    | null, null -> true
    | null, _ | _, null -> false
    | (:? float as x), (:? float as y) -> x = y || (Double.IsNaN x && Double.IsNaN y) || abs (x - y) <= 1e-9 * max 1.0 (max (abs x) (abs y))
    | (:? float32 as x), (:? float32 as y) -> equal (box (float x)) (box (float y))
    | _ when declared (a.GetType()) <> declared (b.GetType()) -> false
    | (:? string), _ -> a.Equals b
    | (:? IDictionary as x), (:? IDictionary as y) ->
        x.Count = y.Count && (x.Keys |> Seq.cast<obj> |> Seq.forall (fun k -> y.Contains k && equal x.[k] y.[k]))
    | (:? ITuple as x), (:? ITuple as y) -> x.Length = y.Length && Seq.forall (fun k -> equal x.[k] y.[k]) { 0 .. x.Length - 1 }
    | (:? IEnumerable as x), (:? IEnumerable as y) when a.GetType().IsArray || generic typedefof<list<_>> (a.GetType()) || generic typedefof<List<_>> (a.GetType()) ->
        let xs = x |> Seq.cast<obj> |> Seq.toArray
        let ys = y |> Seq.cast<obj> |> Seq.toArray
        xs.Length = ys.Length && Array.forall2 equal xs ys
    | _ when generic typedefof<option<_>> (a.GetType()) ->
        let p = a.GetType().GetProperty "Value"
        equal (p.GetValue a) (p.GetValue b)
    | _ when isRecord (a.GetType()) ->
        Array.forall2 equal (FSharpValue.GetRecordFields a) (FSharpValue.GetRecordFields b)
    | _ when isUnion (a.GetType()) ->
        let ca, xa = FSharpValue.GetUnionFields(a, a.GetType())
        let cb, xb = FSharpValue.GetUnionFields(b, b.GetType())
        ca.Tag = cb.Tag && Array.forall2 equal xa xb
    | _ ->
        // a helper's value: argument by argument (a list inside element by element), else its own Equals
        let parts =
            match covering (declared (a.GetType())) with
            | Some h -> (try Some(h.Split.Invoke a, h.Split.Invoke b) with _ -> None)
            | None -> None
        match parts with
        | Some(xa, xb) when not (isNull xa) && not (isNull xb) -> xa.Length = xb.Length && Array.forall2 equal xa xb
        | _ -> a.Equals b

// --- binary: System.Text.Json, only when the round trip is the same ---------------------------------

let private jsonOptions = JsonSerializerOptions(IncludeFields = true)

// every instance field is public, or the backing field of a public property: nothing private is lost
let private allStateVisible (t: Type) =
    let flags = BindingFlags.Instance ||| BindingFlags.Public ||| BindingFlags.NonPublic
    let rec fields (t: Type) = if isNull t || t = typeof<obj> then [] else List.ofArray (t.GetFields(flags ||| BindingFlags.DeclaredOnly)) @ fields t.BaseType
    fields t
    |> List.forall (fun f ->
        f.IsPublic
        || (f.Name.StartsWith "<" && f.Name.EndsWith ">k__BackingField"
            && (let p = t.GetProperty(f.Name.Substring(1, f.Name.IndexOf '>' - 1)) in not (isNull p) && not (isNull (p.GetGetMethod()))))
        )

let private binary (v: obj) : string option =
    try
        let t = v.GetType()
        if typeof<Delegate>.IsAssignableFrom t || typeof<Type>.IsAssignableFrom t || not (allStateVisible t) then None
        else
            let s1 = JsonSerializer.Serialize(v, t, jsonOptions)
            let back = JsonSerializer.Deserialize(s1, t, jsonOptions)
            let s2 = JsonSerializer.Serialize(back, t, jsonOptions)
            if s1 = s2 then Some(Convert.ToBase64String(Encoding.UTF8.GetBytes(t.AssemblyQualifiedName + "\n" + s1))) else None
    with _ -> None

let private unbinary (b64: string) : string * string =
    let s = Encoding.UTF8.GetString(Convert.FromBase64String b64)
    let k = s.IndexOf '\n'
    s.Substring(0, k), s.Substring(k + 1)

// --- display --------------------------------------------------------------------------------------------

let private display (v: obj) : string =
    let text =
        try
            if isNull v then "null"
            else
                let m = v.GetType().GetMethod("ToString", Type.EmptyTypes)
                if not (isNull m) && m.DeclaringType <> typeof<obj> && m.DeclaringType <> typeof<ValueType> then v.ToString()
                else sprintf "%A" v
        with e -> "<display failed: " + e.GetType().Name + ">"
    let text = oneLine text
    if text.Length > 10000 then text.Substring(0, 10000) + "..." else text

// --- one evaluation -------------------------------------------------------------------------------------

/// Begin entry `id`: what `call` and `finish` will report on.
let start (id: string) (timeoutMs: int) (basis: (string * string) option) =
    current <- Some { Id = id; TimeoutMs = timeoutMs; Basis = basis; Outcome = NotRun }

let rec private reason (e: exn) =
    match e with
    | :? TypeInitializationException as t when not (isNull t.InnerException) -> e.GetType().FullName + ": " + e.Message + " <- " + reason t.InnerException
    | :? TargetInvocationException as t when not (isNull t.InnerException) -> reason t.InnerException
    | _ -> e.GetType().FullName + ": " + e.Message

/// Run `f` on its own thread under the entry's time limit (ControlledExecution: a loop is stopped, a
/// native wait only when it returns). The value is kept for `finish`; on a crash or a timeout the
/// binding gets the type's default value.
let call (f: unit -> 'T) : 'T =
    match current with
    | None -> Unchecked.defaultof<'T>
    | Some entry ->
        let mutable result = Crash "the call did not run"
        let worker =
            Thread(
                (fun () ->
                    use cts = new CancellationTokenSource(max entry.TimeoutMs 1)
                    try
                        System.Runtime.ControlledExecution.Run(
                            (fun () ->
                                result <-
                                    try
                                        let v = f ()
                                        Value(box v, typeof<'T>)
                                    with
                                    | :? OperationCanceledException when cts.IsCancellationRequested -> TimedOut
                                    | e -> Crash(reason e)),
                            cts.Token)
                    with
                    | :? OperationCanceledException -> result <- TimedOut
                    | :? ThreadInterruptedException -> result <- TimedOut),
                64 * 1024 * 1024)
        worker.IsBackground <- true
        worker.Start()
        // a native wait (Thread.Sleep, I/O) is not aborted until it returns: stop waiting for it
        if not (worker.Join(max entry.TimeoutMs 1 + 500)) then result <- TimedOut
        entry.Outcome <- result
        match result with
        | Value(v, _) ->
            write (sprintf "XT_CALLED_%s\n" entry.Id)
            unbox<'T> v
        | _ -> Unchecked.defaultof<'T>

let private compare (basis: (string * string) option) (repr: string) (v: obj) (t: Type) (bin: string option) =
    match basis with
    | None -> "none"
    | Some _ when repr = "unsupported" -> "skipped"
    | Some("exact", text) ->
        try (if equal (readAll text t) v then "same" else "changed") with _ -> "changed"
    | Some("binary", b64) ->
        try
            let typeName0, json0 = unbinary b64
            match bin with
            | Some b ->
                let typeName1, json1 = unbinary b
                if typeName0 = typeName1 && json0 = json1 then "same" else "changed"
            | None -> "changed"
        with _ -> "changed"
    | Some _ -> "changed"

let private judge (entry: Entry) (v: obj) (staticType: Type) =
    let t = if isNull v then staticType else declared (v.GetType())
    let exact =
        match literal t v with
        | Some text -> (try (if equal (readAll text t) v then Some text else None) with _ -> None)
        | None -> None
    let repr, shown, bin =
        match exact with
        | Some text -> "exact", text, None
        | None ->
            match (if isNull v then None else binary v) with
            | Some b -> "binary", display v, Some b
            | None -> "unsupported", display v, None
    let lines =
        [ yield "XT_BEGIN_" + entry.Id
          yield "repr=" + repr
          yield "type=" + typeName t
          yield "compare=" + compare entry.Basis repr v t bin
          yield "display=" + oneLine shown
          match bin with
          | Some b -> yield "bin_base64=" + b
          | None -> ()
          yield "XT_END_" + entry.Id ]
    String.Join("\n", lines) + "\n"

// the curried arguments a module function takes as F# sees it: `let f a b` is 2, `let f (a, b)` and
// `let f ()` are 1 (the compiled method has 2 and 0 parameters)
let private attr<'A when 'A :> Attribute> (m: MemberInfo) : 'A option =
    match m.GetCustomAttributes(typeof<'A>, false) with
    | [||] -> None
    | xs -> Some(xs.[0] :?> 'A)

let private curried (m: MethodInfo) =
    match attr<CompilationArgumentCountsAttribute> m with
    | None -> 1
    | Some a -> Seq.length a.Counts

let private isModule (t: Type) =
    match attr<CompilationMappingAttribute> t with
    | None -> false
    | Some a -> a.SourceConstructFlags &&& SourceConstructFlags.KindMask = SourceConstructFlags.Module

// a module's name as F# writes it: fsi's prefix gone, `+` a dot, a `Module` suffix the compiler added gone
let private moduleName (t: Type) =
    let n = sourceName t.FullName
    let suffixed =
        match attr<CompilationRepresentationAttribute> t with
        | None -> false
        | Some a -> a.Flags &&& CompilationRepresentationFlags.ModuleSuffix = CompilationRepresentationFlags.ModuleSuffix
    if suffixed && n.EndsWith "Module" then n.Substring(0, n.Length - 6) else n

let private signature (name: string) (m: MethodInfo) =
    let ps = m.GetParameters() |> Array.map (fun p -> typeName p.ParameterType)
    let args =
        match attr<CompilationArgumentCountsAttribute> m with
        | None -> [ (if ps.Length = 0 then "unit" else String.Join(" * ", ps)) ]
        | Some a ->
            let mutable k = 0
            [ for n in a.Counts ->
                  let group = ps.[k .. k + n - 1]
                  k <- k + n
                  String.Join(" * ", group) ]
    name + " : " + String.Join(" -> ", args @ [ typeName m.ReturnType ])

// a static member as `Show(float, int) : string`, `Scale(float)(Point) : Point`: one group per curried group
let private memberSignature (name: string) (m: MethodInfo) =
    let ps = m.GetParameters() |> Array.map (fun p -> typeName p.ParameterType)
    let groups =
        match attr<CompilationArgumentCountsAttribute> m with
        | None -> [ ps ]
        | Some a ->
            let mutable k = 0
            [ for n in a.Counts ->
                  let group = ps.[k .. k + n - 1]
                  k <- k + n
                  group ]
    name + String.Join("", groups |> List.map (fun g -> "(" + String.Join(", ", g) + ")")) + " : " + typeName m.ReturnType

// why `Ns.Type.M((a), (b))` with `argc` arguments did not compile: the type's static members of that name
let private diagnoseMember (t: Type) (type0: string) (name: string) (argc: int) =
    let flags = BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static
    let all = t.GetMethods flags |> Array.filter (fun m -> m.Name = name)
    let publics = all |> Array.filter (fun m -> m.IsPublic)
    let sigs (ms: MethodInfo[]) = String.Join("; ", ms |> Array.map (memberSignature name))
    if not t.IsVisible then sprintf "the call did not compile: %s is not public" type0
    elif all.Length = 0 && not (isNull (t.GetProperty(name, flags))) then sprintf "the call did not compile: %s.%s is a value, not a function" type0 name
    elif all.Length = 0 then sprintf "the call did not compile: %s has no static member %s" type0 name
    elif publics.Length = 0 then sprintf "the call did not compile: %s.%s is private or internal" type0 name
    elif publics |> Array.exists (fun m -> m.GetParameters().Length = argc) then
        sprintf "the call did not compile: the arguments do not fit %s (fsi's message is in the REPL output)" (sigs publics)
    else
        let counts = publics |> Array.map (fun m -> string (m.GetParameters().Length)) |> Array.distinct
        sprintf "the call did not compile: %s.%s takes %s argument(s), got %d: %s" type0 name (String.Join(" or ", counts)) argc (sigs publics)

// a type's name as F# writes it in a call: fsi's prefix gone, `+` a dot, a generic's `` `1 `` gone
let private typeFullName (t: Type) =
    Text.RegularExpressions.Regex.Replace(sourceName t.FullName, @"`[0-9]+", "")

// why `Ns.Module.f (a) (b)` (or a type's static member) with `argc` arguments did not compile, as far as
// reflection can tell
let private diagnose (module0: string) (name: string) (argc: int) =
    let types =
        AppDomain.CurrentDomain.GetAssemblies()
        |> Seq.collect (fun a -> try a.GetTypes() with :? ReflectionTypeLoadException as e -> e.Types |> Array.filter (isNull >> not))
        |> Seq.filter (fun t -> not (isNull t.FullName))
        |> Array.ofSeq
    // the last one loaded wins, as fsi's own name resolution does
    let t = types |> Seq.filter (fun t -> isModule t && moduleName t = module0) |> Seq.tryLast
    let owner = types |> Seq.filter (fun t -> not (isModule t) && typeFullName t = module0) |> Seq.tryLast
    let logHint () =
        let build =
            try
                if buildLog <> "" && IO.File.Exists buildLog then
                    IO.File.ReadAllLines buildLog
                    |> Array.tryFind (fun l -> l.Contains ": error " || l.StartsWith "XT_LOAD_ERROR ")
                    |> Option.map (fun l ->
                        if l.StartsWith "XT_LOAD_ERROR " then " (a script was not read: " + l.Substring(14).Trim() + ")"
                        else " (the build failed: " + l.Trim() + ")")
                    |> Option.defaultValue ""
                else ""
            with _ -> ""
        let scripts =
            match tried |> Seq.filter (done'.Contains >> not) |> List.ofSeq with
            | [] -> ""
            | xs -> " (did not load: " + String.Join(", ", xs) + "; fsi's message is in the REPL output)"
        build + scripts
    match t, owner with
    | None, Some o -> diagnoseMember o module0 name argc
    | None, None -> sprintf "the call did not compile: no module %s in what was loaded%s" module0 (logHint ())
    | Some t, _ when not t.IsVisible -> sprintf "the call did not compile: %s is not public" module0
    | Some t, _ ->
        let all = t.GetMethods(BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static) |> Array.filter (fun m -> m.Name = name)
        let value = t.GetProperty(name, BindingFlags.Public ||| BindingFlags.NonPublic ||| BindingFlags.Static)
        if all.Length = 0 && not (isNull value) then sprintf "the call did not compile: %s.%s is a value, not a function" module0 name
        elif all.Length = 0 then sprintf "the call did not compile: %s has no function %s" module0 name
        else
            let m = all.[0]
            if not m.IsPublic then sprintf "the call did not compile: %s.%s is private or internal" module0 name
            elif curried m <> argc then sprintf "the call did not compile: %s.%s takes %d argument(s), got %d: %s" module0 name (curried m) argc (signature name m)
            else sprintf "the call did not compile: the arguments do not fit %s (fsi's message is in the REPL output)" (signature name m)

/// Print entry `id`'s record in one write: XT_BEGIN_<id> ... XT_END_<id>, or XT_ERROR_<id> crash|timeout.
let finish (id: string) (module0: string) (name: string) (argc: int) =
    let text =
        match current with
        | Some entry when entry.Id = id ->
            current <- None
            match entry.Outcome with
            | NotRun ->
                loadReferences ()
                // a helper's own terms first (a C# type's methods), else F#'s
                let why =
                    helpers
                    |> Seq.tryPick (fun h -> try Option.ofObj (h.Diagnose.Invoke(module0, name, argc)) with _ -> None)
                    |> Option.defaultWith (fun () -> diagnose module0 name argc)
                sprintf "XT_ERROR_%s crash %s\n" id (oneLine why)
            | Crash r -> sprintf "XT_ERROR_%s crash %s\n" id (oneLine r)
            | TimedOut -> sprintf "XT_ERROR_%s timeout\n" id
            | Value(v, t) ->
                try judge entry v t
                with e -> sprintf "XT_ERROR_%s crash %s\n" id (oneLine ("judging the value: " + reason e))
        | _ -> sprintf "XT_ERROR_%s crash the entry was not started\n" id
    write text

/// An entry the caller refused to send (a call that would leave fsi reading on): its crash record.
let fail (id: string) (why: string) =
    current <- None
    write (sprintf "XT_ERROR_%s crash %s\n" id (oneLine why))

// --- loading a script's definitions only ----------------------------------------------------------------
//
// `#load` runs a script's top level. `Load.write` writes a copy of each script in which only its
// definitions are left, every other top-level declaration blanked (spaces, its newlines kept, so lines and
// columns stay the script's own): `let` bindings of functions, and of values that run nothing (a
// literal, a name, a tuple, list or array of those, a lambda); types, exceptions, `open`, module
// abbreviations; nested modules and namespaces, read the same way (one left with no declarations goes
// whole); hash directives except `#r "nuget: …"` (nothing is fetched). Everything else -- `printfn …`,
// `do …`, `let x = f ()`, `[<EntryPoint>]` attributes on their own -- does not run. The copies keep the
// scripts' paths under another directory, so `#load "other.fsx"` between them loads the other copy.

module Load =
    open FSharp.Compiler.CodeAnalysis
    open FSharp.Compiler.Syntax
    open FSharp.Compiler.Text

    let private checker = lazy (FSharpChecker.Create())

    let rec private runsNothing (e: SynExpr) =
        match e with
        | SynExpr.Const _ | SynExpr.Null _ | SynExpr.Ident _ | SynExpr.LongIdent _ -> true
        | SynExpr.Lambda _ | SynExpr.MatchLambda _ -> true
        | SynExpr.Typed(expr = e) | SynExpr.Paren(expr = e) -> runsNothing e
        | SynExpr.Tuple(exprs = es) | SynExpr.ArrayOrList(exprs = es) -> List.forall runsNothing es
        | SynExpr.ArrayOrListComputed(expr = e) -> runsNothing e
        | SynExpr.Sequential(expr1 = a; expr2 = b) -> runsNothing a && runsNothing b
        | _ -> false

    let private isFunction (b: SynBinding) =
        match b with
        | SynBinding(headPat = SynPat.LongIdent(argPats = SynArgPats.Pats(_ :: _))) -> true
        | SynBinding(headPat = SynPat.LongIdent(argPats = SynArgPats.NamePatPairs(pats = _ :: _))) -> true
        | _ -> false

    let private isDefinition (b: SynBinding) =
        let (SynBinding(expr = e)) = b
        isFunction b || runsNothing e

    let private isNuget (d: ParsedHashDirective) =
        let (ParsedHashDirective(ident, args, _)) = d
        ident = "r"
        && args
           |> List.exists (function
               | ParsedHashDirectiveArgument.String(value = v) -> v.TrimStart().StartsWith("nuget:", StringComparison.OrdinalIgnoreCase)
               | _ -> false)

    // the ranges to blank among `decls`; `true` when none of them is kept
    let rec private dropped (decls: SynModuleDecl list) : range list * bool =
        let parts =
            decls
            |> List.map (fun d ->
                match d with
                | SynModuleDecl.Let(bindings = bs) -> if List.forall isDefinition bs then [], true else [ d.Range ], false
                | SynModuleDecl.Types _ | SynModuleDecl.Exception _ | SynModuleDecl.Open _ | SynModuleDecl.ModuleAbbrev _ -> [], true
                | SynModuleDecl.HashDirective(hashDirective = h) -> if isNuget h then [ d.Range ], false else [], true
                | SynModuleDecl.NestedModule(decls = inner) ->
                    let rs, empty = dropped inner
                    if empty then [ d.Range ], false else rs, true
                | SynModuleDecl.NamespaceFragment(SynModuleOrNamespace(decls = inner)) ->
                    let rs, empty = dropped inner
                    if empty then [ d.Range ], false else rs, true
                | _ -> [ d.Range ], false)
        List.collect fst parts, not (List.exists snd parts)

    let private blank (source: string) (ranges: range list) =
        let starts =
            let xs = ResizeArray [ 0 ]
            source |> Seq.iteri (fun k c -> if c = '\n' then xs.Add(k + 1))
            xs
        let offset (line: int) (col: int) = min source.Length (starts.[line - 1] + col)
        let chars = source.ToCharArray()
        for r in ranges do
            for k in offset r.StartLine r.StartColumn .. offset r.EndLine r.EndColumn - 1 do
                if chars.[k] <> '\n' && chars.[k] <> '\r' then chars.[k] <- ' '
        String(chars)

    /// `source` (the script at `path`) with only its definitions, or the parser's first error.
    let definitions (path: string) (source: string) : Result<string, string> =
        let opts = { FSharpParsingOptions.Default with SourceFiles = [| path |] }
        let res = checker.Force().ParseFile(path, SourceText.ofString source, opts) |> Async.RunSynchronously
        match res.Diagnostics |> Array.tryFind (fun d -> d.Severity = FSharp.Compiler.Diagnostics.FSharpDiagnosticSeverity.Error) with
        | Some d -> Error(sprintf "%s(%d,%d): %s" path d.StartLine (d.StartColumn + 1) d.Message)
        | None ->
            match res.ParseTree with
            | ParsedInput.ImplFile(ParsedImplFileInput(contents = modules)) ->
                let ranges = modules |> List.collect (fun (SynModuleOrNamespace(decls = ds)) -> fst (dropped ds))
                Ok(blank source ranges)
            | _ -> Error(path + ": not a script")

    /// For each script under `root` (paths relative to it), its definitions-only copy under `outDir`.
    /// Prints `XT_LOAD_ERROR <path> <reason>` for a script it could not read; the rest are written.
    let write (root: string) (outDir: string) (paths: string list) =
        for rel in paths do
            let src = IO.Path.Combine(root, rel)
            let dst = IO.Path.Combine(outDir, rel)
            IO.Directory.CreateDirectory(IO.Path.GetDirectoryName dst) |> ignore
            match (try definitions src (IO.File.ReadAllText src) with e -> Error(reason e)) with
            | Ok text -> IO.File.WriteAllText(dst, text)
            | Error why -> write (sprintf "XT_LOAD_ERROR %s %s\n" rel (oneLine why))
