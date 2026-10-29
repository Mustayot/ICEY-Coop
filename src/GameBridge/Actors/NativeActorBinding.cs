using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using IceyCoop.Core;
using UnityEngine;

namespace IceyCoop.GameBridge.Actors
{
    public static class NativeActorBinding
    {
        private static void Set(object target, string name, object value)
        {
            Type type = target.GetType();
            FieldInfo field = null;
            while (type != null && field == null)
            {
                field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                type = type.BaseType;
            }
            if (field == null) throw new MissingFieldException(target.GetType().FullName, name);
            field.SetValue(target, value);
        }

        private static NativePlayerBinding Prepare(Component component)
        {
            byte id;
            if (!BridgeContext.TryResolveOwnerByRoot(component, out id))
                id = BridgeContext.ResolveOwner(component);
            GameObject root = component.gameObject;
            NativePlayerBinding binding = BridgeContext.EnsureBinding(id);
            binding.Root = root;
            binding.Abilities = root.GetComponent<PlayerAbilities>();
            binding.Action = root.GetComponent<PlayerAction>();
            binding.StateMachine = root.GetComponent<StateMachine>();
            binding.Weapon = root.GetComponent<Claymore>();
            binding.Msac = root.GetComponent<MultiSpineAnimationController>();
            binding.AnimListener = root.GetComponent<PlayerAnimEventListener>();
            binding.TimeController = root.GetComponent<PlayerTimeController>();
            if (id == PlayerIds.P1) binding.Attribute = R.Player.Attribute;
            if (binding.Attribute == null) throw new InvalidOperationException("Missing independent attribute for P"+id);
            if (BridgeContext.ActiveCreation != null) BridgeContext.ActiveCreation.Root = root;
            BridgeContext.RegisterRoot(id, root);
            BridgeContext.AttachToRoot(binding.Attribute, root);
            if(binding.Abilities!=null)
            {
                // Start can be the first patched lifecycle callback for vanilla
                // P1. Capture its already-created state objects too.
                foreach(FieldInfo field in typeof(PlayerAbilities).GetFields(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
                {
                    CharacterState state=field.GetValue(binding.Abilities) as CharacterState;
                    if(state!=null) BridgeContext.AttachToRoot(state,root);
                }
                if(binding.Abilities.hurt!=null) binding.HurtAbility=binding.Abilities.hurt;
            }
            // The deep copy must never retain P1's cached platform component.
            Set(binding.Attribute, "_platform", root.GetComponent<PlatformMovement>());
            return binding;
        }

        public static void PrefixAbilitiesAwake(PlayerAbilities __instance) { Prepare(__instance); }

        public static StateMachine LocalStateMachine(object owner)
        {
            return ((Component)owner).GetComponent<StateMachine>();
        }

        public static IEnumerable<CodeInstruction> TranspilerAbilitiesAwake(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> list = new List<CodeInstruction>(instructions);
            int count=0;
            for(int i=0;i<list.Count-1;i++)
            {
                FieldInfo field=list[i].operand as FieldInfo;
                MethodInfo method=list[i+1].operand as MethodInfo;
                if(list[i].opcode==OpCodes.Ldsfld && field!=null && field.DeclaringType==typeof(R) && field.Name=="Player" && method!=null && method.Name=="get_StateMachine")
                {
                    list[i].opcode=OpCodes.Ldarg_0;list[i].operand=null;
                    list[i+1].opcode=OpCodes.Call;list[i+1].operand=AccessTools.Method(typeof(NativeActorBinding),"LocalStateMachine");count++;
                }
            }
            if(count!=1) throw new InvalidOperationException("Abilities.Awake: expected one StateMachine binding, got "+count);
            return list;
        }

        public static void BindState(CharacterState state)
        {
            byte id;
            if(!BridgeContext.Registry.TryResolveOwner(state,out id))
                id=BridgeContext.ResolveOwner(state);
            NativePlayerBinding binding=BridgeContext.EnsureBinding(id);
            if(binding.Root==null) throw new InvalidOperationException("CharacterState root unavailable for P"+id);
            Set(state,"pab",binding.Abilities);Set(state,"pac",binding.Action);Set(state,"pAttr",binding.Attribute);
            Set(state,"stateMachine",binding.StateMachine);Set(state,"weapon",binding.Weapon);
            Set(state,"msac",binding.Msac);Set(state,"listener",binding.AnimListener);
        }

        public static void BindAttribute(Component component) { Set(component,"pAttr",Prepare(component).Attribute); }

        private static bool TryAttribute(PlayerAttribute attribute,out NativePlayerBinding binding)
        {
            byte id;binding=null;
            if(!BridgeContext.Registry.TryResolveOwner(attribute,out id))
            {
                if(R.Player!=null && ReferenceEquals(attribute,R.Player.Attribute)) return false;
                if(BridgeContext.IsClient && ReferenceEquals(attribute,BridgeContext.EnsureBinding(PlayerIds.P2).Attribute)) return false;
                if(!BridgeContext.IsOffline) throw new InvalidOperationException("Unregistered actor attribute; refusing global P1 getter");
                return false;
            }
            binding=BridgeContext.EnsureBinding(id);return binding.Root!=null;
        }

        public static bool PrefixPlatform(PlayerAttribute __instance,ref PlatformMovement __result)
        {
            NativePlayerBinding binding;if(!TryAttribute(__instance,out binding)) return true;
            __result=binding.Root.GetComponent<PlatformMovement>();return false;
        }

        public static bool PrefixBounds(PlayerAttribute __instance,ref Bounds __result)
        {
            NativePlayerBinding binding;if(!TryAttribute(__instance,out binding)) return true;
            __result=binding.Root.GetComponent<Collider2D>().bounds;return false;
        }

        public static bool PrefixCharging(PlayerAttribute __instance,ref bool __result)
        {
            NativePlayerBinding binding;if(!TryAttribute(__instance,out binding)) return true;
            string state=binding.StateMachine.currentState;
            __result=state=="Charging1" || state=="Charge1Ready" || state=="AirCharging";return false;
        }
    }
}
