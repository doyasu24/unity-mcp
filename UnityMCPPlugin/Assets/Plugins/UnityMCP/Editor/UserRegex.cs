using System;
using System.Text.RegularExpressions;

namespace UnityMcpPlugin
{
    /// <summary>
    /// MCP クライアントから渡される正規表現を安全にコンパイル・照合するヘルパ。
    /// ツールはメインスレッドで同期実行されるため、悪意ある/不用意なパターンによる
    /// catastrophic backtracking が Editor とブリッジを停止させうる。
    /// 照合タイムアウトと長さ上限を課し、不正・タイムアウトは ERR_INVALID_PARAMS に正規化する。
    /// すべての利用箇所(list_menu_items, list_scenes, manage_asmdef, read_console)はこのヘルパを通す。
    /// </summary>
    internal static class UserRegex
    {
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);
        private const int MaxPatternLength = 1000;

        internal static Regex Compile(string pattern, string label)
        {
            if (pattern.Length > MaxPatternLength)
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    $"{label} regex is too long (max {MaxPatternLength} characters).");
            }

            try
            {
                return new Regex(pattern, RegexOptions.IgnoreCase, MatchTimeout);
            }
            catch (ArgumentException)
            {
                throw new PluginException("ERR_INVALID_PARAMS", $"Invalid {label} regex: {pattern}");
            }
        }

        internal static bool IsMatch(Regex regex, string input, string label)
        {
            try
            {
                return regex.IsMatch(input);
            }
            catch (RegexMatchTimeoutException)
            {
                throw new PluginException("ERR_INVALID_PARAMS",
                    $"{label} regex evaluation timed out (possible catastrophic backtracking).");
            }
        }
    }
}
