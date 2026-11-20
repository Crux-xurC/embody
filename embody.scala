// embody.scala -- Xipe Totec's embody for Scala 3's REPL (`dotty.tools.repl.Main`).
//
// Loaded with `:load <path>/embody.scala`. It uses only what the REPL itself carries: the Scala 3
// standard library, the JDK, and the compiler (its parser for `Embody.Load`, its ScriptEngine for
// reading a literal back). See ReadMe.full.md, "Scala".
//
// One entry is three REPL inputs, each a complete line:
//
//   Embody.start("<id>", <timeout ms>, <None | Some(("exact", "…")) | Some(("binary", "<base64>"))>)
//   val xt_<id> = Embody.call(() => pkg.Obj.f((a), (b))((c)))
//   Embody.finish("<id>", "pkg.Obj", "f", List(2, 1))
//
// A call that does not compile leaves `xt_<id>` unbound and the compiler's message on the REPL's
// output; `finish` then reports a `crash` with what reflection can tell.
//
// A seam for another JVM language (`embody.java` for Java) registers a helper after `Embody.setup`
// (`Embody.registerType(classOf[…])`): its values are written as Scala source and its failures told in
// its terms.

object Embody {
  import java.io.{ByteArrayInputStream, ByteArrayOutputStream, ObjectInputStream, ObjectOutputStream, ObjectStreamClass, OutputStream, PrintStream}
  import java.lang.reflect.{InvocationTargetException, Method, Modifier}
  import java.util.Base64
  import scala.collection.mutable

  // --- the entry under way ----------------------------------------------------------------------

  private enum Outcome {
    case NotRun
    case Value(v: Any, literal: Option[String], shown: String)
    case Crash(reason: String)
    case TimedOut
  }

  private final class Entry(val id: String, val timeoutMs: Int, val basis: Option[(String, String)]) {
    @volatile var outcome: Outcome = Outcome.NotRun
  }

  private val out: PrintStream = System.out
  @volatile private var current: Option[Entry] = None
  @volatile private var buildLog: String = ""

  private def write(text: String): Unit = out.synchronized { out.print(text); out.flush() }

  private def oneLine(text: String): String = text.replace("\r", "\\r").replace("\n", "\\n")

  /**
   * The version of the protocol with Xipe Totec (the three inputs, `ready`, `Load`; 2: `registerType`): Xipe Totec checks it
   * before starting, so an older embody is named instead of never looking ready.
   */
  val protocol = 2

  /** Prints the ready marker: the caller's ready probe goes through embody, so a REPL without it never looks ready. */
  def ready(): Unit = write("XT_" + "READY\n")

  /** The build's log (read only to explain a call that did not compile); "" for none. */
  def setup(log: String): Unit = buildLog = log

  // --- scripts -----------------------------------------------------------------------------------

  private val tried = mutable.ArrayBuffer.empty[String]
  private val loadedScripts = mutable.Set.empty[String]
  private val unreadable = mutable.Map.empty[String, String] // a script whose copy could not be made, and why

  /** A script's copy is about to be `:load`-ed. */
  def trying(script: String): Unit = tried += script

  /** The last line of a script's copy: reached only when the copy compiled. */
  def markLoaded(script: String): Unit = loadedScripts += script

  // --- names -------------------------------------------------------------------------------------

  // a class's name as Scala writes it: `$` separators as dots, the REPL's `rs$line$N$` wrapper gone
  private def sourceName(c: Class[?]): String = {
    val n = c.getName.replaceAll("""^rs\$line\$\d+\$""", "").replaceAll("""\$line\d+\$read\$""", "")
    val dotted = n.split('$').filter(_.nonEmpty).map(scala.reflect.NameTransformer.decode).mkString(".")
    dotted
  }

  private val primitive: Map[Class[?], String] = Map(
    classOf[java.lang.Integer] -> "Int", classOf[java.lang.Long] -> "Long", classOf[java.lang.Short] -> "Short",
    classOf[java.lang.Byte] -> "Byte", classOf[java.lang.Double] -> "Double", classOf[java.lang.Float] -> "Float",
    classOf[java.lang.Character] -> "Char", classOf[java.lang.Boolean] -> "Boolean", classOf[String] -> "String",
    classOf[scala.runtime.BoxedUnit] -> "Unit",
    java.lang.Integer.TYPE -> "Int", java.lang.Long.TYPE -> "Long", java.lang.Short.TYPE -> "Short",
    java.lang.Byte.TYPE -> "Byte", java.lang.Double.TYPE -> "Double", java.lang.Float.TYPE -> "Float",
    java.lang.Character.TYPE -> "Char", java.lang.Boolean.TYPE -> "Boolean", java.lang.Void.TYPE -> "Unit")

