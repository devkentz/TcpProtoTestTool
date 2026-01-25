using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Google.Protobuf.Collections;

namespace ProtoTestTool.Network;
public static class ObjectInitializer
{
    public static void EnsureNonNullFields(object obj, bool addDefaultElements = false)
    {
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        EnsureNonNullFieldsInternal(obj, visited, addDefaultElements);
    }

    private static void EnsureNonNullFieldsInternal(
        object obj, 
        HashSet<object> visited,
        bool addDefaultElements)
    {
        if (!visited.Add(obj))
            return;

        Type type = obj.GetType();

        foreach (var property in type.GetProperties(
            BindingFlags.Public | BindingFlags.Instance))
        {
            if (!property.CanWrite) 
                continue; // ✅ Read-only 제외

            object? value = property.GetValue(obj);
            Type propertyType = property.PropertyType;

            if (value == null)
            {
                HandleNullProperty(obj, property, propertyType, visited, addDefaultElements);
            }
            else
            {
                HandleNonNullProperty(value, propertyType, visited, addDefaultElements);
            }
        }
    }

    private static void HandleNullProperty(
        object obj,
        PropertyInfo property,
        Type propertyType,
        HashSet<object> visited,
        bool addDefaultElements)
    {
        // RepeatedField<T>
        if (IsRepeatedField(propertyType, out Type? itemType))
        {
            var instance = Activator.CreateInstance(propertyType)!;
            property.SetValue(obj, instance);
            
            if (addDefaultElements)
                AddDefaultElement(instance, itemType);
        }
        // List<T>
        else if (IsList(propertyType, out itemType))
        {
            var instance = Activator.CreateInstance(propertyType)!;
            property.SetValue(obj, instance);
            
            if (addDefaultElements)
                AddDefaultElement(instance, itemType);
        }
        // MapField<K, V>
        else if (IsMapField(propertyType))
        {
            var instance = Activator.CreateInstance(propertyType)!;
            property.SetValue(obj, instance);
        }
        // 일반 클래스/구조체
        else if (IsComplexType(propertyType))
        {
            var instance = Activator.CreateInstance(propertyType)!;
            property.SetValue(obj, instance);
            EnsureNonNullFieldsInternal(instance, visited, addDefaultElements);
        }
    }

    private static void HandleNonNullProperty(
        object value,
        Type propertyType,
        HashSet<object> visited,
        bool addDefaultElements)
    {
        if (value is string) return;

        if (value is IList list && addDefaultElements)
        {
            if (list.Count == 0 && propertyType.IsGenericType)
            {
                Type itemType = propertyType.GetGenericArguments()[0];
                AddDefaultElement(list, itemType);
            }
        }
        else if (IsComplexType(propertyType))
        {
            EnsureNonNullFieldsInternal(value, visited, addDefaultElements);
        }
    }

    private static bool IsRepeatedField(Type type, [NotNullWhen(true)]out Type? itemType)
    {
        itemType = null;
        if (!type.IsGenericType) return false;
        
        if (type.GetGenericTypeDefinition() == typeof(RepeatedField<>))
        {
            itemType = type.GetGenericArguments()[0];
            return true;
        }
        return false;
    }

    private static bool IsList(Type type, [NotNullWhen(true)]out Type? itemType)
    {
        itemType = null;
        if (!type.IsGenericType) return false;
        
        if (type.GetGenericTypeDefinition() == typeof(List<>))
        {
            itemType = type.GetGenericArguments()[0];
            return true;
        }
        return false;
    }

    private static bool IsMapField(Type type)
    {
        return type.IsGenericType && 
               type.GetGenericTypeDefinition() == typeof(MapField<,>);
    }

    private static bool IsComplexType(Type type)
    {
        return !type.IsPrimitive && 
               !type.IsEnum && 
               type != typeof(string) &&
               type != typeof(decimal) &&
               type != typeof(DateTime);
    }

    private static void AddDefaultElement(object collection, Type itemType)
    {
        if (collection is IList list)
        {
            list.Add(GetDefaultValue(itemType));
        }
        else
        {
            // RepeatedField<T>.Add via reflection
            var addMethod = collection.GetType().GetMethod("Add");
            addMethod?.Invoke(collection, [GetDefaultValue(itemType)]);
        }
    }

    private static object GetDefaultValue(Type type)
    {
        if (type == typeof(string))
            return string.Empty;

        return Activator.CreateInstance(type)!;
    }
}