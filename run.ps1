$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$sourcePath = Join-Path $PSScriptRoot 'InterparkSeatClicker.cs'
$frameworkPath = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
$wpfPath = Join-Path $frameworkPath 'WPF'
$references = @('System.dll', 'System.Core.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', (Join-Path $wpfPath 'UIAutomationClient.dll'), (Join-Path $wpfPath 'UIAutomationTypes.dll'), (Join-Path $wpfPath 'WindowsBase.dll'))
Add-Type -Path $sourcePath -ReferencedAssemblies $references
[InterparkNativeClicker.Program]::Run()
