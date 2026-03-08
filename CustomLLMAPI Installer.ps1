#Requires -Version 5.1
<#
.SYNOPSIS
    MateEngine Remote LLM Mod Installer
.DESCRIPTION
    Guides you through setting up a Remote LLM API key and installing the
    CustomLLMAPI mod for MateEngine / MateEngineX.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ── Colour helpers ────────────────────────────────────────────────────────────
function Write-Header {
    param([string]$Text)
    Write-Host "`n╔══════════════════════════════════════════════════════════╗" -ForegroundColor Cyan
    Write-Host   "║  $($Text.PadRight(56))║" -ForegroundColor Cyan
    Write-Host   "╚══════════════════════════════════════════════════════════╝" -ForegroundColor Cyan
}
function Write-Step   { param([string]$T) Write-Host "`n► $T" -ForegroundColor Yellow }
function Write-OK     { param([string]$T) Write-Host "  ✔  $T" -ForegroundColor Green  }
function Write-Warn   { param([string]$T) Write-Host "  ⚠  $T" -ForegroundColor DarkYellow }
function Write-Err    { param([string]$T) Write-Host "  ✖  $T" -ForegroundColor Red    }
function Write-Info   { param([string]$T) Write-Host "  ℹ  $T" -ForegroundColor Gray   }
function Pause-Enter  {
    param([string]$Prompt = "Press ENTER to continue...")
    Write-Host "`n  $Prompt" -ForegroundColor DarkGray -NoNewline
    $null = Read-Host
}

# ── Banner ────────────────────────────────────────────────────────────────────
Clear-Host
Write-Host @"

  ███╗   ███╗ █████╗ ████████╗███████╗    ███████╗███╗   ██╗ ██████╗ ██╗███╗   ██╗███████╗
  ████╗ ████║██╔══██╗╚══██╔══╝██╔════╝    ██╔════╝████╗  ██║██╔════╝ ██║████╗  ██║██╔════╝
  ██╔████╔██║███████║   ██║   █████╗      █████╗  ██╔██╗ ██║██║  ███╗██║██╔██╗ ██║█████╗
  ██║╚██╔╝██║██╔══██║   ██║   ██╔══╝      ██╔══╝  ██║╚██╗██║██║   ██║██║██║╚██╗██║██╔══╝
  ██║ ╚═╝ ██║██║  ██║   ██║   ███████╗    ███████╗██║ ╚████║╚██████╔╝██║██║ ╚████║███████╗
  ╚═╝     ╚═╝╚═╝  ╚═╝   ╚═╝   ╚══════╝    ╚══════╝╚═╝  ╚═══╝ ╚═════╝ ╚═╝╚═╝  ╚═══╝╚══════╝

          Remote LLM Mod Installer  —  CustomLLMAPI  v0.0.2
"@ -ForegroundColor Magenta

Write-Host "  This wizard will:" -ForegroundColor White
Write-Host "    1. Locate your MateEngine installation" -ForegroundColor Gray
Write-Host "    2. Help you set up an LLM provider API key" -ForegroundColor Gray
Write-Host "    3. Test your API key is working" -ForegroundColor Gray
Write-Host "    4. Download & install the CustomLLMAPI mod automatically" -ForegroundColor Gray
Write-Host ""
Pause-Enter "Press ENTER to begin..."


# ══════════════════════════════════════════════════════════════════════════════
# STEP 1 — Find MateEngine installation
# ══════════════════════════════════════════════════════════════════════════════
Write-Header "STEP 1 — Locate MateEngine"

$gameExe    = $null
$gameRoot   = $null

# Helper: validate a candidate directory
function Test-GameDir {
    param([string]$Dir)
    if ([string]::IsNullOrWhiteSpace($Dir)) { return $false }
    $candidate = Join-Path $Dir "MateEngineX.exe"
    return (Test-Path $candidate)
}

# 1-a  Check if MateEngineX.exe is currently running → grab its path first
Write-Step "Checking for a running MateEngine process..."
$runningProc = Get-Process -Name "MateEngineX" -ErrorAction SilentlyContinue |
               Select-Object -First 1

if ($runningProc) {
    try {
        $runningPath = $runningProc.MainModule.FileName   # full path to .exe
        $gameRoot    = Split-Path $runningPath -Parent
        Write-OK "Found running MateEngine at: $gameRoot"
        Write-Warn "MateEngine must be CLOSED before we can install the mod."
        Write-Host ""
        Write-Host "  Please save anything you need in MateEngine, then close it." -ForegroundColor White
        Pause-Enter "Once MateEngine is closed, press ENTER to continue..."
    } catch {
        Write-Warn "Could not read process path (try running this script as Administrator)."
        $gameRoot = $null
    }
} else {
    Write-Info "MateEngine is not currently running."
}

