using PCCExecutive.Browser;
using Xunit;

namespace PCCExecutive.Browser.Tests;

public sealed class BrowserRuntimeTests
{
    [Fact]
    public async Task Ownership_proof_and_kill_all_exclude_personal_chrome()
    {
        var root = Root(); var owned = Runtime(root, "owned"); var personal = Runtime(root, "personal") with { CreatedByPcc = false, AdoptedExplicitly = false, ProcessId = 5002, ProcessStartIdentity = "pid:5002:start:2", OwnershipNonce = "personal", ProfilePath = Path.Combine(root,"personal") };
        var registry = new InMemoryBrowserRuntimeRegistry(); await registry.UpsertAsync(owned); await registry.UpsertAsync(personal);
        var markers = new FakeMarkers(); markers.Set(Marker(owned)); markers.Set(Marker(personal));
        var processes = new FakeProcesses(); processes.Set(owned.ProcessId!.Value, owned.ProcessStartIdentity!, true); processes.Set(personal.ProcessId!.Value, personal.ProcessStartIdentity!, true);
        var host = new FakeHost(root); var controller = new BrowserSessionController(registry, host, new OwnershipProofService(root, markers, processes), markers, processes);
        var proof = await new OwnershipProofService(root, markers, processes).ProveAsync(owned); Assert.True(proof.IsProven);
        var result = await controller.KillAllPccSessionsAsync();
        Assert.Contains(owned.RuntimeId, result.KilledRuntimeIds); Assert.DoesNotContain(personal.RuntimeId, result.KilledRuntimeIds); Assert.Equal("NO_PCC_OWNERSHIP_FLAG", result.SkippedRuntimeReasons[personal.RuntimeId]); Assert.Equal(new[] { owned.RuntimeId }, host.Killed);
    }

    [Fact]
    public async Task Individual_kill_requires_positive_ownership_and_start_identity()
    {
        var root=Root(); var runtime=Runtime(root,"one"); var registry=new InMemoryBrowserRuntimeRegistry(); await registry.UpsertAsync(runtime); var markers=new FakeMarkers(); markers.Set(Marker(runtime)); var processes=new FakeProcesses(); processes.Set(runtime.ProcessId!.Value,"wrong-start",true); var host=new FakeHost(root); var controller=new BrowserSessionController(registry,host,new OwnershipProofService(root,markers,processes),markers,processes);
        var denied=await controller.KillAsync(runtime.RuntimeId); Assert.False(denied.Succeeded); Assert.Equal("PROCESS_START_IDENTITY_MISMATCH",denied.Reason); Assert.Empty(host.Killed);
    }

    [Fact]
    public async Task Dead_orphan_recovery_replaces_without_unsafe_kill_and_preserves_logical_identity()
    {
        var root=Root(); var runtime=Runtime(root,"orphan"); var registry=new InMemoryBrowserRuntimeRegistry(); await registry.UpsertAsync(runtime); var markers=new FakeMarkers(); markers.Set(Marker(runtime)); var processes=new FakeProcesses(); processes.Set(runtime.ProcessId!.Value,runtime.ProcessStartIdentity!,false); var host=new FakeHost(root); var controller=new BrowserSessionController(registry,host,new OwnershipProofService(root,markers,processes),markers,processes);
        var result=await controller.RecoverOrphanAsync(runtime.RuntimeId); Assert.True(result.Succeeded); Assert.Equal("DEAD_ORPHAN_REPLACED_WITH_NEW_PCC_RUNTIME",result.Reason); Assert.Empty(host.Killed); Assert.Equal(runtime.LogicalAgentId,result.Runtime!.LogicalAgentId); Assert.Equal(runtime.ProjectRunId,result.Runtime.ProjectRunId);
    }

    [Fact]
    public void Wrong_chat_guard_matches_exact_bindings_and_unknown_is_fail_safe()
    {
        var runtime=Runtime(Root(),"guard"); var expected=Expectation(runtime); var guard=new WrongChatGuard();
        var ok=guard.Evaluate(runtime,expected,Snapshot()); Assert.True(ok.MaySend);
        var mismatch=guard.Evaluate(runtime with { ConversationIdentity="wrong" },expected,Snapshot()); Assert.False(mismatch.MaySend); Assert.Equal("CONVERSATION_IDENTITY_MISMATCH",mismatch.Reason);
        var unknown=guard.Evaluate(runtime,expected,Snapshot(input:InputState.Unknown)); Assert.False(unknown.MaySend); Assert.Equal("BROWSER_ADAPTER_UNCERTAIN",unknown.Reason);
    }

