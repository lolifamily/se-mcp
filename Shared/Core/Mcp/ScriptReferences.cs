using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Shared.Plugin;

namespace Shared.Mcp;

// The assembly files every script compiles against.
//
// The rule: compile against the file the runtime will load when the script runs. For each assembly
// name that is the first file the binder comes to, so Collect walks the places the binder looks, in
// its order, and keeps the first file it finds for each name:
//   1. assemblies already loaded: the binder hands these back before looking anywhere else
//   2. TPA, the runtime's list of framework files (.NET 10 only)
//   3. the loader's library directory, where Pulsar and Magnetar keep Harmony, NuGet and the like
//   4. the game directory: Bin64, DedicatedServer64 or Game2
//   5. the runtime directory (.NET Framework only): the framework itself, which the binder takes
//      from the GAC, where the same files sit
// That order was checked against the runtime's own loader events, in SE1 on .NET Framework 4.8 and
// on .NET 10, and in SE2. Steps 2 to 5 matter because most game DLLs are still unloaded when the
// first script compiles (in SE2, 70 of Game2's 174 were), and a script can't name a type from an
// unreferenced assembly (CS0246) or use a loaded type whose members mention one (CS0012).
//
// Nothing here loads an assembly: files are only read as metadata. The runtime loads one the first
// time a running script reaches into it, through the binder whose order this follows.
internal static class ScriptReferences
{
    // One file to compile against.
    internal sealed class Entry(string path, string name, string alias, Assembly loadFile)
    {
        public readonly string Path = path;           // the file Roslyn reads
        public readonly string Name = name;           // its assembly's simple name
        public readonly string Alias = alias;         // its extern alias, null for nearly all (see Scope)
        public readonly Assembly LoadFile = loadFile; // set only for a plugin loaded with LoadFile (see Loaded)
    }

    public static List<Entry> Collect(string gameDir)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // names whose file is decided
        var scope = new Scope();                                             // the names the plain references give a script

        // Step 1. Never aliased: these are what scripts compile against today.
        var entries = Loaded(taken);
        foreach (var entry in entries)
            if (ReadTypes(entry.Path, expectedName: null) is { } types)
                scope.Add(types);

