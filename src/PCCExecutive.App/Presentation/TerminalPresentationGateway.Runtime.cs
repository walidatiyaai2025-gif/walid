using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PCCExecutive.Application;
using PCCExecutive.Browser;
using PCCExecutive.Domain;
using PCCExecutive.GitHub;
using PCCExecutive.Infrastructure;
using PCCExecutive.Pcc;

namespace PCCExecutive.App.Presentation;

public sealed partial class TerminalPresentationGateway
{
    private async Task LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        _settings = await _store.LoadSettingsAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(_settings.Provider, "BrowserChat", StringComparison.OrdinalIgnoreCase))
        {
            _settings = _settings with { Provider = "BrowserChat" };
            await _store.SaveSettingsAsync(_settings, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            if (File.Exists(_uiPreferencesPath))
            {
                var json = await File.ReadAllTextAsync(_uiPreferencesPath, cancellationToken).ConfigureAwait(false);
                _uiPreferences = JsonSerializer.Deserialize<UiPreferences>(json) ?? UiPreferences.Default;
            }
        }
        catch (JsonException)
        {
            _uiPreferences = UiPreferences.Default;
        }
        catch (IOException)
        {
            _uiPreferences = UiPreferences.Default;
        }
    }

    private async Task SaveSettingsAsync(string? targetId, CancellationToken cancellationToken)
    {
        var values = ParseSettings(targetId);
        var mode = ParseEnum(values, "dispatch", _uiPreferences.DispatchMode);
        var baseSeconds = ParseInt(values, "base", _settings.BaseDispatchIntervalSeconds, 1, 3600);
        var maxWorkers = ParseInt(values, "maxWorkers", _settings.MaxWorkers, 1, WorkerSlotId.MaxValue);
        var adaptive = ParseBool(values, "adaptive", _settings.AdaptivePacing);
        var autoResume = ParseBool(values, "autoResume", _settings.AutoResume);
        var autoPause = ParseBool(values, "autoPause", _uiPreferences.AutoPauseOnLimit);
        var duplicate = ParseBool(values, "duplicate", _uiPreferences.DuplicateSendProtection);
        var provider = values.TryGetValue("provider", out var requestedProvider) ? requestedProvider : "BrowserWeb";
        if (!string.Equals(provider, ProviderMode.BrowserWeb.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("OpenAI API / Hybrid is not configured. Browser / ChatGPT Web remains the required first-build provider.");

        _settings = new PccExecutiveSettings("BrowserChat", maxWorkers, baseSeconds, adaptive, autoResume);
        _uiPreferences = new UiPreferences(mode, autoPause, duplicate);
        await _store.SaveSettingsAsync(_settings, cancellationToken).ConfigureAwait(false);

        var directory = Path.GetDirectoryName(_uiPreferencesPath)!;
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(_uiPreferences, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(_uiPreferencesPath, json, cancellationToken).ConfigureAwait(false);
        _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "SETTINGS_SAVED", "Browser-first dispatch settings persisted to durable SQLite + UI preference state.", true));
    }

    private async Task ResolveProjectAsync(string project, bool select, CancellationToken cancellationToken)
    {
        _lastResolution = await _resolver.ResolveProjectAsync(project, cancellationToken).ConfigureAwait(false);
        if (!_lastResolution.IsSuccess || _lastResolution.Project is null)
        {
            if (select)
            {
                _selectedProjectId = null;
                _baseline = null;
                _run = null;
                _managerAgentId = null;
                _workerAgentIds = [];
            }
            return;
        }

        if (!select) return;
        _selectedProjectId = _lastResolution.Project.ProjectControlId;
        _baseline = null;
        await EnsureProjectRunAsync(_lastResolution.Project, cancellationToken).ConfigureAwait(false);
        await RefreshBaselineAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureProjectRunAsync(ProjectRoutingSnapshot route, CancellationToken cancellationToken)
    {
        var projectId = DeterministicProjectId(route.ProjectControlId);
        var runId = DeterministicRunId(route.ProjectControlId);
        _run = await _store.LoadProjectRunAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? new ProjectRun(runId, projectId, ProjectRunState.Initializing, DateTimeOffset.UtcNow, new ManagerEstimate(0), new VerifiedCompletion(0), ProjectCompletionMode.Active);

        if (_run.State == ProjectRunState.Initializing)
        {
            var next = new ProjectRunStateMachine().Transition(_run.State, ProjectRunState.ManagerPlanning);
            _run = _run with { State = next };
        }
        await _store.SaveProjectRunAsync(_run, cancellationToken).ConfigureAwait(false);

        _managerAgentId = DeterministicAgentId(route.ProjectControlId, "manager");
        _workerAgentIds = Enumerable.Range(1, WorkerSlotId.MaxValue)
            .Select(slot => DeterministicAgentId(route.ProjectControlId, $"worker-{slot}"))
            .ToArray();

        var manager = await _store.LoadLogicalAgentAsync(_managerAgentId.Value, cancellationToken).ConfigureAwait(false)
            ?? new LogicalAgentSession(_managerAgentId.Value, runId, AgentRole.Manager, null, null, null, LogicalSessionState.Ready);
        await _store.SaveLogicalAgentAsync(manager, cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < _workerAgentIds.Length; index++)
        {
            var id = _workerAgentIds[index];
            var existing = await _store.LoadLogicalAgentAsync(id, cancellationToken).ConfigureAwait(false)
                ?? new LogicalAgentSession(id, runId, AgentRole.Worker, new WorkerSlotId(index + 1), null, null, LogicalSessionState.Ready);
            await _store.SaveLogicalAgentAsync(existing, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RefreshBaselineAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_selectedProjectId)) return;
        var result = await _baselineBuilder.BuildAsync(_selectedProjectId, cancellationToken).ConfigureAwait(false);
        _baseline = result.Value;
        if (!result.IsSuccess)
            _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, result.Status.ToString().ToUpperInvariant(), result.ErrorCode ?? "Live PCC/GitHub evidence refresh did not succeed.", true));
    }

    private async Task ConnectManagerChromeAsync(CancellationToken cancellationToken)
    {
        if (_run is null || _managerAgentId is null)
            throw new InvalidOperationException("NO_PROJECT_SELECTED · Select a project before opening a Manager browser.");

        var runtimes = await _runtimeRegistry.ListAsync(cancellationToken).ConfigureAwait(false);
        var existing = runtimes.FirstOrDefault(x =>
            StringComparer.Ordinal.Equals(x.ProjectRunId, _run.Id.ToString()) &&
            StringComparer.Ordinal.Equals(x.LogicalAgentId, _managerAgentId.Value.ToString()) &&
            !x.IsArchived && x.State is not BrowserSessionState.Killed);

        if (existing is not null)
        {
            var proof = await _ownership.ProveAsync(existing, cancellationToken).ConfigureAwait(false);
            if (proof.IsProven)
            {
                Ensure(await _sessions.BringToFrontAsync(existing.RuntimeId, cancellationToken).ConfigureAwait(false));
                return;
            }
        }

        var runtime = await _sessions.CreateAsync(new BrowserSessionRequest(
            _run.Id.ToString(),
            _managerAgentId.Value.ToString(),
            DefaultVisibility: BrowserVisibility.Visible), cancellationToken).ConfigureAwait(false);
        _recovery.Insert(0, new RecoveryEventSummary(DateTimeOffset.UtcNow, "BROWSER_CREATED", $"Created PCC-owned Manager browser runtime {runtime.RuntimeId}.", true));
    }

    private async Task OpenAttentionAsync(string id, CancellationToken cancellationToken)
    {
        var attention = FindAttention(id) ?? throw new InvalidOperationException("The attention item is no longer active.");
        if (attention.ExactLocation.StartsWith("runtime:", StringComparison.Ordinal))
        {
            var runtimeId = attention.ExactLocation["runtime:".Length..];
            Ensure(await _sessions.BringToFrontAsync(runtimeId, cancellationToken).ConfigureAwait(false));
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_reloadProjects)
        {
            _projects = await LoadProjectsAsync(cancellationToken).ConfigureAwait(false);
            _reloadProjects = false;
        }
        if (_run is not null && !string.IsNullOrWhiteSpace(_selectedProjectId))
            await RefreshRunAsync(cancellationToken).ConfigureAwait(false);

        Snapshot = await BuildSnapshotAsync(cancellationToken).ConfigureAwait(false);
        SnapshotChanged?.Invoke(this, Snapshot);
    }

    private async Task RefreshRunAsync(CancellationToken cancellationToken)
    {
        if (_run is null) return;
        _run = await _store.LoadProjectRunAsync(_run.Id, cancellationToken).ConfigureAwait(false) ?? _run;
    }
}