  // a function value (a lambda, an eta-expanded method); collections are functions too in Scala
  // (a Seq is a PartialFunction) and are not counted
  private def isFunction(v: Any): Boolean = v match {
    case null => false
    case _: Iterable[?] | _: Product | _: Option[?] => false
    case _: Function0[?] | _: Function1[?, ?] | _: Function2[?, ?, ?] | _: Function3[?, ?, ?, ?] => true
    case _ => v.getClass.getName.contains("$$Lambda")
  }

  // the enum a Scala 3 enum value belongs to (`calc.Color` for `Color.Red`, whose class is anonymous)
  private def enumClass(v: scala.reflect.Enum): Class[?] = {
    val c = v.getClass
    if (c.isAnonymousClass || c.getSimpleName.isEmpty) c.getSuperclass else c
  }

  private def elementType(items: Iterable[Any]): String = {
    val names = items.iterator.take(50).map(typeName).toSet
    if (names.isEmpty) "?" else if (names.size == 1) names.head else "Any"
  }

  /** The value's type as far as the running value tells (erasure leaves the static type unknown). */
  def typeName(v: Any): String = v match {
    case null => "Null"
    case _ if primitive.contains(v.getClass) => primitive(v.getClass)
    case a: Array[?] => s"Array[${primitive.getOrElse(a.getClass.getComponentType, sourceName(a.getClass.getComponentType))}]"
    case _: LazyList[?] => "LazyList[?]"
    case o: Option[?] => s"Option[${o.fold("?")(typeName)}]"
    case Left(a) => s"Either[${typeName(a)}, ?]"
    case Right(b) => s"Either[?, ${typeName(b)}]"
    case t: Product if v.getClass.getName.startsWith("scala.Tuple") => t.productIterator.map(typeName).mkString("(", ", ", ")")
    case m: scala.collection.Map[?, ?] =>
      val kind = if (v.isInstanceOf[scala.collection.mutable.Map[?, ?]]) "scala.collection.mutable.Map" else "Map"
      s"$kind[${elementType(m.keys)}, ${elementType(m.values)}]"
    case s: scala.collection.Set[?] => s"${collectionPrefix(v).getOrElse("Set")}[${elementType(s)}]"
    case s: Iterable[?] => s"${collectionPrefix(v).getOrElse(sourceName(v.getClass))}[${elementType(s)}]"
    case e: scala.reflect.Enum => sourceName(enumClass(e))
    case _ if isFunction(v) => "Function"
    case _ => sourceName(v.getClass)
  }

  // --- helpers: another JVM language's values, through a seam ------------------------------------
  //
  // A seam (another language's own helper, such as `embody.java` for Java) registers itself at the start
  // with `registerType`. embody.scala knows nothing of that language: it asks the helpers only where its
  // own writer, equality and diagnosis have no answer. Reading a value back stays embody's own (the text is
  // Scala it evaluates), so a helper only writes a value as Scala source -- given embody's writer for the
  // values inside -- and takes one apart for `equal`. Only the JDK's own types cross, so a helper written
  // in any JVM language can answer.

  private final class Helper(
      val covers: Class[?] => Boolean,
      val write: (Any, Any => String) => String,
      val parts: Any => Array[AnyRef],
      val diagnose: (String, String, Int) => String)

  private val helpers = mutable.ArrayBuffer.empty[Helper]

