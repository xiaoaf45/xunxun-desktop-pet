[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('claude', 'codex')]
    [string]$Provider,
    [string]$ClaudeSettings = (Join-Path $PSScriptRoot 'examples\claude-settings.json')
)

$ErrorActionPreference = 'Stop'
if (-not ('XunXunHook.WindowBinding' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace XunXunHook {
    public static class WindowBinding {
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    }
}
'@
}

$settings = if ($Provider -eq 'claude') { (Resolve-Path -LiteralPath $ClaudeSettings).Path }
$harness = Get-Command $Provider -ErrorAction Stop
$keys = 'XUNXUN_HWND', 'XUNXUN_PID', 'XUNXUN_STARTED'
$previous = @{}
foreach ($key in $keys) { $previous[$key] = [Environment]::GetEnvironmentVariable($key) }

try {
    $handle = [XunXunHook.WindowBinding]::GetForegroundWindow()
    [uint32]$ownerId = 0
    if ($handle -eq [IntPtr]::Zero -or -not [XunXunHook.WindowBinding]::GetWindowThreadProcessId($handle, [ref]$ownerId)) {
        throw 'Run this script from a foreground Windows terminal window.'
    }
    $owner = Get-Process -Id $ownerId
    $env:XUNXUN_HWND = $handle.ToInt64().ToString()
    $env:XUNXUN_PID = $ownerId.ToString()
    $env:XUNXUN_STARTED = $owner.StartTime.ToUniversalTime().Ticks.ToString()

    if ($Provider -eq 'claude') {
        & $harness --settings $settings
    } else {
        & $harness
    }
} finally {
    foreach ($key in $keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key]) }
}
