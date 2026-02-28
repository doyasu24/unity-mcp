using System;
using System.Collections.Generic;

namespace UnityMcpPlugin.Tools
{
    internal static class ComponentTypeResolver
    {
        private static readonly string[] UnityPrefixes =
        {
            "UnityEngine",
            "UnityEngine.UI",
            "UnityEngine.EventSystems",
            "UnityEngine.Animations",
            "UnityEngine.Rendering",
            "TMPro"
        };

        internal static Type Resolve(string typeName)
        {
            var direct = Type.GetType(typeName);
            if (direct != null)
            {
                return direct;
            }

            var candidates = new List<Type>();

            foreach (var prefix in UnityPrefixes)
            {
                var fullName = prefix + "." + typeName;
                var t = Type.GetType(fullName + ", UnityEngine")
                     ?? Type.GetType(fullName + ", UnityEngine.CoreModule")
                     ?? Type.GetType(fullName + ", UnityEngine.UIModule")
                     ?? Type.GetType(fullName + ", UnityEngine.PhysicsModule")
                     ?? Type.GetType(fullName + ", UnityEngine.AnimationModule")
                     ?? Type.GetType(fullName + ", Unity.RenderPipelines.Core.Runtime")
                     ?? Type.GetType(fullName + ", Unity.TextMeshPro");
                if (t != null && !candidates.Contains(t))
                {
                    candidates.Add(t);
                }
            }

            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            if (candidates.Count > 1)
            {
                ThrowAmbiguous(typeName, candidates);
            }

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var t in assembly.GetTypes())
                    {
                        if (string.Equals(t.Name, typeName, StringComparison.Ordinal) ||
                            string.Equals(t.FullName, typeName, StringComparison.Ordinal))
                        {
                            if (!candidates.Contains(t))
                            {
                                candidates.Add(t);
                            }
                        }
                    }
                }
                catch (System.Reflection.ReflectionTypeLoadException)
                {
                    // skip assemblies that can't be loaded
                }
            }

            if (candidates.Count == 1)
            {
                return candidates[0];
            }

            if (candidates.Count > 1)
            {
                ThrowAmbiguous(typeName, candidates);
            }

            return null;
        }

        private static void ThrowAmbiguous(string typeName, List<Type> candidates)
        {
            var names = new string[candidates.Count];
            for (var i = 0; i < candidates.Count; i++)
            {
                names[i] = candidates[i].FullName;
            }

            throw new PluginException(
                SceneToolErrors.ComponentTypeAmbiguous,
                $"'{typeName}' matches multiple types: {string.Join(", ", names)}. Use the fully qualified name.");
        }
    }
}