  /**
   * Register the helper a class's public static methods make (the first registered is asked first):
   *
   *   - `boolean covers(Class<?> c)`: whether values of that class are the helper's
   *   - `String write(Object v, java.util.function.Function<Object, String> inner)`: `v` as Scala source,
   *     `inner` writing a value inside it (null: it cannot); null when `v` cannot be written
   *   - `Object[] parts(Object v)`: what `v` is equal by, compared one by one; null: its own `equals`
   *   - `String diagnose(String module, String name, int count)`: why a call of `module.name` with `count`
   *     arguments did not compile, in the helper's language's terms; null when `module` is not its own
   *
   * A throw is taken as "no answer".
   */
  def registerType(c: Class[?]): Unit = {
    def m(name: String, ps: Class[?]*): Method = c.getMethod(name, ps*)
    val covers = m("covers", classOf[Class[?]])
    val write = m("write", classOf[Object], classOf[java.util.function.Function[?, ?]])
    val parts = m("parts", classOf[Object])
    val diagnose = m("diagnose", classOf[String], classOf[String], java.lang.Integer.TYPE)
    helpers += new Helper(
      cls => covers.invoke(null, cls).asInstanceOf[Boolean],
      (v, inner) =>
        write.invoke(null, v.asInstanceOf[AnyRef], new java.util.function.Function[Object, String] { def apply(x: Object): String = inner(x) })
          .asInstanceOf[String],
      v => parts.invoke(null, v.asInstanceOf[AnyRef]).asInstanceOf[Array[AnyRef]],
      (module0, name, count) => diagnose.invoke(null, module0, name, Integer.valueOf(count)).asInstanceOf[String])
  }

  private def covering(v: Any): Option[Helper] =
    if (v == null) None else helpers.find(h => try h.covers(v.getClass) catch { case _: Throwable => false })

  // --- literals ----------------------------------------------------------------------------------

  private def stringLiteral(s: String): String = {
    val b = new StringBuilder("\"")
    s.foreach {
      case '"' => b.append("\\\"")
      case '\\' => b.append("\\\\")
      case '\n' => b.append("\\n")
      case '\r' => b.append("\\r")
      case '\t' => b.append("\\t")
      case '\b' => b.append("\\b")
      case '\f' => b.append("\\f")
      case c if c < ' ' || c == '\u007f' => b.append(f"\\u${c.toInt}%04x")
      case c => b.append(c)
    }
    b.append('"').toString
  }

  private def charLiteral(c: Char): String = c match {
    case '\'' => "'\\''"
    case '\\' => "'\\\\'"
    case '\n' => "'\\n'"
    case '\r' => "'\\r'"
    case '\t' => "'\\t'"
    case c if c < ' ' || c == '\u007f' => f"'\\u${c.toInt}%04x'"
    case c => s"'$c'"
  }

  private def doubleLiteral(d: Double): String =
    if (d.isNaN) "Double.NaN" else if (d.isPosInfinity) "Double.PositiveInfinity" else if (d.isNegInfinity) "Double.NegativeInfinity"
    else java.lang.Double.toString(d)

  private def floatLiteral(f: Float): String =
    if (f.isNaN) "Float.NaN" else if (f.isPosInfinity) "Float.PositiveInfinity" else if (f.isNegInfinity) "Float.NegativeInfinity"
    else java.lang.Float.toString(f) + "f"

  // the collections whose companion `apply` rebuilds them from their elements
  private def collectionPrefix(v: Any): Option[String] = v match {
    case _: scala.collection.immutable.List[?] => Some("List")
    case _: scala.collection.immutable.Vector[?] => Some("Vector")
    case _: scala.collection.immutable.ListSet[?] => None
    case _: scala.collection.immutable.SortedSet[?] => None
    case _: scala.collection.immutable.Set[?] => Some("Set")
    case _: scala.collection.immutable.ArraySeq[?] => None
    case _: scala.collection.mutable.ArrayBuffer[?] => Some("scala.collection.mutable.ArrayBuffer")
    case _: scala.collection.mutable.ListBuffer[?] => Some("scala.collection.mutable.ListBuffer")
    case _: scala.collection.mutable.HashSet[?] => Some("scala.collection.mutable.HashSet")
    case _ => None
  }

  private final class TooDeep extends RuntimeException(null, null, false, false)

  /** Scala source that evaluates to a value equal to `v`, when this writer covers it. */
  def literal(v: Any): Option[String] =
    try Some(lit(v, 0)).filter(_.length <= 100000) catch { case _: TooDeep => None; case _: MatchError => None }

