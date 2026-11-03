using System;

namespace KydlCore.Ui
{
    /// <summary>
    /// The shared UI code (Kydl.Ui) logs through these hooks: in Unity they write to the console, in the game the sinks are set ONCE by the
    /// code that owns the kit (KydlCore.Bootstrap.Plugin.Awake; a mod built with its own copy of the kit points them at its own ModLog).
    /// <see cref="Owner"/> says WHICH mod the line belongs to while KydlCore draws one mod's tab: KydlCore sets it around the tab's page builder
    /// (<see cref="Within{T}"/>) and every control remembers it for its callbacks (<see cref="Wrap"/>), so a sink can tag the line with the mod
    /// and use that mod's own debug switch. Null = no mod (the kit itself, KydlCore's own pages, Unity).
    /// </summary>
    public static class UiLog
    {
        // Stryker disable once all : the default sink only writes to the Unity console (Unity itself, not reachable from the unit tests)
        public static Action<string> VerboseSink = s => UnityEngine.Debug.Log("[Kydl UI] " + s);
        // Stryker disable once all : the default sink only writes to the Unity console (Unity itself, not reachable from the unit tests)
        public static Action<string> WarnSink = s => UnityEngine.Debug.LogWarning("[Kydl UI] " + s);
        // Stryker disable once all : the default sink only writes to the Unity console (Unity itself, not reachable from the unit tests)
        public static Action<string> ErrorSink = s => UnityEngine.Debug.LogError("[Kydl UI] " + s);

        /// <summary>The plugin GUID of the mod whose UI code is running right now (null = none). Read it in a sink; set it only through <see cref="Within{T}"/> / <see cref="Wrap"/>.</summary>
        public static string Owner;

        public static void Verbose(string s) { VerboseSink?.Invoke(s); }
        public static void Warn(string s) { WarnSink?.Invoke(s); }
        public static void Error(string s) { ErrorSink?.Invoke(s); }

        /// <summary>Runs `work` with <see cref="Owner"/> = `owner` and puts the previous owner back afterwards, also when `work` throws. A null `work` gives default.</summary>
        public static T Within<T>(string owner, Func<T> work)
        {
            if (work == null) return default(T);
            var before = Owner;
            Owner = owner;
            try { return work(); }
            finally { Owner = before; }
        }

        /// <summary>`action` that runs with the <see cref="Owner"/> of the moment it was wrapped (a control made inside a mod's tab keeps that mod for its clicks). Null in, null out.</summary>
        public static Action Wrap(Action action)
        {
            if (action == null) return null;
            var owner = Owner;
            // Stryker disable once Boolean : the bool is only the type argument of Within, nobody reads the result
            return () => Within<bool>(owner, () => { action(); return true; });
        }

        /// <summary>The same for a callback with one argument.</summary>
        public static Action<T> Wrap<T>(Action<T> action)
        {
            if (action == null) return null;
            var owner = Owner;
            // Stryker disable once Boolean : the bool is only the type argument of Within, nobody reads the result
            return value => Within<bool>(owner, () => { action(value); return true; });
        }
    }
}
