
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
 
[CustomPropertyDrawer(typeof(SubclassSelectorAttribute))]
public class SubclassSelectorDrawer : PropertyDrawer
{
    static readonly Dictionary<Type, List<Type>> Cache = new Dictionary<Type, List<Type>>();
 
    public override float GetPropertyHeight(SerializedProperty property, GUIContent label)
    {
        float line = EditorGUIUtility.singleLineHeight;
        if (property.propertyType != SerializedPropertyType.ManagedReference)
            return line;
 
        if (property.managedReferenceValue == null || !property.isExpanded)
            return line;
 
        float height = line;
        foreach (var child in GetChildren(property))
            height += EditorGUI.GetPropertyHeight(child, true) + EditorGUIUtility.standardVerticalSpacing;
        return height;
    }
 
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        if (property.propertyType != SerializedPropertyType.ManagedReference)
        {
            EditorGUI.HelpBox(position, "[SubclassSelector] requires [SerializeReference].", MessageType.Error);
            return;
        }
 
        EditorGUI.BeginProperty(position, label, property);
 
        var current = property.managedReferenceValue?.GetType();
        float line = EditorGUIUtility.singleLineHeight;
 
        // Label / foldout: only the label column, so it can't steal clicks from the dropdown.
        var labelRect = new Rect(position.x, position.y, EditorGUIUtility.labelWidth, line);
        if (current != null)
            property.isExpanded = EditorGUI.Foldout(labelRect, property.isExpanded, label, true);
        else
            EditorGUI.LabelField(labelRect, label);
 
        // Dropdown: the rest of the first row.
        var popupRect = new Rect(
            position.x + EditorGUIUtility.labelWidth + 2,
            position.y,
            position.width - EditorGUIUtility.labelWidth - 2,
            line);
 
        string buttonText = current != null ? ObjectNames.NicifyVariableName(current.Name) : "None";
 
        if (EditorGUI.DropdownButton(popupRect, new GUIContent(buttonText), FocusType.Keyboard))
        {
            var baseType = GetFieldType(property);
            var menu = new GenericMenu();
 
            menu.AddItem(new GUIContent("None"), current == null, () => SetType(property, null));
 
            foreach (var type in GetImplementations(baseType))
            {
                var t = type;
                var path = string.IsNullOrEmpty(t.Namespace)
                    ? ObjectNames.NicifyVariableName(t.Name)
                    : t.Namespace.Replace('.', '/') + "/" + ObjectNames.NicifyVariableName(t.Name);
                menu.AddItem(new GUIContent(path), t == current, () => SetType(property, t));
            }
 
            menu.DropDown(popupRect);
        }
 
        // Child fields, drawn manually below the first row.
        if (current != null && property.isExpanded)
        {
            EditorGUI.indentLevel++;
            var rect = new Rect(position.x, position.y + line + EditorGUIUtility.standardVerticalSpacing,
                                position.width, 0);
 
            foreach (var child in GetChildren(property))
            {
                rect.height = EditorGUI.GetPropertyHeight(child, true);
                EditorGUI.PropertyField(rect, child, true);
                rect.y += rect.height + EditorGUIUtility.standardVerticalSpacing;
            }
            EditorGUI.indentLevel--;
        }
 
        EditorGUI.EndProperty();
    }
 
    static IEnumerable<SerializedProperty> GetChildren(SerializedProperty property)
    {
        var child = property.Copy();
        var end = property.GetEndProperty();
        bool enter = child.NextVisible(true);
        while (enter && !SerializedProperty.EqualContents(child, end))
        {
            yield return child;
            enter = child.NextVisible(false);
        }
    }
 
    static void SetType(SerializedProperty property, Type type)
    {
        property.serializedObject.Update();
 
        object oldValue = property.managedReferenceValue;
        object newValue = type == null ? null : Activator.CreateInstance(type);
 
        if (oldValue != null && newValue != null)
        {
            // copies fields with matching names and types; others keep their defaults
            JsonUtility.FromJsonOverwrite(JsonUtility.ToJson(oldValue), newValue);
        }
 
        property.managedReferenceValue = newValue;
        if (newValue != null) property.isExpanded = true;
        property.serializedObject.ApplyModifiedProperties();
    }
 
    static List<Type> GetImplementations(Type baseType)
    {
        if (baseType == null) return new List<Type>();
        if (Cache.TryGetValue(baseType, out var cached)) return cached;
 
        var list = TypeCache.GetTypesDerivedFrom(baseType)
            .Where(t => !t.IsAbstract
                        && !t.IsInterface
                        && !t.IsGenericTypeDefinition
                        && !typeof(UnityEngine.Object).IsAssignableFrom(t)
                        && t.GetConstructor(Type.EmptyTypes) != null)
            .OrderBy(t => t.FullName)
            .ToList();
 
        Cache[baseType] = list;
        return list;
    }
 
    // managedReferenceFieldTypename looks like "AssemblyName Namespace.TypeName"
    static Type GetFieldType(SerializedProperty property)
    {
        var parts = property.managedReferenceFieldTypename.Split(' ');
        if (parts.Length != 2) return null;
        return Type.GetType($"{parts[1].Replace('/', '+')}, {parts[0]}");
    }
}
 