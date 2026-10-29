using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;

namespace IceyCoop.GameBridge.Actors
{
    /// <summary>
    /// Reflection-based deep copy for native per-player state. UnityEngine.Object
    /// instances are shared by reference (assets/components are identity), while
    /// plain DTOs are copied field by field with cycle detection. Any copy
    /// failure propagates so the caller fails closed instead of aliasing P1.
    /// </summary>
    public static class NativeDeepCopy
    {
        private sealed class RefComparer : IEqualityComparer<object>
        {
            public static readonly RefComparer Instance = new RefComparer();

            public new bool Equals(object x, object y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(object obj)
            {
                return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
            }
        }

        public static SaveDataModel.Enhancement CopyEnhancement(SaveDataModel.Enhancement src)
        {
            if (src == null)
            {
                return null;
            }
            Dictionary<object, object> seen = new Dictionary<object, object>(RefComparer.Instance);
            return (SaveDataModel.Enhancement)Copy(src, seen);
        }

        public static PlayerAttribute CopyAttribute(PlayerAttribute src)
        {
            if (src == null)
            {
                return null;
            }
            Dictionary<object, object> seen = new Dictionary<object, object>(RefComparer.Instance);
            return (PlayerAttribute)Copy(src, seen);
        }

        private static object Copy(object src, Dictionary<object, object> seen)
        {
            if (src == null)
            {
                return null;
            }
            Type t = src.GetType();

            if (IsDirectValue(src, t))
            {
                return src;
            }
            if (typeof(UnityEngine.Object).IsAssignableFrom(t))
            {
                // Unity assets/components are identity: share the reference.
                return src;
            }

            object existing;
            if (seen.TryGetValue(src, out existing))
            {
                return existing;
            }

            if (t.IsArray)
            {
                Array srcArray = (Array)src;
                Type elementType = t.GetElementType();
                Array dstArray = Array.CreateInstance(elementType, srcArray.Length);
                seen[src] = dstArray;
                for (int i = 0; i < srcArray.Length; i++)
                {
                    dstArray.SetValue(Copy(srcArray.GetValue(i), seen), i);
                }
                return dstArray;
            }

            if (src is IList)
            {
                IList srcList = (IList)src;
                object dstObj = CreateInstance(t);
                seen[src] = dstObj;
                IList dstList = dstObj as IList;
                if (dstList != null)
                {
                    foreach (object item in srcList)
                    {
                        dstList.Add(Copy(item, seen));
                    }
                }
                return dstObj;
            }

            if (src is IDictionary)
            {
                IDictionary srcDict = (IDictionary)src;
                object dstObj = CreateInstance(t);
                seen[src] = dstObj;
                IDictionary dstDict = dstObj as IDictionary;
                if (dstDict != null)
                {
                    foreach (DictionaryEntry entry in srcDict)
                    {
                        dstDict.Add(Copy(entry.Key, seen), Copy(entry.Value, seen));
                    }
                }
                return dstObj;
            }

            object dst = CreateInstance(t);
            seen[src] = dst;
            FieldInfo[] fields = t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (FieldInfo field in fields)
            {
                if (field.IsStatic || field.IsLiteral)
                {
                    continue;
                }
                object value = field.GetValue(src);
                field.SetValue(dst, Copy(value, seen));
            }
            return dst;
        }

        private static bool IsDirectValue(object src, Type t)
        {
            if (src is string)
            {
                return true;
            }
            if (t.IsPrimitive)
            {
                return true;
            }
            if (t.IsEnum)
            {
                return true;
            }
            if (t.IsValueType)
            {
                return true;
            }
            return false;
        }

        private static object CreateInstance(Type t)
        {
            try
            {
                return Activator.CreateInstance(t, true);
            }
            catch
            {
                return FormatterServices.GetUninitializedObject(t);
            }
        }
    }
}
