using Citus.Modules.UnityAi.Application;
using Citus.Modules.UnityAi.Application.Contracts;
using Citus.Modules.UnityAi.Domain.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Citus.Modules.UnityAi.Tests;

public sealed class ActionCenterTaskServiceTests
{
    private static readonly Guid CompanyA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid CompanyB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid UserA = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task Regenerate_FromOneProvider_InsertsTask()
    {
        var (service, store, events, _) = BuildService(
            new FakeProvider("p1", new[]
            {
                BuildDraft(CompanyA, "task.smtp.fingerprint", "Configure SMTP"),
            }));

        var result = await service.RegenerateAsync(CompanyA, UserA, CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(0, result.Deduped);
        Assert.Single(store.Records);
        Assert.Equal(ActionCenterTaskStatus.Open, store.Records[0].Status);
        Assert.Single(events.Events);
        Assert.Equal(ActionCenterTaskEventType.Created, events.Events[0].EventType);
    }

    [Fact]
    public async Task Regenerate_DedupesByFingerprint()
    {
        var draft = BuildDraft(CompanyA, "task.fingerprint.x", "First");
        var (service, store, _, _) = BuildService(new FakeProvider("p1", new[] { draft }));

        await service.RegenerateAsync(CompanyA, UserA, CancellationToken.None);
        var second = await service.RegenerateAsync(CompanyA, UserA, CancellationToken.None);

        Assert.Equal(0, second.Inserted);
        Assert.Equal(1, second.Deduped);
        Assert.Single(store.Records);
    }

    [Fact]
    public async Task Regenerate_TaskFromCompanyA_DoesNotAppearInCompanyB()
    {
        var (service, store, _, _) = BuildService(
            new FakeProvider("p1", new[] { BuildDraft(CompanyA, "fp", "title") }));

        await service.RegenerateAsync(CompanyA, UserA, CancellationToken.None);
        var bTasks = await service.GetTasksAsync(CompanyB, null, null, CancellationToken.None);

        Assert.Single(store.Records);
        Assert.Empty(bTasks);
    }

    [Fact]
    public async Task TaskHasReasonAndEvidence()
    {
        var (service, store, _, _) = BuildService(
            new FakeProvider("p1", new[]
            {
                BuildDraft(CompanyA, "fp", "Title", reason: "because", evidenceJson: "{\"x\":1}"),
            }));

        await service.RegenerateAsync(CompanyA, UserA, CancellationToken.None);

        var task = store.Records.Single();
        Assert.Equal("because", task.Reason);
        Assert.Equal("{\"x\":1}", task.EvidenceJson);
    }

    [Fact]
    public async Task StatusTransitions_Start_Done_Dismiss_Snooze()
    {
        var (service, store, events, _) = BuildService(
            new FakeProvider("p1", new[] { BuildDraft(CompanyA, "fp", "T") }));
        await service.RegenerateAsync(CompanyA, UserA, CancellationToken.None);
        var taskId = store.Records.Single().Id;

        var afterStart = await service.StartAsync(CompanyA, taskId, UserA, CancellationToken.None);
        Assert.Equal(ActionCenterTaskStatus.InProgress, afterStart!.Status);

        var afterDone = await service.CompleteAsync(CompanyA, taskId, UserA, CancellationToken.None);
        Assert.Equal(ActionCenterTaskStatus.Done, afterDone!.Status);
        Assert.NotNull(afterDone.CompletedAt);

        var afterDismiss = await service.DismissAsync(CompanyA, taskId, UserA, CancellationToken.None);
        Assert.Equal(ActionCenterTaskStatus.Dismissed, afterDismiss!.Status);

        var until = DateTimeOffset.UtcNow.AddDays(2);
        var afterSnooze = await service.SnoozeAsync(CompanyA, taskId, UserA, until, CancellationToken.None);
        Assert.Equal(ActionCenterTaskStatus.Snoozed, afterSnooze!.Status);
        Assert.Equal(until, afterSnooze.SnoozedUntil);

        // Started, Completed, Dismissed, Snoozed plus Created = 5 events.
        Assert.Equal(5, events.Events.Count);
    }

    [Fact]
    public async Task ProviderThrowing_DoesNotKillRegenerate()
    {
        var throwing = new FakeProvider("bad", _ => throw new InvalidOperationException("provider boom"));
        var ok = new FakeProvider("ok", new[] { BuildDraft(CompanyA, "fp", "OK") });

        var (service, store, _, _) = BuildService(throwing, ok);
        var result = await service.RegenerateAsync(CompanyA, UserA, CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Single(result.ProviderWarnings);
        Assert.Single(store.Records);
    }

    [Fact]
    public async Task Disabled_ReturnsNoTasks_NoStoreCalls()
    {
        var settings = new Dictionary<string, string?> { ["ACTION_CENTER_ENABLED"] = "false" };
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var flags = new UnityAiFeatureFlagAccessor(config);

        var store = new InMemoryActionCenterTaskStore();
        var events = new InMemoryActionCenterTaskEventStore();
        var jobRuns = new InMemoryAiJobRunStore();
        var providers = new IActionCenterTaskProvider[]
        {
            new FakeProvider("p1", new[] { BuildDraft(CompanyA, "fp", "T") })
        };
        var service = new ActionCenterTaskService(store, events, jobRuns, providers, flags, NullLogger<ActionCenterTaskService>.Instance);

        var result = await service.RegenerateAsync(CompanyA, UserA, CancellationToken.None);

        Assert.Equal(0, result.Inserted);
        Assert.Empty(store.Records);
        Assert.Single(result.ProviderWarnings);
    }

    private static (ActionCenterTaskService Service, InMemoryActionCenterTaskStore Store, InMemoryActionCenterTaskEventStore Events, InMemoryAiJobRunStore JobRuns)
        BuildService(params IActionCenterTaskProvider[] providers)
    {
        IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var flags = new UnityAiFeatureFlagAccessor(config);
        var store = new InMemoryActionCenterTaskStore();
        var events = new InMemoryActionCenterTaskEventStore();
        var jobRuns = new InMemoryAiJobRunStore();
        var service = new ActionCenterTaskService(store, events, jobRuns, providers, flags, NullLogger<ActionCenterTaskService>.Instance);
        return (service, store, events, jobRuns);
    }

    private static ActionCenterTaskDraft BuildDraft(CompanyId companyId, string fingerprint, string title,
        string reason = "test reason", string? evidenceJson = null)
        => new(
            CompanyId: companyId,
            AssignedUserId: null,
            TaskType: "test.task",
            SourceEngine: "test",
            SourceType: ActionCenterTaskSourceType.Rule,
            SourceObjectId: null,
            Title: title,
            Description: null,
            Reason: reason,
            EvidenceJson: evidenceJson,
            Priority: ActionCenterTaskPriority.Medium,
            DueDate: null,
            ActionUrl: null,
            Fingerprint: fingerprint);

    private sealed class FakeProvider : IActionCenterTaskProvider
    {
        private readonly Func<Guid, IReadOnlyList<ActionCenterTaskDraft>>? _factory;
        private readonly Func<Guid, Exception>? _throwFactory;

        public FakeProvider(string name, IReadOnlyList<ActionCenterTaskDraft> drafts)
        {
            ProviderName = name;
            _factory = _ => drafts;
        }

        public FakeProvider(string name, Func<Guid, Exception> throwFactory)
        {
            ProviderName = name;
            _throwFactory = throwFactory;
        }

        public string ProviderName { get; }

        public Task<IReadOnlyList<ActionCenterTaskDraft>> GenerateAsync(
            CompanyId companyId, UserId? userId, DateTimeOffset asOfUtc, CancellationToken cancellationToken)
        {
            if (_throwFactory is not null)
            {
                throw _throwFactory(companyId);
            }
            return Task.FromResult(_factory!(companyId));
        }
    }
}
