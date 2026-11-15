using System;
using UnityEngine;
 
/// <summary>
/// Put on a [SerializeReference] field (interface, abstract class or base class)
/// to get a dropdown in the inspector that lets you pick the concrete type.
/// </summary>
[AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
public class SubclassSelectorAttribute : PropertyAttribute { }
 
