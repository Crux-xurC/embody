(* embody.ml -- the OCaml embody: a fixed module loaded into the target
   project's plain `ocaml` toplevel. See ReadMe.md (this directory) for what
   embody is and the output protocol every language's embody shares.

   It knows nothing of the target: what to evaluate arrives as text, and the
   toplevel itself parses, type-checks, evaluates and prints it (Toploop).
   Only the toplevel's own Toploop is used -- loading ocamlcommon.cma would
   give a second copy of the compiler, whose errors the toplevel can't print.

   Loading it (the caller's init file, `ocaml -init <file>`):
     #directory "+compiler-libs";;
     #directory "+unix";;
     #load "unix.cma";;
     #mod_use "<this directory>/embody.ml";;
     Embody.dune_deps "<project root>";;          (a dune project only)
     Embody.load_all ~aliases:[...] "<scratch dir>" [ "<file.ml>"; ... ];;

   Output (ReadMe.md, "出力の約束"):
     XT_BEGIN_<id> / repr= / type= / compare= / display= / XT_END_<id>
     XT_ERROR_<id> timeout | XT_ERROR_<id> crash <one line> *)

exception Timeout

let timed_out = ref false
let check = ref false

let oneline s = String.concat "\\n" (String.split_on_char '\n' (String.trim s))

let new_ppf () =
  let buf = Buffer.create 256 in
  let ppf = Format.formatter_of_buffer buf in
  Format.pp_set_margin ppf 1_000_000;
  Format.pp_set_max_indent ppf 999_999;
  (buf, ppf)

(* toplevel text run as if typed; (ok, what the toplevel printed) *)
let use text =
  let buf, ppf = new_ppf () in
  let ok = Toploop.use_input ppf (Toploop.String text) in
  Format.pp_print_flush ppf ();
  (ok, Buffer.contents buf)

(* SIGALRM after [ms]; the handler raises inside the evaluation *)
let with_timeout ms f =
  timed_out := false;
  let old = Sys.signal Sys.sigalrm (Sys.Signal_handle (fun _ -> timed_out := true; raise Timeout)) in
  let set s = ignore (Unix.setitimer Unix.ITIMER_REAL { Unix.it_interval = 0.; it_value = s }) in
  set (float_of_int ms /. 1000.);
  Fun.protect ~finally:(fun () -> set 0.; Sys.set_signal Sys.sigalrm old) f

(* does [text], evaluated, equal the variable [var]? (structural =; a
   value that can't be compared, or text that doesn't evaluate, is false) *)
let equal_to var text =
  check := false;
  let ok, _ = use (Printf.sprintf "Embody.check := (try (%s) = %s with _ -> false);;" text var) in
  ok && !check

let split_on_equals s =
  let n = String.length s in
  let rec find i = if i + 3 > n then None else if String.sub s i 3 = " = " then Some i else find (i + 1) in
  match find 0 with
  | None -> (String.trim s, "")
  | Some i -> (String.sub s 0 i, String.trim (String.sub s (i + 3) (n - i - 3)))

(* [text] is "let <var> = <call>;;" ([var] is xt_<id>, [id] the markers'
   id). The toplevel prints "val <var> : <type> = <value>"; the value is exact when its printed
   text evaluates back to an equal value (the toplevel prints values as
   source: constructors qualified, records with fields), else unsupported
   (<fun>, <abstr>, <cycle>, an inline record's fields, ...). [prev] is the
   previous value's text: compare is same/changed against it, skipped when
   either side isn't exact. *)
let run id var timeout_ms text prev =
  let ok, printed = with_timeout timeout_ms (fun () -> use text) in
  let out s = print_string s; print_newline () in
  let prefix = Printf.sprintf "val %s : " var in
  if !timed_out then out (Printf.sprintf "XT_ERROR_%s timeout" id)
  else if (not ok) || not (String.starts_with ~prefix printed) then
    out (Printf.sprintf "XT_ERROR_%s crash %s" id (oneline printed))
  else begin
    let rest = String.sub printed (String.length prefix) (String.length printed - String.length prefix) in
    let ty, value = split_on_equals rest in
    let exact = (not (String.contains value '\n')) && equal_to var value in
    let compare =
      match prev with
      | None -> "none"
      | Some p -> if not exact then "skipped" else if equal_to var p then "same" else "changed"
    in
    out
      (String.concat "\n"
         [ "XT_BEGIN_" ^ id; "repr=" ^ (if exact then "exact" else "unsupported"); "type=" ^ oneline ty;
           "compare=" ^ compare; "display=" ^ oneline value; "XT_END_" ^ id ])
  end

(* [path]'s text with its top-level expressions and its `let ()` / `let _`
   bindings blanked out -- newlines kept, so line numbers stay the file's:
   loading a file must not run its side effects (the scripts-mode hazard
   SourceSanitize closes for Elixir). None if the file doesn't parse. *)
let sanitized path =
  let ic = open_in_bin path in
  let src = really_input_string ic (in_channel_length ic) in
  close_in ic;
  let lexbuf = Lexing.from_string src in
  lexbuf.Lexing.lex_curr_p <- { lexbuf.Lexing.lex_curr_p with Lexing.pos_fname = path };
  match !Toploop.parse_use_file lexbuf with
  | exception _ -> None
  | phrases ->
      let side_effect (item : Parsetree.structure_item) =
        match item.pstr_desc with
        | Pstr_eval _ -> true
        | Pstr_value (_, vbs) ->
            List.for_all
              (fun (vb : Parsetree.value_binding) ->
                match vb.pvb_pat.ppat_desc with
                | Ppat_var _ | Ppat_constraint ({ ppat_desc = Ppat_var _; _ }, _) -> false
                | _ -> true)
              vbs
        | _ -> false
      in
      let dropped =
        List.concat_map
          (function Parsetree.Ptop_def items -> List.filter side_effect items | Parsetree.Ptop_dir _ -> [])
          phrases
      in
      let b = Bytes.of_string src in
      List.iter
        (fun (item : Parsetree.structure_item) ->
          for k = item.pstr_loc.loc_start.pos_cnum to item.pstr_loc.loc_end.pos_cnum - 1 do
            if Bytes.get b k <> '\n' then Bytes.set b k ' '
          done)
        dropped;
      Some (Bytes.to_string b)

(* one file as the module its name gives (`#mod_use`), from its sanitized
   copy in [dir]; (ok, what the toplevel printed) *)
let load dir path =
  match sanitized path with
  | None -> (false, Printf.sprintf "%s: does not parse" path)
  | Some text ->
      let copy = Filename.concat dir (Filename.basename path) in
      let oc = open_out_bin copy in
      output_string oc text;
      close_out oc;
      let buf, ppf = new_ppf () in
      let ok = Toploop.mod_use_input ppf (Toploop.File copy) in
      Format.pp_print_flush ppf ();
      (ok, Buffer.contents buf)

(* [aliases]: (Library, [Member; ...]) -- a dune library is wrapped, so code
   outside it says Library.Member; once every member is loaded (as its own
   module, from its file) the alias module is defined. *)
let define_alias (lib, members) =
  let body = String.concat " " (List.map (fun m -> Printf.sprintf "module %s = %s" m m) members) in
  fst (use (Printf.sprintf "module %s = struct %s end;;" lib body))

(* every file, in an order their dependencies allow: a file that fails is
   tried again after the others (and after the aliases that became
   possible), until a round loads nothing more. What still fails is printed
   (the toplevel's own messages). *)
let load_all ?(aliases = []) dir paths =
  let loaded = Hashtbl.create 16 in
  let module_of p = String.capitalize_ascii (Filename.remove_extension (Filename.basename p)) in
  let rec rounds pending aliases =
    let failed =
      List.filter
        (fun p ->
          let ok = fst (load dir p) in
          if ok then Hashtbl.replace loaded (module_of p) ();
          not ok)
        pending
    in
    let ready, waiting = List.partition (fun (_, ms) -> List.for_all (Hashtbl.mem loaded) ms) aliases in
    let defined = List.filter define_alias ready in
    if failed = [] then ()
    else if List.length failed = List.length pending && defined = [] then
      List.iter (fun p -> print_string ("XT_LOAD_FAILED " ^ p ^ "\n" ^ snd (load dir p) ^ "\n")) failed
    else rounds failed waiting
  in
  rounds paths aliases;
  print_newline ()

(* the libraries a dune project depends on, as `dune ocaml top` gives them
   (it builds the project first), without the project's own: those lines
   point under [root], and the project's own code is loaded from its
   sources (load_all) so that none of its top-level effects run. Run in the
   toplevel's own environment -- the project's -- so dune finds what the
   project would. *)
let dune_deps root =
  let cmd = Printf.sprintf "cd %s && dune ocaml top 2>&1" (Filename.quote root) in
  let ic = Unix.open_process_in cmd in
  let rec lines acc = match input_line ic with l -> lines (l :: acc) | exception End_of_file -> List.rev acc in
  let all = lines [] in
  let status = Unix.close_process_in ic in
  let own l =
    let prefix = root ^ "/" in
    let n = String.length prefix in
    let rec find i = i + n <= String.length l && (String.sub l i n = prefix || find (i + 1)) in
    find 0
  in
  match status with
  | Unix.WEXITED 0 ->
      let deps = List.filter (fun l -> String.length l > 0 && l.[0] = '#' && not (own l)) all in
      let ok, printed = use (String.concat "\n" deps) in
      if not ok then print_string ("XT_LOAD_FAILED dune ocaml top\n" ^ printed ^ "\n")
  | _ -> print_string ("XT_LOAD_FAILED dune ocaml top\n" ^ String.concat "\n" all ^ "\n")
