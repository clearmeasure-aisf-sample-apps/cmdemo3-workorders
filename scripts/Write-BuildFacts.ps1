#Requires -Version 7.4

<#
.SYNOPSIS
  Writes build-facts.json: what GET /_build answers about the build an environment runs.

.DESCRIPTION
  The Release workflow calls this after it has zipped the published app, and the file lands in the root of that
  zip (the content root of the running app), where src/UI/Server/BuildFacts serves it.

  Facts and where they come from:
    version, commit, commitUrl, builtAt, buildUrl   the parameters (the Build run that produced the packages)
    code        tracked source files of this checkout (git ls-files), non-blank lines per language
    tests       trx files: artifacts test-results-linux (unit, integration) and test-results-acceptance
    coverage    Cobertura files of artifact code-coverage-linux (unit and integration runs, merged per line)
    complexity  the complexity attribute coverlet writes on every method of those Cobertura files
    crap        artifact crap-metrics-linux (scripts/crap: crap-by-file.json, crap-production-violations.json)
    analysis    qodana.sarif.json of artifact qodana-report: results that are new or unchanged against the baseline

  Every section is optional: an artifact that is missing, expired or unreadable makes its section null and the
  script goes on. Only a package that cannot be stamped fails it.

.PARAMETER OutputPath
  The file to write. Default: build-facts.json in the current directory.

.PARAMETER RepoRoot
  The checkout to count the code of. Default: the parent directory of this script's directory.

.PARAMETER Version
  The version of the build (MAJOR.MINOR.run_number). Default: $env:BUILD_BUILDNUMBER; null without one, and the app
  then answers the version of its assembly.

.PARAMETER Commit
  The full SHA the build was made from. Default: HEAD of RepoRoot.

.PARAMETER Repository
  owner/name on GitHub, for the links. Default: $env:GITHUB_REPOSITORY, then the origin remote of RepoRoot.

.PARAMETER RunId
  The ID of the Build workflow run, for buildUrl and for -DownloadArtifacts. Default: $env:GITHUB_RUN_ID.

.PARAMETER BuiltAt
  When the build finished (any format [datetimeoffset] parses). Default: now.

.PARAMETER ArtifactsPath
  A directory with one subdirectory per Build artifact, named as the artifact. Without it, and without
  -DownloadArtifacts, the sections that need artifacts are null.

.PARAMETER DownloadArtifacts
  Download the artifacts of run RunId into ArtifactsPath with the GitHub CLI (GH_TOKEN with actions: read).

.PARAMETER Package
  A zip to put the file in, as build-facts.json in its root (replacing one that is there).

.EXAMPLE
  pwsh -NoProfile -File scripts/Write-BuildFacts.ps1
  The facts of the working copy: code only, the rest null.

.EXAMPLE
  pwsh -NoProfile -File scripts/Write-BuildFacts.ps1 -Version 2.4.15 -RunId 123 -DownloadArtifacts -Package app.2.4.15.zip
  What the Release workflow runs.
