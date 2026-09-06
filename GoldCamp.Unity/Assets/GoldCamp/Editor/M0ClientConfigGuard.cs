using MCPForUnity.Editor.Services;
using UnityEditor;

namespace GoldCamp.Editor
{
    // This project connects through its own .codex/config.toml. Do not let the
    // upstream delayed startup sweep rewrite user-level MCP client files.
    // SessionState is editor-session local; no global EditorPrefs are changed.
    [InitializeOnLoad]
    public static class M0ClientConfigGuard
    {
        static M0ClientConfigGuard()
        {
            SessionState.SetBool(StartupConfigRewrite.SESSION_GUARD_KEY, true);
        }
    }
}
