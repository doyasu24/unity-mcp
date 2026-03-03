using System;

namespace UnityMcpPlugin
{
    internal sealed class PluginException : Exception
    {
        internal PluginException(string code, string message) : base(message)
        {
            Code = code;
        }

        internal string Code { get; }
    }
}