    [Fact]
    public async Task Submitted_unknown_is_persisted_and_not_blindly_retried()
    {
        var runtime=Runtime(Root(),"send"); var registry=new InMemoryBrowserRuntimeRegistry(); await registry.UpsertAsync(runtime); var adapter=new FakeAdapter { Snapshot=Snapshot(), Submission=new(true,false,true,"uncertain",new[]{"enter-triggered"}) }; var ledger=new InMemoryDispatchLedger(); var provider=new BrowserChatProvider(registry,adapter,ledger,new WrongChatGuard(),new GlobalBrowserSendGate()); var request=new BrowserDispatchRequest("dispatch-1",runtime.ProjectRunId,runtime.LogicalAgentId,runtime.TaskId!,runtime.ConversationIdentity!,runtime.ProviderConversationIdentity!,"prompt");
        var first=await provider.SendAsync(runtime.RuntimeId,request); var second=await provider.SendAsync(runtime.RuntimeId,request); var persisted=await ledger.GetAsync(request.DispatchId);
        Assert.Equal(BrowserDispatchOutcome.SubmittedUnknown,first.Outcome); Assert.Equal(DispatchState.SubmittedUnknown,persisted!.State); Assert.Equal(BrowserDispatchOutcome.DuplicateBlocked,second.Outcome); Assert.Equal(1,adapter.SubmitCalls);
    }

    [Fact]
    public async Task Proven_new_chat_submission_persists_resulting_provider_conversation_identity()
    {
        var runtime = Runtime(Root(), "new-chat") with { ProviderConversationIdentity = "NEW" };
        var registry = new InMemoryBrowserRuntimeRegistry();
        await registry.UpsertAsync(runtime);
        var adapter = new FakeAdapter
        {
            Snapshot = Snapshot(),
            Submission = new(true, true, false, "submitted", ["generation:started"]),
            CurrentConversationIdentity = "created-conversation"
        };
        var provider = new BrowserChatProvider(registry, adapter, new InMemoryDispatchLedger(), new WrongChatGuard(), new GlobalBrowserSendGate());

        var result = await provider.SendAsync(runtime.RuntimeId, new BrowserDispatchRequest("new-dispatch", runtime.ProjectRunId, runtime.LogicalAgentId, runtime.TaskId!, runtime.ConversationIdentity!, "NEW", "prompt"));

        Assert.Equal(BrowserDispatchOutcome.Submitted, result.Outcome);
        Assert.Equal("created-conversation", (await registry.GetAsync(runtime.RuntimeId))!.ProviderConversationIdentity);
    }

    [Fact]
    public void Automatic_staged_dispatch_respects_ten_second_boundary()
    {
        var scheduler=new BrowserDispatchScheduler(); var options=new DispatchSchedulerOptions(); var t=DateTimeOffset.UtcNow; var gate=new GlobalSendGateSnapshot(false,null,null,null);
        Assert.False(scheduler.Evaluate(t.AddMilliseconds(9999),t,0,options,gate).MayDispatch); Assert.True(scheduler.Evaluate(t.AddSeconds(10),t,0,options,gate).MayDispatch); Assert.Equal(5,options.MaximumWorkers); Assert.Equal(TimeSpan.FromSeconds(10),options.EffectiveBaseInterval);
    }

    [Fact]
    public void Global_fault_pauses_new_sends_but_per_session_slow_does_not()
    {
        var classifier=new ChatGptResilienceClassifier(TimeSpan.FromSeconds(1),TimeSpan.FromSeconds(10)); var gate=new GlobalBrowserSendGate(); var now=DateTimeOffset.UtcNow;
        var global=classifier.Classify(Snapshot(health:PageHealth.RateLimited),TimeSpan.Zero); gate.Apply(global,now,TimeSpan.FromMinutes(1)); Assert.True(gate.Snapshot.IsPaused); Assert.Equal(FaultScope.Global,global.Scope);
        var slow=classifier.Classify(Snapshot(generation:GenerationState.Generating),TimeSpan.FromSeconds(2)); Assert.Equal(FaultScope.PerSession,slow.Scope); Assert.False(slow.PauseUnsafeNewSends);
    }

    [Fact]
    public void Partial_response_and_auth_challenge_classification_are_conservative()
    {
        var classifier=new ChatGptResilienceClassifier(); var partial=classifier.Classify(Snapshot(completeness:ResponseCompleteness.Partial),TimeSpan.Zero); Assert.Equal(ChatGptResilienceState.PartialResponse,partial.State); Assert.NotEqual(ChatGptResilienceState.Done,partial.State);
        var challenge=classifier.Classify(Snapshot(auth:AuthState.Challenge),TimeSpan.Zero); Assert.Equal(FaultScope.Global,challenge.Scope); Assert.True(challenge.PauseUnsafeNewSends); Assert.True(challenge.RequiresHumanAction);
    }

