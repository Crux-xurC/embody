# embody

対話環境（REPL・トップレベル）で関数を評価し、その結果を**どんな値でも有限の1行の文字列**にして、
決まったマーカーで囲んで印字する、言語ごとの固定モジュール。

- **対象を知らない**: どの関数を評価するかは、呼び出し側が REPL へ送る1行が決める。embody 自身は
  対象のモジュールに依存せず、言語の標準の範囲（OCaml ならトップレベル自身、Haskell なら `base`）だけで書く
- **必ず返ってくる**: 時間切れ・例外・型エラーも、マーカー付きの1行として返す。無限の構造や関数のような
  「表示しきれない値」も、有限の表示と「読み戻せない」という印（`repr=unsupported`）で返す
- **値に体を与える**: 画面で見るだけなら要らない値でも、記録（データベース）に入れるには何かの形が要る。
  見えない値に一時的に体を与える、というのが名前の由来

Xipe Totec（関数型言語のプロジェクトの関数を計算行として評価・記録するツール）が
各言語の REPL の中で使う部品として作ったもの。出力の約束（下記）に従えば、ほかのツールからも使える。

---

## ファイル

| ファイル | 言語・REPL | 状態 |
|---|---|---|
| `embody.ml` | OCaml（素の `ocaml` トップレベル） | 実装済み。OCaml 5.5.0・dune 3.23.1 で確認 |
| `embody.hs` | Haskell（GHCi） | 実装済み。GHC 9.10.3 の GHCi で確認 |

各ファイルは単独で読み込める。ビルドの設定（flake・dune・cabal など）は持たない。読み込み方は呼び出し側が決める。

---

## 出力の約束（全言語共通）

1回の評価につき、次のどちらかを**1回の書き込みで**印字する（ほかの出力が途中に割り込まないように）。
`<id>` は呼び出し側が決める評価の識別子。

**成功**

```
XT_BEGIN_<id>
repr=<exact|binary|unsupported>
type=<型>
compare=<none|same|changed|skipped>
display=<値の表示、1行>
XT_END_<id>
```

| 欄 | 意味 |
|---|---|
| `repr` | `exact`: `display` をその言語のソースとして評価すると元の値に戻る（ほかの式に埋め込める・比べられる）。`binary`: 文字列では戻らないがバイト列で保存できる（`bin_base64=` の欄が付く）。`unsupported`: 戻せない（関数・循環・抽象型など）。表示だけを記録する |
| `type` | 値の型。言語の粒度のまま（OCaml は `Geometry.shape list` のような型そのもの） |
| `compare` | 前回の値が渡されたとき、それと比べた結果。渡されなければ `none`。どちらかが `exact` でなければ `skipped` |
| `display` | 値の表示。改行は `\n` の2文字にして1行にする |

**失敗**

```
XT_ERROR_<id> timeout
XT_ERROR_<id> crash <理由、1行>
```

`crash` の理由は、その言語自身の文言（例外の表示、型エラー・構文エラーの報告）をそのまま1行にしたもの。

**変えてはいけないもの**: マーカーの文字列（`XT_BEGIN_`・`XT_END_`・`XT_ERROR_`）、欄の名前、1行ずつの
`キー=値` の形。呼び出し側はこれだけを頼りに出力を読む。REPL がほかに何を印字しても（プロンプトなど）、
自分の `<id>` のマーカーの間だけを見ればよい。

---

## OCaml（`embody.ml`）

素の `ocaml` トップレベルに読み込む。`utop` は使わない。コンパイラの部品は、トップレベル自身が持つ
`Toploop` だけを使う（`ocamlcommon.cma` を読むとコンパイラの別の写しになり、型エラーをトップレベルの文言で
報告できなくなる）。

**読み込み**（`ocaml -init <初期化ファイル>` の初期化ファイルに書く）

```ocaml
#directory "+compiler-libs";;
#directory "+unix";;
#load "unix.cma";;
#mod_use "<このディレクトリ>/embody.ml";;
Embody.dune_deps "<プロジェクトの根>";;                          (* dune のプロジェクトだけ *)
Embody.load_all ~aliases:[("Mylib", ["Stats"; "Words"])] "<作業用ディレクトリ>" ["<a.ml>"; "<b.ml>"];;
```

| 関数 | 役割 |
|---|---|
| `Embody.run id var timeout_ms text prev` | `text`（`let <var> = <呼び出し>;;`）をトップレベルに評価させ、表示（`val <var> : <型> = <値>`）から出力の約束の形を作る。`prev` は前回の値のソース（`Some "…"` / `None`） |
| `Embody.load_all ?aliases dir paths` | 対象の `.ml` を1ファイル1モジュールとして読む。トップレベルの式と `let ()`/`let _` は空白で塗りつぶした写し（`dir` に置く、改行は残すので行番号は元のまま）を読むので、副作用は走らない。依存の順は、失敗したものを後で読み直して決める。`aliases` は dune の包まれたライブラリの別名（`module Mylib = struct module Words = Words … end`）で、メンバーがそろったら定義する。読めなかったものは `XT_LOAD_FAILED <path>` と理由を印字する |
| `Embody.dune_deps root` | `root` で `dune ocaml top` を実行し、依存するライブラリの `#directory`/`#load` だけを読む（プロジェクト自身のライブラリの行は除く。自身のコードは `load_all` がソースから読む）。トップレベルの環境（`OCAMLPATH`・dune）で動く。dune がプロジェクトをビルドする |

**呼び出しの例**

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

