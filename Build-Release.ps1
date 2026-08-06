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
$secretPath = Join-Path $projectRoot "src\McModpackTool.App\Services\BuildSecrets.Local.cs"
$releaseFolderName = -join @([char]0x53D1, [char]0x5E03)
$exeName = "MC" + (-join @([char]0x6574, [char]0x5408, [char]0x5305, [char]0x5DE5, [char]0x5177)) + ".exe"
$outputPath = if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    Join-Path $projectRoot $releaseFolderName
}
else {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}

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

    dotnet publish $projectPath -c Release -r $Runtime --self-contained true -o $outputPath --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $coreSymbols = Join-Path $outputPath "McModpackTool.Core.pdb"
    if (Test-Path -LiteralPath $coreSymbols) {
        Remove-Item -LiteralPath $coreSymbols -Force
    }

    $exe = Join-Path $outputPath $exeName
    if (-not (Test-Path -LiteralPath $exe)) {
        throw "The expected executable was not created: $exe"
    }
    Write-Host "Release complete: $exe"
}
finally {
    if ($createdSecretModule -and (Test-Path -LiteralPath $secretPath)) {
        Remove-Item -LiteralPath $secretPath -Force
    }
}