  private def lit(v: Any, depth: Int): String = {
    if (depth > 200) throw new TooDeep
    def all(items: Iterable[Any]) = items.iterator.map(lit(_, depth + 1)).mkString(", ")
    v match {
      case null => "null"
      case s: String => stringLiteral(s)
      case i: Int => i.toString
      case l: Long => s"${l}L"
      case s: Short => s"($s: Short)"
      case b: Byte => s"($b: Byte)"
      case d: Double => doubleLiteral(d)
      case f: Float => floatLiteral(f)
      case c: Char => charLiteral(c)
      case b: Boolean => b.toString
      case _: scala.runtime.BoxedUnit => "()"
      case b: BigInt => s"BigInt(${stringLiteral(b.toString)})"
      case b: BigDecimal => s"BigDecimal(${stringLiteral(b.toString)})"
      case None => "None"
      case Some(x) => s"Some(${lit(x, depth + 1)})"
      case Left(x) => s"Left(${lit(x, depth + 1)})"
      case Right(x) => s"Right(${lit(x, depth + 1)})"
      case a: Array[?] =>
        val elem = primitive.get(a.getClass.getComponentType)
        if (a.isEmpty) elem.map(e => s"Array.empty[$e]").getOrElse(throw new MatchError(v))
        else s"Array(${all(a.toSeq)})"
      case t: Product if v.getClass.getName.startsWith("scala.Tuple") => s"(${all(t.productIterator.toSeq)})"
      case m: scala.collection.immutable.Map[?, ?] if !m.isInstanceOf[scala.collection.immutable.SortedMap[?, ?]] && !m.isInstanceOf[scala.collection.immutable.ListMap[?, ?]] =>
        m.iterator.map { case (k, x) => s"${lit(k, depth + 1)} -> ${lit(x, depth + 1)}" }.mkString("Map(", ", ", ")")
      case m: scala.collection.mutable.HashMap[?, ?] =>
        m.iterator.map { case (k, x) => s"${lit(k, depth + 1)} -> ${lit(x, depth + 1)}" }.mkString("scala.collection.mutable.HashMap(", ", ", ")")
      case s: Iterable[?] if collectionPrefix(v).isDefined => s"${collectionPrefix(v).get}(${all(s)})"
      case e: java.lang.Enum[?] => s"${sourceName(e.getDeclaringClass)}.${e.name}"
      case e: scala.reflect.Enum if e.productArity == 0 => s"${sourceName(enumClass(e))}.${e.productPrefix}"
      case p: Product if isCaseClass(v) => s"${sourceName(v.getClass)}(${all(p.productIterator.toSeq)})"
      case _ if isModuleValue(v) => sourceName(v.getClass)
      case _ if covering(v).isDefined =>
        // a helper's value: its own text, embody writing the values inside
        val inner: Any => String = x => try lit(x, depth + 1) catch { case _: MatchError | _: TooDeep => null }
        Option(try covering(v).get.write(v, inner) catch { case _: Throwable => null }).getOrElse(throw new MatchError(v))
      case _ => throw new MatchError(v)
    }
  }

  // a case class: a Product whose companion has `apply` taking the product's elements
  private def isCaseClass(v: Any): Boolean = v match {
    case p: Product =>
      val c = v.getClass
      !c.isAnonymousClass && !isModuleValue(v) && {
        try {
          val companion = Class.forName(c.getName + "$", false, c.getClassLoader)
          companion.getMethods.exists(m => m.getName == "apply" && m.getParameterCount == p.productArity)
        } catch { case _: Throwable => false }
      }
    case _ => false
  }

  // a Scala `object` (its class ends in `$` and holds MODULE$)
  private def isModuleValue(v: Any): Boolean =
    v != null && v.getClass.getName.endsWith("$") && (try { v.getClass.getField("MODULE$"); true } catch { case _: Throwable => false })

  // --- equality ----------------------------------------------------------------------------------

  private def close(a: Double, b: Double): Boolean =
    (a.isNaN && b.isNaN) || a == b || math.abs(a - b) <= 1e-9 * math.max(1.0, math.max(math.abs(a), math.abs(b)))

