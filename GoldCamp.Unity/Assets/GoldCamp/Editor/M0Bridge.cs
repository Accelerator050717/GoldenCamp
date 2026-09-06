using System;
using MCPForUnity.Editor.Helpers;
using MCPForUnity.Editor.Services;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace GoldCamp.Editor
{
    public static class M0Bridge
    {
        [MenuItem("GoldCamp/M0/Open scene and connect MCP")]
        public static async void OpenAndConnect()
        {
            try
            {
                if (!EditorConfigurationCache.Instance.UseHttpTransport
                    || HttpEndpointUtility.IsRemoteScope()
                    || HttpEndpointUtility.GetBaseUrl() != "http://127.0.0.1:8080")
                    throw new InvalidOperationException("MCP settings differ from project HTTP defaults; no settings changed.");
                if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                    return;
                EditorSceneManager.OpenScene(M0Setup.ScenePath);
                if (!MCPServiceLocator.Bridge.IsRunning && !await MCPServiceLocator.Bridge.StartAsync())
                    throw new InvalidOperationException("MCP bridge did not connect.");
                var result = await MCPServiceLocator.Bridge.VerifyAsync();
                if (!result.Success) throw new InvalidOperationException(result.Message);
                Debug.Log("GOLDCAMP_MCP_CONNECTED: " + result.Message);
            }
            catch (Exception ex) { Debug.LogException(ex); }
        }
    }
}