# 1-b  Auto-detect via Steam registry (if we don't have it yet)
if (-not (Test-GameDir $gameRoot)) {
    Write-Step "Searching Steam library for MateEngine..."
    $steamRoots = @()

    # Primary Steam path from registry
    $steamReg = "HKCU:\Software\Valve\Steam"
    if (Test-Path $steamReg) {
        $steamPath = (Get-ItemProperty $steamReg -ErrorAction SilentlyContinue).SteamPath
        if ($steamPath) { $steamRoots += $steamPath.Replace("/","\" ) }
    }

    # Additional library folders from libraryfolders.vdf
    foreach ($root in $steamRoots) {
        $vdf = Join-Path $root "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            $content = Get-Content $vdf -Raw
            $extraPaths = [regex]::Matches($content, '"path"\s+"([^"]+)"') |
                          ForEach-Object { $_.Groups[1].Value.Replace("\\","\") }
            $steamRoots += $extraPaths
        }
    }

    foreach ($root in ($steamRoots | Select-Object -Unique)) {
        $candidate = Join-Path $root "steamapps\common\MateEngine"
        if (Test-GameDir $candidate) {
            $gameRoot = $candidate
            Write-OK "Auto-detected installation at: $gameRoot"
            break
        }
    }
}

# 1-c  Ask the user manually (open-source build or detection failed)
if (-not (Test-GameDir $gameRoot)) {
    Write-Warn "Could not auto-detect MateEngine. This can happen with:"
    Write-Info "  • The open-source / non-Steam version of MateEngine"
    Write-Info "  • Custom Steam library paths"
    Write-Host ""
    do {
        Write-Host "  Please enter the full path to your MateEngine folder." -ForegroundColor White
        Write-Host "  (The folder that contains MateEngineX.exe)" -ForegroundColor Gray
        Write-Host "  Example: C:\Games\MateEngine" -ForegroundColor DarkGray
        Write-Host ""
        $inputPath = Read-Host "  Path"
        $inputPath = $inputPath.Trim().Trim('"')
        if (-not (Test-GameDir $inputPath)) {
            Write-Err "MateEngineX.exe was not found in that folder. Please try again."
        }
    } while (-not (Test-GameDir $inputPath))
    $gameRoot = $inputPath
    Write-OK "Using: $gameRoot"
}

# 1-d  Final closed-check
Write-Step "Confirming MateEngine is closed..."
$stillRunning = Get-Process -Name "MateEngineX" -ErrorAction SilentlyContinue
while ($stillRunning) {
    Write-Err "MateEngineX.exe is still running! Please close it completely."
    Pause-Enter "Once closed, press ENTER to check again..."
    $stillRunning = Get-Process -Name "MateEngineX" -ErrorAction SilentlyContinue
}
Write-OK "MateEngine is closed. Good to go!"


# ══════════════════════════════════════════════════════════════════════════════
# STEP 2 — Choose LLM Provider
# ══════════════════════════════════════════════════════════════════════════════
Write-Header "STEP 2 — Choose Your LLM Provider"

Write-Host @"

  The mod lets your desktop pet chat using a powerful AI instead of the small
  local model that ships with MateEngine.

  IMPORTANT — API keys are DIFFERENT from a regular account:
  ─────────────────────────────────────────────────────────
  • An API key is a special credential used by apps/mods to call the AI.
  • Most providers do NOT include API access on their free tier.
  • You will need to add a small amount of credit (a few dollars goes a long way).

  If you don't have an API key yet, we recommend DeepSeek — it is the
  cheapest option and works great for chat. A few dollars of credit will
  last months of normal use.

"@ -ForegroundColor White

$providers = @(
    [PSCustomObject]@{ Id=1; Name="DeepSeek";           Label="DeepSeek  (Recommended — cheapest, great quality)";   Endpoint="https://api.deepseek.com/chat/completions";        Model="deepseek-chat";    ProviderNum=2; IsLocal=$false }
    [PSCustomObject]@{ Id=2; Name="Claude (Anthropic)"; Label="Claude    (Anthropic — high quality, higher cost)";    Endpoint="https://api.anthropic.com/v1/messages";            Model="claude-haiku-4-5-20251001"; ProviderNum=3; IsLocal=$false }
    [PSCustomObject]@{ Id=3; Name="OpenAI";             Label="OpenAI    (ChatGPT — well known, moderate cost)";      Endpoint="https://api.openai.com/v1/chat/completions";       Model="gpt-4o-mini";      ProviderNum=1; IsLocal=$false }
    [PSCustomObject]@{ Id=4; Name="OpenAI-Compatible";  Label="Custom OpenAI-compatible endpoint  (advanced)";        Endpoint="";                                                 Model="";                 ProviderNum=1; IsLocal=$false }
    [PSCustomObject]@{ Id=5; Name="Ollama (Local)";     Label="Ollama    (Local — free, runs on your own PC)";        Endpoint="http://localhost:11434/api/chat/completions";      Model="llama3";           ProviderNum=0; IsLocal=$true  }
)

foreach ($p in $providers) {
    Write-Host "  [$($p.Id)] $($p.Label)" -ForegroundColor Cyan
}
Write-Host ""

do {
    $choice = Read-Host "  Enter number (1-5)"
} while ($choice -notmatch '^[1-5]$')

$provider = $providers | Where-Object { $_.Id -eq [int]$choice }
Write-OK "Selected: $($provider.Name)"

# Get API key (skip for local Ollama)
$apiKey = ""
if (-not $provider.IsLocal) {

    # Show signup link
    $signupUrl = switch ($provider.Id) {
        1 { "https://platform.deepseek.com/api_keys" }
        2 { "https://console.anthropic.com/settings/keys" }
        3 { "https://platform.openai.com/api-keys" }
        4 { "" }
        5 { "" }
    }

    Write-Host ""
    if ($signupUrl) {
        Write-Info "Get your API key here: $signupUrl"
        Write-Info "(Opening in your browser...)"
        Start-Process $signupUrl
    }

    # Custom endpoint for OpenAI-compatible
    if ($provider.Id -eq 4) {
        Write-Host ""
        $customEndpoint = Read-Host "  Enter your API endpoint URL"
        $provider.Endpoint = $customEndpoint.Trim()
        $customModel = Read-Host "  Enter the model name"
        $provider.Model = $customModel.Trim()
    }

    Write-Host ""
    Write-Host "  Paste your API key below and press ENTER." -ForegroundColor White
    Write-Host "  (Characters may not be visible as you type — that's normal)" -ForegroundColor DarkGray
    Write-Host ""

    # Use SecureString so the key isn't echoed, then convert back for use
    $secureKey = Read-Host "  API Key" -AsSecureString
    $bstr      = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureKey)
    $apiKey    = [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
    [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)

    if ([string]::IsNullOrWhiteSpace($apiKey)) {
        Write-Err "No API key entered. Exiting."
        exit 1
    }
    Write-OK "API key received."
}


# ══════════════════════════════════════════════════════════════════════════════
# STEP 3 — Choose DLL variant
# ══════════════════════════════════════════════════════════════════════════════
Write-Header "STEP 3 — Choose Mod Variant"

Write-Host @"

  There are two versions of the CustomLLMAPI mod DLL:

  [1] Standard  (Recommended)
      Your pet uses the remote LLM for chat. You stay in full control.
      Source: github.com/maoxig/MateEngine-CustomLLMAPI

  [2] Autonomous  (Experimental — by lee-soft)
      The pet can override your settings, send messages on its own, and
      behave more independently. Fun, but less predictable!
      Source: github.com/lee-soft/MateEngine-CustomLLMAPI

"@ -ForegroundColor White

do {
    $dllChoice = Read-Host "  Enter 1 or 2"
} while ($dllChoice -notmatch '^[12]$')

if ($dllChoice -eq "1") {
    $dllUrl  = "https://github.com/maoxig/MateEngine-CustomLLMAPI/releases/download/v0.0.2/CustomLLMAPI.dll"
    Write-OK "Using Standard variant."
} else {
    $dllUrl  = "https://github.com/lee-soft/MateEngine-CustomLLMAPI/releases/download/v1.0.0/CustomLLMAPI.dll"
    Write-Warn "Using Autonomous (Experimental) variant."
}
$meUrl = "https://github.com/maoxig/MateEngine-CustomLLMAPI/releases/download/v0.0.2/CustomLLMAPI.me"


# ══════════════════════════════════════════════════════════════════════════════
# STEP 4 — Test the API key
# ══════════════════════════════════════════════════════════════════════════════
Write-Header "STEP 4 — Testing Your API Key"
Write-Step "Sending a quick test message to $($provider.Name)..."

$testPassed = $false

if ($provider.IsLocal) {
    # Just check Ollama is reachable
    try {
        $resp = Invoke-WebRequest -Uri "http://localhost:11434" -UseBasicParsing -TimeoutSec 5
        Write-OK "Ollama is running locally."
        $testPassed = $true
    } catch {
        Write-Err "Could not reach Ollama at localhost:11434."
        Write-Info "Make sure Ollama is installed and running: https://ollama.com"
    }
} else {
    # Build a minimal chat completion request
    $headers = @{ "Content-Type" = "application/json" }

    switch ($provider.Id) {
        2 {
            # Anthropic uses a different request format
            $headers["x-api-key"]         = $apiKey
            $headers["anthropic-version"]  = "2023-06-01"
            $body = @{
                model      = $provider.Model
                max_tokens = 20
                messages   = @(@{ role="user"; content="Reply with: OK" })
            } | ConvertTo-Json -Depth 5
        }
        default {
            $headers["Authorization"] = "Bearer $apiKey"
            $body = @{
                model    = $provider.Model
                messages = @(@{ role="user"; content="Reply with the single word: OK" })
                max_tokens = 20
            } | ConvertTo-Json -Depth 5
        }
    }

    try {
        $response = Invoke-RestMethod -Uri $provider.Endpoint `
                        -Method Post `
                        -Headers $headers `
                        -Body $body `
                        -TimeoutSec 30
        Write-OK "API test successful! Your key is working."
        $testPassed = $true
    } catch {
        $statusCode = $null
        if ($_.Exception.Response) {
            $statusCode = [int]$_.Exception.Response.StatusCode
        }
        Write-Err "API test failed (HTTP $statusCode)."
        switch ($statusCode) {
            401 { Write-Warn "Authentication error — double-check your API key." }
            402 { Write-Warn "Payment required — you may need to add credit to your account." }
            429 { Write-Warn "Rate limit hit — your key works but you may be out of credits." }
            default { Write-Warn "Error: $($_.Exception.Message)" }
        }
        Write-Host ""
        Write-Host "  Would you like to continue anyway? (your key may still work inside the mod)" -ForegroundColor White
        $cont = Read-Host "  Continue? (y/n)"
        if ($cont -notmatch '^[Yy]') {
            Write-Err "Installation cancelled."
            exit 1
        }
        Write-Warn "Continuing without a confirmed API test."
    }
}


# ══════════════════════════════════════════════════════════════════════════════
# STEP 5 — Write LLMProxySettings.json
# ══════════════════════════════════════════════════════════════════════════════
Write-Header "STEP 5 — Saving LLM Configuration"

$configDir  = [System.IO.Path]::Combine(
    $env:APPDATA, "..", "LocalLow", "Shinymoon", "MateEngineX"
)
$configDir  = [System.IO.Path]::GetFullPath($configDir)
$configFile = Join-Path $configDir "LLMProxySettings.json"

if (-not (Test-Path $configDir)) {
    New-Item -ItemType Directory -Path $configDir -Force | Out-Null
    Write-OK "Created config directory."
}

$config = [ordered]@{
    version          = "1.0"
    enableRemote     = (-not $provider.IsLocal)
    hidePanelOnStart = $true
    apiConfigs       = @(
        [ordered]@{
            name          = "Default"
            provider      = $provider.ProviderNum
            apiKey        = $apiKey
            apiEndpoint   = $provider.Endpoint
            model         = $provider.Model
            templateIndex = 0
            chatTemplate  = "chatml"
        }
    )
    activeConfigIndex = 0
    proxyPort         = 13333
}

$config | ConvertTo-Json -Depth 5 | Set-Content -Path $configFile -Encoding UTF8
Write-OK "Config saved to:"
Write-Info "  $configFile"


# ══════════════════════════════════════════════════════════════════════════════
# STEP 6 — Download mod files
# ══════════════════════════════════════════════════════════════════════════════
Write-Header "STEP 6 — Downloading Mod Files"

$tempDir = Join-Path $env:TEMP "MateEngineLLMInstall"
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

function Download-File {
    param([string]$Url, [string]$Dest, [string]$Label)
    Write-Step "Downloading $Label..."
    try {
        $wc = New-Object System.Net.WebClient
        $wc.DownloadFile($Url, $Dest)
        Write-OK "Downloaded: $(Split-Path $Dest -Leaf)"
    } catch {
        Write-Err "Failed to download $Label"
        Write-Err $_.Exception.Message
        exit 1
    }
}

$dllDest = Join-Path $tempDir "CustomLLMAPI.dll"
$meDest  = Join-Path $tempDir "CustomLLMAPI.me"

Download-File $dllUrl  $dllDest "CustomLLMAPI.dll"
Download-File $meUrl   $meDest  "CustomLLMAPI.me"


# ══════════════════════════════════════════════════════════════════════════════
# STEP 7 — Install DLL into Managed folder
# ══════════════════════════════════════════════════════════════════════════════
Write-Header "STEP 7 — Installing the DLL"

$managedDir = Join-Path $gameRoot "MateEngineX_Data\Managed"

if (-not (Test-Path $managedDir)) {
    Write-Err "Could not find the Managed folder at:"
    Write-Err "  $managedDir"
    Write-Warn "Please copy CustomLLMAPI.dll there manually from:"
    Write-Info "  $dllDest"
    Pause-Enter
} else {
    $destDll = Join-Path $managedDir "CustomLLMAPI.dll"
    Copy-Item -Path $dllDest -Destination $destDll -Force
    Write-OK "DLL installed to:"
    Write-Info "  $destDll"
}


# ══════════════════════════════════════════════════════════════════════════════
# STEP 8 — Patch ScriptingAssemblies.json
# ══════════════════════════════════════════════════════════════════════════════
Write-Header "STEP 8 — Registering DLL with MateEngine"

$jsonPath = Join-Path $gameRoot "MateEngineX_Data\ScriptingAssemblies.json"

if (-not (Test-Path $jsonPath)) {
    Write-Err "Could not find ScriptingAssemblies.json at:"
    Write-Err "  $jsonPath"
    Write-Warn "Please add `"CustomLLMAPI.dll`" to the names array and 16 to the types array manually."
    Pause-Enter
} else {
    Write-Step "Patching ScriptingAssemblies.json..."

    # Back up first
    $backup = $jsonPath + ".bak"
    Copy-Item -Path $jsonPath -Destination $backup -Force
    Write-OK "Backup saved: $backup"

    $json = Get-Content $jsonPath -Raw | ConvertFrom-Json

    # Check if already patched
    if ($json.names -contains "CustomLLMAPI.dll") {
        Write-OK "ScriptingAssemblies.json already contains CustomLLMAPI.dll — skipping patch."
    } else {
        # Add to names and types arrays
        $namesList  = [System.Collections.Generic.List[string]]$json.names
        $typesList  = [System.Collections.Generic.List[int]]$json.types

        $namesList.Add("CustomLLMAPI.dll")
        $typesList.Add(16)

        $json.names = $namesList.ToArray()
        $json.types = $typesList.ToArray()

        [System.IO.File]::WriteAllText($jsonPath, ($json | ConvertTo-Json -Depth 10), (New-Object System.Text.UTF8Encoding $false))
        Write-OK "ScriptingAssemblies.json patched successfully."
    }
}


# ══════════════════════════════════════════════════════════════════════════════
# STEP 9 — Install the Mod Settings UI
# ══════════════════════════════════════════════════════════════════════════════
Write-Header "STEP 9 — Installing Mod Settings UI"

$modsDir = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::Combine($env:APPDATA, "..", "LocalLow", "Shinymoon", "MateEngineX", "Mods")
)

if (-not (Test-Path $modsDir)) {
    New-Item -ItemType Directory -Path $modsDir -Force | Out-Null
    Write-OK "Created Mods folder."
}

Copy-Item -Path $meDest -Destination (Join-Path $modsDir "CustomLLMAPI.me") -Force
Write-OK "Mod UI installed to:"
Write-Info "  $modsDir\CustomLLMAPI.me"


# ══════════════════════════════════════════════════════════════════════════════
# DONE
# ══════════════════════════════════════════════════════════════════════════════
Write-Header "🎉  Installation Complete!"

Write-Host @"

  Everything is set up! Here's a summary of what was done:

    ✔  LLM provider : $($provider.Name)
    ✔  Config saved : $configFile
    ✔  DLL installed: $managedDir\CustomLLMAPI.dll
    ✔  JSON patched : $jsonPath
    ✔  .me UI file  : $modsDir\CustomLLMAPI.me"

  You can now open MateEngine. Your desktop pet will use $($provider.Name)
  for its chat responses instead of the built-in local model.

  If your pet doesn't respond differently at first, try restarting MateEngine
  or checking the mod settings panel (CustomLLMAPI) inside the app.

  Enjoy your smarter pet! ♡
  ─────────────────────────────────────────────────────────────
  Mod by: maoxig  |  Autonomous variant by: lee-soft
  Installer script — feel free to share!

"@ -ForegroundColor Green

Pause-Enter "Press ENTER to close this window."