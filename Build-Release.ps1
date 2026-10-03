[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$OutputDirectory = "",
    [switch]$RequireCurseForgeKey
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectPath = Join-Path $projectRoot "src\McModpackTool.App\McModpackTool.App.csproj"
$installerProjectPath = Join-Path $projectRoot "src\McModpackTool.Installer\McModpackTool.Installer.csproj"
$secretPath = Join-Path $projectRoot "src\McModpackTool.App\Services\BuildSecrets.Local.cs"
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot ".artifacts\beta6-1-portable"))
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot ".artifacts")) + [IO.Path]::DirectorySeparatorChar
if (-not $stagingRoot.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The staging directory must stay inside the project artifacts directory."
}
$releaseFolderName = -join @([char]0x53D1, [char]0x5E03)
$exeName = "MC" + (-join @([char]0x6574, [char]0x5408, [char]0x5305, [char]0x5DE5, [char]0x5177)) + ".exe"
$installerExeName = "MC" + (-join @([char]0x6574, [char]0x5408, [char]0x5305, [char]0x5DE5, [char]0x5177)) + (-join @([char]0x5B89, [char]0x88C5, [char]0x7A0B, [char]0x5E8F)) + ".exe"
$outputPath = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $projectRoot $releaseFolderName
}
else {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
$portableOutputPath = Join-Path $stagingRoot $Runtime

$apiKey = [Environment]::GetEnvironmentVariable("CURSEFORGE_API_KEY", "Process")
$createdSecretModule = $false

if ([string]::IsNullOrWhiteSpace($apiKey) -and $RequireCurseForgeKey) {
    throw "CURSEFORGE_API_KEY must be set in the current process for this release build."
}
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    Write-Warning "CURSEFORGE_API_KEY is not set. The EXE will use Modrinth fallback or a runtime environment key."
}
if (Test-Path -LiteralPath $secretPath) {
    throw "BuildSecrets.Local.cs already exists; refusing to overwrite it."
}

try {
    if (-not [string]::IsNullOrWhiteSpace($apiKey)) {
        $bytes = [Text.Encoding]::UTF8.GetBytes($apiKey.Trim())
        for ($index = 0; $index -lt $bytes.Length; $index++) {
            $bytes[$index] = $bytes[$index] -bxor ((0x5D + $index * 17) -band 0xFF)
        }
        $encoded = [Convert]::ToBase64String($bytes)
        $source = @"
namespace McModpackTool.App.Services;

internal static partial class BuildSecrets
{
    static partial void ResolveEmbedded(ref string value)
    {
        byte[] bytes = Convert.FromBase64String("$encoded");
        for (int index = 0; index < bytes.Length; index++)
            bytes[index] ^= (byte)((0x5D + index * 17) & 0xFF);
        value = System.Text.Encoding.UTF8.GetString(bytes);
    }
}
"@
        [IO.File]::WriteAllText($secretPath, $source, [Text.UTF8Encoding]::new($false))
        $createdSecretModule = $true
    }

    if (-not (Test-Path -LiteralPath $installerProjectPath)) {
        throw "The installer project was not found: $installerProjectPath"
    }

    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $portableOutputPath -Force | Out-Null
    New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

    dotnet publish $projectPath -c Release -r $Runtime --self-contained true -o $portableOutputPath --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $coreSymbols = Join-Path $portableOutputPath "McModpackTool.Core.pdb"
    if (Test-Path -LiteralPath $coreSymbols) {
        Remove-Item -LiteralPath $coreSymbols -Force
    }

    $portableExe = Join-Path $portableOutputPath $exeName
    if (-not (Test-Path -LiteralPath $portableExe)) {
        throw "The expected portable executable was not created: $portableExe"
    }

    $finalPortableExe = Join-Path $outputPath $exeName
    Copy-Item -LiteralPath $portableExe -Destination $finalPortableExe -Force

    $installerPublishArgs = @(
        $installerProjectPath,
        "-c", "Release",
        "-r", $Runtime,
        "--self-contained", "true",
        "-p:InstallerPayload=$portableExe",
        "-o", $outputPath,
        "--nologo"
    )
    & dotnet publish @installerPublishArgs
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish for the installer failed with exit code $LASTEXITCODE."
    }

    $installerExe = Join-Path $outputPath $installerExeName
    if (-not (Test-Path -LiteralPath $installerExe)) {
        throw "The expected installer executable was not created: $installerExe"
    }

    Write-Host "Release complete:"
    Write-Host "  Portable:  $finalPortableExe"
    Write-Host "  Installer: $installerExe"
}
finally {
    if ($createdSecretModule -and (Test-Path -LiteralPath $secretPath)) {
        Remove-Item -LiteralPath $secretPath -Force
    }
    if (Test-Path -LiteralPath $stagingRoot) {
        Remove-Item -LiteralPath $stagingRoot -Recurse -Force
    }
}
