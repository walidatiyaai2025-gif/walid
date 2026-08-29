$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Get-Content -Raw -LiteralPath $Path
    if (-not $text.Contains($Old)) { throw "Expected source fragment not found in $Path" }
    $text = $text.Replace($Old, $New)
    Set-Content -LiteralPath $Path -Value $text -Encoding utf8NoBOM
}

$browser = 'src/PCCExecutive.Browser/ChatGptBrowserAdapter.cs'
$text = Get-Content -Raw -LiteralPath $browser
$text = $text.Replace('public const string CurrentAdapterVersion = "chatgpt-web-semantic-v3";', 'public const string CurrentAdapterVersion = "chatgpt-web-semantic-v4";')
$startMarker = '    private static async Task<IReadOnlyList<string>> AssistantTextsAsync(IPage page)'
$endMarker = '    private static async Task<IReadOnlyList<string>> UserMessageTextsAsync(IPage page)'
$start = $text.IndexOf($startMarker, [StringComparison]::Ordinal)
$end = $text.IndexOf($endMarker, [StringComparison]::Ordinal)
if ($start -lt 0 -or $end -le $start) { throw 'AssistantTextsAsync markers not found.' }
$replacement = @'
    private static async Task<IReadOnlyList<string>> AssistantTextsAsync(IPage page)
    {
        try
        {
            var texts = await page.EvaluateAsync<string[]>(
                """
                () => {
                  const read = (el) => ((el && (el.innerText || el.textContent)) || '').trim();
                  const out = [];
                  const seen = new Set();
                  const push = (value) => {
                    const text = (value || '').replace(/^\s*(ChatGPT|Assistant)\s+said\s*:?\s*/i, '').trim();
                    if (!text || seen.has(text)) return;
                    seen.add(text);
                    out.push(text);
                  };

                  for (const el of document.querySelectorAll("[data-message-author-role='assistant'], [data-turn='assistant']"))
                    push(read(el));
                  if (out.length) return out;

                  const turns = Array.from(document.querySelectorAll(
                    "article[data-testid*='conversation-turn'], [data-testid*='conversation-turn'], article, [role='article']"));
                  for (const turn of turns) {
                    const role = ((turn.getAttribute('data-turn') || turn.getAttribute('data-message-author-role') || '') + '').toLowerCase();
                    const labels = Array.from(turn.querySelectorAll('h1,h2,h3,h4,h5,h6,[aria-label]'))
                      .map(el => `${read(el)} ${el.getAttribute('aria-label') || ''}`)
                      .join(' ').toLowerCase();
                    const assistantLabel = role === 'assistant' || labels.includes('chatgpt said') || labels.includes('assistant said');
                    const userLabel = role === 'user' || labels.includes('you said') || labels.includes('user said');
                    const renderedAssistant = !!turn.querySelector('.markdown, [class*="markdown"], .prose, [class*="prose"], [data-message-content="assistant"]');
                    if (!userLabel && (assistantLabel || renderedAssistant)) push(read(turn));
                  }
                  if (out.length) return out;

                  const labels = Array.from(document.querySelectorAll('h1,h2,h3,h4,h5,h6,[aria-label]'));
                  for (const label of labels) {
                    const marker = `${read(label)} ${label.getAttribute('aria-label') || ''}`.toLowerCase();
                    if (!marker.includes('chatgpt said') && !marker.includes('assistant said')) continue;
                    let container = label.closest("article, [role='article'], [data-testid*='conversation-turn'], [data-turn]");
                    if (!container) container = label.parentElement?.parentElement || label.parentElement;
                    push(read(container));
                  }
                  if (out.length) return out;

                  for (const content of document.querySelectorAll('.markdown, [class*="markdown"], .prose, [class*="prose"]')) {
                    const turn = content.closest("article, [role='article'], [data-testid*='conversation-turn'], [data-turn]") || content.parentElement;
                    const marker = read(turn).toLowerCase();
                    if (marker.startsWith('you said') || marker.startsWith('user said')) continue;
                    push(read(turn));
                  }
                  return out;
                }
                """).ConfigureAwait(false);
            return texts ?? Array.Empty<string>();
        }
        catch (PlaywrightException)
        {
            return Array.Empty<string>();
        }
    }

'@
$text = $text.Substring(0, $start) + $replacement + $text.Substring($end)
Set-Content -LiteralPath $browser -Value $text -Encoding utf8NoBOM

$xaml = 'src/PCCExecutive.App/MainWindow.xaml'
Replace-Exact $xaml @'
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="Auto"/>
                                <ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>
