# ============================================================
# Sync104ToBpmErp Deployment Packaging Script
# Usage: .\package.ps1 [-SelfContained] [-Runtime win-x64]
# ============================================================
param(
    [switch]$FrameworkDependent,
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$script:ProjectRoot = $PSScriptRoot

# ---------- Path Config ----------
$ProjectName    = "Sync104ToBpmErp"
$PublishDir     = Join-Path $script:ProjectRoot "publish"
$DeployDir      = Join-Path $script:ProjectRoot "deploy"
$PackageDate    = Get-Date -Format "yyyyMMdd"
$PackageName    = "${ProjectName}_deploy_${PackageDate}"
$PackagePath    = Join-Path $DeployDir $PackageName
$ZipPath        = "${PackagePath}.zip"
$CsprojPath     = Join-Path $script:ProjectRoot "${ProjectName}.csproj"

# ---------- Helper Functions ----------
function Write-Step([int]$step, [int]$total, [string]$desc) {
    Write-Host ("[{0}/{1}] {2}" -f $step, $total, $desc) -ForegroundColor Cyan
}

function Write-OK([string]$msg) {
    Write-Host "  OK  $msg" -ForegroundColor Green
}

function Write-Fail([string]$msg) {
    Write-Host "  FAIL $msg" -ForegroundColor Red
}

# ============================================================
# MAIN
# ============================================================
Write-Host "========================================"
Write-Host "  Sync104ToBpmErp Deployment Packager"
Write-Host "========================================"
Write-Host ""

if ($FrameworkDependent) {
    Write-Host "Mode: Framework-Dependent (requires .NET 8 Runtime)" -ForegroundColor Magenta
} else {
    Write-Host "Mode: Self-Contained (includes .NET Runtime)  Platform: $Runtime" -ForegroundColor Magenta
}
Write-Host ""

# --- Step 1: Check prerequisites ---
Write-Step 1 6 "Checking prerequisites..."

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Fail "dotnet CLI not found. Please install .NET 8 SDK"
    exit 1
}

if (-not (Test-Path $CsprojPath)) {
    Write-Fail "Project file not found: $CsprojPath"
    exit 1
}

$dotnetVersion = dotnet --version
Write-OK "dotnet version: $dotnetVersion"
Write-OK "Project file exists"

# --- Step 2: Clean old outputs ---
Write-Step 2 6 "Cleaning old publish/deploy directories..."

if (Test-Path $PublishDir) {
    Remove-Item -Recurse -Force $PublishDir
    Write-OK "Cleaned publish/"
}
if (Test-Path $DeployDir) {
    Remove-Item -Recurse -Force $DeployDir
    Write-OK "Cleaned deploy/"
}

# --- Step 3: Build and publish ---
Write-Step 3 6 "Publishing project (dotnet publish)..."

$publishArgs = @(
    "publish", $CsprojPath,
    "-c", $Configuration,
    "-o", $PublishDir
)

if ($FrameworkDependent) {
    $publishArgs += "--self-contained", "false"
} else {
    $publishArgs += "--self-contained", "true"
    $publishArgs += "-r", $Runtime
}

Write-Host "  dotnet $($publishArgs -join ' ')"

$publishResult = & dotnet $publishArgs 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Fail "Publish failed!"
    Write-Host $publishResult
    exit 1
}
Write-OK "Publish succeeded"

$exePath = Join-Path $PublishDir "${ProjectName}.exe"
if (-not (Test-Path $exePath)) {
    Write-Fail "Publish completed but ${ProjectName}.exe not found"
    exit 1
}
$exeSize = (Get-Item $exePath).Length
$exeSizeMB = [math]::Round($exeSize / 1MB, 2)
Write-OK "${ProjectName}.exe (${exeSizeMB} MB)"

# --- Step 4: Create deployment directory structure ---
Write-Step 4 6 "Creating deployment directory structure..."

New-Item -ItemType Directory -Force -Path $PackagePath           | Out-Null
New-Item -ItemType Directory -Force -Path "$PackagePath\Logs"    | Out-Null
New-Item -ItemType Directory -Force -Path "$PackagePath\SQL"     | Out-Null
Write-OK "Directory structure created"

# --- Step 5: Copy files ---
Write-Step 5 6 "Copying files..."

# 5-1: Copy entire publish output (includes runtimes, localization, etc.)
$totalFiles = (Get-ChildItem $PublishDir -Recurse -File).Count
Copy-Item -Recurse -Force "$PublishDir\*" $PackagePath
Write-OK "Copied publish/ to deploy package ($totalFiles files)"

# 5-2: Copy appsettings.json (template)
Copy-Item -Force (Join-Path $script:ProjectRoot "appsettings.json") "$PackagePath\appsettings.json"
Write-OK "Copied appsettings.json (template)"

# 5-3: Copy SQL scripts
$sqlFiles = Get-ChildItem (Join-Path $script:ProjectRoot "SQL") -Filter "*.sql"
foreach ($f in $sqlFiles) {
    Copy-Item -Force $f.FullName "$PackagePath\SQL\"
}
Write-OK "Copied SQL scripts ($($sqlFiles.Count) files)"

# 5-4: Copy documentation
Copy-Item -Force (Join-Path $script:ProjectRoot "README.md")    "$PackagePath\USAGE.md"
Copy-Item -Force (Join-Path $script:ProjectRoot "deploy\packaging-guide.md") "$PackagePath\PACKAGING_GUIDE.md" -ErrorAction SilentlyContinue
Write-OK "Copied documentation"

# --- Clean up dev-only files ---
@("obj", "bin", ".git", ".vs") | ForEach-Object {
    $path = Join-Path $PackagePath $_
    if (Test-Path $path) {
        Remove-Item -Recurse -Force $path
    }
}

# --- Step 6: Create ZIP ---
Write-Step 6 6 "Creating archive..."

if (Test-Path $ZipPath) {
    Remove-Item -Force $ZipPath
}

Compress-Archive -Path $PackagePath -DestinationPath $ZipPath -CompressionLevel Optimal -Force

if (-not (Test-Path $ZipPath)) {
    Write-Fail "Archive creation failed!"
    exit 1
}

$zipSize = (Get-Item $ZipPath).Length
$zipSizeMB = [math]::Round($zipSize / 1MB, 2)
$pkgFileCount = (Get-ChildItem $PackagePath -Recurse -File).Count

# ============================================================
# Done
# ============================================================
Write-Host ""
Write-Host "========================================"
Write-Host "  Deployment Package Complete!"
Write-Host "========================================"
Write-Host ""
Write-Host "  ZIP : $ZipPath" -ForegroundColor Green
Write-Host "  Dir : $PackagePath" -ForegroundColor Green
Write-Host "  Size: $zipSizeMB MB" -ForegroundColor Cyan
Write-Host "  Files: $pkgFileCount" -ForegroundColor Cyan
Write-Host "  Mode: $(if (-not $FrameworkDependent) { 'Self-Contained (no runtime needed)' } else { 'Framework-Dependent (needs .NET 8)' })" -ForegroundColor Cyan
Write-Host ""
Write-Host "Next steps:" -ForegroundColor Yellow
Write-Host "  1. Copy $ZipPath to client machine"
Write-Host "  2. Extract to e.g. C:\Sync104ToBpmErp\"
Write-Host "  3. Edit appsettings.json with client's connection info"
Write-Host "  4. Run as Admin CMD: Sync104ToBpmErp.exe -t"
Write-Host "  5. Test API: Sync104ToBpmErp.exe -a"
Write-Host ""
