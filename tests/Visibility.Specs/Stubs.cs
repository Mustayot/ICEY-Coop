using System;
namespace UnityEngine {
 public class Object { public static Object[] FindObjectsOfType(Type t)=>Array.Empty<Object>(); public bool Destroyed; public static implicit operator bool(Object o)=>!ReferenceEquals(o,null)&&!o.Destroyed; public static bool operator ==(Object a,Object b)=> (ReferenceEquals(a,null)||a.Destroyed)&&(ReferenceEquals(b,null)||b.Destroyed)||ReferenceEquals(a,b); public static bool operator !=(Object a,Object b)=>!(a==b); public override bool Equals(object o)=>ReferenceEquals(this,o); public override int GetHashCode()=>base.GetHashCode(); }
 public class Renderer:Object { public bool enabled; public GameObject owner; }
 public class Transform:Object { public GameObject gameObject; public Transform root=>this; }
 public class GameObject:Object { static int next; readonly int id=++next; public Renderer[] renderers=Array.Empty<Renderer>(); public Transform transform; public bool activeSelf=true; public string name="stub"; public GameObject(){transform=new Transform{gameObject=this};} public int GetInstanceID()=>id; public T[] GetComponentsInChildren<T>(bool includeInactive) where T:class { foreach(var r in renderers) r.owner=this; return renderers as T[]??Array.Empty<T>(); } public void SetActive(bool active){activeSelf=active;} }
}
public static class R { public static Player Player; }
public class Player { public UnityEngine.Transform transform; }
public static class LevelManager { public static string SceneName=""; }
public class PlayerControllerDemo { public static PlayerControllerDemo Instance; public UnityEngine.GameObject gameObject; }
public class EnemyBaseAction:UnityEngine.Object { public UnityEngine.GameObject gameObject; }
public static class ReplicaFactory { public static bool IsReplicaRoot(UnityEngine.GameObject go)=>false; }
namespace IceyCoop.GameBridge { public static partial class BridgeContext { public static bool IsClient,IsHost,IsOffline; public static void Info(string s){} public static void Error(string s){} } }
