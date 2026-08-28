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

public sealed partial class TerminalPresentationGateway : IPccExecutivePresentationGateway, IAsyncDisposable
{
    public async Task ExecuteAsync(UiAction action, string? targetId = null, CancellationToken cancellationToken = default)
    {
        if (!CanExecute(action, targetId))
            throw new InvalidOperationException(DisabledReason(action, targetId) ?? $"{action} is unavailable.");

        switch (action)
        {
            case UiAction.Refresh:
                _reloadProjects = true;
                break;
            case UiAction.RetryHealth:
                break;
            case UiAction.ResolveProject:
                await ResolveProjectAsync(targetId!, select: false, cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.SelectProject:
                await ResolveProjectAsync(targetId!, select: true, cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.ConnectChrome:
                await ConnectManagerChromeAsync(cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.OpenSession:
                Ensure(await _sessions.OpenAsync(targetId!, cancellationToken).ConfigureAwait(false));
                break;
            case UiAction.BringSessionToFront:
                Ensure(await _sessions.BringToFrontAsync(targetId!, cancellationToken).ConfigureAwait(false));
                break;
            case UiAction.HideSession:
                Ensure(await _sessions.HideAsync(targetId!, cancellationToken).ConfigureAwait(false));
                break;
            case UiAction.RestartSession:
                Ensure(await _sessions.RestartAsync(targetId!, cancellationToken).ConfigureAwait(false));
                break;
            case UiAction.KillSession:
                Ensure(await _sessions.KillAsync(targetId!, cancellationToken).ConfigureAwait(false));
                break;
            case UiAction.KillAllPccSessions:
                await _sessions.KillAllPccSessionsAsync(cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.RunVerification:
                await RefreshBaselineAsync(cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.OpenAttentionLocation:
                await OpenAttentionAsync(targetId!, cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.SaveSettings:
                await SaveSettingsAsync(targetId, cancellationToken).ConfigureAwait(false);
                break;
            case UiAction.CheckForUpdates:
                _ = ReadUpdateState();
                break;
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }
}
