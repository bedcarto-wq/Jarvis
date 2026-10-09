using Jarvis.Core.Security;
using Jarvis.Templates;

namespace Jarvis.Tests;

public class ExecutorTests
{
    [Fact]
    public async Task Sequence_ExecutesInOrder()
    {
        var h = new ExecutorHarness();
        var t = h.Add("Работа", Steps.Say("1"), Steps.Say("2"), Steps.Say("3"));
        var r = await h.Executor.RunAsync(t);
        Assert.Equal(ExecutionStatus.Completed, r.Status);
        Assert.Equal(["1", "2", "3"], h.Runner.Executed);
    }

    [Fact]
    public async Task DisabledSteps_AreSkipped()
    {
        var h = new ExecutorHarness();
        var off = Steps.Say("off"); off.Enabled = false;
        var r = await h.Executor.RunAsync(h.Add("t", Steps.Say("a"), off, Steps.Say("b")));
        Assert.Equal(["a", "b"], h.Runner.Executed);
    }

    [Theory]
    [InlineData(true, "then")]
    [InlineData(false, "else")]
    public async Task IfElse_ChoosesBranch(bool condition, string expected)
    {
        var h = new ExecutorHarness();
        h.Runner.Condition = _ => condition;
        var ifStep = new TemplateStep
        {
            Kind = StepKind.If,
            Condition = new StepCondition { Kind = ConditionKind.WindowExists },
            Then = [Steps.Say("then")],
            Else = [Steps.Say("else")],
        };
        await h.Executor.RunAsync(h.Add("t", ifStep, Steps.Say("after")));
        Assert.Equal([expected, "after"], h.Runner.Executed);
    }

    [Fact]
    public async Task Condition_Negate_And_PreviousStepSucceeded()
    {
        var h = new ExecutorHarness();
        var failing = Steps.Say("fail"); failing.ContinueOnError = true; failing.Retries = 1;
        h.Runner.Outcome = s => s.Get(Jarvis.Templates.P.Text) == "fail" ? StepOutcome.Fail("x") : StepOutcome.Ok();
        var check = new TemplateStep
        {
            Kind = StepKind.If,
            Condition = new StepCondition { Kind = ConditionKind.PreviousStepSucceeded, Negate = true },
            Then = [Steps.Say("recover")],
        };
        var r = await h.Executor.RunAsync(h.Add("t", failing, check));
        Assert.Equal(ExecutionStatus.Completed, r.Status);
        Assert.Equal(["fail", "recover"], h.Runner.Executed);
    }

    [Fact]
    public async Task FailingStep_RetriesThreeTimesByDefault_ThenFails()
    {
        var h = new ExecutorHarness();
        h.Runner.Outcome = _ => StepOutcome.Fail("нет окна");
        var r = await h.Executor.RunAsync(h.Add("t", Steps.Say("x"), Steps.Say("never")));
        Assert.Equal(ExecutionStatus.Failed, r.Status);
        Assert.Equal(3, h.Runner.Executed.Count(e => e == "x"));
        Assert.DoesNotContain("never", h.Runner.Executed);
    }

    [Fact]
    public async Task Retries_CannotExceedCap()
    {
        var h = new ExecutorHarness();
        h.Settings.MaxRetriesCap = 5;
        h.Runner.Outcome = _ => StepOutcome.Fail("x");
        var s = Steps.Say("x"); s.Retries = 1000;
        await h.Executor.RunAsync(h.Add("t", s));
        Assert.Equal(5, h.Runner.Executed.Count);
    }

    [Fact]
    public async Task NonRetryableFailure_IsNotRepeated()
    {
        var h = new ExecutorHarness();
        h.Runner.Outcome = _ => StepOutcome.Fail("файл не найден", retryable: false);
        await h.Executor.RunAsync(h.Add("t", Steps.Say("x")));
        Assert.Single(h.Runner.Executed);
    }

    [Fact]
    public async Task Repeat_IsLimitedBySettings()
    {
        var h = new ExecutorHarness();
        h.Settings.MaxRepeatCount = 10;
        var rep = new TemplateStep { Kind = StepKind.Repeat, Parameters = { [P.Count] = "5000" }, Body = [Steps.Say("r")] };
        await h.Executor.RunAsync(h.Add("t", rep));
        Assert.Equal(10, h.Runner.Executed.Count);
    }

    [Fact]
    public async Task Repeat_StopsWhenExitConditionTrue()
    {
        var h = new ExecutorHarness();
        int evals = 0;
        h.Runner.Condition = _ => ++evals > 2;
        var rep = new TemplateStep
        {
            Kind = StepKind.Repeat, Parameters = { [P.Count] = "50" }, Body = [Steps.Say("r")],
            Condition = new StepCondition { Kind = ConditionKind.TextOnScreen },
        };
        await h.Executor.RunAsync(h.Add("t", rep));
        Assert.Equal(2, h.Runner.Executed.Count);
    }

