using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LocalModelHub.App.Services;
using LocalModelHub.Core.Models;
using LocalModelHub.Core.Services;

namespace LocalModelHub.App.Views;

public partial class AddModelWindow : Window
{
    private IReadOnlyList<RepoFileOption> _fileOptions = [];

    public AddModelWindow()
    {
        InitializeComponent();

        CountryBox.ItemsSource = new[] { "国内", "海外" };
        CountryBox.SelectedIndex = 0;
        ProviderBox.ItemsSource = new[] { "ModelScope", "HuggingFace" };
        ProviderBox.SelectedIndex = 0;

        RepoLinkBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) await ParseRepoAsync();
        };
    }

    public ModelEntry? CreatedModel { get; private set; }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);

    private void OnParseRepo(object? sender, RoutedEventArgs e) => _ = ParseRepoAsync();

    /// <summary>调用仓库 API 拉取 GGUF 文件列表，成功后自动弹出选择下拉框并回填表单。</summary>
    private async Task ParseRepoAsync()
    {
        var input = RepoLinkBox.Text?.Trim() ?? "";
        StatusText.IsVisible = false;
        ParseStatus.IsVisible = false;
        if (input.Length == 0)
        {
            ShowError("请先粘贴仓库链接或填写 owner/name 仓库 ID。");
            return;
        }

        ParseButton.IsEnabled = false;
        ParseStatus.Text = "正在解析仓库…";
        ParseStatus.IsVisible = true;
        try
        {
            var provider = ProviderBox.SelectedIndex == 1 ? "HuggingFace" : "ModelScope";
            var result = await AppServices.Current.RemoteCatalog.InspectRepoAsync(
                input, provider, CancellationToken.None);

            if (result is null)
            {
                ShowError("无法识别仓库地址。示例：https://huggingface.co/Qwen/Qwen3-8B-GGUF");
                return;
            }

            ApplyInspection(result);
        }
        catch (Exception ex)
        {
            ShowError($"解析失败：{ex.Message}");
        }
        finally
        {
            ParseButton.IsEnabled = true;
        }
    }

    private void ApplyInspection(RepoInspection result)
    {
        RepoBox.Text = result.Repo;
        ProviderBox.SelectedIndex = result.Provider.Equals("HuggingFace", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        if (!string.IsNullOrWhiteSpace(result.DisplayName)) NameBox.Text = result.DisplayName;
        if (!string.IsNullOrWhiteSpace(result.Developer)) DeveloperBox.Text = result.Developer;
        CountryBox.SelectedIndex = result.Country.Equals("CN", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

        var (parameters, _) = RemoteCatalogService.GuessParametersFromName(result.DisplayName);
        if (parameters > 0) ParamsBox.Value = (decimal)Math.Round(parameters, 1, MidpointRounding.AwayFromZero);
        ContextBox.Value = RemoteCatalogService.GuessContextFromName(result.DisplayName);

        _fileOptions = result.Files;
        FileCombo.ItemsSource = result.Files.Select(option => option.Display).ToList();

        if (result.Files.Count == 0)
        {
            FileCombo.IsVisible = false;
            FileLabel.IsVisible = false;
            ShowError("该仓库没有可单独使用的 GGUF 文件（可能只有分片或附属文件）。");
            return;
        }

        FileLabel.IsVisible = true;
        FileCombo.IsVisible = true;
        FileCombo.SelectedIndex = 0;
        ParseStatus.Text = $"已解析 {result.Files.Count} 个 GGUF 文件" +
                           (string.IsNullOrWhiteSpace(result.License) ? "" : $" · {result.License}");
        ParseStatus.IsVisible = true;
        Dispatcher.UIThread.Post(() => FileCombo.IsDropDownOpen = true, DispatcherPriority.Background);
    }

    private void OnFileSelected(object? sender, SelectionChangedEventArgs e)
    {
        var index = FileCombo.SelectedIndex;
        if (index < 0 || index >= _fileOptions.Count) return;

        var option = _fileOptions[index];
        FileBox.Text = option.Path;
        if (!string.IsNullOrWhiteSpace(option.Label)) QuantBox.Text = option.Label;
        if (option.Bytes > 0)
        {
            SizeBox.Value = (decimal)Math.Round(
                option.Bytes / 1024d / 1024d / 1024d, 3, MidpointRounding.AwayFromZero);
        }
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        var name = NameBox.Text?.Trim() ?? "";
        var developer = DeveloperBox.Text?.Trim() ?? "";
        var repo = RepoBox.Text?.Trim() ?? "";
        var fileName = FileBox.Text?.Trim() ?? "";
        var quantLabel = QuantBox.Text?.Trim() ?? "";
        var description = DescriptionBox.Text?.Trim() ?? "";

        if (name.Length == 0 || repo.Length == 0 || fileName.Length == 0)
        {
            ShowError("请填写模型名称、仓库 ID 与文件名。也可以粘贴仓库链接后点「解析」自动填写。");
            return;
        }

        if (!fileName.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            ShowError("文件名必须以 .gguf 结尾。");
            return;
        }

        var sizeGb = SizeBox.Value ?? 0m;
        var parametersB = ParamsBox.Value ?? 0m;
        var context = ContextBox.Value ?? 0m;

        if (sizeGb <= 0)
        {
            ShowError("请填写文件大小（GB），需与仓库中的文件一致。");
            return;
        }

        if (parametersB <= 0 || context < 1024)
        {
            ShowError("参数量需大于 0，上下文至少 1024。");
            return;
        }

        var country = CountryBox.SelectedIndex == 1 ? "US" : "CN";
        var provider = ProviderBox.SelectedIndex == 1 ? "HuggingFace" : "ModelScope";
        var bytes = (long)(sizeGb * 1024m * 1024m * 1024m);

        // 解析过仓库时沿用来源提供的 SHA-256，下载完成后可校验文件完整性。
        var digest = _fileOptions
            .FirstOrDefault(option => option.Path.Equals(fileName, StringComparison.OrdinalIgnoreCase))
            ?.Digest;

        CreatedModel = new ModelEntry
        {
            Name = name,
            DisplayName = name,
            Developer = developer.Length == 0 ? "用户自定义" : developer,
            Country = country,
            Family = "自定义",
            ParametersB = (double)parametersB,
            ActiveParametersB = (double)parametersB,
            ContextLength = (int)context,
            ReleaseYear = DateTime.Now.Year,
            License = "自定义",
            Description = description.Length == 0 ? "用户自定义模型。" : description,
            Tags = [country == "CN" ? "中文" : "英文", "自定义"],
            HfRepo = repo,
            Quants =
            [
                new ModelQuant
                {
                    Label = quantLabel.Length == 0 ? "Q4_K_M" : quantLabel,
                    TotalBytes = bytes,
                    Files = [new ModelFile { Path = fileName, Bytes = bytes, Digest = digest }],
                    Sources = [new ModelSource { Provider = provider, Repo = repo, Revision = "master" }]
                }
            ]
        };

        Close(true);
    }

    private void ShowError(string message)
    {
        ParseStatus.IsVisible = false;
        StatusText.Text = message;
        StatusText.IsVisible = true;
    }
}
