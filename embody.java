// embody.java -- Java's seam for embody.scala: Java's meaning brought to Scala 3's REPL, on the side that
// receives the result. Java calls are compiled by Scala's REPL with embody.scala (Java has no REPL that
// evaluates one call and goes on: jshell's state and output are not a pipe's); this file adds what Scala
// does not know of Java:
//
//   - embody.await: a method returning CompletableFuture, CompletionStage or Future is called inside it, so
//     the entry's value is the T (a later row gets a T)
//   - the helper embody.scala asks (Embody.registerType(classOf[embodyjava.embody])): Java records written
//     as their canonical constructor's call (`sample.Point(2.0, 3.0)`), java.util lists, sets, maps and
//     Optional as their constructors (a boxed element type written: `java.util.List.of[java.lang.Integer](0, 1)`),
//     read back by embody.scala itself (it evaluates the text) and compared part by part
//   - diagnose: why a call to a Java method did not compile, in Java's terms (not public, an instance
//     method, package-private, a field, how many arguments its overloads take, their Java signatures)
//
// Compiled alone with the JDK (`javac -d <dir> embody.java`; it works by reflection over what is loaded and
// references no target), and put on the REPL's JVM class path:
//
//     java -cp "<scala's lib>/*:<dir>:<the target's class path>" dotty.tools.repl.Main -usejavacp
//     :load <path-to-embody>/embody.scala
//     Embody.registerType(classOf[embodyjava.embody])
//
// The class is named as its file (`embody`), so `javac embody.java` compiles it as it is. See ReadMe.full.md,
// "Java through Scala's REPL (`embody.java`)".

package embodyjava;

import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.lang.reflect.Modifier;
import java.lang.reflect.RecordComponent;
import java.lang.reflect.Type;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.Collection;
import java.util.Comparator;
import java.util.HashMap;
import java.util.HashSet;
import java.util.LinkedHashMap;
import java.util.LinkedHashSet;
import java.util.LinkedList;
import java.util.List;
import java.util.Map;
import java.util.Optional;
import java.util.Set;
import java.util.TreeMap;
import java.util.TreeSet;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionStage;
import java.util.concurrent.ExecutionException;
import java.util.concurrent.Future;
import java.util.function.Function;
import java.util.stream.Collectors;

public final class embody {
    private embody() {}

    /** The version of the protocol with Xipe Totec (await, the helper): checked before starting. */
    public static final int PROTOCOL = 1;

    // --- futures -------------------------------------------------------------------------------------

    /** A future's value, waited for on the call's own thread (embody.scala's time limit applies). */
    public static <T> T await(CompletableFuture<T> f) throws Throwable { return get(f); }

    public static <T> T await(CompletionStage<T> f) throws Throwable { return get(f.toCompletableFuture()); }

    public static <T> T await(Future<T> f) throws Throwable { return get(f); }

    private static <T> T get(Future<T> f) throws Throwable {
        try {
            return f.get();
        } catch (ExecutionException e) {
            // the method's own exception, as a direct call would throw it
            throw e.getCause() != null ? e.getCause() : e;
        }
    }

    // --- the helper: writing Java's values as Scala source -------------------------------------------

    private static final Set<Class<?>> LISTS = Set.of(ArrayList.class, LinkedList.class);
    private static final Set<Class<?>> SETS = Set.of(HashSet.class, LinkedHashSet.class, TreeSet.class);
    private static final Set<Class<?>> MAPS = Set.of(HashMap.class, LinkedHashMap.class, TreeMap.class);

    // the classes of List.of / Set.of / Map.of (java.util.ImmutableCollections$…), public by their interface only
    private static boolean immutable(Class<?> c) { return c.getName().startsWith("java.util.ImmutableCollections$"); }

    private static boolean publicAllTheWay(Class<?> c) {
        for (Class<?> k = c; k != null; k = k.getEnclosingClass()) if (!Modifier.isPublic(k.getModifiers())) return false;
        return true;
    }

    /** Whether values of class `c` are this helper's: public records, java.util's own lists, sets, maps, Optional. */
    public static boolean covers(Class<?> c) {
        if (c.isRecord()) return publicAllTheWay(c);
        if (LISTS.contains(c) || SETS.contains(c) || MAPS.contains(c) || c == Optional.class) return true;
        return immutable(c) && (List.class.isAssignableFrom(c) || Set.class.isAssignableFrom(c) || Map.class.isAssignableFrom(c));
    }

