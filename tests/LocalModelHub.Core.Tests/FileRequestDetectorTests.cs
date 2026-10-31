using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class FileRequestDetectorTests
{
    [Theory]
    [InlineData("帮我修改 main.cs 里的空指针问题")]
    [InlineData("读取 appsettings.json 并把端口改成 8080")]
    [InlineData("新建一个 README.md")]
    [InlineData("把这个函数重构一下")]
    [InlineData("看一下项目里有哪些文件")]
    public void Detects_file_requests(string text) =>
        Assert.True(FileRequestDetector.Matches(text));

    [Theory]
    [InlineData("解释一下这段代码是什么意思")]
    [InlineData("怎么读取文件内容")]
    [InlineData("Python 如何读取 CSV")]
    [InlineData("今天天气怎么样")]
    [InlineData("")]
    public void Ignores_general_questions(string text) =>
        Assert.False(FileRequestDetector.Matches(text));
}
