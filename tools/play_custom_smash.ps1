param([string]$DolphinExe)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$game = Join-Path $root 'Melee - Custom Smash.iso'
$work = Join-Path $root 'output\custom-smash'
$profile = Join-Path $work 'DolphinUser'
$selectionFile = Join-Path $work 'dolphin-path.txt'

try {
    if (-not (Test-Path -LiteralPath $game -PathType Leaf)) {
        throw 'The combined game has not been built at the project root. Follow the combined-game build instructions in .github/README.md first.'
    }

    # Explicit choices must be valid; do not silently launch a different emulator.
    if (-not $DolphinExe) {
        $DolphinExe = $env:DOLPHIN_EXE
    }
    if ($DolphinExe) {
        if (-not (Test-Path -LiteralPath $DolphinExe -PathType Leaf)) {
            throw "Dolphin was not found at the specified path: $DolphinExe"
        }
    } else {
        # A remembered location may be stale after moving the project to another PC.
        if (Test-Path -LiteralPath $selectionFile -PathType Leaf) {
            $remembered = (Get-Content -LiteralPath $selectionFile -Raw).Trim()
            if ($remembered -and (Test-Path -LiteralPath $remembered -PathType Leaf)) {
                $DolphinExe = $remembered
            }
        }
        if (-not $DolphinExe) {
            $command = Get-Command Dolphin.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($command) {
                $DolphinExe = $command.Source
            }
        }
        if (-not $DolphinExe) {
            foreach ($programFiles in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
                if (-not $programFiles) { continue }
                $candidate = Join-Path $programFiles 'Dolphin Emulator\Dolphin.exe'
                if (Test-Path -LiteralPath $candidate -PathType Leaf) {
                    $DolphinExe = $candidate
                    break
                }
            }
        }
        if (-not $DolphinExe) {
            Add-Type -AssemblyName System.Windows.Forms
            $picker = New-Object System.Windows.Forms.OpenFileDialog
            try {
                $picker.Title = 'Choose the Dolphin program to play Custom Smash'
                $picker.Filter = 'Dolphin (Dolphin.exe)|Dolphin.exe'
                $picker.CheckFileExists = $true
                $picker.RestoreDirectory = $true
                Write-Host 'Select Dolphin.exe in the file picker. This choice will be remembered on this computer.'
                if ($picker.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
                    return
                }
                $DolphinExe = $picker.FileName
            } finally {
                $picker.Dispose()
            }
        }
    }

    $DolphinExe = (Resolve-Path -LiteralPath $DolphinExe).Path
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    Set-Content -LiteralPath $selectionFile -Value $DolphinExe -Encoding UTF8
    # Start-Process joins arguments into a Windows command line: quote paths with spaces.
    $arguments = @('-u', ('"' + $profile + '"'), '-C', 'Dolphin.DSP.Volume=75', '-e', ('"' + $game + '"'))
    $process = Start-Process -FilePath $DolphinExe -ArgumentList $arguments -WorkingDirectory (Split-Path -Parent $DolphinExe) -PassThru
    Write-Host "Started Custom Smash in Dolphin (process $($process.Id))."
} catch {
    Write-Host "Cannot start Custom Smash: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