    // a class as Scala writes it in source: a boxed primitive by its full name, nested classes with dots
    private static String scalaName(Class<?> c) {
        if (c == String.class) return "String";
        if (c.getName().startsWith("java.lang.")) return c.getName();
        return c.getCanonicalName();
    }

    // the type argument to write for these values: one non-generic class they all share, else none
    // (inference then decides; a text that does not read back is not `exact`)
    private static String typeArg(Collection<?> items) {
        Set<Class<?>> classes = new HashSet<>();
        for (Object x : items) {
            if (x == null) return null;
            classes.add(x.getClass());
        }
        if (classes.size() != 1) return null;
        Class<?> c = classes.iterator().next();
        if (c.getTypeParameters().length > 0 || c.getCanonicalName() == null || (c.isRecord() && !publicAllTheWay(c))) return null;
        if (c.isEnum() || c.getSuperclass() != null && c.getSuperclass().isEnum()) c = c.isEnum() ? c : c.getSuperclass();
        return scalaName(c);
    }

    // (List.of's and Set.of's own contains(null) throws)
    private static boolean hasNull(Collection<?> items) {
        for (Object x : items) if (x == null) return true;
        return false;
    }

    private static String args(Collection<?> items, Function<Object, String> inner) {
        List<String> parts = new ArrayList<>();
        for (Object x : items) {
            String t = inner.apply(x);
            if (t == null) return null;
            parts.add(t);
        }
        return String.join(", ", parts);
    }

    private static String typed(String head, String... targs) {
        for (String t : targs) if (t == null) return head;
        return head + "[" + String.join(", ", targs) + "]";
    }

    /** `v` as Scala source; `inner` writes a value inside it (null: it cannot). Null when `v` cannot be written. */
    public static String write(Object v, Function<Object, String> inner) {
        if (v == null) return null;
        Class<?> c = v.getClass();
        try {
            if (c.isRecord()) {
                List<Object> values = new ArrayList<>();
                for (RecordComponent rc : c.getRecordComponents()) values.add(rc.getAccessor().invoke(v));
                String a = args(values, inner);
                return a == null ? null : c.getCanonicalName() + "(" + a + ")";
            }
            if (v instanceof Optional<?> o) {
                if (o.isEmpty()) return "java.util.Optional.empty()";
                String x = inner.apply(o.get());
                return x == null ? null : typed("java.util.Optional.of", typeArg(List.of(o.get()))) + "(" + x + ")";
            }
            if (v instanceof List<?> l) {
                if (hasNull(l)) return null;
                String a = args(l, inner);
                if (a == null) return null;
                String e = typeArg(l);
                String of = typed("java.util.List.of", e) + "(" + a + ")";
                return immutable(c) ? of : (l.isEmpty() ? c.getName() + "()" : typed(c.getName(), e) + "(" + of + ")");
            }
            if (v instanceof Set<?> s) {
                if (hasNull(s)) return null;
                if (v instanceof TreeSet<?> t && t.comparator() != null) return null;
                String a = args(s, inner);
                if (a == null) return null;
                String e = typeArg(s);
                if (immutable(c)) return typed("java.util.Set.of", e) + "(" + a + ")";
                return s.isEmpty() ? c.getName() + "()" : typed(c.getName(), e) + "(" + typed("java.util.List.of", e) + "(" + a + "))";
            }
            if (v instanceof Map<?, ?> m) {
                if (v instanceof TreeMap<?, ?> t && t.comparator() != null) return null;
                String k = typeArg(m.keySet()), x = typeArg(m.values());
                List<String> entries = new ArrayList<>();
                for (Map.Entry<?, ?> en : m.entrySet()) {
                    if (en.getKey() == null || en.getValue() == null) return null;
                    String kt = inner.apply(en.getKey()), vt = inner.apply(en.getValue());
                    if (kt == null || vt == null) return null;
                    entries.add(typed("java.util.Map.entry", k, x) + "(" + kt + ", " + vt + ")");
                }
                String of = typed("java.util.Map.ofEntries", k, x) + "(" + String.join(", ", entries) + ")";
                return immutable(c) ? of : (m.isEmpty() ? c.getName() + "()" : typed(c.getName(), k, x) + "(" + of + ")");
            }
        } catch (ReflectiveOperationException | RuntimeException e) {
            return null;
        }
        return null;
    }