    [Fact]
    public async Task NestedTemplate_Runs()
    {
        var h = new ExecutorHarness();
        h.Add("Мессенджеры", Steps.Say("tg"), Steps.Say("discord"));
        var main = h.Add("Работа", Steps.Say("chrome"), Steps.Of(StepKind.RunTemplate, (P.Template, "Мессенджеры")), Steps.Say("claude"));
        var r = await h.Executor.RunAsync(main);
        Assert.Equal(ExecutionStatus.Completed, r.Status);
        Assert.Equal(["chrome", "tg", "discord", "claude"], h.Runner.Executed);
    }

    [Fact]
    public async Task CyclicTemplates_AreBlocked()
    {
        var h = new ExecutorHarness();
        var a = h.Add("A", Steps.Say("a"), Steps.Of(StepKind.RunTemplate, (P.Template, "B")));
        h.Add("B", Steps.Say("b"), Steps.Of(StepKind.RunTemplate, (P.Template, "A")));
        var r = await h.Executor.RunAsync(a);
        Assert.Equal(ExecutionStatus.Failed, r.Status);
        Assert.Contains("Циклический", r.Message);
        Assert.Equal(["a", "b"], h.Runner.Executed);
    }

    [Fact]
    public async Task SelfRecursion_IsBlocked()
    {
        var h = new ExecutorHarness();
        var a = h.Add("A", Steps.Of(StepKind.RunTemplate, (P.Template, "A")));
        var r = await h.Executor.RunAsync(a);
        Assert.Equal(ExecutionStatus.Failed, r.Status);
    }

    [Fact]
    public async Task EndTemplate_EndsOnlyCurrentTemplate()
    {
        var h = new ExecutorHarness();
        h.Add("Inner", Steps.Say("i1"), StepCatalog.Create(StepKind.EndTemplate), Steps.Say("i2"));
        var outer = h.Add("Outer", Steps.Of(StepKind.RunTemplate, (P.Template, "Inner")), Steps.Say("o"));
        await h.Executor.RunAsync(outer);
        Assert.Equal(["i1", "o"], h.Runner.Executed);
    }

    [Fact]
    public async Task Stop_InterruptsImmediately_AndReleasesKeys()
    {
        var h = new ExecutorHarness();
        h.Runner.BeforeExecute = async (s, ct) =>
        {
            if (s.Get(P.Text) == "2") { h.Stop.StopAll(); ct.ThrowIfCancellationRequested(); }
            await Task.Yield();
        };
        var r = await h.Executor.RunAsync(h.Add("t", Steps.Say("1"), Steps.Say("2"), Steps.Say("3")));
        Assert.Equal(ExecutionStatus.Stopped, r.Status);
        Assert.Equal(["1"], h.Runner.Executed);
        Assert.True(h.Input.ReleaseAllCount >= 1);
    }

    [Fact]
    public async Task Stop_DuringLongWait()
    {
        var h = new ExecutorHarness();
        var wait = Steps.Of(StepKind.Wait, (P.Ms, "3600000"));
        var task = h.Executor.RunAsync(h.Add("t", wait, Steps.Say("after")));
        while (h.Delay.TotalMs < 1000) await Task.Yield();
        h.Stop.StopAll();
        var r = await task;
        Assert.Equal(ExecutionStatus.Stopped, r.Status);
        Assert.Empty(h.Runner.Executed);
    }

    [Fact]
    public async Task NewRunAfterStop_Works()
    {
        var h = new ExecutorHarness();
        h.Stop.StopAll();
        var r = await h.Executor.RunAsync(h.Add("t", Steps.Say("ok")));
        Assert.Equal(ExecutionStatus.Completed, r.Status);
    }

    [Fact]
    public async Task Pause_BlocksUntilResume()
    {
        var h = new ExecutorHarness();
        h.Runner.BeforeExecute = (s, ct) => { if (s.Get(P.Text) == "1") h.Stop.Pause.Pause(); return Task.CompletedTask; };
        var task = h.Executor.RunAsync(h.Add("t", Steps.Say("1"), Steps.Say("2")));
        await Task.Delay(100);
        Assert.Equal(["1"], h.Runner.Executed);
        Assert.False(task.IsCompleted);
        h.Stop.Pause.Resume();
        var r = await task;
        Assert.Equal(ExecutionStatus.Completed, r.Status);
        Assert.Equal(["1", "2"], h.Runner.Executed);
    }

