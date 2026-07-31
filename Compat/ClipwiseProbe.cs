// Deliberately in the global namespace: Clipwise finds this type by the cheap, safe lookup
// Assembly.GetType("ClipwiseProbe"), and only falls back to enumerating an assembly's types when that misses.
//
// That fallback is the reason this file exists at all. Clipwise's scanner calls Assembly.GetTypes(), which forces
// every type in the assembly to resolve - and doing that to a mod full of IL2CPP interop references can take the
// whole runtime down rather than throw something catchable. Yoink was hitting exactly that: the game died during
// boot in Clipwise's scan loop on roughly every other launch, with the log ending mid-scan and no exception
// anywhere. Being findable by name means the scan never enumerates us, and the crash has nothing to work with.
//
// Registering an actual clipboard category is a side benefit - the winch is one buyable tool, so there is nothing
// worth a tab of its own yet. If that changes, it goes here.
internal static class ClipwiseProbe
{
    internal static void Register()
    {
        // Nothing to register yet. The type's existence is the point.
    }
}
