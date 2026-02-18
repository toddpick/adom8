param(
    [string]$ConfigPath = "",
    [switch]$InitConfig,
    [switch]$SkipTerraform,
    [switch]$SkipFunctions,
    [switch]$SkipDashboard
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Message)
    Write-Host "`n=== $Message ===" -ForegroundColor Cyan
}

function Require-Command {
    param([string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' not found in PATH."
    }
}

function Get-ConfigValue {
    param(
        [object]$Object,
        [string]$Path,
        [object]$Default = $null,
        [switch]$Required
    )

    $current = $Object
    foreach ($segment in ($Path -split '\.')) {
        if ($null -eq $current) { break }
        $property = $current.PSObject.Properties[$segment]
        if ($null -eq $property) {
            $current = $null
        }
        else {
            $current = $property.Value
        }
    }

    if ($null -eq $current -or ([string]$current).Trim().Length -eq 0) {
        if ($Required) {
            throw "Missing required config value: $Path"
        }
        return $Default
    }

    return $current
}

function To-SettingValue {
    param([object]$Value)
    if ($Value -is [bool]) {
        return $Value.ToString().ToLowerInvariant()
    }
    return [string]$Value
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$defaultConfigPath = Join-Path $PSScriptRoot "bootstrap.config.json"
$exampleConfigPath = Join-Path $PSScriptRoot "bootstrap.config.example.json"

if ([string]::IsNullOrWhiteSpace($ConfigPath)) {
    $ConfigPath = $defaultConfigPath
}
elseif (-not [System.IO.Path]::IsPathRooted($ConfigPath)) {
    $ConfigPath = Join-Path $repoRoot $ConfigPath
}

if ($InitConfig) {
    if (-not (Test-Path $exampleConfigPath)) {
        throw "Example config not found: $exampleConfigPath"
    }

    if (Test-Path $ConfigPath) {
        throw "Config already exists: $ConfigPath"
    }

    Copy-Item $exampleConfigPath $ConfigPath
    Write-Host "Created config file: $ConfigPath" -ForegroundColor Green
    Write-Host "Fill in PATs/secrets, then run: .\scripts\bootstrap.ps1 -ConfigPath `"$ConfigPath`""
    exit 0
}

if (-not (Test-Path $ConfigPath)) {
    throw "Config file not found: $ConfigPath. Run with -InitConfig first."
}

Write-Step "Loading bootstrap config"
$config = Get-Content $ConfigPath -Raw | ConvertFrom-Json

Require-Command az
if (-not $SkipTerraform) { Require-Command terraform }
if (-not $SkipFunctions) { Require-Command func }
if (-not $SkipDashboard) { Require-Command npx }

Write-Step "Verifying Azure login"
try {
    $null = az account show | Out-String
}
catch {
    Write-Host "Azure login required. Opening browser login..." -ForegroundColor Yellow
    az login | Out-Null
}

$subscriptionId = Get-ConfigValue -Object $config -Path 'azure.subscriptionId' -Default ''
if (-not [string]::IsNullOrWhiteSpace([string]$subscriptionId)) {
    Write-Step "Setting Azure subscription"
    az account set --subscription $subscriptionId | Out-Null
}

$resourceGroupName = Get-ConfigValue -Object $config -Path 'infrastructure.resourceGroupName' -Required
$location = Get-ConfigValue -Object $config -Path 'azure.location' -Default (Get-ConfigValue -Object $config -Path 'infrastructure.location' -Default 'eastus')
$environment = Get-ConfigValue -Object $config -Path 'infrastructure.environment' -Default 'dev'
$functionAppName = Get-ConfigValue -Object $config -Path 'infrastructure.functionAppName' -Required
$storageAccountName = Get-ConfigValue -Object $config -Path 'infrastructure.storageAccountName' -Required
$staticWebAppName = Get-ConfigValue -Object $config -Path 'infrastructure.staticWebAppName' -Required
$alertEmail = Get-ConfigValue -Object $config -Path 'infrastructure.alertEmail' -Required

$infraPath = Join-Path $repoRoot "infrastructure"
$tfvarsPath = Join-Path $infraPath "terraform.tfvars"

$tfvarsContent = @"
resource_group_name  = "$resourceGroupName"
location             = "$location"
environment          = "$environment"
function_app_name    = "$functionAppName"
storage_account_name = "$storageAccountName"
static_web_app_name  = "$staticWebAppName"
alert_email          = "$alertEmail"
"@

Write-Step "Writing infrastructure/terraform.tfvars"
Set-Content -Path $tfvarsPath -Value $tfvarsContent -Encoding UTF8

$tfOutput = $null
if (-not $SkipTerraform) {
    Write-Step "Provisioning infrastructure with Terraform"
    Push-Location $infraPath
    try {
        terraform init
        terraform apply -auto-approve
        $tfOutput = terraform output -json | ConvertFrom-Json
    }
    finally {
        Pop-Location
    }
}
else {
    Write-Step "Reading existing Terraform outputs"
    Push-Location $infraPath
    try {
        $tfOutput = terraform output -json | ConvertFrom-Json
    }
    finally {
        Pop-Location
    }
}

if ($null -eq $tfOutput) {
    throw "Terraform output not available."
}

$functionAppUrl = [string]$tfOutput.function_app_url.value
$dashboardUrl = [string]$tfOutput.dashboard_url.value
$dashboardApiKey = [string](Get-ConfigValue -Object $config -Path 'deployment.dashboardDeploymentToken' -Default '')
if ([string]::IsNullOrWhiteSpace($dashboardApiKey)) {
    $dashboardApiKey = [string]$tfOutput.dashboard_api_key.value
}

Write-Step "Configuring Function App settings"
$appSettings = [ordered]@{
    "AI__Provider" = (Get-ConfigValue -Object $config -Path 'ai.provider' -Required)
    "AI__Model" = (Get-ConfigValue -Object $config -Path 'ai.model' -Required)
    "AI__ApiKey" = (Get-ConfigValue -Object $config -Path 'ai.apiKey' -Required)
    "AI__MaxTokens" = (Get-ConfigValue -Object $config -Path 'ai.maxTokens' -Default 4096)
    "AI__Temperature" = (Get-ConfigValue -Object $config -Path 'ai.temperature' -Default 0.3)
    "AzureDevOps__OrganizationUrl" = (Get-ConfigValue -Object $config -Path 'ado.organizationUrl' -Required)
    "AzureDevOps__Pat" = (Get-ConfigValue -Object $config -Path 'ado.pat' -Required)
    "AzureDevOps__Project" = (Get-ConfigValue -Object $config -Path 'ado.project' -Required)
    "Git__Provider" = (Get-ConfigValue -Object $config -Path 'git.provider' -Required)
    "Git__RepositoryUrl" = (Get-ConfigValue -Object $config -Path 'git.repositoryUrl' -Required)
    "Git__Username" = (Get-ConfigValue -Object $config -Path 'git.username' -Default 'x-token-auth')
    "Git__Token" = (Get-ConfigValue -Object $config -Path 'git.token' -Required)
    "Git__Email" = (Get-ConfigValue -Object $config -Path 'git.email' -Default 'ai-agent@your-org.com')
    "Git__Name" = (Get-ConfigValue -Object $config -Path 'git.name' -Default 'AI Agent Bot')
    "Deployment__PipelineName" = (Get-ConfigValue -Object $config -Path 'deployment.pipelineName' -Default 'Deploy-To-Production')
    "Deployment__DefaultAutonomyLevel" = (Get-ConfigValue -Object $config -Path 'deployment.defaultAutonomyLevel' -Default 3)
    "Deployment__DefaultMinimumReviewScore" = (Get-ConfigValue -Object $config -Path 'deployment.defaultMinimumReviewScore' -Default 85)
}

$aiEndpoint = [string](Get-ConfigValue -Object $config -Path 'ai.endpoint' -Default '')
if (-not [string]::IsNullOrWhiteSpace($aiEndpoint)) {
    $appSettings["AI__Endpoint"] = $aiEndpoint
}

$pipelineId = [string](Get-ConfigValue -Object $config -Path 'deployment.pipelineId' -Default '')
if (-not [string]::IsNullOrWhiteSpace($pipelineId)) {
    $appSettings["Deployment__PipelineId"] = $pipelineId
}

$gitProvider = ([string]$appSettings["Git__Provider"]).ToLowerInvariant()
if ($gitProvider -eq 'github') {
    $appSettings["GitHub__Owner"] = (Get-ConfigValue -Object $config -Path 'github.owner' -Required)
    $appSettings["GitHub__Repo"] = (Get-ConfigValue -Object $config -Path 'github.repo' -Required)
    $appSettings["GitHub__Token"] = (Get-ConfigValue -Object $config -Path 'github.token' -Required)
    $appSettings["GitHub__DeployWorkflow"] = (Get-ConfigValue -Object $config -Path 'github.deployWorkflow' -Default 'deploy.yml')
}

$copilotEnabled = [bool](Get-ConfigValue -Object $config -Path 'copilot.enabled' -Default $false)
if ($copilotEnabled) {
    $appSettings["Copilot__Enabled"] = $true
    $appSettings["Copilot__Mode"] = (Get-ConfigValue -Object $config -Path 'copilot.mode' -Default 'Auto')
    $appSettings["Copilot__ComplexityThreshold"] = (Get-ConfigValue -Object $config -Path 'copilot.complexityThreshold' -Default 8)
    $appSettings["Copilot__CreateIssue"] = [bool](Get-ConfigValue -Object $config -Path 'copilot.createIssue' -Default $true)
    $appSettings["Copilot__TimeoutMinutes"] = (Get-ConfigValue -Object $config -Path 'copilot.timeoutMinutes' -Default 30)
    $appSettings["Copilot__AutoCloseCopilotPr"] = [bool](Get-ConfigValue -Object $config -Path 'copilot.autoCloseCopilotPr' -Default $true)

    $copilotWebhookSecret = [string](Get-ConfigValue -Object $config -Path 'copilot.webhookSecret' -Default '')
    if (-not [string]::IsNullOrWhiteSpace($copilotWebhookSecret)) {
        $appSettings["Copilot__WebhookSecret"] = $copilotWebhookSecret
    }

    $copilotModel = [string](Get-ConfigValue -Object $config -Path 'copilot.model' -Default '')
    if (-not [string]::IsNullOrWhiteSpace($copilotModel)) {
        $appSettings["Copilot__Model"] = $copilotModel
    }
}

$settingArgs = @()
foreach ($entry in $appSettings.GetEnumerator()) {
    $settingArgs += ("{0}={1}" -f $entry.Key, (To-SettingValue -Value $entry.Value))
}

az functionapp config appsettings set --name $functionAppName --resource-group $resourceGroupName --settings $settingArgs | Out-Null

if (-not $SkipFunctions) {
    Write-Step "Deploying Azure Functions"
    $functionsPath = Join-Path $repoRoot "src\AIAgents.Functions"
    Push-Location $functionsPath
    try {
        func azure functionapp publish $functionAppName
    }
    finally {
        Pop-Location
    }
}

Write-Step "Updating dashboard API URL"
$dashboardIndexPath = Join-Path $repoRoot "dashboard\index.html"
$dashboardContent = Get-Content $dashboardIndexPath -Raw
$targetApiUrl = "$($functionAppUrl.TrimEnd('/'))/api/status"
$updatedDashboardContent = [regex]::Replace($dashboardContent, "const API_URL = 'https://[^']+/api/status';", "const API_URL = '$targetApiUrl';")
if ($updatedDashboardContent -ne $dashboardContent) {
    Set-Content -Path $dashboardIndexPath -Value $updatedDashboardContent -Encoding UTF8
}

if (-not $SkipDashboard) {
    if ([string]::IsNullOrWhiteSpace($dashboardApiKey)) {
        throw "Dashboard deployment token missing. Set deployment.dashboardDeploymentToken or ensure terraform output dashboard_api_key exists."
    }

    Write-Step "Deploying dashboard"
    Push-Location $repoRoot
    try {
        npx @azure/static-web-apps-cli deploy dashboard --deployment-token $dashboardApiKey --env production --verbose
    }
    finally {
        Pop-Location
    }
}

Write-Step "Bootstrap complete"
Write-Host "Function App: $functionAppName" -ForegroundColor Green
Write-Host "Function URL: $functionAppUrl" -ForegroundColor Green
Write-Host "Dashboard URL: $dashboardUrl" -ForegroundColor Green
Write-Host ""
Write-Host "Next: retrieve a function key for secured dashboard actions:" -ForegroundColor Yellow
Write-Host "az functionapp keys list --name $functionAppName --resource-group $resourceGroupName --query functionKeys.default -o tsv"
Write-Host "Then set it from dashboard header 🔓 button (or localStorage key: adom8-function-key)." -ForegroundColor Yellow