  /** Equal as `compare` and the `exact` check see it: floats within 1e-9, containers element by element. */
  def equal(a: Any, b: Any): Boolean = (a, b) match {
    case (x: Double, y: Double) => close(x, y)
    case (x: Float, y: Float) => close(x.toDouble, y.toDouble)
    case (x: Array[?], y: Array[?]) => x.length == y.length && x.indices.forall(i => equal(x(i), y(i)))
    case (x: scala.collection.Map[?, ?], y: scala.collection.Map[?, ?]) =>
      x.size == y.size && x.forall { case (k, v) => y.asInstanceOf[scala.collection.Map[Any, Any]].get(k).exists(equal(v, _)) }
    case (x: scala.collection.Set[?], y: scala.collection.Set[?]) => x == y
    case (x: scala.collection.Seq[?], y: scala.collection.Seq[?]) => x.length == y.length && x.iterator.zip(y.iterator).forall(equal(_, _))
    case (x: Product, y: Product) if x.getClass == y.getClass && x.productArity > 0 && !isFunction(x) =>
      x.productArity == y.productArity && x.productIterator.zip(y.productIterator).forall(equal(_, _))
    case _ if a != null && b != null && a.getClass == b.getClass && covering(a).isDefined =>
      // a helper's value: part by part (floats within 1e-9, containers element by element), else its own equals
      val h = covering(a).get
      (try Option(h.parts(a)).zip(Option(h.parts(b))) catch { case _: Throwable => None }) match {
        case Some((x, y)) => x.length == y.length && x.indices.forall(i => equal(x(i), y(i)))
        case None => a == b
      }
    case _ => a == b
  }

  // --- reading a literal back --------------------------------------------------------------------

  private object Silent extends PrintStream(new OutputStream { def write(b: Int): Unit = () })

  // the compiler's ScriptEngine: a second compiler on the JVM's class path (the target with it);
  // its own messages are not wanted on the REPL's output
  private lazy val engine = quietly(new dotty.tools.repl.ScriptEngine())

  private def quietly[A](body: => A): A = {
    val (o, e) = (System.out, System.err)
    System.setOut(Silent); System.setErr(Silent)
    try Console.withOut(Silent)(Console.withErr(Silent)(body))
    finally { System.setOut(o); System.setErr(e) }
  }

  /** `text` evaluated as Scala; None when it does not compile or throws. */
  def readBack(text: String): Option[Any] =
    try quietly(Option(engine.eval(text)).orElse(if (text == "null") Some(null) else None)) catch { case _: Throwable => None }

  // --- binary ------------------------------------------------------------------------------------

  private def serialize(v: Any): Option[Array[Byte]] = v match {
    case s: java.io.Serializable if !isFunction(v) =>
      try {
        val bytes = new ByteArrayOutputStream()
        val o = new ObjectOutputStream(bytes)
        o.writeObject(s); o.close()
        Some(bytes.toByteArray)
      } catch { case _: Throwable => None }
    case _ => None
  }

  private def deserialize(bytes: Array[Byte], loader: ClassLoader): Any = {
    val in = new ObjectInputStream(new ByteArrayInputStream(bytes)) {
      override def resolveClass(d: ObjectStreamClass): Class[?] =
        try Class.forName(d.getName, false, loader) catch { case _: ClassNotFoundException => super.resolveClass(d) }
    }
    try in.readObject() finally in.close()
  }

  private def loaderOf(v: Any): ClassLoader =
    Option(v).flatMap(x => Option(x.getClass.getClassLoader)).getOrElse(getClass.getClassLoader)

  // Java serialization that reads back to an equal value
  private def binary(v: Any): Option[String] =
    serialize(v).filter(b => try equal(deserialize(b, loaderOf(v)), v) catch { case _: Throwable => false })
      .map(Base64.getEncoder.encodeToString)

  // --- one evaluation ----------------------------------------------------------------------------

  /** Begin entry `id`: what `call` and `finish` will report on. */
  def start(id: String, timeoutMs: Int, basis: Option[(String, String)]): Unit =
    current = Some(new Entry(id, timeoutMs, basis))

  private def reason(e: Throwable): String = e match {
    case t: InvocationTargetException if t.getCause != null => reason(t.getCause)
    case t: ExceptionInInitializerError if t.getCause != null => s"${t.getClass.getName} <- ${reason(t.getCause)}"
    case _ => e.getClass.getName + (if (e.getMessage == null) "" else ": " + e.getMessage)
  }