#>
[CmdletBinding()]
param(
    [string]$OutputPath = 'build-facts.json',
    [string]$RepoRoot = '',
    [string]$Version = '',
    [string]$Commit = '',
    [string]$Repository = '',
    [string]$RunId = '',
    [string]$BuiltAt = '',
    [string]$ArtifactsPath = '',
    [switch]$DownloadArtifacts,
    [string]$Package = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$factsFileName = 'build-facts.json'

# Build artifacts the facts are read from (.github/workflows/build.yml uploads them).
$coverageArtifact = 'code-coverage-linux'
$crapArtifact = 'crap-metrics-linux'
$qodanaArtifact = 'qodana-report'

# The same tests run on several platforms: each kind is counted once, from the first of these that has it.
$testArtifacts = @(
    'test-results-linux', 'test-results-acceptance', 'test-results-sqlite', 'test-results-windows',
    'test-results-arm-sqlite', 'test-results-acceptance-arm'
)
$downloadedArtifacts = @($coverageArtifact, $crapArtifact, $qodanaArtifact, 'test-results-linux', 'test-results-acceptance')

# Languages counted as code, by file extension.
$languageByExtension = @{
    '.cs' = 'C#'; '.csx' = 'C#'
    '.razor' = 'Razor'; '.cshtml' = 'Razor'
    '.ts' = 'TypeScript'; '.tsx' = 'TypeScript'
    '.js' = 'JavaScript'; '.jsx' = 'JavaScript'; '.mjs' = 'JavaScript'; '.cjs' = 'JavaScript'
    '.css' = 'CSS'; '.scss' = 'CSS'; '.sass' = 'CSS'; '.less' = 'CSS'
    '.html' = 'HTML'; '.htm' = 'HTML'
    '.sql' = 'SQL'
    '.ps1' = 'PowerShell'; '.psm1' = 'PowerShell'; '.psd1' = 'PowerShell'
    '.sh' = 'Shell'; '.bash' = 'Shell'
    '.py' = 'Python'
    '.yml' = 'YAML'; '.yaml' = 'YAML'
    '.bicep' = 'Bicep'
    '.md' = 'Markdown'
}

# Not written by hand: build output, packages, vendored libraries, minified and generated files.
$generatedOrVendored = [regex]::new(
    '(^|/)(bin|obj|node_modules|generated)/|(^|/)wwwroot/lib/|\.min\.[^/]+$|\.designer\.cs$|\.g(\.i)?\.cs$|modelsnapshot\.cs$',
    [System.Text.RegularExpressions.RegexOptions]'IgnoreCase, CultureInvariant')

# One match per line that has anything but white space.
$nonBlankLine = [regex]::new('(?m)^[^\S\n]*\S', [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)

function Get-ArtifactDirectory {
    param([string]$Name)

    if (-not $ArtifactsPath) { return $null }
    $directory = Join-Path $ArtifactsPath $Name
    if (Test-Path -LiteralPath $directory -PathType Container) { return $directory }
    return $null
}

function Read-XmlFile {
    param([string]$Path)

    # No DTD is fetched or expanded, whatever the file declares.
    $settings = [System.Xml.XmlReaderSettings]::new()
    $settings.DtdProcessing = [System.Xml.DtdProcessing]::Ignore
    $settings.XmlResolver = $null
    $reader = [System.Xml.XmlReader]::Create($Path, $settings)
    try {
        $document = [System.Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
        return $document
    }
    finally {
        $reader.Dispose()
    }
}

function Get-Number {
    param([string]$Text)

    return [double]::Parse($Text, [System.Globalization.CultureInfo]::InvariantCulture)
}

function Save-BuildArtifacts {
    if (-not $RunId -or -not $Repository) {
        Write-Host 'SKIP download of the build artifacts: no run ID or no repository'
        return
    }

    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Write-Host 'SKIP download of the build artifacts: the GitHub CLI is not installed'
        return
    }

    foreach ($name in $downloadedArtifacts) {
        $target = Join-Path $ArtifactsPath $name

        # An artifact the run does not have (a job that did not run, retention that ran out) is no failure.
        $PSNativeCommandUseErrorActionPreference = $false
        $output = gh run download $RunId --repo $Repository --name $name --dir $target 2>&1
        $exitCode = $LASTEXITCODE
        $PSNativeCommandUseErrorActionPreference = $true
        if ($exitCode -eq 0) {
            Write-Host "PASS artifact $name downloaded"
        }
        else {
            Write-Host "SKIP artifact ${name}: $(($output | Out-String).Trim())"
        }
    }
}

function Get-CodeFacts {
    $tracked = git -C $RepoRoot -c core.quotepath=false ls-files
    $byLanguage = @{}
    foreach ($relativePath in $tracked) {
        if ($generatedOrVendored.IsMatch($relativePath)) { continue }
        $language = $languageByExtension[[System.IO.Path]::GetExtension($relativePath)]
        if (-not $language) { continue }
        $path = Join-Path $RepoRoot $relativePath
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { continue }

        if (-not $byLanguage.ContainsKey($language)) { $byLanguage[$language] = @{ lines = 0; files = 0 } }
        $byLanguage[$language].lines += $nonBlankLine.Matches([System.IO.File]::ReadAllText($path)).Count
        $byLanguage[$language].files += 1
    }

    if ($byLanguage.Count -eq 0) { return $null }

    # Largest first; the name decides between equals, so the order never depends on the machine.
    $languages = @(
        $byLanguage.GetEnumerator() |
            Sort-Object -Property @{ Expression = { $_.Value.lines }; Descending = $true }, @{ Expression = { $_.Key } } |
            ForEach-Object { [ordered]@{ name = $_.Key; lines = $_.Value.lines; files = $_.Value.files } }
    )
    return [ordered]@{
        linesOfCode = [int]($languages | ForEach-Object { $_.lines } | Measure-Object -Sum).Sum
        files       = [int]($languages | ForEach-Object { $_.files } | Measure-Object -Sum).Sum
        languages   = $languages
    }
}

function Get-TestKind {
    param([string]$Text)

    # The test assemblies are ClearMeasure.Bootcamp.UnitTests, .IntegrationTests and .AcceptanceTests.
    switch -Regex ($Text) {
        'acceptancetests' { return 'acceptance' }
        'integrationtests' { return 'integration' }
        'unittests' { return 'unit' }
    }
    return $null
}

function Read-TestResultFile {
    param([System.IO.FileInfo]$File)

    $document = Read-XmlFile -Path $File.FullName
    $counters = $document.SelectSingleNode("/*[local-name()='TestRun']/*[local-name()='ResultSummary']/*[local-name()='Counters']")
    if ($null -eq $counters) { return $null }

    # The assembly the tests ran from names the kind; the path of the file does when the run recorded no test.
    $testMethod = $document.SelectSingleNode("//*[local-name()='UnitTest']/*[local-name()='TestMethod']")
    $kind = if ($null -ne $testMethod) { Get-TestKind -Text ([System.IO.Path]::GetFileName($testMethod.GetAttribute('codeBase'))) }
    if (-not $kind) { $kind = Get-TestKind -Text $File.FullName }
    if (-not $kind) { return $null }

    # Tests that ran: skipped and ignored ones are in "total" but not in "executed".
    $count = $counters.GetAttribute('executed')
    if (-not $count) { $count = $counters.GetAttribute('total') }
    return @{ kind = $kind; count = [int]$count }
}

function Get-TestFacts {
    if (-not $ArtifactsPath -or -not (Test-Path -LiteralPath $ArtifactsPath -PathType Container)) { return $null }

    $known = @($testArtifacts | ForEach-Object { Get-ArtifactDirectory -Name $_ } | Where-Object { $_ })
    $others = @(
        Get-ChildItem -LiteralPath $ArtifactsPath -Directory |
            Sort-Object -Property Name |
            ForEach-Object { $_.FullName } |
            Where-Object { $_ -notin $known }
    )

    $counts = @{}
    foreach ($directory in $known + $others) {
        $inDirectory = @{}
        foreach ($file in Get-ChildItem -LiteralPath $directory -Recurse -File -Filter '*.trx' | Sort-Object -Property FullName) {
            $result = Read-TestResultFile -File $file
            if ($null -eq $result) { continue }
            $inDirectory[$result.kind] = [int]$inDirectory[$result.kind] + $result.count
        }

        foreach ($kind in $inDirectory.Keys) {
            if (-not $counts.ContainsKey($kind)) { $counts[$kind] = $inDirectory[$kind] }
        }
    }

    if ($counts.Count -eq 0) { return $null }
    return [ordered]@{
        unit        = $counts['unit']
        integration = $counts['integration']
        acceptance  = $counts['acceptance']
    }
}

function Read-CoberturaFiles {
    $directory = Get-ArtifactDirectory -Name $coverageArtifact
    if (-not $directory) { return $null }
    $files = @(Get-ChildItem -LiteralPath $directory -Recurse -File -Filter '*.cobertura.xml' | Sort-Object -Property FullName)
    if ($files.Count -eq 0) { return $null }

    # The unit and the integration run instrument the same assemblies. A line counts once, covered when either run
    # hit it; of a line's branches, as many count as the run that covered most of them.
    $lineHits = [System.Collections.Generic.Dictionary[string, bool]]::new()
    $branchesCovered = [System.Collections.Generic.Dictionary[string, int]]::new()
    $branchesTotal = [System.Collections.Generic.Dictionary[string, int]]::new()
    $complexity = [System.Collections.Generic.Dictionary[string, int]]::new()
    $conditions = [regex]::new('\((\d+)/(\d+)\)')

    foreach ($file in $files) {
        $document = Read-XmlFile -Path $file.FullName
        foreach ($class in $document.SelectNodes('//class')) {
            $classKey = "$($class.GetAttribute('filename').Replace('\', '/'))|$($class.GetAttribute('name'))"

            foreach ($line in $class.SelectNodes('lines/line')) {
                $key = "$classKey|$($line.GetAttribute('number'))"
                $hit = $line.GetAttribute('hits') -ne '0'
                $known = $false
                if (-not $lineHits.TryGetValue($key, [ref]$known) -or ($hit -and -not $known)) { $lineHits[$key] = $hit }

                $match = $conditions.Match($line.GetAttribute('condition-coverage'))
                if (-not $match.Success) { continue }
                $covered = [int]$match.Groups[1].Value
                $total = [int]$match.Groups[2].Value
                $knownCount = 0
                if (-not $branchesCovered.TryGetValue($key, [ref]$knownCount) -or $covered -gt $knownCount) { $branchesCovered[$key] = $covered }
                if (-not $branchesTotal.TryGetValue($key, [ref]$knownCount) -or $total -gt $knownCount) { $branchesTotal[$key] = $total }
            }

            foreach ($method in $class.SelectNodes('methods/method')) {
                $value = $method.GetAttribute('complexity')
                if (-not $value) { continue }
                $complexity["$classKey|$($method.GetAttribute('name'))|$($method.GetAttribute('signature'))"] = [int](Get-Number -Text $value)
            }
        }
    }

    return @{
        lines           = $lineHits.Count
        linesCovered    = @($lineHits.Values | Where-Object { $_ }).Count
        branches        = [int]($branchesTotal.Values | Measure-Object -Sum).Sum
        branchesCovered = [int]($branchesCovered.Values | Measure-Object -Sum).Sum
        complexity      = @($complexity.Values)
    }
}

function Get-CoverageFacts {
    param($Cobertura)

    if ($null -eq $Cobertura -or $Cobertura.lines -eq 0) { return $null }
    return [ordered]@{
        linePercent   = [Math]::Round(100.0 * $Cobertura.linesCovered / $Cobertura.lines, 1)
        branchPercent = if ($Cobertura.branches -gt 0) { [Math]::Round(100.0 * $Cobertura.branchesCovered / $Cobertura.branches, 1) } else { $null }
    }
}

function Get-ComplexityFacts {
    param($Cobertura)

    if ($null -eq $Cobertura -or $Cobertura.complexity.Count -eq 0) { return $null }
    $measured = $Cobertura.complexity | Measure-Object -Average -Maximum
    return [ordered]@{
        average = [Math]::Round([double]$measured.Average, 1)
        max     = [int]$measured.Maximum
        methods = [int]$measured.Count
    }
}

# The value of a property whatever the case of its name: the CRAP reports mix camelCase and PascalCase.
function Get-JsonValue {
    param($Map, [string]$Name)

    if ($null -eq $Map) { return $null }
    foreach ($key in $Map.Keys) {
        if ($key -ieq $Name) { return $Map[$key] }
    }
    return $null
}

function Read-JsonFile {
    param([string]$Directory, [string]$Name)

    $file = Get-ChildItem -LiteralPath $Directory -Recurse -File -Filter $Name |
        Sort-Object -Property @{ Expression = { $_.FullName.Length } }, FullName |
        Select-Object -First 1
    if ($null -eq $file) { return $null }
    return Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable -Depth 100
}

function Get-CrapFacts {
    $directory = Get-ArtifactDirectory -Name $crapArtifact
    if (-not $directory) { return $null }

    # rollup-file-scores.csx writes both: scores per file (production or not), and the production methods over
    # the gate's threshold (scripts/crap/crap-gate-threshold.json).
    $byFile = Read-JsonFile -Directory $directory -Name 'crap-by-file.json'
    $violations = Read-JsonFile -Directory $directory -Name 'crap-production-violations.json'
    if ($null -eq $byFile -and $null -eq $violations) { return $null }

    $max = $null
    $threshold = $null
    $overThreshold = $null
    if ($null -ne $byFile) {
        $production = @(Get-JsonValue -Map $byFile -Name 'files' | Where-Object { Get-JsonValue -Map $_ -Name 'isProduction' })
        if ($production.Count -gt 0) {
            $max = [Math]::Round([double]($production | ForEach-Object { Get-JsonValue -Map $_ -Name 'maxCrap' } | Measure-Object -Maximum).Maximum, 1)
            $overThreshold = [int]($production | ForEach-Object { Get-JsonValue -Map $_ -Name 'crappyMethodCount' } | Measure-Object -Sum).Sum
        }

        $threshold = Get-JsonValue -Map $byFile -Name 'threshold'
    }

    if ($null -ne $violations) {
        $gateThreshold = Get-JsonValue -Map $violations -Name 'threshold'
        $violationCount = Get-JsonValue -Map $violations -Name 'violationCount'
        if ($null -ne $gateThreshold) { $threshold = $gateThreshold }
        if ($null -ne $violationCount) { $overThreshold = [int]$violationCount }
    }

    return [ordered]@{
        max           = $max
        threshold     = $threshold
        overThreshold = $overThreshold
    }
}

function Get-AnalysisFacts {
    $directory = Get-ArtifactDirectory -Name $qodanaArtifact
    if (-not $directory) { return $null }
    $sarif = Read-JsonFile -Directory $directory -Name 'qodana.sarif.json'
    if ($null -eq $sarif) { return $null }

    # The problems the code has now: new ones and those the baseline already knew. "absent" ones are gone.
    $problems = 0
    foreach ($run in @(Get-JsonValue -Map $sarif -Name 'runs')) {
        foreach ($result in @(Get-JsonValue -Map $run -Name 'results')) {
            if ($null -eq $result) { continue }
            if ((Get-JsonValue -Map $result -Name 'baselineState') -in @($null, 'new', 'unchanged')) { $problems++ }
        }
    }

    return [ordered]@{ qodanaProblems = $problems }
}

# A section that cannot be read is null in the file and a warning in the log; the release goes on.
function Get-Section {
    param([string]$Name, [scriptblock]$Read)

    try {
        $value = & $Read
    }
    catch {
        Write-Host "::warning title=Build facts::The section '$Name' could not be read: $($_.Exception.Message)"
        Write-Host "SKIP ${Name}: $($_.Exception.Message)"
        return $null
    }

    if ($null -eq $value) {
        Write-Host "SKIP ${Name}: no input"
        return $null
    }

    Write-Host "PASS $Name"
    return $value
}

function Add-FactsToPackage {
    param([string]$FactsPath)

    if (-not (Test-Path -LiteralPath $Package -PathType Leaf)) { throw "Package not found: $Package" }
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::Open((Resolve-Path -LiteralPath $Package).Path, [System.IO.Compression.ZipArchiveMode]::Update)
    try {
        $existing = $archive.GetEntry($factsFileName)
        if ($null -ne $existing) { $existing.Delete() }
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive, $FactsPath, $factsFileName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    finally {
        $archive.Dispose()
    }
}

Write-Host '==> Build facts'

$RepoRoot = if ($RepoRoot) { (Resolve-Path -LiteralPath $RepoRoot).Path } else { (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path }
if (-not $Version) { $Version = $env:BUILD_BUILDNUMBER }
if (-not $Repository) { $Repository = $env:GITHUB_REPOSITORY }
if (-not $RunId) { $RunId = $env:GITHUB_RUN_ID }

if (-not $Commit) {
    $Commit = Get-Section -Name 'commit' -Read { (git -C $RepoRoot rev-parse HEAD).Trim() }
}

if (-not $Repository) {
    $Repository = Get-Section -Name 'repository' -Read {
        $origin = [regex]::Match((git -C $RepoRoot remote get-url origin), 'github\.com[:/]+([^/]+/[^/]+?)(\.git)?/?$')
        if ($origin.Success) { $origin.Groups[1].Value }
    }
}

$builtAtTime = if ($BuiltAt) {
    [datetimeoffset]::Parse($BuiltAt, [System.Globalization.CultureInfo]::InvariantCulture, [System.Globalization.DateTimeStyles]::AssumeUniversal)
}
else {
    [datetimeoffset]::UtcNow
}

if ($DownloadArtifacts) {
    if (-not $ArtifactsPath) {
        $temp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [System.IO.Path]::GetTempPath() }
        $ArtifactsPath = Join-Path $temp "build-facts-artifacts-$RunId"
    }

    New-Item -ItemType Directory -Path $ArtifactsPath -Force | Out-Null
    Write-Host "==> Artifacts of run $RunId"
    Save-BuildArtifacts
}

if ($ArtifactsPath) { $ArtifactsPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ArtifactsPath) }

Write-Host '==> Sections'
$cobertura = Get-Section -Name 'coverage reports' -Read { Read-CoberturaFiles }
$github = if ($Repository) { "https://github.com/$Repository" } else { $null }
$facts = [ordered]@{
    version    = if ($Version) { $Version } else { $null }
    commit     = if ($Commit) { $Commit } else { $null }
    commitUrl  = if ($github -and $Commit) { "$github/commit/$Commit" } else { $null }
    builtAt    = $builtAtTime.UtcDateTime.ToString('yyyy-MM-ddTHH:mm:ssZ', [System.Globalization.CultureInfo]::InvariantCulture)
    buildUrl   = if ($github -and $RunId) { "$github/actions/runs/$RunId" } else { $null }
    code       = Get-Section -Name 'code' -Read { Get-CodeFacts }
    tests      = Get-Section -Name 'tests' -Read { Get-TestFacts }
    coverage   = Get-Section -Name 'coverage' -Read { Get-CoverageFacts -Cobertura $cobertura }
    complexity = Get-Section -Name 'complexity' -Read { Get-ComplexityFacts -Cobertura $cobertura }
    crap       = Get-Section -Name 'crap' -Read { Get-CrapFacts }
    analysis   = Get-Section -Name 'analysis' -Read { Get-AnalysisFacts }
}

$json = $facts | ConvertTo-Json -Depth 8
$OutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
[System.IO.File]::WriteAllText($OutputPath, "$json`n", [System.Text.UTF8Encoding]::new($false))
Write-Host "==> $OutputPath"
Write-Host $json

if ($Package) {
    Add-FactsToPackage -FactsPath $OutputPath
    Write-Host "PASS $factsFileName is in $Package"
}

# A download that found no artifact left its exit code behind, and a workflow step ends with the last exit code.
exit 0
