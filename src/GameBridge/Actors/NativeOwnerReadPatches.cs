using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace IceyCoop.GameBridge.Actors
{
    // Exact native call-site list, rather than replacing R.Player process-wide.
    public static class NativeOwnerReadPatches
    {
        private static readonly Dictionary<MethodBase,int> expected = new Dictionary<MethodBase,int>();
        public const int MethodCount = 30;

        public static void Install(Harmony harmony)
        {
            InstallOne(harmony,typeof(PlayerJumpAbility),"Jump",5);
            InstallOne(harmony,typeof(PlayerUpRisingAbility),"UpJumpAttack",3);
            InstallOne(harmony,typeof(PlayerHitGroundAbility),"HitGround",2);
            InstallOne(harmony,typeof(PlayerHitGroundAbility),"Update",2);
            InstallOne(harmony,typeof(PlayerHitGroundAbility),"OnStateMachineStateTransfer",1);
            InstallOne(harmony,typeof(PlayerAttackAbility),"Update",1);
            InstallOne(harmony,typeof(PlayerAttackAbility),"PlayerAttack",2);
            InstallOne(harmony,typeof(PlayerAttackAbility),"PlayerCirtPressAttack",3);
            InstallOne(harmony,typeof(PlayerFlashAbility),"FlashFace",1);
            InstallOne(harmony,typeof(PlayerFlashAbility),"FlashOnObstacle",1);
            InstallOne(harmony,typeof(PlayerFlashAbility),"Swipe",1);
            InstallOne(harmony,typeof(PlayerFlashAbility),"StateInit",1);
            InstallOne(harmony,typeof(PlayerHurtAbility),"HurtStiff",1);
            InstallOne(harmony,typeof(PlayerAnimEventListener),"Update",5);
            InstallOne(harmony,typeof(PlayerAnimEventListener),"Speed",1);
            InstallOne(harmony,typeof(PlayerAnimEventListener),"AirPhysic",1);
            InstallOne(harmony,AccessTools.TypeByName("PlayerAnimEventListener+<FlashPositionSet>c__Iterator5E"),"MoveNext",2);
            InstallOne(harmony,typeof(PlayWeaponAnim),"OnEnable",2);
            InstallOne(harmony,typeof(PlatformMovement),"get_m_rayLayer",2);
            // Round 60: ordinary attack charging and the Claymore combo upgrade reads.
            InstallOne(harmony,typeof(PlayerChargingAbility),"Charging",1);
            InstallOne(harmony,typeof(PlayerChargingAbility),"StartChargeGround",1);
            InstallOne(harmony,typeof(PlayerChargingAbility),"StartChargeInAir",1);
            InstallOne(harmony,typeof(Claymore),"CirtComboCheck",4);
            InstallOne(harmony,typeof(Claymore),"ComboCheck",1);
            InstallOne(harmony,typeof(Claymore),"AirCirtComboCheck",3);
            InstallOne(harmony,typeof(PlayerAction),"AbsorbEnergyBall",1);
            InstallOne(harmony,typeof(PlayerAction),"QTEHPRecover",1);
            // T09 execution reads: slow time and coroutine movement must use the
            // executing CharacterState's owner rather than the global R.Player.
            InstallOne(harmony,typeof(PlayerExecuteAbility),"GetExecuteEnemy",1);
            Type groundExecute = AccessTools.TypeByName("PlayerExecuteAbility+<GroundMoveToExecuteEnemy>c__Iterator5B");
            if(groundExecute==null) throw new TypeLoadException("PlayerExecuteAbility+<GroundMoveToExecuteEnemy>c__Iterator5B");
            InstallOne(harmony,groundExecute,"MoveNext",1);
            Type airExecute = AccessTools.TypeByName("PlayerExecuteAbility+<AirMoveToExecuteEnemy>c__Iterator5C");
            if(airExecute==null) throw new TypeLoadException("PlayerExecuteAbility+<AirMoveToExecuteEnemy>c__Iterator5C");
            InstallOne(harmony,airExecute,"MoveNext",1);
        }

        private static void InstallOne(Harmony harmony,Type type,string name,int count)
        {
            MethodInfo method=AccessTools.Method(type,name);
            if(method==null || method.IsStatic) throw new MissingMethodException(type.FullName,name);
            expected[method]=count;
            harmony.Patch(method,null,null,new HarmonyMethod(AccessTools.Method(typeof(NativeOwnerReadPatches),"Transpiler")));
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions,MethodBase __originalMethod)
        {
            List<CodeInstruction> list=new List<CodeInstruction>(instructions);
            int count=0;
            for(int i=0;i<list.Count-1;i++)
            {
                FieldInfo player=list[i].operand as FieldInfo;
                if(list[i].opcode!=OpCodes.Ldsfld || player==null || player.DeclaringType!=typeof(R) || player.Name!="Player") continue;
                FieldInfo field=list[i+1].operand as FieldInfo;
                MethodInfo method=list[i+1].operand as MethodInfo;
                string helper=null;
                MethodInfo replacement=null;
                if(field!=null && field.Name=="Attribute") helper="Attribute";
                if(method!=null)
                {
                    if(method.Name=="get_TimeController") helper="TimeController";
                    else if(method.Name=="get_Rigidbody2D") helper="Body";
                    else if(method.Name=="get_transform") helper="Transform";
                    else if(method.Name=="get_StateMachine") helper="StateMachine";
                    else if(method.Name=="get_Enhancement") helper="Enhancement";
                    else if(method.Name=="get_Action") helper="Action";
                    else if(method.Name=="GetComponent" && method.IsGenericMethod)
                    {
                        string componentHelper = __originalMethod.DeclaringType == typeof(PlayWeaponAnim)
                            ? "ComponentEffect" : "Component";
                        replacement=AccessTools.Method(typeof(NativeOwnerReadPatches),componentHelper).MakeGenericMethod(method.GetGenericArguments());
                    }
                }
                if(__originalMethod.DeclaringType==typeof(PlayWeaponAnim) && helper=="Action")
                {
                    helper=null;
                    replacement=AccessTools.Method(typeof(NativeOwnerReadPatches),"ActionEffect");
                }
                if(replacement==null && helper!=null) replacement=AccessTools.Method(typeof(NativeOwnerReadPatches),helper);
                if(replacement==null) throw new InvalidOperationException("Unrecognized owner read in "+__originalMethod+": "+list[i+1]);
                list[i].opcode=OpCodes.Ldarg_0;list[i].operand=null;
                list[i+1].opcode=OpCodes.Call;list[i+1].operand=replacement;
                count++;
            }
            if(count!=expected[__originalMethod]) throw new InvalidOperationException(__originalMethod+": expected "+expected[__originalMethod]+" owner reads, got "+count);
            BridgeContext.Info("[owner] "+__originalMethod.DeclaringType.Name+"."+__originalMethod.Name+" reads="+count);
            return list;
        }

        private static object Normalize(object owner)
        {
            if(owner is PlayerAnimEventListener || owner is CharacterState || owner is UnityEngine.Component) return owner;
            FieldInfo capture=owner.GetType().GetField("<>f__this",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic);
            if(capture==null || !(typeof(PlayerAnimEventListener).IsAssignableFrom(capture.FieldType) ||
                typeof(CharacterState).IsAssignableFrom(capture.FieldType)))
                throw new InvalidOperationException("Unrecognized native coroutine owner: "+owner.GetType());
            object capturedOwner=capture.GetValue(owner);
            if(capturedOwner==null) throw new InvalidOperationException("Native coroutine owner capture is null: "+owner.GetType());
            return capturedOwner;
        }

        public static PlayerTimeController TimeController(object owner) { return BridgeContext.GetTimeControllerForOwner(Normalize(owner)); }
        public static PlayerAttribute Attribute(object owner) { return BridgeContext.GetAttributeForOwner(Normalize(owner)); }
        public static StateMachine StateMachine(object owner) { return BridgeContext.GetStateMachineForOwner(Normalize(owner)); }
        public static SaveDataModel.Enhancement Enhancement(object owner) { return BridgeContext.GetEnhancementForOwner(Normalize(owner)); }
        public static PlayerAction Action(object owner) { return BridgeContext.GetActionForOwner(Normalize(owner)); }
        public static T Component<T>(object owner) where T:UnityEngine.Component { return Action(owner).GetComponent<T>(); }

        /// <summary>
        /// PlayWeaponAnim also lives on weapon effect prefabs: EffectController.Generate
        /// instantiates a clone whose root is never registered, so the strict owner
        /// resolution throws. For that one visual-only component the vanilla single-player
        /// semantics (the global player) are restored, because an effect must follow the
        /// player whose anim spawned it and the clone carries no owner link. This only
        /// affects unregistered instances; a registered player weapon still resolves its
        /// own owner. Logged once per owner type so the fallback stays visible.
        /// </summary>
        public static PlayerAction ActionEffect(object owner)
        {
            try { return Action(owner); }
            catch (InvalidOperationException) { LogEffectFallback(owner); return R.Player.Action; }
        }

        public static T ComponentEffect<T>(object owner) where T:UnityEngine.Component
        {
            try { return Component<T>(owner); }
            catch (InvalidOperationException) { LogEffectFallback(owner); return R.Player.GetComponent<T>(); }
        }

        private static readonly HashSet<string> effectFallbackLogged = new HashSet<string>();

        private static void LogEffectFallback(object owner)
        {
            string key = owner == null ? "<null>" : owner.GetType().Name;
            lock (effectFallbackLogged)
            {
                if (effectFallbackLogged.Add(key))
                {
                    BridgeContext.Info("[owner] effect fallback to R.Player for unbound " + key);
                }
            }
        }
        public static Rigidbody2D Body(object owner) { return Action(owner).GetComponent<Rigidbody2D>(); }
        public static Transform Transform(object owner) { return Action(owner).transform; }
    }
}