  private def shownOf(v: Any, literal: Option[String]): String = {
    val text = literal.getOrElse {
      try {
        if (v == null) "null"
        else if (isFunction(v)) "<function>"
        else String.valueOf(v)
      } catch { case e: Throwable => s"<display failed: ${e.getClass.getName}>" }
    }
    val one = oneLine(text)
    if (one.length > 10000) one.take(10000) + "..." else one
  }

  /**
   * Run `f` on a thread of its own under the entry's time limit, and the value's display with it (a
   * `toString` that does not end is part of the call). The JVM cannot stop a running thread: at the
   * limit the thread is left running and the entry is a `timeout` (the caller starts a new session).
   * The binding gets the value, or `null` (the type's default) after a crash or a timeout.
   */
  def call[T](f: () => T): T = current match {
    case None => null.asInstanceOf[T]
    case Some(entry) =>
      @volatile var result: Outcome = Outcome.Crash("the call did not run")
      val worker = new Thread(null, () => {
        result =
          try {
            val v = f()
            val l = literal(v)
            Outcome.Value(v, l, shownOf(v, l))
          } catch {
            case _: InterruptedException => Outcome.TimedOut
            case e: Throwable => Outcome.Crash(reason(e))
          }
      }, "embody-" + entry.id, 64L * 1024 * 1024)
      worker.setDaemon(true)
      worker.start()
      worker.join(math.max(entry.timeoutMs, 1).toLong)
      if (worker.isAlive) {
        worker.interrupt() // a sleep or a wait ends; a running loop does not
        result = Outcome.TimedOut
      }
      entry.outcome = result
      result match {
        case Outcome.Value(v, _, _) =>
          write(s"XT_CALLED_${entry.id}\n")
          v.asInstanceOf[T]
        case _ => null.asInstanceOf[T]
      }
  }

  private def compare(basis: Option[(String, String)], repr: String, v: Any): String = basis match {
    case None => "none"
    case Some(_) if repr == "unsupported" => "skipped"
    case Some(("exact", text)) => readBack(text) match {
      case Some(prev) => if (equal(prev, v)) "same" else "changed"
      case None => "changed"
    }
    case Some(("binary", b64)) =>
      try if (equal(deserialize(Base64.getDecoder.decode(b64), loaderOf(v)), v)) "same" else "changed"
      catch { case _: Throwable => "changed" }
    case Some(_) => "changed"
  }

  private def judge(entry: Entry, v: Any, literal: Option[String], shown: String): String = {
    val exact = literal.filter(text => readBack(text).exists(equal(_, v)) || (v == null && text == "null"))
    val (repr, bin) = exact match {
      case Some(_) => ("exact", None)
      case None => binary(v) match {
        case Some(b) => ("binary", Some(b))
        case None => ("unsupported", None)
      }
    }
    val lines = Seq(
      s"XT_BEGIN_${entry.id}",
      s"repr=$repr",
      s"type=${typeName(v)}",
      s"compare=${compare(entry.basis, repr, v)}",
      s"display=$shown") ++ bin.map("bin_base64=" + _) :+ s"XT_END_${entry.id}"
    lines.mkString("", "\n", "\n")
  }

  // --- why a call did not compile ----------------------------------------------------------------

  private def logHint(): String = {
    val build =
      try {
        val f = new java.io.File(buildLog)
        if (buildLog.nonEmpty && f.exists) {
          val lines = scala.io.Source.fromFile(f).getLines().toList
          lines.find(l => l.startsWith("XT_BUILD_ERROR ") || l.startsWith("XT_LOAD_ERROR ") || l.contains("[error]")).map { l =>
            if (l.startsWith("XT_BUILD_ERROR ")) s" (${l.stripPrefix("XT_BUILD_ERROR ").trim})"
            else if (l.startsWith("XT_LOAD_ERROR ")) s" (a script was not read: ${l.stripPrefix("XT_LOAD_ERROR ").trim})"
            else s" (the build failed: ${l.trim})"
          }.getOrElse("")
        } else ""
      } catch { case _: Throwable => "" }
    val scripts = tried.filterNot(loadedScripts.contains).toList match {
      case Nil => ""
      case xs => s" (did not load: ${xs.mkString(", ")}; the compiler's message is in the REPL output)"
    }
    build + scripts
  }

