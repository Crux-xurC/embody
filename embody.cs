// embody.cs -- C#'s seam for embody.fsx: C#'s meaning brought to F# Interactive, on the side that receives
// the result. C# calls are compiled by F# Interactive with embody.fsx (C# text cannot be evaluated: Roslyn's
// scripting is not in the SDK); this file adds what F# does not know of C#:
//
//   - Seam.Await: a method returning Task, Task<T>, ValueTask or ValueTask<T> is called inside it, so the
//     entry's value is the T (a later row gets a T)
//   - the helper embody.fsx asks (Embody.registerType typeof<EmbodyCs.Seam>): C#'s positional records
//     written as their primary constructor's call (`Sample.Geo.Point(2.0, 3.0)`), read back and compared
//     property by property (a List inside element by element)
//   - Diagnose: why a call to a C# method did not compile, in C#'s terms (not public, an instance method, a
//     property, how many arguments its overloads take, their C# signatures)
//
// Built as a class library of its own in the caller's temp dir (only the framework, nothing fetched), and
// `#r`-ed into F# Interactive after embody.fsx:
//
//     #r "<dir>/EmbodyCs.dll"
//     Embody.registerType typeof<EmbodyCs.Seam>;;
//
// It works by reflection over what is loaded, so it does not reference the target. See ReadMe.md, "C#
// through F# Interactive (`embody.cs`)".

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace EmbodyCs
{
    public static class Seam
    {
        /// <summary>The version of the protocol with Xipe Totec (the inputs, Await, the helper): checked before starting.</summary>
        public const int Protocol = 1;

        // --- async ------------------------------------------------------------------------------------

        /// <summary>A Task's value: waited for on the call's own thread (embody.fsx's time limit applies).</summary>
        public static T Await<T>(Task<T> task) => task.GetAwaiter().GetResult();

        public static void Await(Task task) => task.GetAwaiter().GetResult();

        public static T Await<T>(ValueTask<T> task) => task.AsTask().GetAwaiter().GetResult();

        public static void Await(ValueTask task) => task.AsTask().GetAwaiter().GetResult();

        // --- the helper: C#'s positional records --------------------------------------------------------

        const BindingFlags Instance = BindingFlags.Public | BindingFlags.Instance;

        // a record (class or struct): the compiler writes a PrintMembers for it
        static bool IsRecord(Type t) =>
            t.GetMethod("PrintMembers", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(StringBuilder) }, null)
                is MethodInfo m && m.GetCustomAttribute<CompilerGeneratedAttribute>() != null;

        // the primary constructor: the public one whose parameters are, by name, every public property
        // (EqualityContract is protected); a record with properties set outside it has none
        static ConstructorInfo? Primary(Type t)
        {
            var props = t.GetProperties(Instance).Where(p => p.GetIndexParameters().Length == 0).Select(p => p.Name).ToHashSet();
            return t.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Where(c => !(c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == t))
                .FirstOrDefault(c => c.GetParameters().Select(p => p.Name!).ToHashSet().SetEquals(props));
        }

        /// <summary>A public, non-generic C# record with a primary constructor that holds all of it.</summary>
        public static bool Covers(Type t) =>
            t.IsVisible && !t.IsGenericType && IsRecord(t) && Primary(t) != null;

        public static string Head(Type t) => t.FullName!.Replace('+', '.');

        public static Type[] Parts(Type t) => Primary(t)!.GetParameters().Select(p => p.ParameterType).ToArray();

        public static object?[]? Split(object v)
        {
            var t = v.GetType();
            var c = Primary(t);
            if (c == null) return null;
            return c.GetParameters().Select(p => t.GetProperty(p.Name!, Instance)!.GetValue(v)).ToArray();
        }

        public static object Make(Type t, object?[] args) => Primary(t)!.Invoke(args);

        // --- C#'s names ---------------------------------------------------------------------------------

        static readonly Dictionary<Type, string> Keywords = new()
        {
            [typeof(bool)] = "bool", [typeof(byte)] = "byte", [typeof(sbyte)] = "sbyte", [typeof(char)] = "char",
            [typeof(short)] = "short", [typeof(ushort)] = "ushort", [typeof(int)] = "int", [typeof(uint)] = "uint",
            [typeof(long)] = "long", [typeof(ulong)] = "ulong", [typeof(float)] = "float", [typeof(double)] = "double",
            [typeof(decimal)] = "decimal", [typeof(string)] = "string", [typeof(object)] = "object", [typeof(void)] = "void",
        };

        /// <summary>A type as C# writes it: int, int[], List&lt;int&gt;, int?, (int, string), Sample.Geo.Point.</summary>
        public static string CsName(Type t)
        {
            if (t.IsByRef) return CsName(t.GetElementType()!);
            if (Keywords.TryGetValue(t, out var k)) return k;
            if (t.IsArray) return CsName(t.GetElementType()!) + "[" + new string(',', t.GetArrayRank() - 1) + "]";
            if (t.IsGenericParameter) return t.Name;
            if (t.IsGenericType)
            {
                var args = t.GetGenericArguments().Select(CsName).ToArray();
                var d = t.GetGenericTypeDefinition();
                if (d == typeof(Nullable<>)) return args[0] + "?";
                if (d.FullName != null && d.FullName.StartsWith("System.ValueTuple`")) return "(" + string.Join(", ", args) + ")";
                var n = t.Name;
                return n.Substring(0, n.IndexOf('`')) + "<" + string.Join(", ", args) + ">";
            }
            return t.Namespace is "System" or "System.Collections.Generic" or "System.Threading.Tasks" ? t.Name : (t.FullName ?? t.Name).Replace('+', '.');
        }

        static string Param(ParameterInfo p)
        {
            var mod = p.IsOut ? "out " : p.ParameterType.IsByRef ? (p.IsIn ? "in " : "ref ") : p.GetCustomAttribute<ParamArrayAttribute>() != null ? "params " : "";
            var dflt = p.HasDefaultValue ? " = " + (p.DefaultValue switch { null => "null", string s => "\"" + s + "\"", bool b => b ? "true" : "false", var x => Convert.ToString(x, System.Globalization.CultureInfo.InvariantCulture) }) : "";
            return mod + CsName(p.ParameterType) + " " + p.Name + dflt;
        }

        /// <summary>A method as C# declares it: `static int Add(int a, int b)`.</summary>
        public static string Signature(MethodInfo m)
        {
            var generic = m.IsGenericMethodDefinition ? "<" + string.Join(", ", m.GetGenericArguments().Select(a => a.Name)) + ">" : "";
            return (m.IsStatic ? "static " : "") + CsName(m.ReturnType) + " " + m.Name + generic + "(" + string.Join(", ", m.GetParameters().Select(Param)) + ")";
        }

        // the counts of arguments a caller writes: out parameters left out, optional ones may be, params any
        static bool Takes(MethodInfo m, int count)
        {
            var ps = m.GetParameters().Where(p => !p.IsOut).ToArray();
            var required = ps.Count(p => !p.HasDefaultValue && p.GetCustomAttribute<ParamArrayAttribute>() == null);
            var hasParams = ps.Any(p => p.GetCustomAttribute<ParamArrayAttribute>() != null);
            return count >= required && (hasParams || count <= ps.Length);
        }

        static string Counts(MethodInfo m)
        {
            var ps = m.GetParameters().Where(p => !p.IsOut).ToArray();
            var required = ps.Count(p => !p.HasDefaultValue && p.GetCustomAttribute<ParamArrayAttribute>() == null);
            if (ps.Any(p => p.GetCustomAttribute<ParamArrayAttribute>() != null)) return required + " or more";
            return required == ps.Length ? required.ToString() : required + " to " + ps.Length;
        }

        // --- why a call did not compile ---------------------------------------------------------------

        // an F# assembly carries this attribute; C#'s do not
        static bool IsFSharp(Assembly a) =>
            a.GetCustomAttributes().Any(x => x.GetType().FullName == "Microsoft.FSharp.Core.FSharpInterfaceDataVersionAttribute")
            || a.FullName!.StartsWith("FSI-ASSEMBLY") || a.FullName.StartsWith("FSharp.");

        static IEnumerable<Type> Types(Assembly a)
        {
            try { return a.GetTypes(); }
            catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null)!; }
        }

        /// <summary>
        /// Why `module.name` called with `count` arguments did not compile, in C#'s terms; null when `module`
        /// is no type of a C# assembly (embody.fsx's F# diagnosis follows).
        /// </summary>
        public static string? Diagnose(string module, string name, int count)
        {
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !IsFSharp(a))
                .SelectMany(Types)
                .LastOrDefault(t => t.FullName != null && t.FullName.Replace('+', '.') == module);
            if (type == null) return null;

            const string head = "the call did not compile: ";
            if (!type.IsVisible) return head + module + " is not public";
            var all = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Where(m => m.Name == name && !m.IsSpecialName).ToArray();
            if (all.Length == 0)
            {
                var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance);
                return head + module + (prop != null ? "." + name + " is a property, not a method" : " has no method " + name);
            }
            var callable = all.Where(m => m.IsPublic && m.IsStatic).ToArray();
            if (callable.Length == 0)
            {
                var m = all[0];
                if (!m.IsStatic && m.IsPublic) return head + module + "." + name + " is an instance method: only public static methods are called (" + Signature(m) + ")";
                var access = m.IsPrivate ? "private" : m.IsAssembly ? "internal" : m.IsFamily ? "protected" : "not public";
                return head + module + "." + name + " is " + access + ": only public static methods are called";
            }
            var sigs = string.Join("; ", callable.Select(Signature));
            if (callable.Any(m => Takes(m, count)))
                return head + "the arguments do not fit " + sigs + " (fsi's message is in the REPL output)";
            var counts = string.Join(" or ", callable.Select(Counts).Distinct());
            return head + module + "." + name + " takes " + counts + " argument(s), got " + count + ": " + sigs;
        }
    }
}
