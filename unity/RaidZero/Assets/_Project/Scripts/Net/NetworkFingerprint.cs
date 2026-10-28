using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FishNet.Object;
using FishNet.Object.Prediction;
using FishNet.Object.Synchronizing.Internal;

namespace RaidZero.Net
{
    /// <summary>
    /// A short hash of the game's network surface: every networked class's RPCs, prediction methods
    /// (Replicate and Reconcile) and synced fields. Fish-Net needs the client and server to have exactly the
    /// same ones, and a mismatch otherwise shows up as a kick on the first message that differs. So clients
    /// send this when they join, and the server turns away a different one up front (version_mismatch).
    ///
    /// Method bodies don't count: changing how something moves is fine, adding or changing an RPC isn't.
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

        /// <summary>What the fingerprint covers, one line per RPC or synced field, sorted. For working out what differs.</summary>
        public static List<string> Surface(IEnumerable<Type> types)
        {
            var lines = new List<string>();
            foreach (var type in types)
            {
                if (type == null || !typeof(NetworkBehaviour).IsAssignableFrom(type))
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
                            var parameters = string.Join(",", method.GetParameters().Select(p => NameOf(p.ParameterType)));
                            lines.Add(NameOf(type) + "." + method.Name + "(" + parameters + ") " + attribute.Name);
                        }
                    }
                }

                foreach (var field in type.GetFields(Declared))
                {
                    if (typeof(SyncBase).IsAssignableFrom(field.FieldType))
                    {
                        lines.Add(NameOf(type) + "." + field.Name + " : " + NameOf(field.FieldType));
                    }
                }
            }

            lines.Sort(StringComparer.Ordinal);
            return lines;
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
