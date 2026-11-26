[CmdletBinding()]
param(
    [string]$Executable = (Join-Path $PSScriptRoot '..\release\XunXun.exe'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'examples')
)

$ErrorActionPreference = 'Stop'
$appPath = (Resolve-Path -LiteralPath $Executable).Path
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Add-Observer($Config, $EventName, $Handler, $Matcher) {
    $entry = @{ hooks = @($Handler) }
    if ($Matcher) { $entry['matcher'] = $Matcher }
    $Config['hooks'][$EventName] = @($entry)
}

$claude = @{ hooks = [ordered]@{} }
$claudeHook = @{ type = 'command'; command = $appPath; args = @('--hook', 'claude'); timeout = 3 }
foreach ($name in 'UserPromptSubmit', 'PermissionRequest', 'PostToolUse', 'Elicitation', 'ElicitationResult', 'Stop', 'SessionEnd') {
    Add-Observer $claude $name $claudeHook
}
Add-Observer $claude 'PreToolUse' $claudeHook '^AskUserQuestion$'

$codex = @{ hooks = [ordered]@{} }
$unixStylePath = $appPath.Replace('\', '/').Replace("'", "'\''")
$powershellPath = $appPath.Replace("'", "''")
$codexHook = @{
    type = 'command'
    command = "'$unixStylePath' --hook codex"
    commandWindows = "& '$powershellPath' --hook codex"
    timeout = 3
}
foreach ($name in 'UserPromptSubmit', 'PermissionRequest', 'PostToolUse', 'Stop', 'Interrupt', 'SessionEnd') {
    Add-Observer $codex $name $codexHook
}

$folder = (Resolve-Path -LiteralPath $OutputDirectory).Path
[IO.File]::WriteAllText((Join-Path $folder 'claude-settings.json'), ($claude | ConvertTo-Json -Depth 9), $utf8)
[IO.File]::WriteAllText((Join-Path $folder 'codex-hooks.json'), ($codex | ConvertTo-Json -Depth 9), $utf8)
Get-ChildItem -LiteralPath $folder -Filter '*.json' | Select-Object -ExpandProperty FullName