        // Steps 2 to 5. A clashing assembly is set aside, to be aliased below.
        var clashing = new List<(string Path, string Name, string Clash)>();
        var found = 0;
        foreach (var path in Candidates(gameDir))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (taken.Contains(name) || ReadTypes(path, name) is not { } types) continue;
            taken.Add(name);
            found++;
            if (scope.Clash(types) is { } clash)
            {
                clashing.Add((path, name, clash));
                continue;
            }
            scope.Add(types);
            entries.Add(new Entry(path, name, null, null));
        }

        // Every script declares every alias (Compiler.InitShared), and an alias spelled like a namespace
        // or a type a script uses shadows it there (CS0118). So aliases are named last, once all plain
        // references are in, and one spelled like a name in them gets a '_' more until it isn't.
        var spelled = scope.Names();
        foreach (var (path, name, clash) in clashing)
        {
            var alias = AliasOf(name);
            while (!spelled.Add(alias)) alias += "_";
            Common.Logger.Info($"referencing {name} as extern alias {alias}: it would change what {clash} means in a script");
            entries.Add(new Entry(path, name, alias, null));
        }
        Common.Logger.Info($"{entries.Count - found} loaded assemblies referenced, {found} not loaded yet ({clashing.Count} of them under an extern alias)");
        return entries;
    }

    // Step 1. Assembly.Load(name) asks the binder which copy it resolves that name to, so where several
    // copies are loaded, scripts get the one the runtime uses. It fails for assemblies loaded with
    // LoadFile, which is how Pulsar loads cached GitHub plugins, under randomized names. Of those the
    // highest version is kept, with its instance: at run time the binder can't find them by name either,
    // so Compiler's AssemblyResolve handler hands that instance back.
    // Every loaded name is taken, referenced or not: the binder answers it with the loaded copy, so no
    // file found later may stand in for it.
    private static List<Entry> Loaded(HashSet<string> taken)
    {
        var loadContext = new Dictionary<string, string>();   // name -> the file the binder resolves it to
        var loadFile = new Dictionary<string, Assembly>();    // name -> the highest version loaded by LoadFile
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (asm.IsDynamic) continue;
            var name = asm.GetName().Name;
            if (name == null || loadContext.ContainsKey(name)) continue;
            taken.Add(name);
            try
            {
                loadContext[name] = Assembly.Load(name).Location;
            }
            catch
            {
                if (string.IsNullOrEmpty(asm.Location)) continue;
                if (!loadFile.TryGetValue(name, out var prev) || VersionOf(asm) > VersionOf(prev))
                    loadFile[name] = asm;
            }
        }

        return loadContext.Select(kv => new Entry(kv.Value, kv.Key, null, null))
            .Concat(loadFile.Select(kv => new Entry(kv.Value.Location, kv.Key, null, kv.Value)))
            .Where(e => !string.IsNullOrEmpty(e.Path) && !LeftOut(e.Path))   // empty: loaded from bytes
            .ToList();
    }

    private static Version VersionOf(Assembly asm) => asm.GetName().Version ?? new Version(0, 0);

    // Loaded, but not referenced. VRage.Native.dll: Roslyn fails reading its metadata (0bf2d88).
    // Mono.Cecil*: its types resolve ambiguously across the loaded assemblies, so naming one is CS0433
    // (5d51df6).
    private static bool LeftOut(string path)
    {
        var file = Path.GetFileName(path);
        return file == "VRage.Native.dll" || file.StartsWith("Mono.Cecil", StringComparison.Ordinal);
    }

    // Steps 2 to 5. A directory that isn't there adds nothing.
    private static IEnumerable<string> Candidates(string gameDir)
    {
        // Pulsar and Magnetar both load Harmony from their library directory.
        var harmony = typeof(HarmonyLib.Harmony).Assembly.Location;
        var library = string.IsNullOrEmpty(harmony) ? null : Path.GetDirectoryName(harmony);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa)
        {
            Common.Logger.Info($"looking for assemblies not loaded yet in: TPA, {library}, {gameDir}");
            return tpa.Split(Path.PathSeparator).Concat(Probe(library)).Concat(Probe(gameDir));
        }

        // .NET Framework: there is no TPA. Where the game ships its own copy of a framework assembly,
        // the copy a script's reference binds to depends on binding redirects and on the framework's
        // own unification, per version asked for, and no fixed order gets that right. In SE1 all four
        // such names are loaded before the first compile, so step 1 settles them. Only *.dll from the
        // runtime directory: its *.exe are tools, never in the GAC.
        var runtime = RuntimeEnvironment.GetRuntimeDirectory();
        Common.Logger.Info($"looking for assemblies not loaded yet in: {library}, {gameDir}, {runtime}");
        return Probe(library).Concat(Probe(gameDir))
            .Concat(Files(runtime, "*.dll")).Concat(Files(Path.Combine(runtime, "WPF"), "*.dll"));
    }

    // What the loaders' resolvers try for a name: dir\name.dll, then dir\name.exe.
    private static IEnumerable<string> Probe(string dir) => Files(dir, "*.dll").Concat(Files(dir, "*.exe"));

    // Sorted, so the same files clash the same way on every start.
    private static IEnumerable<string> Files(string dir, string pattern) =>
        string.IsNullOrEmpty(dir) || !Directory.Exists(dir)
            ? []
            : Directory.GetFiles(dir, pattern).OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

    // An assembly's top-level types, as (full name, public?). Top-level types are all a name can
    // clash over (Scope): a nested type is only ever found through the type it is in.
    //
    // null when the file holds no assembly the binder would load under expectedName: the file is
    // missing (TPA lists every file named in the loader's deps.json, shipped or not), it's native, it's
    // a module with no manifest, or its assembly has another name. The binder looks a name up by file
    // name (TPA's keys, the resolvers' dir\name.dll), so nothing would ever load an assembly named
    // otherwise under this one. A null expectedName accepts any assembly (step 1: already loaded).
    private static List<(string Name, bool Public)> ReadTypes(string path, string expectedName)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var file = File.OpenRead(path);
            using var pe = new PEReader(file);
            if (!pe.HasMetadata) return null;
            var md = pe.GetMetadataReader();
            if (!md.IsAssembly) return null;
            if (expectedName != null && !string.Equals(md.GetString(md.GetAssemblyDefinition().Name),
                    expectedName, StringComparison.OrdinalIgnoreCase))
                return null;

            var types = new List<(string, bool)>();
            foreach (var handle in md.TypeDefinitions)
            {
                var type = md.GetTypeDefinition(handle);
                if (!type.GetDeclaringType().IsNil || IsEmbedded(md, type)) continue;
                var ns = md.GetString(type.Namespace);
                var name = md.GetString(type.Name);
                types.Add((ns.Length == 0 ? name : ns + "." + name,
                    (type.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public));
            }
            return types;
        }
        catch (Exception ex)
        {
            Common.Logger.Info($"can't read {path}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    // The names the plain references so far give a script, as name lookup finds them. With
    // ignore-accessibility it finds internal types as well as public ones, and it tries a simple name
    // in the global namespace (the types with no namespace, and each namespace's first part) before
    // any using. So before an assembly is added, Clash looks through its types for a name whose
    // meaning the assembly would change:
    //   a type's full name
    //     not there yet                    fine
    //     there twice or more              already broken, one more changes nothing
    //     there once, both sides internal  fine: refusing these would hide Microsoft.CSharp, which
    //                                      `dynamic` needs and which copies some CoreLib internals, all
    //                                      to spare a script that names one of those internals
    //     there once, either side public   CS0433 ("exists in both") for a name scripts can use today
    //   a type's own name, when it has no namespace and the global namespace has no such type yet
    //     the name a using brings in       hidden wherever a script wrote it: the internal `Exception`
    //                                      of a C++/CLI assembly hid System.Exception
    //     a namespace's first part         the two collide
    //   a namespace's first part, when the global namespace has no such namespace yet
    //     a type's own name                hidden, or collided with, the same way
    // The first such name is the clash. The assembly then goes in under an extern alias: its types
    // leave the global namespace and collide with nothing. They're still found through any signature
    // that leads to them, and by name as <alias>::Namespace.Type, the template declaring the alias in
    // every script (Compiler.InitShared).
    private sealed class Scope
    {
        private readonly Dictionary<string, (int Count, bool Public)> types = new();   // full name -> definitions
        private readonly HashSet<string> reach = [];   // a namespaced type's own name (List`1): what a using brings in
        private readonly HashSet<string> roots = [];   // each namespace's first part

        public void Add(List<(string Name, bool Public)> assembly)
        {
            foreach (var (name, isPublic) in assembly)
            {
                types[name] = types.TryGetValue(name, out var d) ? (d.Count + 1, d.Public || isPublic) : (1, isPublic);
                var dot = name.LastIndexOf('.');
                if (dot < 0) continue;
                reach.Add(name.Substring(dot + 1));
                roots.Add(name.Substring(0, name.IndexOf('.')));
            }
        }

        public string Clash(List<(string Name, bool Public)> assembly)
        {
            foreach (var (name, isPublic) in assembly)
            {
                if (types.TryGetValue(name, out var d))
                {
                    if (d.Count == 1 && (d.Public || isPublic)) return name;
                    continue;
                }
                var dot = name.IndexOf('.');
                if (dot < 0)
                {
                    if (reach.Contains(name) || roots.Contains(name)) return name;
                    continue;
                }
                var root = name.Substring(0, dot);
                if (!roots.Contains(root) && (reach.Contains(root) || types.ContainsKey(root))) return root;
            }
            return null;
        }

        // Every name a script can write on its own, a generic type's without the arity (List`1 is
        // List): what an extern alias must not be spelled like.
        public HashSet<string> Names()
        {
            var names = new HashSet<string>(roots);
            foreach (var name in reach.Concat(types.Keys.Where(n => n.IndexOf('.') < 0)))
            {
                var tick = name.IndexOf('`');
                names.Add(tick < 0 ? name : name.Substring(0, tick));
            }
            return names;
        }
    }

    // [Microsoft.CodeAnalysis.Embedded] types aren't counted: helpers a compiler generates into an
    // assembly that needs them (NullableAttribute and the like, of which many assemblies carry their
    // own copy), which lookup never sees from outside that assembly.
    private static bool IsEmbedded(MetadataReader md, TypeDefinition type)
    {
        foreach (var handle in type.GetCustomAttributes())
        {
            var ctor = md.GetCustomAttribute(handle).Constructor;
            var owner = ctor.Kind switch
            {
                HandleKind.MethodDefinition => md.GetMethodDefinition((MethodDefinitionHandle)ctor).GetDeclaringType(),
                HandleKind.MemberReference => md.GetMemberReference((MemberReferenceHandle)ctor).Parent,
                _ => default
            };
            if (IsNamed(md, owner, "Microsoft.CodeAnalysis", "EmbeddedAttribute")) return true;
        }
        return false;
    }

    private static bool IsNamed(MetadataReader md, EntityHandle type, string ns, string name)
    {
        switch (type.Kind)
        {
            case HandleKind.TypeDefinition:
                var definition = md.GetTypeDefinition((TypeDefinitionHandle)type);
                return md.StringComparer.Equals(definition.Name, name) && md.StringComparer.Equals(definition.Namespace, ns);
            case HandleKind.TypeReference:
                var reference = md.GetTypeReference((TypeReferenceHandle)type);
                return md.StringComparer.Equals(reference.Name, name) && md.StringComparer.Equals(reference.Namespace, ns);
            default:
                return false;
        }
    }

    // An assembly's extern alias: its name, with each character an identifier can't hold (the dots,
    // mostly) as '_'. VRage.CodeAnalysis becomes VRage_CodeAnalysis.
    private static string AliasOf(string name)
    {
        var alias = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return char.IsDigit(alias[0]) ? "_" + alias : alias;
    }
}
