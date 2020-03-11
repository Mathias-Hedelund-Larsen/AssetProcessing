using FoldergeistAssets.EditorCoroutines;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace FoldergeistAssets
{
    namespace AssetProcessing
    {
        public class WhenDeleteAsset : UnityEditor.AssetModificationProcessor
        {
            public static AssetDeleteResult OnWillDeleteAsset(string path, RemoveAssetOptions options)
            {
                var asset = AssetDatabase.LoadAssetAtPath(path, typeof(UnityEngine.Object));

                if (AssetHasReferences(path, asset))
                {
                    if (EditorUtility.DisplayDialog("Warning. The Asset has references.", $"Stopped the deletion of file {path}\nUse the Track Usage option to find different references" +
                        $" which needs to be deleted.\nRemember to Unpack prefab instances aswell by rightclicking the prefab instance and clicking Unpack prefab (completely).",
                        "Clean up and Delete", "Close"))
                    {
                        RemoveReferences(path, asset);

                        if(asset is GameObject)
                        {
                            BeforePrefabAssetDestroyed(asset as GameObject);
                        }

                        return AssetDeleteResult.DidNotDelete;
                    }

                    return AssetDeleteResult.FailedDelete;
                }
                else
                {
                    if (asset is GameObject)
                    {
                        BeforePrefabAssetDestroyed(asset as GameObject);
                    }

                    return AssetDeleteResult.DidNotDelete;
                }
            }

            private static void BeforePrefabAssetDestroyed(GameObject go)
            {
                var components = go.GetComponents<MonoBehaviour>().ToList();

                components.AddRange(go.GetComponentsInChildren<MonoBehaviour>());

                for (int t = 0; t < components.Count; t++)
                {
                    var onDestroyMethod = components[t].GetType().GetMethod("OnPrefabAssetDestroy", BindingFlags.Instance | BindingFlags.NonPublic);

                    if (onDestroyMethod != null)
                    {
                        onDestroyMethod.Invoke(components[t], null);
                    }
                }
            }

            private static void RemoveReferences(string path, UnityEngine.Object asset)
            {
                string guid = AssetDatabase.AssetPathToGUID(path);

                var prefabGuids = AssetDatabase.FindAssets($"t:Prefab");
                var scriptableObjectGuids = AssetDatabase.FindAssets($"t:ScriptableObject");
                var sceneAssetGuids = AssetDatabase.FindAssets($"t:SceneAsset");

                int openSceneCount = EditorSceneManager.sceneCount;
                List<Scene> openScenes = new List<Scene>();

                for (int i = 0; i < openSceneCount; i++)
                {
                    openScenes.Add(EditorSceneManager.GetSceneAt(i));
                }

                for (int i = 0; i < prefabGuids.Length; i++)
                {
                    string prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuids[i]);

                    using (StreamReader reader = new StreamReader(prefabPath))
                    {
                        var data = reader.ReadToEnd();

                        if (data.Contains(guid))
                        {
                            if (asset is MonoScript)
                            {
                                GameObject prefabGO = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);

                                var components = prefabGO.GetComponents((asset as MonoScript).GetClass()).ToList();

                                components.AddRange(prefabGO.GetComponentsInChildren((asset as MonoScript).GetClass(), true));

                                for (int t = components.Count - 1; t >= 0; t--)
                                {
                                    if (components[t])
                                    {
                                        UnityEngine.Object.DestroyImmediate(components[t], true);
                                    }
                                }
                            }
                            else if (asset is GameObject)
                            {
                                var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);

                                SerializedObject sObj = new SerializedObject(prefabAsset);

                                CheckAndSetThroughComponents(prefabAsset.GetComponents<MonoBehaviour>(), asset);
                                CheckAndSetThroughComponents(prefabAsset.GetComponentsInChildren<MonoBehaviour>(true), asset);

                                sObj.ApplyModifiedProperties();
                            }
                        }
                    }
                }

                for (int i = 0; i < scriptableObjectGuids.Length; i++)
                {
                    string scriptableObjectPath = AssetDatabase.GUIDToAssetPath(scriptableObjectGuids[i]);

                    using (StreamReader reader = new StreamReader(scriptableObjectPath))
                    {
                        var data = reader.ReadToEnd();

                        if (data.Contains(guid))
                        {
                            if (asset is MonoScript)
                            {
                                ScriptableObject scriptableObject = AssetDatabase.LoadAssetAtPath<ScriptableObject>(scriptableObjectPath);

                                if (scriptableObject.GetType() == (asset as MonoScript).GetClass())
                                {
                                    EditorCoroutinesManager.Execute(DeleteObjectDelayed(scriptableObject, scriptableObjectPath, EditorApplication.timeSinceStartup + 1));
                                }
                            }
                            else if (asset is GameObject)
                            {
                                var scriptableObjectAsset = AssetDatabase.LoadAssetAtPath<ScriptableObject>(scriptableObjectPath);

                                SerializedObject sObj = new SerializedObject(scriptableObjectAsset);

                                var propertyIterator = sObj.GetIterator();

                                if (propertyIterator.propertyType == SerializedPropertyType.ObjectReference && propertyIterator.objectReferenceValue == asset)
                                {
                                    propertyIterator.objectReferenceValue = null;
                                }

                                while (propertyIterator.Next(true))
                                {
                                    if (propertyIterator.propertyType == SerializedPropertyType.ObjectReference && propertyIterator.objectReferenceValue == asset)
                                    {
                                        propertyIterator.objectReferenceValue = null;
                                    }
                                }

                                sObj.ApplyModifiedProperties();
                            }
                        }
                    }
                }

                for (int i = 0; i < sceneAssetGuids.Length; i++)
                {
                    string scenePath = AssetDatabase.GUIDToAssetPath(sceneAssetGuids[i]);

                    using (StreamReader reader = new StreamReader(scenePath))
                    {
                        var data = reader.ReadToEnd();

                        if (data.Contains(guid))
                        {
                            if (!openScenes.Any(s => s.path == scenePath))
                            {
                                var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);

                                if (asset is MonoScript)
                                {
                                    RemoveComponentOfTypeFromSceneObjects(scene.GetRootGameObjects(), (asset as MonoScript).GetClass());
                                }
                                else if (asset is GameObject)
                                {
                                    RemoveReferencesToPrefabInScene(asset, scene);
                                    UnpackPrefabReferencesInScene(asset, scene);
                                }

                                EditorCoroutinesManager.Execute(SaveAndCloseSceneDelayed(scene, scene.path, EditorApplication.timeSinceStartup + 3));
                            }
                            else
                            {
                                var scene = openScenes.Find(s => s.path == scenePath);

                                if (asset is MonoScript)
                                {
                                    RemoveComponentOfTypeFromSceneObjects(scene.GetRootGameObjects(), (asset as MonoScript).GetClass());
                                }
                                else if (asset is GameObject)
                                {
                                    RemoveReferencesToPrefabInScene(asset, scene);
                                    UnpackPrefabReferencesInScene(asset, scene);
                                }

                                EditorCoroutinesManager.Execute(SaveSceneDelayed(scene, scene.path, EditorApplication.timeSinceStartup + 5));
                            }
                        }
                    }
                }
            }

            private static void UnpackPrefabReferencesInScene(UnityEngine.Object asset, Scene scene)
            {
                var rootObjs = scene.GetRootGameObjects();

                for (int i = 0; i < rootObjs.Length; i++)
                {
                    if (PrefabUtility.IsPartOfNonAssetPrefabInstance(rootObjs[i]))
                    {
                        if (PrefabUtility.GetCorrespondingObjectFromSource(rootObjs[i]) == asset)
                        {
                            PrefabUtility.UnpackPrefabInstance(rootObjs[i], PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
                        }
                    }
                }
            }

            private static void RemoveReferencesToPrefabInScene(UnityEngine.Object asset, Scene scene)
            {
                var rootObjs = scene.GetRootGameObjects();

                for (int i = 0; i < rootObjs.Length; i++)
                {
                    var components = rootObjs[i].GetComponents<MonoBehaviour>().ToList();
                    components.AddRange(rootObjs[i].GetComponentsInChildren<MonoBehaviour>(true));

                    for (int t = 0; t < components.Count; t++)
                    {
                        SerializedObject sComp = new SerializedObject(components[t]);

                        var propertyIterator = sComp.GetIterator();

                        if (propertyIterator.propertyType == SerializedPropertyType.ObjectReference && propertyIterator.objectReferenceValue == asset)
                        {
                            propertyIterator.objectReferenceValue = null;
                        }

                        while (propertyIterator.Next(true))
                        {
                            if (propertyIterator.propertyType == SerializedPropertyType.ObjectReference && propertyIterator.objectReferenceValue == asset)
                            {
                                propertyIterator.objectReferenceValue = null;
                            }
                        }
                    }
                }
            }

            private static void CheckAndSetThroughComponents(MonoBehaviour[] componentsArray, UnityEngine.Object asset)
            {
                for (int i = 0; i < componentsArray.Length; i++)
                {
                    SerializedObject sComp = new SerializedObject(componentsArray[i]);

                    var compIterator = sComp.GetIterator();

                    if (compIterator.propertyType == SerializedPropertyType.ObjectReference && compIterator.objectReferenceValue == asset)
                    {
                        compIterator.objectReferenceValue = null;
                    }

                    while (compIterator.Next(true))
                    {
                        if (compIterator.propertyType == SerializedPropertyType.ObjectReference && compIterator.objectReferenceValue == asset)
                        {
                            compIterator.objectReferenceValue = null;
                        }
                    }

                    sComp.ApplyModifiedProperties();
                }
            }

            private static IEnumerator DeleteObjectDelayed(ScriptableObject scriptableObject, string path, double waitTill)
            {
                while (EditorApplication.timeSinceStartup < waitTill)
                {
                    yield return null;
                }

                UnityEngine.Object.DestroyImmediate(scriptableObject, true);

                if (AssetDatabase.DeleteAsset(path))
                {
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                }
            }

            private static IEnumerator SaveSceneDelayed(Scene scene, string path, double waitTill)
            {
                while (EditorApplication.timeSinceStartup < waitTill)
                {
                    yield return null;
                }

                if (!EditorSceneManager.SaveScene(scene, path))
                {
                    Debug.LogWarning($"Didnt save scene: {scene.name}");
                }

                var obj = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
                new SerializedObject(obj).ApplyModifiedProperties();

                EditorUtility.SetDirty(obj);
            }

            private static IEnumerator SaveAndCloseSceneDelayed(Scene scene, string path, double waitTill)
            {
                while (EditorApplication.timeSinceStartup < waitTill)
                {
                    yield return null;
                }

                if (!EditorSceneManager.SaveScene(scene, path))
                {
                    Debug.LogWarning($"Didnt save scene: {scene.name}");
                }

                if (!EditorSceneManager.CloseScene(scene, true))
                {
                    Debug.LogWarning($"Didnt close scene: {scene.name}");
                }

                var obj = AssetDatabase.LoadAssetAtPath<SceneAsset>(path);
                new SerializedObject(obj).ApplyModifiedProperties();

                EditorUtility.SetDirty(obj);
            }

            private static void RemoveComponentOfTypeFromSceneObjects(GameObject[] rootGameObjects, Type componentTypeToRemove)
            {
                for (int i = 0; i < rootGameObjects.Length; i++)
                {
                    var components = rootGameObjects[i].GetComponents(componentTypeToRemove).ToList();
                    components.AddRange(rootGameObjects[i].GetComponentsInChildren(componentTypeToRemove, true));

                    for (int t = components.Count - 1; t >= 0; t--)
                    {
                        if (components[t])
                        {
                            UnityEngine.Object.DestroyImmediate(components[t], true);
                        }
                    }
                }
            }

            private static bool AssetHasReferences(string path, UnityEngine.Object asset)
            {
                string guid = AssetDatabase.AssetPathToGUID(path);

                var prefabGuids = AssetDatabase.FindAssets($"t:Prefab");
                var scriptableObjectGuids = AssetDatabase.FindAssets($"t:ScriptableObject");
                var sceneAssetGuids = AssetDatabase.FindAssets($"t:SceneAsset");

                if (EditorBuildSettings.scenes.Any(s => s.path == path))
                {
                    return true;
                }

                for (int i = 0; i < prefabGuids.Length; i++)
                {
                    string prefabPath = AssetDatabase.GUIDToAssetPath(prefabGuids[i]);

                    using (StreamReader reader = new StreamReader(prefabPath))
                    {
                        var data = reader.ReadToEnd();

                        if (data.Contains(guid))
                        {
                            return true;
                        }
                    }
                }

                for (int i = 0; i < sceneAssetGuids.Length; i++)
                {
                    string scenePath = AssetDatabase.GUIDToAssetPath(sceneAssetGuids[i]);

                    using (StreamReader reader = new StreamReader(scenePath))
                    {
                        var data = reader.ReadToEnd();

                        if (data.Contains(guid))
                        {
                            return true;
                        }
                    }
                }

                for (int i = 0; i < scriptableObjectGuids.Length; i++)
                {
                    string scriptableObjectPath = AssetDatabase.GUIDToAssetPath(scriptableObjectGuids[i]);

                    using (StreamReader reader = new StreamReader(scriptableObjectPath))
                    {
                        var data = reader.ReadToEnd();

                        if (data.Contains(guid))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
        }
    }
}