    [Fact]
    public async Task Conversation_rollover_commits_lineage_only_after_validation()
    {
        var active=Conversation("old",1); var store=new FakeLifecycleStore(); var manager=new ConversationLifecycleManager(new FakeCheckpoint(),new FakeCreator(),new FakeSender(true),new FakeValidator(true),store); var result=await manager.RolloverAsync(active,"growth","continue");
        Assert.True(result.Succeeded); Assert.Equal("old",result.ActiveConversation.PredecessorConversationId); Assert.Equal(result.ActiveConversation.ConversationId,result.RetiredConversation!.SuccessorConversationId); Assert.Equal(2,result.ActiveConversation.Sequence); Assert.True(store.Committed);
    }

    [Fact]
    public async Task Failed_rollover_preserves_old_active_conversation()
    {
        var active=Conversation("old",1); var store=new FakeLifecycleStore(); var manager=new ConversationLifecycleManager(new FakeCheckpoint(),new FakeCreator(),new FakeSender(true),new FakeValidator(false),store); var result=await manager.RolloverAsync(active,"growth","continue");
        Assert.False(result.Succeeded); Assert.Equal("old",result.ActiveConversation.ConversationId); Assert.Equal(ConversationLifecycleState.Active,result.ActiveConversation.State); Assert.False(store.Committed); Assert.True(store.FailedRecorded);
    }

    [Fact]
    public void Conversation_health_is_explicitly_heuristic_not_token_counter()
    {
        var result=new ConversationHealthEstimator().Assess(new ConversationHealthObservation(190,750000,TimeSpan.FromDays(2),1)); Assert.Equal(ConversationHealthState.RolloverSoon,result.State); Assert.True(result.IsHeuristic); Assert.Contains("no authoritative remaining-token claim",result.Reason,StringComparison.OrdinalIgnoreCase);
    }

    private static string Root()=>Path.Combine(Path.GetTempPath(),"pcc-browser-tests",Guid.NewGuid().ToString("N"));
    private static BrowserRuntimeRecord Runtime(string root,string id)=>new(){RuntimeId=id,ProjectRunId="project-run",LogicalAgentId="agent-3",WorkerSlotId="3",TaskId="task-3",ProcessId=5001,ProcessStartIdentity="pid:5001:start:1",ContextIdentity="ctx-1",ProfilePath=Path.Combine(root,id),CreatedByPcc=true,ConversationIdentity="conversation-3",ProviderConversationIdentity="https://chatgpt.com/c/provider-conversation-3",Visibility=BrowserVisibility.Hidden,State=BrowserSessionState.Hidden,LastHeartbeatAt=DateTimeOffset.UtcNow,LastActivityAt=DateTimeOffset.UtcNow,OwnershipNonce="nonce-1"};
    private static OwnershipMarker Marker(BrowserRuntimeRecord r)=>new(r.RuntimeId,r.ProcessId!.Value,r.ProcessStartIdentity!,r.ContextIdentity!,r.ProfilePath,r.CreatedByPcc,r.AdoptedExplicitly,r.OwnershipNonce);
    private static BrowserDispatchExpectation Expectation(BrowserRuntimeRecord r)=>new(r.ProjectRunId,r.LogicalAgentId,r.TaskId!,r.ConversationIdentity!,r.ProviderConversationIdentity!);
    private static ChatGptSemanticSnapshot Snapshot(InputState input=InputState.Ready,GenerationState generation=GenerationState.Idle,AuthState auth=AuthState.Authenticated,ConversationMatch conversation=ConversationMatch.Match,PageHealth health=PageHealth.Healthy,ResponseCompleteness completeness=ResponseCompleteness.None)=>new(SemanticDetection<InputState>.Create(input,input==InputState.Unknown?0:.9,"test","input"),SemanticDetection<GenerationState>.Create(generation,generation==GenerationState.Unknown?0:.9,"test","generation"),SemanticDetection<AuthState>.Create(auth,auth==AuthState.Unknown?0:.9,"test","auth"),SemanticDetection<ConversationMatch>.Create(conversation,conversation==ConversationMatch.Unknown?0:.9,"test","conversation"),SemanticDetection<PageHealth>.Create(health,health==PageHealth.Unknown?0:.9,"test","health"),completeness,0,null,DateTimeOffset.UtcNow,"test");
    private static ConversationRecord Conversation(string id,int seq)=>new(){ConversationId=id,LogicalAgentId="agent",ProjectRunId="run",Sequence=seq,UrlOrProviderIdentity=$"https://chatgpt.com/c/{id}-provider",CreatedAt=DateTimeOffset.UtcNow,State=ConversationLifecycleState.Active};

