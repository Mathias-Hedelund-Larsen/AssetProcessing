using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace FoldergeistAssets
{
    namespace AssetProcessing
    {
        public class OnAssetsUpdateProcessor : AssetPostprocessor
        {
            public static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
            {
                for (int i = 0; i < importedAssets.Length; i++)
                {
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(importedAssets[i]);

                    if (go)
                    {
                        var components = go.GetComponents<MonoBehaviour>().ToList();

                        components.AddRange(go.GetComponentsInChildren<MonoBehaviour>());

                        for (int t = 0; t < components.Count; t++)
                        {
                            var awakeMethod = components[t].GetType().GetMethod("OnPrefabAssetCreated", BindingFlags.Instance | BindingFlags.NonPublic);

                            if (awakeMethod != null)
                            {
                                awakeMethod.Invoke(components[t], null);
                            }
                        }
                    }
                }
            }           
        }
    }
}