- `repr`: 表示された値をもう一度評価して `=` で等しければ `exact`、それ以外は `unsupported`（`<fun>`・
  `<cycle>`・`<abstr>`・インラインレコードの表示など）。`binary` はまだ無い
- 時間の上限は `Unix.setitimer` の SIGALRM。メモリの上限は無い（固まればトップレベルごと止める前提）
- 評価中に SIGINT を受けると、その句だけが止まり（`Interrupted.`）、トップレベルは読み続ける
- 同じ回の後の評価から、前の結果を変数（`xt_c1`）で参照できる

**制約**: ppx を使うファイル（ソースをそのまま読むので前処理されない）、同じ名前のファイルが2つある構成、
1つの `dune` ファイルに複数のライブラリ（別名を作らない）。

---

## Haskell（`embody.hs`）

GHCi に読み込む。依存は `base` だけ。GHC 9.10.3 で確かめた。

**GHCi の起動**

```
ghci -v0 -fdefer-type-errors -Wno-deferred-type-errors -Wno-deferred-out-of-scope-variables \
     <このディレクトリ>/embody.hs <対象の .hs ...> -i<ソースのディレクトリ>
:module + *Target ...
```

`-v0` ではバナーもプロンプトも出ない。ソースから読み込んで `*Target` をスコープに入れれば、export されていない
関数も `Target.f` で呼べる。`main` は走らない。

**1件の送り方**（`-fdefer-type-errors` は `let` の束縛にだけ効くので、振り分けに使う式はすべて `let` で束縛する）

```haskell
let xt_<id> = Target.f (x)
let xt_<id>_w = Embody.whnf xt_<id>
let xt_<id>_x = Embody.exact <前回: Nothing | Just "…"> xt_<id>
let xt_<id>_s = Embody.shown xt_<id>
let xt_<id>_t = Embody.typed xt_<id>
Embody.run "<id>" <時間の上限（マイクロ秒）> xt_<id>_w xt_<id>_x xt_<id>_s xt_<id>_t
```

- `run` は `whnf`（呼び出しが走ったか）→ `exact`（`Read`・`Show`・`Eq` があり、`show` が読み戻せて等しい）→
  `shown`（`Show` だけ：`show` を2000文字で切る。無限の構造でも返る）→ 型（`Typeable`）の順に試す。インスタンスが
  無い段は、その `let` が実行時の型エラーになるので飛ばす
- 例外・呼び出しの型エラーは `crash`（GHC の文言）。時間の上限は `System.Timeout.timeout`（解釈実行ではループも止まる）
- 評価中に SIGINT を受けると、その評価だけが止まり（`Interrupted.`）、GHCi は読み続ける
- メモリの上限は持たない（GHC の割り当ての上限 `enableAllocationLimit` は効くが、生きているメモリではなく割り当ての総量なので使っていない）
- `cut :: [a] -> [a]`（先頭100要素）: 比べたい値の横に「観測の行」を置くためのもの。切ったリストは
  `Read` を持つので `exact` になり、前回と比べられる
- 未定: `IO` を返す関数の実行（`observeIO`）。いまは実行せず、型を表示にする。浮動小数点の比較の許容誤差も無い（`==`）

---

## 新しい言語の embody を書くとき

embody を足すのは、その言語の REPL で「どんな値でも、有限の1行と、読み戻せるかどうか」を返す仕組みが要るとき。
次を満たすこと（どれも OCaml・Haskell で実機を相手に確かめてから決めた）。

1. **対象を知らない**: 評価する関数は、呼び出し側が送るテキストで届く。解釈は REPL 自身に任せる。対象のモジュールに
   コンパイル時に依存させない（対象ごとに作り直しが要り、型も分からない）
2. **言語の標準の範囲だけで書く**: 外部のパッケージに依存すると、対象の環境にそれが無いときに読み込めない
3. **必ず返る**: 例外・型エラー・構文エラー・インスタンスの欠け・無限ループのどれでも、マーカー付きの1行で返す。
   REPL が続きの入力を待つ状態（閉じていない括弧など）に入らないよう、呼び出しの渡し方を工夫する
   （OCaml: 呼び出しを文字列で渡す。Haskell: `let` で束縛してから実行の行を送る）
4. **1回の書き込みで印字する**: ほかの出力が割り込まないように。最後に出力を flush する
5. **時間の上限を持つ**: 1件ごとに。割り当ての無いループでも止まるかを確かめる
6. **読み戻せる値だけを `exact` にする**: 表示をその言語のソースとして評価して、等しいかを確かめる。表示しきれない値
   （無限・関数・循環）は、有限の表示（切った文字列や型）と `repr=unsupported` で返す
7. **前の結果を変数で参照できる**: 同じ回の後の評価から `xt_<id>` で使えるように、REPL のトップレベルに束縛する

書く前に、その言語の REPL を pipe でつないで、起動の確認・失敗の返り方・時間の上限・SIGINT で評価だけが止まるか、を
確かめる。手順と確かめた例は Xipe Totec の `document/拡張の手引き.md` §2.2。

書いたら、この ReadMe の「ファイル」の表と、その言語の節（読み込み方・送り方・確かめた版・制約）を足す。

---

## 使っているところ

- **Xipe Totec**: 評価のたびに、その言語の embody を REPL に読み込み、各行の呼び出しを必ず embody で包んで
  送る。embody の置き場所は、Xipe Totec の隣（`../embody/`）が既定で、環境変数 `XIPE_TOTEC_EMBODY_DIR` で
  変えられる