  private def scalaType(c: Class[?]): String =
    if (c.isArray) s"Array[${scalaType(c.getComponentType)}]" else primitive.getOrElse(c, sourceName(c))

  private def signature(name: String, m: Method): String =
    s"$name(${m.getParameters.map(p => s"${p.getName}: ${scalaType(p.getType)}").mkString(", ")}): ${scalaType(m.getReturnType)}"

  private def diagnose(module0: String, name: String, shape: List[Int]): String = {
    val loader = getClass.getClassLoader
    val obj = try Some(Class.forName(module0 + "$", false, loader)) catch { case _: Throwable => None }
    val argc = shape.sum
    // a script's module is its path without `.sc`, then its nested objects: `scripts/tool.Text`
    val head = module0.lastIndexOf('/') match {
      case -1 => module0.takeWhile(_ != '.')
      case i => module0.take(i + 1) + module0.drop(i + 1).takeWhile(_ != '.')
    }
    val script = (tried ++ unreadable.keys).find(_.stripSuffix(".sc") == head)
    obj match {
      case None if script.exists(s => !loadedScripts.contains(s)) =>
        val s = script.get
        unreadable.get(s) match {
          case Some(why) => s"the call did not compile: the script $s was not read: $why"
          case None => s"the call did not compile: the script $s did not load (the compiler's message is in the REPL output)"
        }
      case None => s"the call did not compile: no object $module0 in what was loaded${logHint()}"
      case Some(c) =>
        val encoded = scala.reflect.NameTransformer.encode(name)
        val all = c.getDeclaredMethods.filter(m => m.getName == encoded && !m.isSynthetic)
        if (all.isEmpty) s"the call did not compile: $module0 has no function $name"
        else if (all.forall(m => !Modifier.isPublic(m.getModifiers))) s"the call did not compile: $module0.$name is private"
        else {
          val public = all.filter(m => Modifier.isPublic(m.getModifiers))
          // a `using` list is compiled into more parameters: only fewer than the call gives is a wrong count
          if (!public.exists(_.getParameterCount >= argc))
            s"the call did not compile: $module0.$name takes ${public.map(_.getParameterCount).distinct.sorted.mkString(" or ")} argument(s), got $argc: ${public.map(signature(name, _)).mkString(" | ")}"
          else s"the call did not compile: the arguments do not fit ${public.map(signature(name, _)).mkString(" | ")} (the compiler's message is in the REPL output)"
        }
    }
  }

  /** Print entry `id`'s record in one write: XT_BEGIN_<id> … XT_END_<id>, or XT_ERROR_<id> crash|timeout. */
  def finish(id: String, module0: String, name: String, shape: List[Int]): Unit = {
    val text = current match {
      case Some(entry) if entry.id == id =>
        current = None
        entry.outcome match {
          case Outcome.NotRun =>
            // a helper's own terms first (a Java class's methods), else Scala's
            val why = helpers.iterator
              .flatMap(h => Option(try h.diagnose(module0, name, shape.sum) catch { case _: Throwable => null }))
              .nextOption()
              .getOrElse(diagnose(module0, name, shape))
            s"XT_ERROR_${id} crash ${oneLine(why)}\n"
          case Outcome.Crash(r) => s"XT_ERROR_${id} crash ${oneLine(r)}\n"
          case Outcome.TimedOut => s"XT_ERROR_${id} timeout\n"
          case Outcome.Value(v, l, shown) =>
            try judge(entry, v, l, shown) catch { case e: Throwable => s"XT_ERROR_${id} crash ${oneLine("judging the value: " + reason(e))}\n" }
        }
      case _ => s"XT_ERROR_${id} crash the entry was not started\n"
    }
    write(text)
  }

  /** An entry the caller refused to send (a call that would leave the REPL reading on): its crash record. */
  def fail(id: String, why: String): Unit = {
    current = None
    write(s"XT_ERROR_${id} crash ${oneLine(why)}\n")
  }

  // --- loading a script's definitions only -------------------------------------------------------
  //
  // `:load` runs a script's top level. `Load.write` writes a copy of each script holding only its
  // definitions, inside `object <script's path without .sc> { … }` (`` `scripts/tool` ``), and a
  // last line telling `markLoaded` that it compiled: `def`s, `val`s that run nothing (a literal, a
  // name or a chain of names, a tuple or a standard collection of those, a lambda), classes, traits,
  // objects, enums, type aliases, givens and extensions, `import`s and `export`s. Everything else
  // -- `println(…)`, `val n = f()`, a `//> using dep` (a comment: nothing is fetched) -- does not run.

