using System;
using System.Reflection;
using HarmonyLib;
using IceyCoop.Core;

namespace IceyCoop.GameBridge.Actors
{
    /// <summary>
    /// Remote-driven ordinary attack charging (<c>PlayerChargingAbility</c>).
    ///
    /// Vanilla (IL 274652-274662) starts a charge from
    /// <c>Core.Input.Game.Charging.LongPressed</c> and ends it from
    /// <c>Core.Input.Game.Atk.OnReleased</c> inside
    /// <c>PlayerChargingAbility.Update</c> (IL 258613-258669). Both are process-wide
    /// keyboard reads, so on the host the remote actor would start charging when the
    /// host's own keyboard holds attack and release when the host lets go - cross-talk
    /// in both directions. The update path is therefore suppressed for the remote
    /// actor only, and the charge is driven from the remote action router, while the
    /// native gates (Enhancement.Charging, ChipManager.HasChipInScene, grounded) stay
    /// exactly as vanilla wrote them.
    /// </summary>
    public static class NativeChargingPatches
    {
        private const string AirChargeStateName = "AirCharging";

        /// <summary>Methods this class patches directly (counted by PatchInstaller).</summary>
        public const int PatchedMethodCount = 1;

        public static void Install(Harmony harmony)
        {
            MethodInfo update = AccessTools.Method(typeof(PlayerChargingAbility), "Update");
            if (update == null)
            {
                throw new MissingMethodException("PlayerChargingAbility", "Update");
            }
            harmony.Patch(update,
                new HarmonyMethod(AccessTools.Method(typeof(NativeChargingPatches), "PrefixUpdate")), null);
            BridgeContext.Info("[t17] PlayerChargingAbility.Update suppressed for the remote actor");
        }

        /// <summary>
        /// The remote actor must never react to the host's keyboard: its charges are
        /// released by <see cref="ReleaseCharge"/> when the client's own release edge
        /// arrives. The local player keeps the vanilla update path untouched.
        /// </summary>
        public static bool PrefixUpdate(PlayerChargingAbility __instance)
        {
            byte owner;
            if (BridgeContext.Registry.TryResolveOwner(__instance, out owner) && owner == PlayerIds.P2)
            {
                return false;
            }
            return true;
        }

        /// <summary>Starts (or keeps running) the native charge for the remote actor.</summary>
        public static bool StartCharge(PlayerInput input)
        {
            PlayerChargingAbility charge = ResolveCharge(input);
            if (charge == null)
            {
                return false;
            }
            charge.Charging();
            return true;
        }

        /// <summary>
        /// Replicates vanilla's release branch for the remote actor: with a chargeable
        /// weapon the charge is released (facing first), otherwise the state machine is
        /// moved to the end state and the charge is cancelled. Returns false when the
        /// actor was not in a charge state, so a stray release edge can never fabricate
        /// an attack.
        /// </summary>
        public static bool ReleaseCharge(PlayerInput input)
        {
            PlayerChargingAbility charge = ResolveCharge(input);
            if (charge == null)
            {
                return false;
            }
            StateMachine machine = Field<StateMachine>(charge, "stateMachine");
            PlayerAction action = Field<PlayerAction>(charge, "pac");
            Claymore weapon = Field<Claymore>(charge, "weapon");
            if (machine == null || action == null)
            {
                return false;
            }
            string state = machine.currentState;
            if (!IsChargeState(state))
            {
                return false;
            }
            bool inAir = state == AirChargeStateName;
            if (weapon != null && weapon.canChargeAttack)
            {
                int direction = ReadTempDir(action);
                action.TurnRound(direction);
                weapon.ReleaseCharge(inAir);
            }
            else
            {
                action.ChangeState((PlayerAction.StateEnum)(inAir ? 1 : 0), 1f);
                charge.CancelCharge();
            }
            return true;
        }

        /// <summary>State name for rejection logging: "the release found no charge" is
        /// otherwise indistinguishable from "the charge never started".</summary>
        public static string CurrentStateName(PlayerInput input)
        {
            PlayerChargingAbility charge = ResolveCharge(input);
            if (charge == null)
            {
                return "<no charge ability>";
            }
            try
            {
                StateMachine machine = Field<StateMachine>(charge, "stateMachine");
                return machine == null ? "<no machine>" : machine.currentState;
            }
            catch (Exception ex)
            {
                return "<error: " + ex.Message + ">";
            }
        }

        /// <summary>Gate snapshot for the trace: what the charge start would decide on.</summary>
        public static string Gates(PlayerInput input)
        {
            PlayerChargingAbility charge = ResolveCharge(input);
            if (charge == null)
            {
                return "charge=null";
            }
            string state = "?";
            try
            {
                StateMachine machine = Field<StateMachine>(charge, "stateMachine");
                if (machine != null)
                {
                    state = machine.currentState;
                }
            }
            catch (Exception ex)
            {
                state = "error:" + ex.Message;
            }
            string enhancement = "?";
            try
            {
                SaveDataModel.Enhancement data = BridgeContext.GetEnhancementForOwner(charge);
                enhancement = data == null ? "null" : data.Charging.ToString();
            }
            catch (Exception ex)
            {
                enhancement = "error:" + ex.Message;
            }
            string chip = "?";
            try
            {
                Type type = AccessTools.TypeByName("ChipManager");
                MethodInfo method = type == null ? null : AccessTools.Method(type, "HasChipInScene");
                chip = method == null ? "missing" : method.Invoke(null, null).ToString();
            }
            catch (Exception ex)
            {
                chip = "error:" + ex.Message;
            }
            string grounded = "?";
            try
            {
                PlayerAttribute attribute = Field<PlayerAttribute>(charge, "pAttr");
                grounded = attribute == null ? "null" : attribute.isOnGround.ToString();
            }
            catch (Exception ex)
            {
                grounded = "error:" + ex.Message;
            }
            return string.Format("state={0} enhancementCharging={1} hasChipInScene={2} onGround={3}",
                state, enhancement, chip, grounded);
        }

        private static PlayerChargingAbility ResolveCharge(PlayerInput input)
        {
            if (input == null)
            {
                return null;
            }
            FieldInfo pabField = AccessTools.Field(typeof(PlayerInput), "pab");
            object pab = pabField == null ? null : pabField.GetValue(input);
            if (pab == null)
            {
                return null;
            }
            FieldInfo chargeField = AccessTools.Field(pab.GetType(), "charge");
            return chargeField == null ? null : chargeField.GetValue(pab) as PlayerChargingAbility;
        }

        private static bool IsChargeState(string state)
        {
            if (string.IsNullOrEmpty(state))
            {
                return false;
            }
            FieldInfo table = AccessTools.Field(typeof(PlayerAction), "ChargeSta");
            string[] states = table == null ? null : table.GetValue(null) as string[];
            if (states == null)
            {
                return false;
            }
            for (int i = 0; i < states.Length; i++)
            {
                if (states[i] == state)
                {
                    return true;
                }
            }
            return false;
        }

        private static int ReadTempDir(PlayerAction action)
        {
            FieldInfo field = AccessTools.Field(typeof(PlayerAction), "tempDir");
            if (field == null)
            {
                return 3;
            }
            object value = field.GetValue(action);
            return value is int ? (int)value : 3;
        }

        private static T Field<T>(object owner, string name) where T : class
        {
            FieldInfo field = AccessTools.Field(typeof(CharacterState), name);
            if (field == null)
            {
                return null;
            }
            return field.GetValue(owner) as T;
        }
    }
}
