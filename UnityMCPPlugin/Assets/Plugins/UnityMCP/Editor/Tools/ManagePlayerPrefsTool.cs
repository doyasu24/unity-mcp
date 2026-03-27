using Newtonsoft.Json.Linq;
using UnityEngine;

namespace UnityMcpPlugin.Tools
{
    internal sealed class ManagePlayerPrefsTool : SyncToolHandler
    {
        public override string ToolName => ToolNames.ManagePlayerPrefs;

        public override object Execute(JObject parameters)
        {
            var action = Payload.GetString(parameters, "action");
            if (!ManagePlayerPrefsActions.IsSupported(action))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    $"action must be {ManagePlayerPrefsActions.Get}|{ManagePlayerPrefsActions.Set}|{ManagePlayerPrefsActions.Delete}|{ManagePlayerPrefsActions.HasKey}|{ManagePlayerPrefsActions.DeleteAll}");
            }

            if (action == ManagePlayerPrefsActions.DeleteAll)
            {
                return ExecuteDeleteAll();
            }

            var key = Payload.GetString(parameters, "key");
            if (string.IsNullOrEmpty(key))
            {
                throw new PluginException("ERR_INVALID_PARAMS", "key is required");
            }

            switch (action)
            {
                case ManagePlayerPrefsActions.Get:
                    return ExecuteGet(key);
                case ManagePlayerPrefsActions.Set:
                    return ExecuteSet(parameters, key);
                case ManagePlayerPrefsActions.Delete:
                    return ExecuteDelete(key);
                case ManagePlayerPrefsActions.HasKey:
                    return ExecuteHasKey(key);
                default:
                    throw new PluginException("ERR_INVALID_PARAMS", $"Unknown action: {action}");
            }
        }

        private static ManagePlayerPrefsGetPayload ExecuteGet(string key)
        {
            var exists = PlayerPrefs.HasKey(key);
            return new ManagePlayerPrefsGetPayload(
                ManagePlayerPrefsActions.Get,
                key,
                exists,
                PlayerPrefs.GetString(key, ""),
                PlayerPrefs.GetInt(key, 0),
                PlayerPrefs.GetFloat(key, 0f));
        }

        private static ManagePlayerPrefsSetPayload ExecuteSet(JObject parameters, string key)
        {
            var valueType = Payload.GetString(parameters, "value_type") ?? ManagePlayerPrefsValueTypes.String;
            if (!ManagePlayerPrefsValueTypes.IsSupported(valueType))
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    $"value_type must be {ManagePlayerPrefsValueTypes.String}|{ManagePlayerPrefsValueTypes.Int}|{ManagePlayerPrefsValueTypes.Float}");
            }

            switch (valueType)
            {
                case ManagePlayerPrefsValueTypes.String:
                {
                    var value = Payload.GetString(parameters, "value");
                    if (value == null)
                    {
                        throw new PluginException("ERR_INVALID_PARAMS", "value is required for 'set' action");
                    }

                    PlayerPrefs.SetString(key, value);
                    break;
                }
                case ManagePlayerPrefsValueTypes.Int:
                {
                    var value = Payload.GetInt(parameters, "value");
                    if (value == null)
                    {
                        throw new PluginException("ERR_INVALID_PARAMS", "value must be a valid integer for value_type='int'");
                    }

                    PlayerPrefs.SetInt(key, value.Value);
                    break;
                }
                case ManagePlayerPrefsValueTypes.Float:
                {
                    var value = Payload.GetFloat(parameters, "value");
                    if (value == null)
                    {
                        throw new PluginException("ERR_INVALID_PARAMS", "value must be a valid number for value_type='float'");
                    }

                    PlayerPrefs.SetFloat(key, value.Value);
                    break;
                }
            }

            SavePlayerPrefs();
            return new ManagePlayerPrefsSetPayload(ManagePlayerPrefsActions.Set, key, valueType, true);
        }

        private static ManagePlayerPrefsDeletePayload ExecuteDelete(string key)
        {
            PlayerPrefs.DeleteKey(key);
            SavePlayerPrefs();
            return new ManagePlayerPrefsDeletePayload(ManagePlayerPrefsActions.Delete, key, true);
        }

        private static ManagePlayerPrefsHasKeyPayload ExecuteHasKey(string key)
        {
            return new ManagePlayerPrefsHasKeyPayload(
                ManagePlayerPrefsActions.HasKey,
                key,
                PlayerPrefs.HasKey(key));
        }

        private static ManagePlayerPrefsDeleteAllPayload ExecuteDeleteAll()
        {
            PlayerPrefs.DeleteAll();
            SavePlayerPrefs();
            return new ManagePlayerPrefsDeleteAllPayload(ManagePlayerPrefsActions.DeleteAll, true);
        }

        /// <summary>
        /// PlayerPrefs.Save() は一部プラットフォームでストレージ制限時に
        /// PlayerPrefsException を投げる可能性がある。
        /// </summary>
        private static void SavePlayerPrefs()
        {
            try
            {
                PlayerPrefs.Save();
            }
            catch (System.Exception ex)
            {
                throw new PluginException("ERR_UNITY_EXECUTION", $"PlayerPrefs.Save() failed: {ex.Message}");
            }
        }
    }
}