    /** What `v` is equal by, compared one by one by embody.scala (floats within 1e-9): a record's components, a list's elements, an Optional's value. */
    public static Object[] parts(Object v) {
        try {
            if (v != null && v.getClass().isRecord()) {
                RecordComponent[] rcs = v.getClass().getRecordComponents();
                Object[] out = new Object[rcs.length];
                for (int i = 0; i < rcs.length; i++) out[i] = rcs[i].getAccessor().invoke(v);
                return out;
            }
            if (v instanceof List<?> l) return l.toArray();
            if (v instanceof Optional<?> o) return o.isPresent() ? new Object[] {o.get()} : new Object[0];
        } catch (ReflectiveOperationException | RuntimeException e) {
            return null;
        }
        // sets and maps: their own equals
        return null;
    }

    // --- why a call did not compile ------------------------------------------------------------------

    // `sample.Outer.Inner` as a class: the dots from the right made `$` until one loads
    private static Class<?> load(String name) {
        String n = name;
        while (true) {
            try {
                return Class.forName(n, false, embody.class.getClassLoader());
            } catch (ClassNotFoundException | LinkageError e) {
                int i = n.lastIndexOf('.');
                if (i < 0) return null;
                n = n.substring(0, i) + "$" + n.substring(i + 1);
            }
        }
    }

    private static String typeName(Type t) {
        return t.getTypeName().replaceAll("\\bjava\\.lang\\.(?=[A-Z])", "").replaceAll("\\bjava\\.util\\.(?=[A-Z])", "");
    }

    /** A method as Java declares it: `static int add(int, int)`, `static int sum(int...)`. */
    public static String signature(Method m) {
        Type[] ps = m.getGenericParameterTypes();
        List<String> names = new ArrayList<>();
        for (int i = 0; i < ps.length; i++) {
            String n = typeName(ps[i]);
            if (m.isVarArgs() && i == ps.length - 1 && n.endsWith("[]")) n = n.substring(0, n.length() - 2) + "...";
            names.add(n);
        }
        String generic = m.getTypeParameters().length == 0 ? ""
            : "<" + Arrays.stream(m.getTypeParameters()).map(Type::getTypeName).collect(Collectors.joining(", ")) + "> ";
        return (Modifier.isStatic(m.getModifiers()) ? "static " : "") + generic + typeName(m.getGenericReturnType()) + " " + m.getName()
            + "(" + String.join(", ", names) + ")";
    }

    private static String access(int mods) {
        if (Modifier.isPrivate(mods)) return "private";
        if (Modifier.isProtected(mods)) return "protected";
        return "package-private";
    }

    /**
     * Why `module.name` called with `count` arguments did not compile, in Java's terms; null when `module` is
     * no Java class that is loaded (a Scala object: its class `module$` exists), so embody.scala's diagnosis follows.
     */
    public static String diagnose(String module, String name, int count) {
        Class<?> c = load(module);
        if (c == null || load(module + "$") != null) return null;
        String head = "the call did not compile: ";
        if (!publicAllTheWay(c)) return head + module + " is not public";
        List<Method> all = new ArrayList<>();
        for (Method m : c.getDeclaredMethods()) if (m.getName().equals(name) && !m.isSynthetic() && !m.isBridge()) all.add(m);
        if (all.isEmpty()) {
            for (Field f : c.getDeclaredFields()) if (f.getName().equals(name)) return head + module + "." + name + " is a field, not a method";
            return head + module + " has no method " + name;
        }
        // the order reflection gives is none: by count, then by signature
        all.sort(Comparator.comparingInt(Method::getParameterCount).thenComparing(embody::signature));
        List<Method> callable = all.stream().filter(m -> Modifier.isPublic(m.getModifiers()) && Modifier.isStatic(m.getModifiers())).toList();
        if (callable.isEmpty()) {
            Method m = all.get(0);
            int mods = m.getModifiers();
            if (Modifier.isPublic(mods)) return head + module + "." + name + " is an instance method: only public static methods are called (" + signature(m) + ")";
            return head + module + "." + name + " is " + access(mods) + ": only public static methods are called";
        }
        String sigs = callable.stream().map(embody::signature).collect(Collectors.joining("; "));
        // a varargs parameter is one argument (an array), as the call writes it
        if (callable.stream().anyMatch(m -> m.getParameterCount() == count))
            return head + "the arguments do not fit " + sigs + " (the compiler's message is in the REPL output)";
        String counts = callable.stream().map(m -> String.valueOf(m.getParameterCount())).distinct().collect(Collectors.joining(" or "));
        return head + module + "." + name + " takes " + counts + " argument(s), got " + count + ": " + sigs;
    }
}