  object Load {
    import dotty.tools.dotc.ast.untpd
    import dotty.tools.dotc.core.Contexts.{Context, ContextBase}
    import dotty.tools.dotc.parsing.Parsers.Parser
    import dotty.tools.dotc.util.SourceFile

    private val collections = Set("List", "Vector", "Seq", "Set", "Map", "Array", "Some", "Option", "Left", "Right", "Nil", "None")

    private def runsNothing(t: untpd.Tree)(using Context): Boolean = t match {
      case _: untpd.Literal | _: untpd.Number | _: untpd.Ident => true
      case untpd.Select(q, _) => runsNothing(q)
      case untpd.Parens(e) => runsNothing(e)
      case untpd.Typed(e, _) => runsNothing(e)
      case untpd.Tuple(es) => es.forall(runsNothing)
      case _: untpd.Function => true
      case untpd.InfixOp(l, op, r) => op.name.toString == "->" && runsNothing(l) && runsNothing(r)
      case untpd.Apply(untpd.Ident(n), args) => collections(n.toString) && args.forall(runsNothing)
      case untpd.Apply(untpd.TypeApply(untpd.Ident(n), _), args) => collections(n.toString) && args.forall(runsNothing)
      case untpd.Apply(untpd.Select(untpd.Ident(n), apply), args) => collections(n.toString) && apply.toString == "apply" && args.forall(runsNothing)
      case untpd.EmptyTree => false
      case _ => false
    }

    private def kept(t: untpd.Tree)(using Context): Boolean = t match {
      case _: untpd.DefDef | _: untpd.TypeDef | _: untpd.ModuleDef | _: untpd.Import | _: untpd.Export | _: untpd.ExtMethods => true
      case v: untpd.ValDef => runsNothing(v.rhs)
      case _ => false
    }

    /** A script's object name, as a call writes it: its path without `.sc` (`tool`, `` `scripts/tool` ``), so scripts of one file name in different directories stay apart. */
    def objectName(path: String): String = {
      val base = path.stripSuffix(".sc")
      if (base.matches("[A-Za-z_][A-Za-z0-9_]*")) base else s"`$base`"
    }

    /** `source` (the script at `rel`) with only its definitions, wrapped; or the parser's first error. */
    def definitions(rel: String, source: String): Either[String, String] = {
      given ctx: Context = (new ContextBase).initialCtx.fresh.setReporter(new dotty.tools.dotc.reporting.StoreReporter(null))
      val src = SourceFile.virtual(rel, source)
      val stats = new Parser(src).blockStatSeq()
      val errors = ctx.reporter.removeBufferedMessages
      errors.find(_.level >= dotty.tools.dotc.interfaces.Diagnostic.ERROR) match {
        case Some(d) =>
          val pos = d.pos
          Left(s"$rel:${if (pos.exists) pos.line + 1 else 0}: ${d.message}")
        case None =>
          val parts = stats.filter(kept).map(t => source.substring(t.span.start, t.span.end))
          Right(s"object ${objectName(rel)} {\n${parts.mkString("\n")}\n}\nEmbody.markLoaded(${stringLiteral(rel)})\n")
      }
    }

    /** For each script under `root` (paths relative to it), its copy under `outDir`; prints `XT_LOAD_ERROR <path> <reason>` for one it could not read. */
    def write(root: String, outDir: String, paths: List[String]): Unit =
      for (rel <- paths) {
        val src = new java.io.File(root, rel)
        val dst = new java.io.File(outDir, rel)
        dst.getParentFile.mkdirs()
        val result =
          try definitions(rel, new String(java.nio.file.Files.readAllBytes(src.toPath), "UTF-8"))
          catch { case e: Throwable => Left(reason(e)) }
        result match {
          case Right(text) => java.nio.file.Files.write(dst.toPath, text.getBytes("UTF-8"))
          case Left(why) =>
            unreadable(rel) = oneLine(why)
            Embody.write(s"XT_LOAD_ERROR $rel ${oneLine(why)}\n")
        }
      }
  }
}
