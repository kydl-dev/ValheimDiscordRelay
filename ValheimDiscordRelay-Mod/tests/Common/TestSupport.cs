using System;
using System.IO;
using System.Reflection;

namespace ValheimDiscordRelay.Tests
{
    /// <summary>Where things are, found by walking up from the test assembly to the folder that holds Directory.Build.props.</summary>
    internal static class TestPaths
    {
        static string _testsRoot;

        /// ValheimDiscordRelay-Mod\tests
        public static string TestsRoot
        {
            get
            {
                if (_testsRoot != null) return _testsRoot;
                var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Directory.Build.props"))) dir = dir.Parent;
                if (dir == null) throw new DirectoryNotFoundException("Directory.Build.props not found above " + AppDomain.CurrentDomain.BaseDirectory);
                return _testsRoot = dir.FullName;
            }
        }

        /// ValheimDiscordRelay-Mod: the parent of tests (MIGRATION_DECISIONS A1).
        public static string ModDir { get { return Directory.GetParent(TestsRoot).FullName; } }
        /// The project root (the repo root): parent of ValheimDiscordRelay-Mod.
        public static string ProjectRoot { get { return Directory.GetParent(ModDir).FullName; } }
        /// src of the mod.
        public static string Src { get { return Path.Combine(ModDir, "src"); } }
        /// The TestLab suite folder of this mod.
        public static string SuiteDir { get { return Path.Combine(TestsRoot, "TestLab", "suites", "ValheimDiscordRelay"); } }

        /// Game / BepInEx / Jotunn DLLs: the central E:\Projects\Valheim\Claude\ref. KYDL_REF_DIR overrides (e.g. the installed game's Managed folder).
        public static string RefDir { get { return Env("KYDL_REF_DIR") ?? Path.GetFullPath(Path.Combine(ProjectRoot, "..", "Claude", "ref")); } }

        /// The built mod (the copy in assets\ is the real one, MIGRATION_DECISIONS A8). KYDL_MOD_DLL overrides.
        public static string ModDll { get { return Env("KYDL_MOD_DLL") ?? Path.Combine(ModDir, "assets", "ValheimDiscordRelay.dll"); } }

        /// KydlCore.dll, which every mod requires: the built KydlCore next to this project (E:\Projects\Valheim\KydlCore). KYDL_CORE_DLL overrides.
        public static string CoreDll
        {
            get
            {
                var env = Env("KYDL_CORE_DLL");
                if (env != null) return env;
                var spec = Path.GetFullPath(Path.Combine(ProjectRoot, "..", "KydlCore", "KydlCore-Mod", "assets", "KydlCore.dll"));
                return File.Exists(spec) ? spec : Path.GetFullPath(Path.Combine(ProjectRoot, "..", "KydlCore", "KydlCore-Mod", "build", "KydlCore.dll"));
            }
        }

        static string Env(string name)
        {
            var v = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
    }

    /// <summary>
    /// Loads the built mod DLL and reaches its internal static members by reflection (the mod keeps its logic internal; production code is
    /// not changed for tests). Game assemblies are only loaded when a test touches a type that needs them.
    /// </summary>
    internal static class ModAssembly
    {
        static Assembly _asm;
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        public static Assembly Load()
        {
            if (_asm != null) return _asm;
            if (!File.Exists(TestPaths.ModDll)) throw new FileNotFoundException("Built mod DLL not found. Build the mod first (dotnet build).", TestPaths.ModDll);
            return _asm = Assembly.LoadFrom(TestPaths.ModDll);
        }

        static Assembly _core;
        static Assembly CoreAsm()
        {
            if (_core != null) return _core;
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies()) if (a.GetName().Name == "KydlCore") return _core = a;
            return _core = Assembly.LoadFrom(TestPaths.CoreDll);
        }

        public static Type Type(string fullName)
        {
            var t = Load().GetType(fullName, false);
            if (t == null && fullName.StartsWith("KydlCore.")) t = CoreAsm().GetType(fullName, false);   // shared code lives in KydlCore.dll, not in the mod DLL
            if (t == null) throw new MissingMemberException("Type not found in the mod DLL: " + fullName);
            return t;
        }

        public static object GetStatic(string typeName, string field)
        {
            var f = Type(typeName).GetField(field, All);
            if (f == null) throw new MissingFieldException(typeName, field);
            return f.GetValue(null);
        }

        public static object GetStaticProperty(string typeName, string property)
        {
            var p = Type(typeName).GetProperty(property, All);
            if (p == null) throw new MissingMemberException(typeName, property);
            try { return p.GetValue(null); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
        }

        public static void SetStatic(string typeName, string field, object value)
        {
            var f = Type(typeName).GetField(field, All);
            if (f == null) throw new MissingFieldException(typeName, field);
            f.SetValue(null, value);
        }

        public static object Call(string typeName, string method, params object[] args)
        {
            MethodInfo m = null;
            foreach (var c in Type(typeName).GetMethods(All))
                if (c.Name == method && c.GetParameters().Length == args.Length) { m = c; break; }
            if (m == null) throw new MissingMethodException(typeName, method);
            try { return m.Invoke(null, args); }
            catch (TargetInvocationException e) { throw e.InnerException ?? e; }
        }
    }

    /// Makes the test process find game / BepInEx / Jotunn assemblies in the ref folder, and KydlCore.dll in KydlCore's build folder, when something asks for them.
    [SetUpFixture]
    public sealed class GameAssemblyResolver
    {
        [OneTimeSetUp]
        public void Install() { AppDomain.CurrentDomain.AssemblyResolve += Resolve; }

        [OneTimeTearDown]
        public void Remove() { AppDomain.CurrentDomain.AssemblyResolve -= Resolve; }

        static Assembly Resolve(object sender, ResolveEventArgs args)
        {
            var name = new AssemblyName(args.Name).Name;
            var path = Path.Combine(TestPaths.RefDir, name + ".dll");
            if (File.Exists(path)) return Assembly.LoadFrom(path);
            return name == "KydlCore" && File.Exists(TestPaths.CoreDll) ? Assembly.LoadFrom(TestPaths.CoreDll) : null;
        }
    }
}