    private sealed class FakeMarkers:IOwnershipMarkerStore { private readonly Dictionary<string,OwnershipMarker> _m=new(StringComparer.Ordinal); public void Set(OwnershipMarker m)=>_m[m.ProfilePath]=m; public Task WriteAsync(OwnershipMarker m,CancellationToken c=default){Set(m);return Task.CompletedTask;} public Task<OwnershipMarker?> ReadAsync(string p,CancellationToken c=default)=>Task.FromResult(_m.TryGetValue(p,out var m)?m:null); }
    private sealed class FakeProcesses:IProcessInspector { private readonly Dictionary<int,(string Start,bool Alive)> _p=new(); public void Set(int id,string s,bool a)=>_p[id]=(s,a); public bool IsAlive(int id)=>_p.TryGetValue(id,out var p)&&p.Alive; public string? GetStartIdentity(int id)=>_p.TryGetValue(id,out var p)?p.Start:null; }
    private sealed class FakeHost:IBrowserRuntimeHost { private readonly string _root; public List<string> Killed{get;}=new(); public FakeHost(string root)=>_root=root; public Task<BrowserRuntimeRecord> LaunchAsync(BrowserSessionRequest r,CancellationToken c=default){var id=r.RuntimeId??Guid.NewGuid().ToString("N"); return Task.FromResult(Runtime(_root,id) with {ProjectRunId=r.ProjectRunId,LogicalAgentId=r.LogicalAgentId,WorkerSlotId=r.WorkerSlotId,TaskId=r.TaskId,ConversationIdentity=r.ConversationIdentity,ProviderConversationIdentity=r.ProviderConversationIdentity});} public Task<bool> RecoverAsync(BrowserRuntimeRecord r,CancellationToken c=default)=>Task.FromResult(true); public Task SetVisibilityAsync(BrowserRuntimeRecord r,BrowserVisibility v,bool b,CancellationToken c=default)=>Task.CompletedTask; public Task KillAsync(BrowserRuntimeRecord r,OwnershipProof p,CancellationToken c=default){Assert.True(p.IsProven);Killed.Add(r.RuntimeId);return Task.CompletedTask;} public Task<BrowserRuntimeTelemetry> GetTelemetryAsync(BrowserRuntimeRecord r,CancellationToken c=default)=>Task.FromResult(new BrowserRuntimeTelemetry(r.RuntimeId,true,1,1,TimeSpan.Zero,r.LastHeartbeatAt,false,r.IsArchived)); }
    private sealed class FakeAdapter:IChatGptBrowserAdapter { public string AdapterVersion=>"test"; public ChatGptSemanticSnapshot Snapshot{get;init;}=BrowserRuntimeTests.Snapshot(); public AdapterSubmissionResult Submission{get;init;}=new(false,false,false,"not-triggered",Array.Empty<string>()); public string? CurrentConversationIdentity{get;init;} public int SubmitCalls{get;private set;} public Task<ChatGptSemanticSnapshot> InspectAsync(BrowserRuntimeRecord r,BrowserDispatchExpectation e,CancellationToken c=default)=>Task.FromResult(Snapshot); public Task<AdapterSubmissionResult> SubmitAsync(BrowserRuntimeRecord r,BrowserDispatchExpectation e,string p,CancellationToken c=default){SubmitCalls++;return Task.FromResult(Submission);} public Task<string?> GetCurrentConversationIdentityAsync(BrowserRuntimeRecord r,CancellationToken c=default)=>Task.FromResult(CurrentConversationIdentity); }
    private sealed class FakeCheckpoint:IConversationCheckpointPort { public Task<string> CreateCheckpointAsync(ConversationRecord a,CancellationToken c=default)=>Task.FromResult("checkpoint"); }
    private sealed class FakeCreator:IConversationCreator { public Task<ConversationCreationResult> CreateAsync(ConversationRecord p,CancellationToken c=default)=>Task.FromResult(new ConversationCreationResult("new","https://chatgpt.com/c/new-provider")); }
    private sealed class FakeSender(bool result):IContinuationSender { public Task<bool> SendContinuationAsync(ConversationRecord c,string id,string p,CancellationToken ct=default)=>Task.FromResult(result); }
    private sealed class FakeValidator(bool result):IContinuationValidator { public Task<ContinuationValidationResult> ValidateAsync(ConversationRecord c,CancellationToken ct=default)=>Task.FromResult(new ContinuationValidationResult(result,result?"ok":"bad")); }
    private sealed class FakeLifecycleStore:IConversationLifecycleStore { public bool Committed{get;private set;} public bool FailedRecorded{get;private set;} public Task SaveCandidateAsync(ConversationRecord c,string id,CancellationToken ct=default)=>Task.CompletedTask; public Task CommitRolloverAsync(ConversationRecord p,ConversationRecord s,string id,CancellationToken ct=default){Committed=true;return Task.CompletedTask;} public Task RecordFailedRolloverAsync(ConversationRecord p,ConversationRecord? f,string reason,CancellationToken ct=default){FailedRecorded=true;return Task.CompletedTask;} }
}
