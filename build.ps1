#requires -Version 5.1
<#
.SYNOPSIS
    Builds the explorer into a module folder.
.DESCRIPTION
    Delegates to the SDK's build pipeline and always asks for the folder result: the module, its
    metadata and the modules it carries, laid out ready to copy. This application is delivered that
    way and never as an installable file, so the output form is not an option here.

    Set SHARPPROSPERO_ROOT to the SDK folder, or pass -SdkRoot. The SDK sits beside this project in
    the same checkout, which is the default.
.PARAMETER TitleId
    The title this build carries. An installer treats a title already on the machine as present and
    declines to replace it, so a build meant to sit beside the last one needs a title of its own.
#>
param(
    [string]$SdkRoot = "",
    [string]$Configuration = "Release",
    [string]$OutputFolder = "",
    [string]$TitleId = ""
)

$ErrorActionPreference = "Stop"
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# Most specific first: what was passed, then the SDK beside this project, then the environment. The
# sibling beats the environment variable on purpose - a machine pointing that variable at another
# checkout would otherwise build this against an SDK it was never written for, silently.
$sibling = Join-Path (Split-Path -Parent $here) "SharpProspero"
if (-not $SdkRoot -and (Test-Path (Join-Path $sibling "build/build-app.ps1"))) { $SdkRoot = $sibling }
if (-not $SdkRoot) { $SdkRoot = $env:SHARPPROSPERO_ROOT }
if (-not $SdkRoot -or -not (Test-Path (Join-Path $SdkRoot "build/build-app.ps1"))) {
    throw "No SharpProspero SDK found. Expected it beside this project, or pass -SdkRoot, or set SHARPPROSPERO_ROOT."
}
$SdkRoot = [System.IO.Path]::GetFullPath($SdkRoot)
Write-Host "Building against the SDK at $SdkRoot" -ForegroundColor Cyan

if (-not $OutputFolder) { $OutputFolder = Join-Path $here "out" }

$arguments = @{
    ProjectPath  = Join-Path $here "ProsperoExplorer.csproj"
    Output       = "Folder"
    OutputFolder = $OutputFolder
    Configuration = $Configuration
    SdkRoot      = $SdkRoot
}
if ($TitleId) { $arguments.TitleId = $TitleId }

& (Join-Path $SdkRoot "build/build-app.ps1") @arguments

Write-Host ""
Write-Host "The module folder is $(Join-Path $OutputFolder 'module'). Copy the whole folder." -ForegroundColor Green
