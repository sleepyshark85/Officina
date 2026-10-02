using Taskr.Core;
using Xunit;

namespace Taskr.Tests;

public class PlanTests
{
    private static TaskFile File(params (string Name, string[] Deps)[] tasks) => new()
    {
        Folder = Path.GetTempPath(),
        Tasks = tasks.ToDictionary(task => task.Name, task => new TaskDefinition("true", task.Deps, [], [], new Dictionary<string, string>())),
    };

    [Fact]
    public void Runs_dependencies_first_and_each_once() =>
        Assert.Equal(["lib", "app", "test"], File(("lib", []), ("app", ["lib"]), ("test", ["lib", "app"])).Plan(["test", "app"]));

    [Fact]
    public void Reports_a_cycle_with_its_path() =>
        Assert.Contains("cycle: a -> b -> a", Assert.Throws<PlanException>(() => File(("a", ["b"]), ("b", ["a"])).Plan(["a"])).Problems);

    [Fact]
    public void Reports_an_unknown_task_and_a_missing_dependency() =>
        Assert.Equal(
            ["unknown task: x", "c depends on nothing, which does not exist"],
            Assert.Throws<PlanException>(() => File(("c", ["nothing"])).Plan(["x", "c"])).Problems);
}
