param(
    [switch]$Click
)

$ErrorActionPreference = 'Continue'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class CursorTest {
    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
'@

$points = @(
    @(300, 300),
    @(600, 400),
    @(900, 500),
    @(1200, 600),
    @(600, 400)
)

foreach ($p in $points) {
    [CursorTest]::SetCursorPos([int]$p[0], [int]$p[1]) | Out-Null
    Start-Sleep -Milliseconds 1800
}

if ($Click) {
    [CursorTest]::SetCursorPos(600, 400) | Out-Null
    Start-Sleep -Milliseconds 300
    [CursorTest]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero) # left down
    Start-Sleep -Milliseconds 80
    [CursorTest]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero) # left up
}

Write-Output "cursor-test-done"
