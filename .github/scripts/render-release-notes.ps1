[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Repository,
    [Parameter(Mandatory = $true)][string]$ReleaseTag,
    [Parameter(Mandatory = $true)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
$directory = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
python "$PSScriptRoot/release_assets.py" notes --repository $Repository --release-tag $ReleaseTag --asset-name PidSimulator-win-x64.zip --output-dir $directory
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$generated = Join-Path $directory 'release-notes.md'
if ([IO.Path]::GetFullPath($generated) -ne [IO.Path]::GetFullPath($OutputPath)) { Move-Item -LiteralPath $generated -Destination $OutputPath -Force }
