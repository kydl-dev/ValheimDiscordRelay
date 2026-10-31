using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;

namespace ValheimDiscordRelay.Tests.Integration
{
    /// <summary>
    /// Game-update guard (user, 2026-10-28: "more automated testing, game logic included"): every attribute Harmony patch of the BUILT mod
    /// is checked against the game DLLs in Claude\ref the way Harmony checks it in game, without running the game:
    ///   * the patched method exists (declaring type, name, parameter types, getter / setter / constructor) and is not ambiguous;
    ///   * every parameter the patch asks for exists: ___field on the patched type (or a base type), a parameter of that name,
    ///     __instance only on instance methods, __0..__n in range.
    /// It fails when a game update renamed / removed a patched method, changed its parameters or renamed a field - mistakes that
    /// otherwise only show up in game. Read with Mono.Cecil (metadata only): Unity 6's DLLs use interface features .NET Framework cannot
    /// load, so neither reflection nor really applying the patches works in a test. Not covered (listed in the output): TargetMethod(s)
    /// classes (their code picks the target) and manual harmony.Patch(...) calls - put those targets into ManualTargets.
    /// Run after every update of Claude\ref; Claude\tools\game-api-diff.ps1 shows what changed.
    /// </summary>
    [TestFixture]
    public class PatchTargetsTests
    {
        /// "Namespace.Type:Method" (nested types "Outer/Inner") of assembly_valheim methods the mod patches by hand (harmony.Patch in code)
        /// or through TargetMethod(s), e.g. "Player:Update", "ItemDrop/ItemData:GetTooltip".
        static readonly string[] ManualTargets = { };

        static readonly string[] PatchNames = { "Prefix", "Postfix", "Transpiler", "Finalizer", "ILManipulator" };
        static readonly string[] Injected = { "__instance", "__result", "__resultRef", "__state", "__args", "__originalMethod", "__runOriginal", "__exception" };

        sealed class Target
        {
            public TypeReference Type;
            public string Method;
            public List<TypeReference> Args;
            public int Kind;   // HarmonyLib.MethodType: 0 Normal, 1 Getter, 2 Setter, 3 Constructor, 4 StaticConstructor, 5 Enumerator, 6 Async

            public Target Merge(Target over)
            {
                return new Target { Type = over.Type ?? Type, Method = over.Method ?? Method, Args = over.Args ?? Args, Kind = over.Kind != 0 ? over.Kind : Kind };
            }

            public override string ToString()
            {
                var kind = Kind == 0 ? "" : " [" + new[] { "", "getter", "setter", "constructor", "static constructor", "enumerator", "async" }[Math.Min(Kind, 6)] + "]";
                var args = Args == null ? "" : "(" + string.Join(", ", Args.Select(a => a.Name)) + ")";
                return (Type?.FullName ?? "?") + "." + (Method ?? "") + args + kind;
            }
        }

        static ModuleDefinition _mod;

        static ModuleDefinition Mod()
        {
            if (_mod != null) return _mod;
            var resolver = new DefaultAssemblyResolver();
            resolver.AddSearchDirectory(TestPaths.RefDir);
            return _mod = ModuleDefinition.ReadModule(TestPaths.ModDll, new ReaderParameters { AssemblyResolver = resolver });
        }

        static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (var t in types)
            {
                yield return t;
                foreach (var n in AllTypes(t.NestedTypes)) yield return n;
            }
        }

        static bool Harmony(CustomAttribute a, string name) { return a.AttributeType.Namespace == "HarmonyLib" && a.AttributeType.Name == name; }

