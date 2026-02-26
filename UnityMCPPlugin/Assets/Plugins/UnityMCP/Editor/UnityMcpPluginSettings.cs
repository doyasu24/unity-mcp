using UnityEditor;

namespace UnityMcpPlugin
{
    [FilePath("ProjectSettings/UnityMcpPluginSettings.asset", FilePathAttribute.Location.ProjectFolder)]
    internal sealed class UnityMcpPluginSettings : ScriptableSingleton<UnityMcpPluginSettings>
    {
        internal const int SupportedSchemaVersion = 1;
        internal const int DefaultPort = 48091;

        public int schemaVersion = SupportedSchemaVersion;
        public int port = DefaultPort;

        internal ValidationResult Validate()
        {
            if (schemaVersion != SupportedSchemaVersion)
            {
                return ValidationResult.Failed(
                    "ERR_CONFIG_SCHEMA_VERSION",
                    $"schemaVersion must be {SupportedSchemaVersion}. current={schemaVersion}");
            }

            if (port < 1 || port > 65535)
            {
                return ValidationResult.Failed(
                    "ERR_CONFIG_VALIDATION",
                    $"port must be between 1 and 65535. current={port}");
            }

            return ValidationResult.Ok();
        }

        internal void SaveToProjectSettings()
        {
            Save(true);
        }

        internal readonly struct ValidationResult
        {
            private ValidationResult(bool isValid, string code, string message)
            {
                IsValid = isValid;
                Code = code;
                Message = message;
            }

            internal bool IsValid { get; }
            internal string Code { get; }
            internal string Message { get; }

            internal static ValidationResult Ok()
            {
                return new ValidationResult(true, string.Empty, string.Empty);
            }

            internal static ValidationResult Failed(string code, string message)
            {
                return new ValidationResult(false, code, message);
            }
        }
    }
}
