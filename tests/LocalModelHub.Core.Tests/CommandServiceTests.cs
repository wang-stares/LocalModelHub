using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public sealed class CommandServiceTests
{
    [Theory]
    [InlineData("dotnet test")]
    [InlineData("dotnet build")]
    [InlineData("npm test")]
    [InlineData("npm run build")]
    [InlineData("python -m pytest")]
    [InlineData("pytest -q")]
    [InlineData("go test ./...")]
    [InlineData("cargo test")]
    [InlineData("make test")]
    public void Allows_build_and_test_commands(string command)
    {
        Assert.True(CommandService.IsAllowed(command));
    }

    [Theory]
    [InlineData("rm -rf /")]
    [InlineData("dotnet test && rm -rf /")]
    [InlineData("npm install")]
    [InlineData("dotnet run")]
    [InlineData("curl https://example.com")]
    [InlineData("dotnet test | tee out.log")]
    [InlineData("sh -c 'rm -rf'")]
    public void Rejects_other_commands_and_shell_operators(string command)
    {
        Assert.False(CommandService.IsAllowed(command));
    }

    [Fact]
    public void Tokenize_handles_quotes_and_rejects_unclosed_quotes()
    {
        var tokens = CommandService.Tokenize("dotnet test --filter \"A=B\"");

        Assert.Equal(4, tokens.Count);
        Assert.Equal("A=B", tokens[3]);
        Assert.Empty(CommandService.Tokenize("dotnet test \"unclosed"));
    }
}