        /// One [HarmonyPatch(...)]: Type / string / Type[] / MethodType arguments in any of Harmony's constructor orders.
        static Target Parse(CustomAttribute a)
        {
            var t = new Target();
            var strings = new List<string>();
            foreach (var arg in a.ConstructorArguments)
            {
                if (arg.Value is TypeReference tr) t.Type = tr;
                else if (arg.Value is string s) strings.Add(s);
                else if (arg.Value is CustomAttributeArgument[] arr && arr.Length > 0 && arr[0].Value is TypeReference) t.Args = arr.Select(x => (TypeReference)x.Value).ToList();
                else if (arg.Value is CustomAttributeArgument[] empty && empty.Length == 0 && arg.Type.GetElementType().FullName == "System.Type") t.Args = new List<TypeReference>();
                else if (arg.Type.FullName == "HarmonyLib.MethodType") t.Kind = Convert.ToInt32(arg.Value);
            }
            if (strings.Count == 1) t.Method = strings[0];
            else if (strings.Count >= 2) { t.Type = t.Type ?? new TypeReference("", strings[0], null, null); t.Method = strings[1]; }   // (string type, string method)
            return t;
        }

        static Target Merged(IEnumerable<CustomAttribute> attrs)
        {
            var t = new Target();
            foreach (var a in attrs.Where(x => Harmony(x, "HarmonyPatch"))) t = t.Merge(Parse(a));
            return t;
        }

        static TypeDefinition Resolve(TypeReference r)
        {
            try { return r?.Resolve(); } catch (Exception) { return null; }
        }

        static string Plain(TypeReference t) { return t.IsByReference ? ((ByReferenceType)t).ElementType.FullName : t.FullName; }

        static bool SameArgs(MethodDefinition m, List<TypeReference> args)
        {
            if (args == null) return true;
            if (m.Parameters.Count != args.Count) return false;
            for (var i = 0; i < args.Count; i++)
                if (Plain(m.Parameters[i].ParameterType) != Plain(args[i])) return false;
            return true;
        }

        /// Harmony's GetOriginalMethod: declared members first, then base types; no argument types + several overloads = ambiguous.
        static MethodDefinition Find(Target t, out string error)
        {
            error = null;
            var type = Resolve(t.Type);
            if (type == null) { error = "type not found: " + t.Type?.FullName; return null; }
            for (var ty = type; ty != null; ty = Resolve(ty.BaseType))
            {
                List<MethodDefinition> hits;
                switch (t.Kind)
                {
                    case 1: hits = ty.Properties.Where(p => p.Name == t.Method && p.GetMethod != null).Select(p => p.GetMethod).ToList(); break;
                    case 2: hits = ty.Properties.Where(p => p.Name == t.Method && p.SetMethod != null).Select(p => p.SetMethod).ToList(); break;
                    case 3: hits = ty.Methods.Where(m => m.IsConstructor && !m.IsStatic && SameArgs(m, t.Args ?? new List<TypeReference>())).ToList(); break;
                    case 4: hits = ty.Methods.Where(m => m.IsConstructor && m.IsStatic).ToList(); break;
                    default: hits = ty.Methods.Where(m => m.Name == t.Method && SameArgs(m, t.Args)).ToList(); break;
                }
                if (hits.Count > 1 && t.Args == null && t.Kind == 0) { error = "ambiguous (" + hits.Count + " overloads, give the argument types): " + t; return null; }
                if (hits.Count > 0) return hits[0];
                if (t.Kind >= 3) break;
            }
            error = "game method not found: " + t;
            return null;
        }

        static bool HasField(TypeDefinition type, string name)
        {
            for (var t = type; t != null; t = Resolve(t.BaseType))
                if (t.Fields.Any(f => f.Name == name)) return true;
            return false;
        }

