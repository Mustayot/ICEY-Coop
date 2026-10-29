using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

/// <summary>
/// Drives the real client snapshot handler against an isolated SessionOrchestrator
/// while observing the live P1 presenter. The probe restores presenter state,
/// transform state, and presentation trace counters before returning.
/// </summary>
public static class RuntimePlaybackProbe
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private sealed class FieldValue
    {
        public FieldInfo Field;
        public object Value;
    }

    private sealed class TransformState
    {
        public object Transform;
        public object Position;
        public object Rotation;
        public object LocalScale;
    }

    public static void Run(object liveSession, Action<bool, string> assert, Action<string> log)
    {
        if (liveSession == null) throw new ArgumentNullException("liveSession");
        if (assert == null) throw new ArgumentNullException("assert");

        Type sessionType = liveSession.GetType();
        Type replicaFactoryType = FindType("IceyCoop.GameBridge.World.ReplicaFactory", sessionType.Assembly);
        Type presenterType = FindType("IceyCoop.GameBridge.Presentation.ActorPresenter", sessionType.Assembly);
        Type storeType = FindType("IceyCoop.Protocol.ReplicaStore", sessionType.Assembly);
        Type recordType = FindType("IceyCoop.Protocol.EntityRecord", sessionType.Assembly);
        Type gateType = FindType("IceyCoop.Protocol.SnapshotFreshnessGate", sessionType.Assembly);
        Type messageType = FindType("IceyCoop.Protocol.StateSnapshotMessage", sessionType.Assembly);
        Type entityType = FindType("IceyCoop.Protocol.EntitySnapshot", sessionType.Assembly);

        FieldInfo liveJoinField = RequireField(sessionType, "clientJoin", InstanceFlags);
        object liveJoin = liveJoinField.GetValue(liveSession);
        object liveJoinState = ReadProperty(liveJoin, "State");
        Require(liveJoinState != null && liveJoinState.ToString() == "Active", assert,
            "runtime playback requires the live client JoinState Active");

        FieldInfo playersField = RequireField(replicaFactoryType, "players", StaticFlags);
        IDictionary players = playersField.GetValue(null) as IDictionary;
        Require(players != null, assert, "ReplicaFactory.players is unavailable");
        object p1 = players.Contains((ulong)1) ? players[(ulong)1] : null;
        object p2 = players.Contains((ulong)2) ? players[(ulong)2] : null;
        Require(p1 != null && p2 != null, assert, "runtime playback requires real P1 and P2 presenters");
        Require(p1.GetType() == presenterType && p2.GetType() == presenterType, assert,
            "player dictionary entries are not ActorPresenter instances");
        Require(Convert.ToByte(ReadField(p1, "PlayerId")) == 1 && Convert.ToByte(ReadField(p2, "PlayerId")) == 2,
            assert, "presenter dictionary does not contain P1/P2 in the expected slots");

        object transform = ReadField(p1, "Transform");
        Require(transform != null, assert, "P1 presenter has no live Transform");
        TransformState transformState = CaptureTransform(transform);
        List<FieldValue> presenterFields = CaptureFields(p1, InstanceFlags);
        List<FieldValue> presenterStatics = CaptureFields(presenterType, StaticFlags);
        List<FieldValue> factoryStatics = CaptureFields(replicaFactoryType, StaticFlags);
        FieldInfo consumedHitKeysField = RequireField(replicaFactoryType, "consumedHitKeys", StaticFlags);
        HashSet<string> consumedHitKeys = consumedHitKeysField.GetValue(null) as HashSet<string>;
        Require(consumedHitKeys != null, assert, "ReplicaFactory hit-event dedupe set is unavailable");
        HashSet<string> consumedHitKeysBefore = new HashSet<string>(consumedHitKeys);

        try
        {
            float originalX = ReadFloatComponent(transformState.Position, "x");
            float originalY = ReadFloatComponent(transformState.Position, "y");
            float originalZ = ReadFloatComponent(transformState.Position, "z");
            object originalPositionTarget = ReadField(p1, "positionTarget");
            object fixture = Activator.CreateInstance(sessionType, true);
            Require(fixture != null, assert, "could not create isolated SessionOrchestrator fixture");
            SetField(fixture, "role", Enum.Parse(RequireField(sessionType, "role", InstanceFlags).FieldType, "Client"));
            SetField(fixture, "clientJoin", liveJoin); // Read-only borrow: handler only checks State.
            SetField(fixture, "clientBaselineEpoch", (uint)1);
            SetField(fixture, "telemetryEpoch", (uint)1);
            SetField(fixture, "telemetryEpochSet", true);

            object store = Activator.CreateInstance(storeType, true);
            SetField(fixture, "replicaStore", store);
            ApplyBaseline(store, recordType, 1, 10);

            object gate = ReadField(fixture, "snapshotFreshness");
            Require(gate != null && gateType.IsInstanceOfType(gate), assert,
                "fixture snapshot freshness gate is unavailable");
            Call(gate, "BeginBaseline", new object[] { (uint)1, (ulong)100, (ulong)10 });

            // Keep the real presenter on its current non-mutating animation path.
            // ApplySnapshot still runs the production position/HP logic, but cannot
            // restart a live Spine track for this synthetic regression sample.
            SetField(p1, "hasAppliedAnimation", true);
            SetField(p1, "appliedAnimId", (uint)0);
            SetField(p1, "activeSkeletonIndex", -1);
            SetField(p1, "appliedAnimationName", null);

            MethodInfo handler = RequireMethod(sessionType, "HandleClientSnapshot", new Type[] { typeof(byte[]) });
            byte[] newest = EncodeSnapshot(messageType, entityType, 42, 102, 10, 1,
                originalX + 1f, originalY, 0, 222);
            Invoke(handler, fixture, new object[] { newest });
            object acceptedTarget = ReadField(p1, "positionTarget");
            float acceptedX = ReadFloatComponent(acceptedTarget, "x");
            Require(Convert.ToInt32(ReadField(p1, "DisplayHp")) == 222, assert,
                "newest snapshot did not update P1 DisplayHp to 222");
            Require(Math.Abs(acceptedX - (originalX + 1f)) < 0.001f, assert,
                "newest snapshot did not update P1 positionTarget");
            Pass(assert, log, "new snapshot 42 applied to P1 presenter");

            byte[] older = EncodeSnapshot(messageType, entityType, 41, 101, 10, 1,
                originalX + 2f, originalY, 0, 111);
            Invoke(handler, fixture, new object[] { older });
            AssertPresenterUnchanged(p1, 222, acceptedX, assert,
                "older snapshot 41 rolled back the P1 presenter");
            Pass(assert, log, "older snapshot 41 did not roll back P1");

            byte[] duplicatePart = EncodeSnapshot(messageType, entityType, 42, 102, 10, 1,
                originalX + 3f, originalY, 0, 333);
            Invoke(handler, fixture, new object[] { duplicatePart });
            AssertPresenterUnchanged(p1, 222, acceptedX, assert,
                "duplicate snapshot 42 part rolled back the P1 presenter");
            Pass(assert, log, "duplicate snapshot 42 part did not roll back P1");

            object nextStore = Activator.CreateInstance(storeType, true);
            SetField(fixture, "replicaStore", nextStore);
            SetField(fixture, "clientBaselineEpoch", (uint)2);
            SetField(fixture, "telemetryEpoch", (uint)2);
            SetField(fixture, "telemetryEpochSet", true);
            ApplyBaseline(nextStore, recordType, 2, 20);
            Call(gate, "BeginBaseline", new object[] { (uint)2, (ulong)200, (ulong)20 });

            byte[] preBaselineTick = EncodeSnapshot(messageType, entityType, 99, 199, 20, 1,
                originalX + 4f, originalY, 0, 444);
            Invoke(handler, fixture, new object[] { preBaselineTick });
            AssertPresenterUnchanged(p1, 222, acceptedX, assert,
                "pre-baseline host tick snapshot changed the P1 presenter");
            Pass(assert, log, "old-scene tick 199 rejected after epoch 2 baseline tick 200");

            // Exercise the real reliable-event and snapshot handlers against a
            // fresh, isolated store. The synthetic enemy id has no GameObject or
            // presenter, so these decoded wire messages cannot change the live P1.
            Type eventType = FindType("IceyCoop.Protocol.WorldEventMessage", sessionType.Assembly);
            Type eventSubtypeType = FindType("IceyCoop.Protocol.WorldEventSubType", sessionType.Assembly);
            Type hitPayloadType = FindType("IceyCoop.Protocol.HitResolvedPayload", sessionType.Assembly);
            Type hitCodecType = FindType("IceyCoop.Protocol.HitResolvedCodec", sessionType.Assembly);
            Type outcomeType = FindType("IceyCoop.Core.HitOutcome", sessionType.Assembly);
            object eventStore = Activator.CreateInstance(storeType, true);
            ApplyBaseline(eventStore, recordType, 3, 30, (ulong)424242, (ulong)5, 1);
            SetField(fixture, "replicaStore", eventStore);
            SetField(fixture, "clientBaselineEpoch", (uint)3);
            SetField(fixture, "telemetryEpoch", (uint)3);
            SetField(fixture, "telemetryEpochSet", true);
            Call(gate, "BeginBaseline", new object[] { (uint)3, (ulong)300, (ulong)30 });

            MethodInfo worldEventHandler = RequireMethod(sessionType, "HandleClientWorldEvent", new Type[] { typeof(byte[]) });
            MethodInfo snapshotHandler = RequireMethod(sessionType, "HandleClientSnapshot", new Type[] { typeof(byte[]) });
            byte[] hit31 = EncodeHitResolvedEvent(eventType, eventSubtypeType, hitPayloadType,
                hitCodecType, outcomeType, 31, 424242, 6, 900001, 555);
            Invoke(worldEventHandler, fixture, new object[] { hit31 });
            AssertEntity(eventStore, recordType, 424242, 6, 1, 31, assert,
                "real WorldEvent handler did not preserve the midfight hit revision");
            Pass(assert, log, "reliable HitResolved seq 31 applied to synthetic baseline enemy rev 5");

            byte[] betweenHits = EncodeSnapshotForEntity(messageType, entityType,
                1, 301, 31, 424242, 1, 6, 0f, 0f, 0, 555);
            Invoke(snapshotHandler, fixture, new object[] { betweenHits });
            AssertEntity(eventStore, recordType, 424242, 6, 1, 31, assert,
                "same-cursor snapshot changed event cursor/revision unexpectedly");
            Pass(assert, log, "same-cursor snapshot between reliable hits merged through real handler");

            byte[] hit32 = EncodeHitResolvedEvent(eventType, eventSubtypeType, hitPayloadType,
                hitCodecType, outcomeType, 32, 424242, 7, 900002, 444);
            Invoke(worldEventHandler, fixture, new object[] { hit32 });
            AssertEntity(eventStore, recordType, 424242, 7, 1, 32, assert,
                "second reliable hit after snapshot did not advance the store");
            Pass(assert, log, "reliable HitResolved seq 32 applied after same-cursor snapshot");
        }
        finally
        {
            RestoreFields(p1, presenterFields);
            RestoreTransform(transformState);
            RestoreFields(null, presenterStatics);
            RestoreFields(null, factoryStatics);
            consumedHitKeys.Clear();
            foreach (string key in consumedHitKeysBefore) consumedHitKeys.Add(key);
        }
    }

    private static void ApplyBaseline(object store, Type recordType, uint epoch, ulong includedEventSeq)
    {
        ApplyBaseline(store, recordType, epoch, includedEventSeq, (ulong)1, (ulong)0, 0);
    }

    private static void ApplyBaseline(object store, Type recordType, uint epoch, ulong includedEventSeq,
        ulong entityId, ulong revision, int kind)
    {
        object record = Activator.CreateInstance(recordType,
            new object[] { entityId, revision, kind, false });
        Array records = Array.CreateInstance(recordType, 1);
        records.SetValue(record, 0);
        object result = Call(store, "ApplyBaseline", new object[] { epoch, includedEventSeq, records });
        if (result == null || result.ToString() != "Applied")
            throw new InvalidOperationException("fixture baseline rejected: " + (result == null ? "null" : result.ToString()));
    }

    private static byte[] EncodeSnapshot(Type messageType, Type entityType, ulong snapshotId,
        ulong hostTick, ulong includedEventSeq, ulong revision, float x, float y,
        uint animationId, int hp)
    {
        return EncodeSnapshotForEntity(messageType, entityType, snapshotId, hostTick,
            includedEventSeq, (ulong)1, 0, revision, x, y, animationId, hp);
    }

    private static byte[] EncodeSnapshotForEntity(Type messageType, Type entityType, ulong snapshotId,
        ulong hostTick, ulong includedEventSeq, ulong entityId, int kind, ulong revision,
        float x, float y, uint animationId, int hp)
    {
        object entity = Activator.CreateInstance(entityType, true);
        SetField(entity, "EntityId", entityId);
        SetField(entity, "Kind", kind);
        SetField(entity, "Revision", revision);
        SetField(entity, "X", x);
        SetField(entity, "Y", y);
        SetField(entity, "Vx", 0f);
        SetField(entity, "Vy", 0f);
        SetField(entity, "Facing", 1);
        SetField(entity, "AnimId", animationId);
        SetField(entity, "AnimProgress", 0f);
        SetField(entity, "Hp", hp);
        SetField(entity, "Shield", 0);
        SetField(entity, "LifeState", (byte)1);
        SetField(entity, "TargetEntityId", (ulong)0);
        SetField(entity, "PrefabKey", string.Empty);

        object message = Activator.CreateInstance(messageType, true);
        SetField(message, "SnapshotId", snapshotId);
        SetField(message, "HostTick", hostTick);
        SetField(message, "IncludedEventSeq", includedEventSeq);
        SetField(message, "PartIndex", (ushort)0);
        SetField(message, "PartCount", (ushort)1);
        Array entities = Array.CreateInstance(entityType, 1);
        entities.SetValue(entity, 0);
        SetField(message, "Entities", entities);
        return (byte[])Call(message, "Encode", new object[0]);
    }

    private static byte[] EncodeHitResolvedEvent(Type eventType, Type subtypeType,
        Type payloadType, Type codecType, Type outcomeType, ulong eventSeq,
        ulong targetEntityId, ulong revision, ulong attackSeq, int hpAfter)
    {
        object payload = Activator.CreateInstance(payloadType, true);
        SetField(payload, "AttackerEntityId", (ulong)2);
        SetField(payload, "AttackSeq", attackSeq);
        SetField(payload, "TargetEntityId", targetEntityId);
        SetField(payload, "HitOrdinal", (uint)0);
        SetField(payload, "Outcome", Convert.ToByte(RequireField(outcomeType, "Damage", StaticFlags).GetRawConstantValue()));
        SetField(payload, "HpAfter", hpAfter);
        SetField(payload, "ShieldAfter", 0);
        SetField(payload, "Revision", revision);

        MethodInfo encodePayload = codecType.GetMethod("Encode", StaticFlags);
        if (encodePayload == null) throw new MissingMethodException(codecType.FullName, "Encode");
        byte[] payloadBytes = (byte[])Invoke(encodePayload, null, new object[] { payload });

        object message = Activator.CreateInstance(eventType, true);
        SetField(message, "EventSeq", eventSeq);
        SetField(message, "HostTick", eventSeq + 300);
        SetField(message, "SubType", Convert.ToByte(Enum.Parse(subtypeType, "HitResolved")));
        SetField(message, "TargetEntityId", targetEntityId);
        SetField(message, "EntityRevision", revision);
        SetField(message, "Payload", payloadBytes);
        return (byte[])Call(message, "Encode", new object[0]);
    }

    private static void AssertEntity(object store, Type recordType, ulong entityId,
        ulong expectedRevision, int expectedKind, ulong expectedEventSeq,
        Action<bool, string> assert, string failure)
    {
        MethodInfo tryGet = RequireMethod(store.GetType(), "TryGetEntity",
            new Type[] { typeof(ulong), recordType.MakeByRefType() });
        object[] args = new object[] { entityId, Activator.CreateInstance(recordType) };
        bool found = Convert.ToBoolean(Invoke(tryGet, store, args));
        Require(found, assert, failure + " (entity missing)");
        object record = args[1];
        Require(Convert.ToUInt64(ReadField(record, "Revision")) == expectedRevision,
            assert, failure + " (revision)");
        Require(Convert.ToInt32(ReadField(record, "Kind")) == expectedKind,
            assert, failure + " (kind)");
        Require(Convert.ToUInt64(ReadProperty(store, "AppliedEventSeq")) == expectedEventSeq,
            assert, failure + " (AppliedEventSeq)");
    }

    private static void AssertPresenterUnchanged(object presenter, int hp, float targetX,
        Action<bool, string> assert, string failure)
    {
        Require(Convert.ToInt32(ReadField(presenter, "DisplayHp")) == hp, assert,
            failure + " (DisplayHp)");
        object target = ReadField(presenter, "positionTarget");
        Require(Math.Abs(ReadFloatComponent(target, "x") - targetX) < 0.001f, assert,
            failure + " (positionTarget)");
    }

    private static TransformState CaptureTransform(object transform)
    {
        return new TransformState
        {
            Transform = transform,
            Position = ReadProperty(transform, "position"),
            Rotation = ReadProperty(transform, "rotation"),
            LocalScale = ReadProperty(transform, "localScale")
        };
    }

    private static void RestoreTransform(TransformState state)
    {
        if (state == null || state.Transform == null) return;
        WriteProperty(state.Transform, "position", state.Position);
        WriteProperty(state.Transform, "rotation", state.Rotation);
        WriteProperty(state.Transform, "localScale", state.LocalScale);
    }

    private static List<FieldValue> CaptureFields(object target, BindingFlags flags)
    {
        Type type = target == null ? null : target as Type;
        object instance = target is Type ? null : target;
        if (type == null && instance != null) type = instance.GetType();
        List<FieldValue> values = new List<FieldValue>();
        while (type != null)
        {
            FieldInfo[] fields = type.GetFields(flags | BindingFlags.DeclaredOnly);
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field.IsLiteral || field.IsInitOnly) continue;
                values.Add(new FieldValue { Field = field, Value = field.GetValue(instance) });
            }
            type = type.BaseType;
        }
        return values;
    }

    private static void RestoreFields(object instance, List<FieldValue> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            values[i].Field.SetValue(instance, values[i].Value);
        }
    }

    private static Type FindType(string fullName, Assembly preferredAssembly)
    {
        Type type = preferredAssembly.GetType(fullName, false);
        if (type != null) return type;
        Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
        for (int i = 0; i < loaded.Length; i++)
        {
            type = loaded[i].GetType(fullName, false);
            if (type != null) return type;
        }
        throw new InvalidOperationException("required runtime type is not loaded: " + fullName);
    }

    private static FieldInfo RequireField(Type type, string name, BindingFlags flags)
    {
        FieldInfo field = FindField(type, name, flags);
        if (field == null) throw new MissingFieldException(type.FullName, name);
        return field;
    }

    private static FieldInfo FindField(Type type, string name, BindingFlags flags)
    {
        while (type != null)
        {
            FieldInfo field = type.GetField(name, flags | BindingFlags.DeclaredOnly);
            if (field != null) return field;
            type = type.BaseType;
        }
        return null;
    }

    private static MethodInfo RequireMethod(Type type, string name, Type[] parameterTypes)
    {
        MethodInfo method = type.GetMethod(name, InstanceFlags, null, parameterTypes, null);
        if (method == null) throw new MissingMethodException(type.FullName, name);
        return method;
    }

    private static object ReadField(object instance, string name)
    {
        FieldInfo field = RequireField(instance.GetType(), name, InstanceFlags);
        return field.GetValue(instance);
    }

    private static void SetField(object instance, string name, object value)
    {
        FieldInfo field = RequireField(instance.GetType(), name, InstanceFlags);
        field.SetValue(instance, value);
    }

    private static object ReadProperty(object instance, string name)
    {
        if (instance == null) throw new ArgumentNullException("instance");
        PropertyInfo property = instance.GetType().GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null) throw new MissingMemberException(instance.GetType().FullName, name);
        return property.GetValue(instance, null);
    }

    private static void WriteProperty(object instance, string name, object value)
    {
        PropertyInfo property = instance.GetType().GetProperty(name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property == null || !property.CanWrite)
            throw new MissingMemberException(instance.GetType().FullName, name + " setter");
        property.SetValue(instance, value, null);
    }

    private static float ReadFloatComponent(object value, string name)
    {
        if (value == null) throw new InvalidOperationException("vector value is null");
        Type type = value.GetType();
        FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null) return Convert.ToSingle(field.GetValue(value));
        PropertyInfo property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (property != null) return Convert.ToSingle(property.GetValue(value, null));
        throw new MissingMemberException(type.FullName, name);
    }

    private static object Call(object target, string name, object[] args)
    {
        MethodInfo method = target.GetType().GetMethod(name, InstanceFlags);
        if (method == null) throw new MissingMethodException(target.GetType().FullName, name);
        return Invoke(method, target, args);
    }

    private static object Invoke(MethodInfo method, object target, object[] args)
    {
        try
        {
            return method.Invoke(target, args);
        }
        catch (TargetInvocationException ex)
        {
            throw new InvalidOperationException("runtime playback handler failed: " +
                (ex.InnerException == null ? ex.Message : ex.InnerException.ToString()), ex.InnerException ?? ex);
        }
    }

    private static void Require(bool condition, Action<bool, string> assert, string message)
    {
        assert(condition, message);
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Pass(Action<bool, string> assert, Action<string> log, string message)
    {
        assert(true, message);
        if (log != null) log("PASS RuntimePlayback " + message);
    }
}
