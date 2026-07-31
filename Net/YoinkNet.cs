using System;
using System.Globalization;
using Il2CppFishNet;
using Il2CppFishNet.Managing;
using GameQuestManager = Il2CppScheduleOne.Quests.QuestManager;
using EQuestState = Il2CppScheduleOne.Quests.EQuestState;

namespace Yoink.Net
{
    /// <summary>What a winch message asks for.</summary>
    internal enum YoinkOp
    {
        /// <summary>Client -> host: I am reeling this object toward this anchor.</summary>
        PullStart = 1,
        /// <summary>Client -> host: I stopped.</summary>
        PullStop = 2,
        /// <summary>Host -> everyone: whoever is driving this vehicle, apply this pull locally.</summary>
        DriverPull = 3,
        /// <summary>Host -> everyone: driver pull is over.</summary>
        DriverStop = 4,
    }

    /// <summary>One winch message. Ids and vectors ride as text, which the carrier below is happy to take.</summary>
    internal sealed class YoinkMsg
    {
        internal YoinkOp Op;
        internal string TargetId = string.Empty;
        internal Vector3 PivotLocal;
        internal Vector3 Anchor;

        private const string Prefix = "YNK";

        internal string Encode()
        {
            CultureInfo inv = CultureInfo.InvariantCulture;
            return string.Join("|", new[]
            {
                Prefix, ((int)Op).ToString(inv), TargetId,
                PivotLocal.x.ToString("R", inv), PivotLocal.y.ToString("R", inv), PivotLocal.z.ToString("R", inv),
                Anchor.x.ToString("R", inv), Anchor.y.ToString("R", inv), Anchor.z.ToString("R", inv),
            });
        }

        internal static bool TryDecode(string raw, out YoinkMsg msg)
        {
            msg = null;
            if (string.IsNullOrEmpty(raw) || raw.Length < 4 || raw[0] != 'Y' || !raw.StartsWith(Prefix + "|", StringComparison.Ordinal)) return false;

            string[] p = raw.Split('|');
            if (p.Length != 9) return false;

            try
            {
                CultureInfo inv = CultureInfo.InvariantCulture;
                msg = new YoinkMsg
                {
                    Op = (YoinkOp)int.Parse(p[1], inv),
                    TargetId = p[2],
                    PivotLocal = new Vector3(float.Parse(p[3], NumberStyles.Float, inv), float.Parse(p[4], NumberStyles.Float, inv), float.Parse(p[5], NumberStyles.Float, inv)),
                    Anchor = new Vector3(float.Parse(p[6], NumberStyles.Float, inv), float.Parse(p[7], NumberStyles.Float, inv), float.Parse(p[8], NumberStyles.Float, inv)),
                };
                return true;
            }
            catch { msg = null; return false; }
        }

        public override string ToString() => Op + " " + TargetId;
    }

    /// <summary>
    /// Host-authoritative message bus for the winch, riding the game's quest RPCs exactly like
    /// RVRepairVan/Net/NetworkBus.cs does - client intents on SendQuestState (a ServerRpc), host state on
    /// ReceiveQuestState (an ObserversRpc), both carrying a private "YNK|" payload that the vanilla handler never
    /// sees because our Harmony prefix takes it off the wire first. No custom FishNet serializer is involved.
    ///
    /// Traffic is tiny by design: a pull sends one message when it starts and one when it stops. The direction is
    /// not streamed - the anchor is frozen when reeling begins (that is the design), so every machine can derive
    /// the pull direction itself from the anchor and the object's current pivot.
    /// </summary>
    internal static class YoinkNet
    {
        internal static Action<YoinkMsg> OnServerIntent;   // host: a client wants to pull
        internal static Action<YoinkMsg> OnClientState;    // client: the host is telling us something

        private static NetworkManager Nm
        {
            get { try { return InstanceFinder.NetworkManager; } catch { return null; } }
        }

        /// <summary>True while in a real networked session. False = offline single-player, where nothing is sent.</summary>
        internal static bool Online
        {
            get { var nm = Nm; try { return nm != null && (nm.IsServer || nm.IsClient); } catch { return false; } }
        }

        /// <summary>True on the host - the authority for every pull that is not handed to a driver's client.</summary>
        internal static bool IsServer
        {
            get { var nm = Nm; try { return nm != null && nm.IsServer; } catch { return false; } }
        }

        private static GameQuestManager Qm
        {
            get { try { return GameQuestManager.Instance; } catch { return null; } }
        }

        internal static void SendToHost(YoinkMsg m)
        {
            try
            {
                var qm = Qm;
                if (qm == null) { Core.Log.Warning("[Net] no QuestManager - " + m + " dropped."); return; }
                qm.SendQuestState(m.Encode(), EQuestState.Inactive);
            }
            catch (Exception e) { Core.Log.Warning("[Net] send failed: " + e.Message); }
        }

        internal static void BroadcastToAll(YoinkMsg m)
        {
            try
            {
                var qm = Qm;
                if (qm == null) return;
                qm.ReceiveQuestState(null, m.Encode(), EQuestState.Inactive);
            }
            catch (Exception e) { Core.Log.Warning("[Net] broadcast failed: " + e.Message); }
        }

        internal static void DispatchServerIntent(YoinkMsg m)
        {
            try { OnServerIntent?.Invoke(m); } catch (Exception e) { Core.Log.Warning("[Net] server dispatch failed: " + e.Message); }
        }

        /// <summary>
        /// On a listen server the game relays a client's SendQuestState to ALL observers, so the host actually
        /// receives client intents through this path too. Route by role, the same way RVRepairVan does.
        /// </summary>
        internal static void DispatchClientState(YoinkMsg m)
        {
            try
            {
                if (IsServer) OnServerIntent?.Invoke(m);
                else OnClientState?.Invoke(m);
            }
            catch (Exception e) { Core.Log.Warning("[Net] dispatch failed: " + e.Message); }
        }
    }
}
