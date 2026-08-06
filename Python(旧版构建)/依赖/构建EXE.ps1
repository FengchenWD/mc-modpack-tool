param(
    [switch]$RequireCurseForgeKey,
    [string]$PythonExecutable = "python"
)

$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$specPath = Join-Path $projectRoot "依赖\打包配置\MC整合包工具.spec"
$distPath = Join-Path $projectRoot "发布"
$workPath = Join-Path $projectRoot "缓存\exe构建"
$exePath = Join-Path $distPath "MC整合包工具.exe"
$secretModulePath = Join-Path $projectRoot "build_secrets.py"
$apiKey = [Environment]::GetEnvironmentVariable("CURSEFORGE_API_KEY", "Process")
$createdSecretModule = $false

Push-Location $projectRoot
try {
    if (-not [string]::IsNullOrWhiteSpace($apiKey)) {
        if (Test-Path -LiteralPath $secretModulePath) {
            throw "build_secrets.py 已存在。为避免覆盖本地文件，构建已停止。"
        }

        $serializedKey = ConvertTo-Json -Compress -InputObject $apiKey
        $moduleContent = "# Generated temporarily by 依赖/构建EXE.ps1. Do not commit.`r`nCURSEFORGE_API_KEY = $serializedKey`r`n"
        $createdSecretModule = $true
        [IO.File]::WriteAllText(
            $secretModulePath,
            $moduleContent,
            [Text.UTF8Encoding]::new($false)
        )
    }
    else {
        if ($RequireCurseForgeKey) {
            throw "发布构建要求 CURSEFORGE_API_KEY，但当前进程未提供。"
        }
        Write-Warning "未设置 CURSEFORGE_API_KEY；生成的 EXE 将不包含 CurseForge Key。"
    }

    & $PythonExecutable -m PyInstaller `
        --clean `
        --noconfirm `
        --distpath $distPath `
        --workpath $workPath `
        $specPath
    if ($LASTEXITCODE -ne 0) {
        throw "PyInstaller 构建失败，退出代码：$LASTEXITCODE"
    }

    if ($createdSecretModule) {
        $archiveContents = & $PythonExecutable -m PyInstaller.utils.cliutils.archive_viewer -r -b $exePath
        if ($LASTEXITCODE -ne 0) {
            throw "无法检查构建产物中的临时注入模块。"
        }
        $embeddedSecretModule = $archiveContents | Where-Object { $_.Trim() -eq "build_secrets" }
        if (-not $embeddedSecretModule) {
            throw "构建产物未包含 build_secrets，发布已停止。"
        }
    }
}
finally {
    if ($createdSecretModule -and (Test-Path -LiteralPath $secretModulePath)) {
        Remove-Item -LiteralPath $secretModulePath -Force
    }
    $apiKey = $null
    Pop-Location
}