'@ @'
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="Auto"/>
                                <ColumnDefinition Width="Auto"/>
                                <ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>
'@
Replace-Exact $xaml @'
                            <TextBlock x:Name="RuntimeActivityDetailText" Grid.Column="3" Text="Loading runtime activity..." Foreground="#D6E3F0" FontSize="10" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
                            <TextBlock x:Name="RuntimeActivityAgeText" Grid.Column="4" Text="Runtime event: now" Foreground="{StaticResource Brush.Muted}" FontSize="10" VerticalAlignment="Center" Margin="12,0,0,0"/>
                            <TextBlock x:Name="RuntimeAutoResumeText" Grid.Column="5" Text="Auto-resume: —" Foreground="{StaticResource Brush.Muted}" FontSize="10" VerticalAlignment="Center" Margin="12,0,0,0"/>
'@ @'
                            <TextBlock x:Name="RuntimeActivityDetailText" Grid.Column="3" Text="Loading runtime activity..." Foreground="#D6E3F0" FontSize="10" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
                            <TextBlock x:Name="RuntimeActivityElapsedText" Grid.Column="4" Text="Step: 0s" Foreground="#FDE68A" FontSize="10" FontWeight="SemiBold" VerticalAlignment="Center" Margin="12,0,0,0" ToolTip="Seconds spent in the current background operation."/>
                            <TextBlock x:Name="RuntimeActivityAgeText" Grid.Column="5" Text="Runtime event: now" Foreground="{StaticResource Brush.Muted}" FontSize="10" VerticalAlignment="Center" Margin="12,0,0,0"/>
                            <TextBlock x:Name="RuntimeAutoResumeText" Grid.Column="6" Text="Auto-resume: —" Foreground="{StaticResource Brush.Muted}" FontSize="10" VerticalAlignment="Center" Margin="12,0,0,0"/>
'@

$code = 'src/PCCExecutive.App/MainWindow.xaml.cs'
Replace-Exact $code @'
    private DateTimeOffset _lastRuntimeSnapshotAt = DateTimeOffset.UtcNow;
    private int _pulseFrame;
'@ @'
    private DateTimeOffset _lastRuntimeSnapshotAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _activityStateSince = DateTimeOffset.UtcNow;
    private string _activityKey = string.Empty;
    private int _pulseFrame;
'@
Replace-Exact $code @'
        RuntimeActivityDetailText.Text = detail;
        RuntimeActivityProgress.IsIndeterminate = moving;
        RuntimeActivityProgress.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;

        var age = DateTimeOffset.UtcNow - _lastRuntimeSnapshotAt;
'@ @'
        RuntimeActivityDetailText.Text = detail;
        RuntimeActivityProgress.IsIndeterminate = moving;
        RuntimeActivityProgress.Visibility = moving ? Visibility.Visible : Visibility.Collapsed;

        var activityKey = $"{state}|{stage}|{detail}";
        if (!string.Equals(_activityKey, activityKey, StringComparison.Ordinal))
        {
            _activityKey = activityKey;
            _activityStateSince = DateTimeOffset.UtcNow;
        }
        var elapsed = DateTimeOffset.UtcNow - _activityStateSince;
        RuntimeActivityElapsedText.Text = $"Step: {FormatElapsed(elapsed)}";

        var age = DateTimeOffset.UtcNow - _lastRuntimeSnapshotAt;
'@
Replace-Exact $code @'
    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalSeconds < 60) return $"{Math.Floor(age.TotalSeconds)}s";
        if (age.TotalMinutes < 60) return $"{Math.Floor(age.TotalMinutes)}m {age.Seconds}s";
        return $"{Math.Floor(age.TotalHours)}h {age.Minutes}m";
    }
'@ @'
    private static string FormatAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age.TotalSeconds < 60) return $"{Math.Floor(age.TotalSeconds)}s";
        if (age.TotalMinutes < 60) return $"{Math.Floor(age.TotalMinutes)}m {age.Seconds}s";
        return $"{Math.Floor(age.TotalHours)}h {age.Minutes}m";
    }

    private static string FormatElapsed(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
        var totalSeconds = (long)Math.Floor(elapsed.TotalSeconds);
        if (totalSeconds < 60) return $"{totalSeconds}s";
        return $"{totalSeconds / 60}m {totalSeconds % 60:D2}s";
    }
'@

Write-Host 'Assistant turn semantic detection and visible per-operation elapsed timer applied.'
