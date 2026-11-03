namespace ValheimDiscordRelay
{
    /// <summary>Pure rules that need no game types (unit-tested).</summary>
    internal static class Functions
    {
        /// <summary>A functionality is on only when [General] EnableAll and its own [Section] Enable are both on.</summary>
        /// <param name="enableAll">The global switch.</param>
        /// <param name="own">The functionality's own switch.</param>
        /// <returns>True when the functionality should run.</returns>
        internal static bool IsOn(bool enableAll, bool own)
        {
            return enableAll && own;
        }
    }
}