        /// Harmony's injection rules (what it throws in game when a name no longer matches).
        static void CheckParameters(MethodDefinition patch, MethodDefinition original, List<string> errors)
        {
            if (patch.Name == "Transpiler" || patch.Name == "ILManipulator" || patch.CustomAttributes.Any(a => Harmony(a, "HarmonyTranspiler") || Harmony(a, "HarmonyILManipulator"))) return;
            var where = patch.DeclaringType.FullName + "." + patch.Name + " on " + original.DeclaringType.Name + "." + original.Name + ": ";
            foreach (var p in patch.Parameters)
            {
                if (p.CustomAttributes.Any(a => Harmony(a, "HarmonyArgument"))) continue;
                var n = p.Name;
                if (n == "__instance") { if (original.IsStatic) errors.Add(where + "__instance on a static method"); continue; }
                if (Injected.Contains(n)) continue;
                if (n.StartsWith("___"))
                {
                    if (!HasField(original.DeclaringType, n.Substring(3))) errors.Add(where + "field '" + n.Substring(3) + "' not found on " + original.DeclaringType.FullName);
                    continue;
                }
                if (n.StartsWith("__") && int.TryParse(n.Substring(2), out var index))
                {
                    if (index >= original.Parameters.Count) errors.Add(where + n + " but the method has " + original.Parameters.Count + " parameter(s)");
                    continue;
                }
                if (original.Parameters.All(o => o.Name != n))
                    errors.Add(where + "parameter '" + n + "' not found (has: " + string.Join(", ", original.Parameters.Select(o => o.Name)) + ")");
            }
        }

        [Test]
        public void EveryAttributePatch_MatchesTheCurrentGame()
        {
            var classes = AllTypes(Mod().Types).Where(t => t.CustomAttributes.Any(a => Harmony(a, "HarmonyPatch"))).OrderBy(t => t.FullName).ToList();
            if (classes.Count == 0) Assert.Pass("the mod has no attribute Harmony patches");
            var errors = new List<string>();
            var ok = new List<string>();
            var notChecked = new List<string>();
            foreach (var c in classes)
            {
                if (c.Methods.Any(m => m.IsStatic && (m.Name == "TargetMethod" || m.Name == "TargetMethods" || m.CustomAttributes.Any(a => Harmony(a, "HarmonyTargetMethod") || Harmony(a, "HarmonyTargetMethods")))))
                { notChecked.Add(c.FullName + " (TargetMethod(s): list its targets in ManualTargets)"); continue; }
                var classTarget = Merged(c.CustomAttributes);
                var patches = c.Methods.Where(m => m.IsStatic && (PatchNames.Contains(m.Name) || m.CustomAttributes.Any(a => PatchNames.Any(n => Harmony(a, "Harmony" + n))))).ToList();
                if (patches.Count == 0) { errors.Add(c.FullName + ": [HarmonyPatch] class without Prefix / Postfix / Transpiler / Finalizer"); continue; }
                foreach (var p in patches)
                {
                    var target = classTarget.Merge(Merged(p.CustomAttributes));
                    var original = Find(target, out var error);
                    if (original == null) { errors.Add(c.FullName + "." + p.Name + ": " + error); continue; }
                    CheckParameters(p, original, errors);
                    ok.Add(c.Name + "." + p.Name + " -> " + target);
                }
            }
            TestContext.Out.WriteLine("checked " + ok.Count + " patch method(s) in " + classes.Count + " class(es):");
            foreach (var x in ok) TestContext.Out.WriteLine("  " + x);
            foreach (var x in notChecked) TestContext.Out.WriteLine("  NOT CHECKED " + x);
            Assert.That(errors, Is.Empty, "patches that would fail in game (game updated? see Claude\\tools\\game-api-diff.ps1):\n  " + string.Join("\n  ", errors));
        }

        [Test]
        public void EveryManualTarget_ExistsInTheCurrentGame()
        {
            if (ManualTargets.Length == 0) Assert.Pass("no manual patch targets listed");
            var missing = new List<string>();
            foreach (var m in ManualTargets)
            {
                var parts = m.Split(':');
                var type = Mod().AssemblyResolver.Resolve(AssemblyNameReference.Parse("assembly_valheim")).MainModule.GetType(parts[0]);
                if (type == null || !type.Methods.Any(x => x.Name == parts[1])) missing.Add(m);
            }
            Assert.That(missing, Is.Empty, "manual patch targets missing in the current game DLLs (assembly_valheim)");
        }
    }
}
