[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$')]
    [string]$ReleaseTag,

    [string]$VirusTotalContent = 'VirusTotal：検査中。',

    [Parameter(Mandatory = $true)]
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'
$templatePath = Join-Path (Split-Path -Parent $PSScriptRoot) 'release-notes.md'
$template = Get-Content -LiteralPath $templatePath -Raw -Encoding UTF8
$downloadUrl = "https://github.com/$Repository/releases/download/$ReleaseTag/PidSimulator-$ReleaseTag-win-x64.zip"
$notes = $template.Replace('{{DOWNLOAD_URL}}', $downloadUrl).Replace('{{VIRUSTOTAL_CONTENT}}', $VirusTotalContent)
$destination = [System.IO.Path]::GetFullPath($OutputPath)
[System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($destination)) | Out-Null
[System.IO.File]::WriteAllText($destination, $notes, [System.Text.UTF8Encoding]::new($false))
