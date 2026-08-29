$ErrorActionPreference = 'Stop'

$path = 'src/PCCExecutive.App/Presentation/IntegratedPresentationGateway.cs'
$text = Get-Content -Raw -LiteralPath $path
$old = @'
        var request = new AgentRequest(
            run.Id,
            managerAgentId,
            new ConversationId(Guid.Parse(runtime.ConversationIdentity)),
            DispatchId.New(),
            repairPrompt,
            repairHash,
            null,
            null,
            null,
            runtime.ProviderConversationIdentity);
        var result = await _agentProvider.SendAsync(request, cancellationToken).ConfigureAwait(false);
'@
$new = @'
        var repairConversation = new ConversationId(Guid.Parse(runtime.ConversationIdentity));
        var repairTaskKey = $"{runtime.TaskId ?? $"manager-plan:{run.Id}"}:format-repair:{rejectedResponseHash}";
        var repairTaskId = CanonicalDispatchIdentity.StableTask(run.Id, repairTaskKey);
        var repairWaveId = CanonicalDispatchIdentity.StableWave(run.Id, repairTaskKey);
        var repairCorrelation = new DurableDispatchCorrelation(
            run.Id,
            managerAgentId,
            null,
            repairTaskId,
            repairWaveId,
            repairConversation,
            runtime.ProviderConversationIdentity,
            repairHash);
        var repairDispatch = await _dispatchReservations.ReserveOrRecoverAsync(repairCorrelation, cancellationToken).ConfigureAwait(false);
        var request = new AgentRequest(
            run.Id,
            managerAgentId,
            repairConversation,
            repairDispatch.Id,
            repairPrompt,
            repairHash,
            null,
            null,
            null,
            runtime.ProviderConversationIdentity);
        var result = await _agentProvider.SendAsync(request, cancellationToken).ConfigureAwait(false);
'@

if (-not $text.Contains($old.Trim())) {
    throw 'Unsafe Manager format-repair dispatch block was not found at the expected source boundary.'
}
$text = $text.Replace($old.Trim(), $new.Trim())
Set-Content -LiteralPath $path -Value $text -Encoding utf8NoBOM
Write-Host 'Manager format repair now uses canonical durable dispatch reservation; no ad-hoc DispatchId is created.'
