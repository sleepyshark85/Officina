using Microsoft.Extensions.DependencyInjection;

namespace Sleepyshark.Officina.Claude.Tests;

/// <summary>A Claude model registered under its key is made once, when first needed.</summary>
public sealed class ClaudeServicesTests
{
    [Fact]
    public void A_model_is_made_once_under_its_key_when_first_needed()
    {
        var made = 0;
        using var services = new ServiceCollection()
            .AddClaudeModel("chat", _ => new ClaudeModel("test-key") { Model = ClaudeModel.Opus55, Effort = ClaudeEffort.Low, MaxOutputTokens = made++ + 1_000 })
            .BuildServiceProvider();
        Assert.Equal(0, made);

        var model = services.GetRequiredKeyedService<IModel>("chat");

        Assert.Same(model, services.GetRequiredKeyedService<IModel>("chat"));
        Assert.Equal((1, ClaudeModel.Opus55), (made, model.Name));
        Assert.Null(services.GetKeyedService<IModel>("other"));
    }
}
