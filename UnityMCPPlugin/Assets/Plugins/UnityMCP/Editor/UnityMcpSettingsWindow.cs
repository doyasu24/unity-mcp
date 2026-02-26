using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace UnityMcpPlugin
{
    internal sealed class UnityMcpSettingsWindow : EditorWindow
    {
        private int _portInput;
        private string _status = string.Empty;
        private MessageType _statusType = MessageType.Info;
        private bool _isApplying;

        [MenuItem("Window/Unity MCP Settings")]
        private static void OpenWindow()
        {
            var window = GetWindow<UnityMcpSettingsWindow>("Unity MCP Settings");
            window.minSize = new Vector2(420f, 160f);
        }

        private void OnEnable()
        {
            _portInput = UnityMcpPluginSettings.instance.port;
            _status = "Edit port and click Apply.";
            _statusType = MessageType.Info;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Unity MCP Plugin Settings", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            using (new EditorGUI.DisabledScope(_isApplying))
            {
                _portInput = EditorGUILayout.IntField("Port", _portInput);
            }

            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_isApplying))
                {
                    if (GUILayout.Button("Apply", GUILayout.Height(28f)))
                    {
                        _ = ApplyAsync();
                    }
                }

                if (GUILayout.Button("Reload", GUILayout.Height(28f)))
                {
                    _portInput = UnityMcpPluginSettings.instance.port;
                    _status = "Reloaded from settings asset.";
                    _statusType = MessageType.Info;
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(_status, _statusType);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField($"Active Port: {UnityMcpPluginRuntime.Instance.GetActivePort()}");
            EditorGUILayout.LabelField($"Bridge State: {UnityMcpPluginRuntime.Instance.GetRuntimeSummary()}");
        }

        private async Task ApplyAsync()
        {
            _isApplying = true;
            _status = "Applying...";
            _statusType = MessageType.Info;
            Repaint();

            try
            {
                var result = await UnityMcpPluginRuntime.Instance.ApplyPortChangeAsync(_portInput);
                if (result.Status == PortReconfigureStatus.Applied)
                {
                    var settings = UnityMcpPluginSettings.instance;
                    settings.port = _portInput;
                    settings.schemaVersion = UnityMcpPluginSettings.SupportedSchemaVersion;
                    settings.SaveToProjectSettings();

                    _status = $"Applied. Active port is {result.ActivePort}.";
                    _statusType = MessageType.Info;
                    return;
                }

                if (result.Status == PortReconfigureStatus.RolledBack)
                {
                    _portInput = result.ActivePort;
                    _status =
                        $"Failed to connect new port. Rolled back to {result.ActivePort}. " +
                        $"{result.ErrorCode}: {result.ErrorMessage}";
                    _statusType = MessageType.Warning;
                    return;
                }

                _portInput = result.ActivePort;
                _status =
                    $"Apply failed. Active port is {result.ActivePort}. " +
                    $"{result.ErrorCode}: {result.ErrorMessage}";
                _statusType = MessageType.Error;
            }
            catch (Exception ex)
            {
                _status = $"Unexpected error while applying settings: {ex.Message}";
                _statusType = MessageType.Error;
            }
            finally
            {
                _isApplying = false;
                Repaint();
            }
        }
    }
}
