using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FishNet.Broadcast;
using FishNet.Object;
using FishNet.Object.Prediction;
using FishNet.Object.Synchronizing.Internal;

namespace RaidZero.Net
{
    /// <summary>
    /// A short hash of the game's network surface: every networked class's RPCs, prediction methods
    /// (Replicate and Reconcile) and synced fields, the broadcasts, and the layout of everything they carry
    /// (the fields of the replicate and reconcile structs, say, and of any of our types inside those). Fish-Net
    /// needs the client and server to have exactly the same ones, and a mismatch otherwise shows up as a kick,
    /// or as garbled input, on the first message that differs. So clients send this when they join, and the
    /// server turns away a different one up front (version_mismatch).
    ///
    /// Method bodies don't count: changing how something moves is fine; adding or changing an RPC, or adding a
    /// field to PlayerReplicatedData, isn't. Types from Unity, Fish-Net and .NET count by name only, as both
    /// sides have the same versions of those.
    /// </summary>
    public static class NetworkFingerprint
    {
        private const BindingFlags Declared =
            BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly Type[] NetworkAttributes =
        {
            typeof(ServerRpcAttribute),
            typeof(ObserversRpcAttribute),
            typeof(TargetRpcAttribute),
            typeof(ReplicateAttribute),
            typeof(ReconcileAttribute),
        };

        private static string _current;

        /// <summary>This build's fingerprint, worked out once.</summary>
        public static string Current => _current ?? (_current = Compute(RuntimeTypes()));

        /// <summary>The fingerprint of these types: the network surface of the NetworkBehaviours among them.</summary>
        public static string Compute(IEnumerable<Type> types)
        {
            var lines = Surface(types);
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
                return BitConverter.ToString(hash, 0, 6).Replace("-", "").ToLowerInvariant();
            }
        }

        /// <summary>What the fingerprint covers, one line per RPC, synced field, broadcast or carried field, sorted. For working out what differs.</summary>
        public static List<string> Surface(IEnumerable<Type> types)
        {
            var lines = new List<string>();
            var carried = new List<Type>();
            foreach (var type in types)
            {
                if (type == null)
                {
                    continue;
                }

                if (type.IsValueType && typeof(IBroadcast).IsAssignableFrom(type) && !IsFromOutside(type))
                {
                    lines.Add(NameOf(type) + " broadcast");
                    carried.Add(type);
                    continue;
                }

                if (!typeof(NetworkBehaviour).IsAssignableFrom(type))
                {
                    continue;
                }

                foreach (var method in type.GetMethods(Declared))
                {
                    // Fish-Net's generated helpers (RpcLogic___, RpcWriter___ and so on) follow from the methods.
                    if (method.Name.Contains("___"))
                    {
                        continue;
                    }

                    foreach (var attribute in NetworkAttributes)
                    {
                        if (method.IsDefined(attribute, false))
                        {
                            var parameters = method.GetParameters();
                            lines.Add(NameOf(type) + "." + method.Name + "(" + string.Join(",", parameters.Select(p => NameOf(p.ParameterType))) + ") " + attribute.Name);
                            carried.AddRange(parameters.Select(p => p.ParameterType));
                        }
                    }
                }

                foreach (var field in type.GetFields(Declared))
                {
                    if (typeof(SyncBase).IsAssignableFrom(field.FieldType))
                    {
                        lines.Add(NameOf(type) + "." + field.Name + " : " + NameOf(field.FieldType));
                        carried.Add(field.FieldType);
                    }
                }
            }

            var described = new HashSet<Type>();
            foreach (var type in carried)
            {
                Describe(type, described, lines);
            }

            lines.Sort(StringComparer.Ordinal);
            return lines;
        }

        /// <summary>
        /// Adds the layout of a type that goes over the network: its fields in order, and theirs in turn. Fish-Net
        /// writes a struct field by field, so a field added, removed, reordered or retyped on one side only would
        /// have the other read the rest of the message wrongly.
        /// </summary>
        private static void Describe(Type type, HashSet<Type> described, List<string> lines)
        {
            if (type == null || !described.Add(type))
            {
                return;
            }

            if (type.IsByRef || type.IsArray)
            {
                Describe(type.GetElementType(), described, lines);
                return;
            }

            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    Describe(argument, described, lines);
                }
            }

            if (type.IsPrimitive || type.IsGenericParameter || IsFromOutside(type))
            {
                return;
            }

            if (type.IsEnum)
            {
                var names = Enum.GetNames(type);
                var values = names.Select(n => n + "=" + Convert.ToString(
                    Convert.ChangeType(Enum.Parse(type, n), Enum.GetUnderlyingType(type)), System.Globalization.CultureInfo.InvariantCulture));
                lines.Add(NameOf(type) + " enum " + NameOf(Enum.GetUnderlyingType(type)) + " {" + string.Join(",", values) + "}");
                return;
            }

            var baseType = type.BaseType;
            if (baseType != null && baseType != typeof(object) && baseType != typeof(ValueType))
            {
                lines.Add(NameOf(type) + " : " + NameOf(baseType));
                Describe(baseType, described, lines);
            }

            // In the order they're declared, which is the order they're written in.
            int index = 0;
            foreach (var field in type.GetFields(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                lines.Add(NameOf(type) + " field " + index.ToString("00", System.Globalization.CultureInfo.InvariantCulture) + " " + field.Name + " : " + NameOf(field.FieldType));
                index++;
                Describe(field.FieldType, described, lines);
            }
        }

        // Unity's, Fish-Net's and .NET's own types: the same on both sides, as both are built with the same versions.
        private static bool IsFromOutside(Type type)
        {
            var ns = type.Namespace ?? "";
            return ns == "System" || ns.StartsWith("System.", StringComparison.Ordinal)
                || ns == "UnityEngine" || ns.StartsWith("UnityEngine.", StringComparison.Ordinal)
                || ns.StartsWith("Unity.", StringComparison.Ordinal)
                || ns == "FishNet" || ns.StartsWith("FishNet.", StringComparison.Ordinal)
                || ns.StartsWith("GameKit.", StringComparison.Ordinal);
        }

        /// <summary>A type's name without assembly versions, which can differ between the editor and a build.</summary>
        private static string NameOf(Type type)
        {
            if (type.IsByRef)
            {
                return NameOf(type.GetElementType()) + "&";
            }

            if (type.IsArray)
            {
                return NameOf(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            }

            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                return (definition.FullName ?? definition.Name) + "<" + string.Join(",", type.GetGenericArguments().Select(NameOf)) + ">";
            }

            return type.FullName ?? type.Name;
        }

        /// <summary>
        /// Types from the assemblies a player build has. A build only loads those, but the editor also loads
        /// editor and test assemblies, so there Unity's own list of player assemblies decides.
        /// </summary>
        private static IEnumerable<Type> RuntimeTypes()
        {
#if UNITY_EDITOR
            var playerAssemblies = new HashSet<string>(
                UnityEditor.Compilation.CompilationPipeline
                    .GetAssemblies(UnityEditor.Compilation.AssembliesType.PlayerWithoutTestAssemblies)
                    .Select(a => a.name),
                StringComparer.Ordinal);
#endif
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                {
                    continue;
                }

#if UNITY_EDITOR
                if (!playerAssemblies.Contains(assembly.GetName().Name))
                {
                    continue;
                }
#endif
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = exception.Types;
                }

                foreach (var type in types)
                {
                    if (type != null)
                    {
                        yield return type;
                    }
                }
            }
        }
    }
}
