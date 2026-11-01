using LocalModelHub.Core.Models;

namespace LocalModelHub.Core.Services.Abstractions;

/// <summary>工作区读写契约：所有路径都按相对路径解析，越界访问由实现拒绝。</summary>
public interface IWorkspace
{
    /// <summary>当前工作文件夹的绝对路径；未设置时为空。</summary>
    string? Root { get; }

    /// <summary>工作文件夹已设置且目录存在。</summary>
    bool IsConfigured { get; }

    void SetRoot(string? path);

    WorkspaceResult ListFiles(string? relativePath);

    WorkspaceResult ReadFile(string? relativePath);

    WorkspaceResult ReadFile(string? relativePath, int? startLine, int? endLine);

    WorkspaceResult Search(string? query, string? include, int maxResults = 24, int contextLines = 0);

    WorkspaceResult FindSymbols(string? query, string? include = null, int maxResults = 20);

    WorkspaceResult WriteFile(string? relativePath, string? content);

    WorkspaceResult ReplaceInFile(string? relativePath, string? oldText, string? newText);

    WorkspaceResult ApplyPatch(string? patch);

    bool TryResolve(string? relativePath, out string fullPath, out string error);
}
