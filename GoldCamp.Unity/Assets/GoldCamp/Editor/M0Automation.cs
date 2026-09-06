using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GoldCamp.Editor
{
    public static class M0Automation
    {
        [MenuItem("GoldCamp/M0/Verify Editor Automation")]
        public static void Run()
        {
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;
            var scene = EditorSceneManager.OpenScene(M0Setup.ScenePath);
            var probe = GameObject.Find("M0_EditorProbe") ?? new GameObject("M0_EditorProbe");
            probe.transform.position = new Vector3(0, -2, 0);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene)) throw new IOException("Scene save failed.");
            scene = EditorSceneManager.OpenScene(M0Setup.ScenePath);
            var roots = scene.GetRootGameObjects();
            if (roots.Count(go => go.name == "M0_EditorProbe") != 1) throw new InvalidOperationException("Automation probe was duplicated or missing.");
            string path = Path.GetFullPath(Path.Combine(Application.dataPath, "../../Builds/Logs/editor-automation.txt"));
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, $"Unity {Application.unityVersion}\nScene: {scene.path}\nRoots: {string.Join(", ", roots.Select(go => go.name))}\nProbe position: {GameObject.Find("M0_EditorProbe").transform.position}\n");
            Debug.Log("GOLDCAMP_EDITOR_AUTOMATION_OK: queried, modified, saved, reopened. " + path);
        }
    }
}