    [Fact]
    public async Task ConcurrentRun_ReturnsBusy()
    {
        var h = new ExecutorHarness();
        h.Runner.BeforeExecute = (s, ct) => { h.Stop.Pause.Pause(); return Task.CompletedTask; };
        var first = h.Executor.RunAsync(h.Add("a", Steps.Say("1"), Steps.Say("2")));
        await Task.Delay(50);
        var second = await h.Executor.RunAsync(h.Add("b", Steps.Say("x")));
        Assert.Equal(ExecutionStatus.Busy, second.Status);
        h.Stop.StopAll();
        await first;
    }

    [Fact]
    public async Task DangerousStep_RequiresConfirmation_DeniedCancels()
    {
        var h = new ExecutorHarness();
        h.Ui.ConfirmResult = false;
        var del = Steps.Of(StepKind.DeleteFile, (P.Path, @"C:\tmp\a.txt"));
        var r = await h.Executor.RunAsync(h.Add("t", del, Steps.Say("after")));
        Assert.Equal(ExecutionStatus.Cancelled, r.Status);
        Assert.Equal(1, h.Ui.ConfirmCalls);
        Assert.Empty(h.Runner.Executed);
    }

    [Fact]
    public async Task DangerousStep_Confirmed_Executes()
    {
        var h = new ExecutorHarness();
        var r = await h.Executor.RunAsync(h.Add("t", Steps.Of(StepKind.SystemOperation, (P.Operation, "Shutdown"))));
        Assert.Equal(ExecutionStatus.Completed, r.Status);
        Assert.Equal(1, h.Ui.ConfirmCalls);
        Assert.Single(h.Runner.Executed);
    }

    [Fact]
    public async Task CategoryDisabledByUser_NoConfirmation()
    {
        var h = new ExecutorHarness();
        h.Settings.DangerousOperations.First(d => d.Category == DangerCategory.DeleteFiles).RequireConfirmation = false;
        await h.Executor.RunAsync(h.Add("t", Steps.Of(StepKind.DeleteFile, (P.Path, "x"))));
        Assert.Equal(0, h.Ui.ConfirmCalls);
    }

    [Fact]
    public async Task UserMarkedStep_AlwaysConfirms()
    {
        var h = new ExecutorHarness();
        var send = Steps.Of(StepKind.PressKeys, (P.Keys, "Enter"));
        send.MarkedDangerous = DangerCategory.SendMessages;
        await h.Executor.RunAsync(h.Add("t", send));
        Assert.Equal(1, h.Ui.ConfirmCalls);
    }

    [Fact]
    public async Task DangerousHotkey_FromSettingsList_Confirms()
    {
        var h = new ExecutorHarness();
        await h.Executor.RunAsync(h.Add("t", Steps.Of(StepKind.PressKeys, (P.Keys, "shift+delete"))));
        Assert.Equal(1, h.Ui.ConfirmCalls);
        await h.Executor.RunAsync(h.Add("t2", Steps.Of(StepKind.PressKeys, (P.Keys, "Ctrl+T"))));
        Assert.Equal(1, h.Ui.ConfirmCalls);
    }

    [Fact]
    public async Task CancelPendingConfirmation_CancelsAction()
    {
        var h = new ExecutorHarness();
        h.Ui.HangOnConfirm = true;
        var task = h.Executor.RunAsync(h.Add("t", Steps.Of(StepKind.DeleteFile, (P.Path, "x")), Steps.Say("after")));
        await h.Ui.ConfirmStarted.Task;
        h.Stop.CancelPendingConfirmation();
        var r = await task;
        Assert.Equal(ExecutionStatus.Cancelled, r.Status);
        Assert.Empty(h.Runner.Executed);
    }

    [Fact]
    public async Task StopWorks_WhileWaitingForConfirmation()
    {
        var h = new ExecutorHarness();
        h.Ui.HangOnConfirm = true;
        var task = h.Executor.RunAsync(h.Add("t", Steps.Of(StepKind.DeleteFile, (P.Path, "x"))));
        await h.Ui.ConfirmStarted.Task;
        h.Stop.StopAll();
        var r = await task;
        Assert.Equal(ExecutionStatus.Stopped, r.Status);
    }

    [Fact]
    public async Task RunnerException_IsHandledAsFailure()
    {
        var h = new ExecutorHarness();
        h.Runner.BeforeExecute = (s, ct) => throw new InvalidOperationException("окно исчезло");
        var r = await h.Executor.RunAsync(h.Add("t", Steps.Say("x")));
        Assert.Equal(ExecutionStatus.Failed, r.Status);
        Assert.Contains("окно исчезло", r.Message);
    }

    [Fact]
    public async Task MissingNestedTemplate_Fails()
    {
        var h = new ExecutorHarness();
        var r = await h.Executor.RunAsync(h.Add("t", Steps.Of(StepKind.RunTemplate, (P.Template, "нет такого"))));
        Assert.Equal(ExecutionStatus.Failed, r.Status);
    }
}
