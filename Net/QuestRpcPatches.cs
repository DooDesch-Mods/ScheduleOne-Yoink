using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using GameQuestManager = Il2CppScheduleOne.Quests.QuestManager;

namespace Yoink.Net
{
    /// <summary>
    /// Takes our "YNK|" payloads off the quest RPCs before the vanilla handler can choke on a guid that is not a
    /// quest, and hands them to the bus. Real quest guids fall through untouched, and other mods riding the same
    /// carrier (RVRepairVan, BreedToSeed) are unaffected because each only claims its own prefix.
    ///
    /// Patched by hand rather than through PatchAll so that a method that cannot be resolved on some build just
    /// disables co-op instead of taking the mod's other patches down with it.
    /// </summary>
    internal static class QuestRpcPatches
    {
        internal static void Apply(HarmonyLib.Harmony h)
        {
            TryPatch(h, "RpcLogic___SendQuestState", nameof(SendStatePrefix));
            TryPatch(h, "RpcLogic___ReceiveQuestState", nameof(ReceiveStatePrefix));
        }

        private static void TryPatch(HarmonyLib.Harmony h, string namePrefix, string prefixMethod)
        {
            try
            {
                MethodInfo target = typeof(GameQuestManager)
                    .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name.StartsWith(namePrefix, StringComparison.Ordinal));
                if (target == null) { Core.Log.Warning("[Net] could not find " + namePrefix + " - co-op winching disabled."); return; }

                MethodInfo pre = typeof(QuestRpcPatches).GetMethod(prefixMethod, BindingFlags.NonPublic | BindingFlags.Static);
                h.Patch(target, prefix: new HarmonyMethod(pre));
            }
            catch (Exception e) { Core.Log.Warning("[Net] patch " + namePrefix + " failed: " + e.Message); }
        }

        // Host side: RpcLogic___SendQuestState(string guid, EQuestState state) - the guid is arg 0.
        private static bool SendStatePrefix(string __0)
        {
            if (YoinkMsg.TryDecode(__0, out YoinkMsg m)) { YoinkNet.DispatchServerIntent(m); return false; }
            return true;
        }

        // Client side: RpcLogic___ReceiveQuestState(NetworkConnection conn, string guid, EQuestState state) - arg 1.
        private static bool ReceiveStatePrefix(string __1)
        {
            if (YoinkMsg.TryDecode(__1, out YoinkMsg m)) { YoinkNet.DispatchClientState(m); return false; }
            return true;
        }
    }
}
