using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;
using Xunit;

namespace LocalModelHub.Core.Tests;

public class AgentProtocolTests
{
    [Fact]
    public void ParsePlan_reads_numbered_steps()
    {
        var plan = AgentProtocol.ParsePlan("""
            计划如下：
            1. 读取 README.md 确认构建命令
            2. 修改 src/app.cs 中的入口
            3. 复核改动并给出验收结果
            以上。
            """);

        Assert.True(plan.HasSteps);
        Assert.Equal(3, plan.Steps.Count);
        Assert.Contains("README.md", plan.Steps[0]);
        Assert.Contains("验收结果", plan.Steps[2]);
    }

    [Fact]
    public void ParsePlan_accepts_bullets_and_chinese_numbering()
    {
        var plan = AgentProtocol.ParsePlan("- 查看目录\n* 读取配置\n1）修改配置\n第2步：复核");

        Assert.Equal(4, plan.Steps.Count);
    }

    [Fact]
    public void ParsePlan_caps_steps_and_ignores_prose()
    {
        var lines = Enumerable.Range(1, 12).Select(index => $"{index}. 步骤 {index} 完成");
        var plan = AgentProtocol.ParsePlan(string.Join('\n', lines));

        Assert.Equal(8, plan.Steps.Count);
        Assert.Empty(AgentProtocol.ParsePlan("我建议先读文件，再修改。").Steps);
        Assert.Empty(AgentProtocol.ParsePlan(null).Steps);
    }

    [Fact]
    public void ParseVerification_reads_json_contract()
    {
        var verification = AgentProtocol.ParseVerification("""
            ```json
            {"pass": false, "summary": "缺少验收", "issues": ["未读取目标文件", "回答声称写入但没有 write_file"], "evidence": ["a.txt"]}
            ```
            """);

        Assert.False(verification.Passed);
        Assert.Equal("json", verification.Source);
        Assert.Equal("未通过", verification.OutcomeLabel);
        Assert.Equal(2, verification.Issues.Count);
        Assert.Single(verification.Evidence);
    }

    [Fact]
    public void ParseVerification_accepts_string_booleans()
    {
        var verification = AgentProtocol.ParseVerification("""{"pass": "true", "summary": "完成"}""");

        Assert.True(verification.Passed);
        Assert.Equal("json", verification.Source);
    }

    [Fact]
    public void ParseVerification_falls_back_to_text_and_skips_empty()
    {
        var failed = AgentProtocol.ParseVerification("自检：未通过。遗漏了用户要求的测试文件。");
        var skipped = AgentProtocol.ParseVerification("");

        Assert.False(failed.Passed);
        Assert.Equal("text", failed.Source);
        Assert.NotEmpty(failed.Issues);
        Assert.True(skipped.Passed);
        Assert.False(skipped.Verified);
        Assert.Equal("跳过", skipped.OutcomeLabel);
    }

    [Fact]
    public void Verify_prompt_requires_tool_evidence_and_json()
    {
        var plan = new AgentPlan(["读取 a.txt", "写入 a.txt"], "raw");
        var steps = new[]
        {
            new AgentStep(AgentStage.Act, 1, "先读", "read_file",
                """{"path":"a.txt"}""", "a.txt：内容", true)
        };

        var prompt = AgentProtocol.BuildVerifyPrompt("把 a.txt 改成新内容", plan, steps);

        Assert.Contains(AgentProtocol.VerifyMarker, prompt);
        Assert.Contains("把 a.txt 改成新内容", prompt);
        Assert.Contains("read_file", prompt);
        Assert.Contains("只输出 JSON", prompt);
        Assert.Contains("pass", prompt);
    }

    [Fact]
    public void Repair_prompt_lists_concrete_issues()
    {
        var verification = new AgentVerification(
            false, "未通过", ["没有实际写入 a.txt"], Array.Empty<string>(), "json");

        var prompt = AgentProtocol.BuildRepairPrompt(verification);

        Assert.Contains(AgentProtocol.RepairMarker, prompt);
        Assert.Contains("没有实际写入 a.txt", prompt);
        Assert.Contains("不要重复已经成功的调用", prompt);
    }

    [Fact]
    public void MissingPlanSections_reports_only_absent_parts()
    {
        var complete = "目标：修改入口\n步骤：1. 读取 2. 修改\n风险：回滚方法\n验收：构建通过";
        Assert.Empty(AgentProtocol.MissingPlanSections(complete));

        var partial = AgentProtocol.MissingPlanSections("目标：修改入口。\n1. 读取文件");
        Assert.Contains("步骤", partial);
        Assert.Contains("风险", partial);
        Assert.Contains("验收", partial);
        Assert.DoesNotContain("目标", partial);
    }

    [Fact]
    public void SummarizeSteps_keeps_path_and_result_not_file_body()
    {
        var steps = new[]
        {
            new AgentStep(AgentStage.Act, 1, "先读", "read_file",
                """{"path":"a.txt"}""", "a.txt：\n```\n" + new string('x', 5000) + "\n```", true)
        };

        var ledger = AgentProtocol.SummarizeSteps(steps);

        Assert.Contains("a.txt", ledger);
        Assert.Contains("成功", ledger);
        Assert.DoesNotContain(new string('x', 200), ledger);
        Assert.True(ledger.Length < 1400);
    }
}
