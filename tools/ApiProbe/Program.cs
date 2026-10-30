using System.Reflection;

// ApiProbe <ManagedDir> <command> [args]
//   types  <regex>             list type names matching regex
//   dump   <TypeName>...       list a type's base type, fields, properties and method signatures
//   find   <memberRegex>       list types that declare a member matching regex
// Inspection only: MetadataLoadContext never executes code from the inspected assemblies.
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: ApiProbe <ManagedDir> types|dump|find <args>");
    return 2;
}

string managed = args[0];
var paths = Directory.GetFiles(managed, "*.dll").ToList();
using var mlc = new MetadataLoadContext(new PathAssemblyResolver(paths), "mscorlib");
// MINEKILL_PROBE_ASSEMBLY selects another assembly of the game (e.g. System.Numerics.dll).
string target = Environment.GetEnvironmentVariable("MINEKILL_PROBE_ASSEMBLY") ?? "Assembly-CSharp.dll";
Assembly game = mlc.LoadFromAssemblyPath(Path.Combine(managed, target));
const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

Type[] SafeTypes()
{
    try { return game.GetTypes(); }
    catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray()!; }
}

string Name(Type? t)
{
    if (t == null) return "?";
    try { return t.IsGenericType ? $"{t.Name.Split('`')[0]}<{string.Join(",", t.GetGenericArguments().Select(Name))}>" : t.Name; }
    catch { return t.Name; }
}

string Sig(MethodBase m)
{
    string ps;
    try { ps = string.Join(", ", m.GetParameters().Select(p => $"{Name(p.ParameterType)} {p.Name}")); }
    catch { ps = "<unresolved>"; }
    string ret = m is MethodInfo mi ? Name(mi.ReturnType) + " " : "";
    string vis = m.IsPublic ? "public" : m.IsPrivate ? "private" : "internal/protected";
    return $"{vis}{(m.IsStatic ? " static" : "")} {ret}{m.Name}({ps})";
}

switch (args[1])
{
    case "types":
    {
        var re = new System.Text.RegularExpressions.Regex(args.Length > 2 ? args[2] : ".");
        foreach (var t in SafeTypes().Where(t => re.IsMatch(t.FullName ?? t.Name)).OrderBy(t => t.FullName))
            Console.WriteLine($"{t.FullName}  : {Name(SafeBase(t))}");
        break;
    }
    case "dump":
        foreach (string tn in args.Skip(2))
        {
            Type? t = game.GetType(tn) ?? SafeTypes().FirstOrDefault(x => x.Name == tn);
            if (t == null) { Console.WriteLine($"## {tn}: NOT FOUND"); continue; }
            Console.WriteLine($"## {t.FullName} : {Name(SafeBase(t))}");
            foreach (var f in Try(() => t.GetFields(All))) Console.WriteLine($"  field {(f.IsPublic ? "public" : "private")}{(f.IsStatic ? " static" : "")} {Name(TryType(() => f.FieldType))} {f.Name}");
            foreach (var p in Try(() => t.GetProperties(All))) Console.WriteLine($"  prop  {Name(TryType(() => p.PropertyType))} {p.Name}");
            foreach (var m in Try(() => t.GetConstructors(All).Cast<MethodBase>().Concat(t.GetMethods(All))))
                if (!m.Name.StartsWith("<")) Console.WriteLine($"  meth  {Sig(m)}");
        }
        break;
    case "find":
    {
        var re = new System.Text.RegularExpressions.Regex(args[2]);
        foreach (var t in SafeTypes())
            foreach (var m in Try(() => t.GetMembers(All)))
                if (re.IsMatch(m.Name)) Console.WriteLine($"{t.FullName}.{m.Name} ({m.MemberType})");
        break;
    }
    default:
        Console.Error.WriteLine("unknown command");
        return 2;
}
return 0;

static Type? SafeBase(Type t) { try { return t.BaseType; } catch { return null; } }
static Type? TryType(Func<Type> f) { try { return f(); } catch { return null; } }
static IEnumerable<T> Try<T>(Func<IEnumerable<T>> f) { try { return f().ToList(); } catch { return Array.Empty<T>(); } }
