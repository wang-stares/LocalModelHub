using System.Text.RegularExpressions;

namespace LocalModelHub.Core.Services;

/// <summary>
/// 识别「用户想读取 / 修改工作区文件」的请求。
/// 用于普通对话模式自动启用文件工具，避免用户必须先理解模式之间的差别。
/// </summary>
public static class FileRequestDetector
{
    private static readonly Regex FileName = new(
        @"[\w\-./\\]+\.(cs|ts|tsx|js|jsx|py|java|kt|go|rs|cpp|cc|c|h|hpp|csproj|sln|md|txt|json|ya?ml|xml|html|css|scss|sh|ps1|sql|toml|ini|cfg|conf|log)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Action = new(
        @"(读取|读一下|读读|打开|查看|看一下|看下|修改|编辑|改写|重构|修复|改一下|创建|新建|添加|写|保存|删除|替换|补全|整理)[^，。！？\n]{0,16}(文件|代码|函数|方法|类|脚本|目录|文件夹|工程|项目|配置|文档)",
        RegexOptions.Compiled);

    /// <summary>名词在前的说法，如「把这个函数重构一下」。</summary>
    private static readonly Regex ReverseAction = new(
        @"(文件|代码|函数|方法|类|脚本|目录|文件夹|工程|项目|配置|文档)[^，。！？\n]{0,8}(读取|打开|查看|修改|编辑|改写|重构|修复|创建|新建|删除|替换|补全|整理)",
        RegexOptions.Compiled);

    private static readonly Regex Question = new(@"(怎么|如何|为什么|什么是)", RegexOptions.Compiled);

    public static bool Matches(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (FileName.IsMatch(text)) return true;

        // 「怎么读取文件」这类知识问答不当成文件操作请求。
        return !Question.IsMatch(text) && (Action.IsMatch(text) || ReverseAction.IsMatch(text));
    }
}
