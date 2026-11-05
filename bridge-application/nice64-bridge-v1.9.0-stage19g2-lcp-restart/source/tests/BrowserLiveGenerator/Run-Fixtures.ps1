param(
    [string]$GeneratorPath = (Join-Path $PSScriptRoot "..\..\modules\browser-live\scripts\Generate-NiceDocument.ps1")
)

$ErrorActionPreference = "Stop"
$fixtureRoot = Join-Path $PSScriptRoot "fixtures"
$scratch = Join-Path ([IO.Path]::GetTempPath()) ("nice64-generator-fixtures-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null

function Invoke-Fixture {
    param([string]$Name, [string]$ExpectedMode)
    $output = Join-Path $scratch $Name
    & $GeneratorPath -Url "https://fixture.example/$Name" -OutputDirectory $output -PicoId fixture -SessionId fixture -RequestId $Name -Mode AUTO -InputHtmlPath (Join-Path $fixtureRoot "$Name.html")
    if ($LASTEXITCODE -ne 0) { throw "Fixture '$Name' exited $LASTEXITCODE" }
    $document = Get-Content (Join-Path $output 'document.json') -Raw | ConvertFrom-Json
    if ($document.mode -ne $ExpectedMode) { throw "Fixture '$Name' expected $ExpectedMode, got $($document.mode)" }
    return $document
}

function Invoke-SearchFixture {
    param([string]$Name)
    $output = Join-Path $scratch $Name
    & $GeneratorPath -Url 'https://search.brave.com/search?q=beta%20n64' -OutputDirectory $output -PicoId fixture -SessionId fixture -RequestId $Name -Mode AUTO -InputHtmlPath (Join-Path $fixtureRoot "$Name.html") -SearchResultMode true
    if ($LASTEXITCODE -ne 0) { throw "Search fixture '$Name' exited $LASTEXITCODE" }
    return Get-Content (Join-Path $output 'document.json') -Raw | ConvertFrom-Json
}

try {
    $landing = Invoke-Fixture landing LANDING
    $landingTexts = @($landing.blocks | ForEach-Object { $_.text })
    if ('Games' -notin $landingTexts -or 'Hardware' -notin $landingTexts -or 'Login' -in $landingTexts) { throw 'Landing primary navigation selection failed.' }
    if ('Nintendo Switch 2' -notin $landingTexts -or 'Super Mario' -notin $landingTexts) { throw 'Landing card grouping failed.' }
    if (@($landing.blocks | Where-Object { $_.type -eq 'IMAGE_PLACEHOLDER' }).Count -ne 1) { throw 'Landing image budget failed.' }

    $article = Invoke-Fixture article ARTICLE
    if (@($article.blocks | Where-Object { $_.type -eq 'PARAGRAPH' }).Count -lt 2) { throw 'Article prose extraction regressed.' }

    $recipe = Invoke-Fixture recipe ARTICLE
    if ('Ingredients' -notin @($recipe.blocks | ForEach-Object { $_.text })) { throw 'Recipe heading extraction regressed.' }

    $empty = Invoke-Fixture empty-shell ARTICLE
    $emptyLinks = @($empty.blocks | Where-Object { $_.type -eq 'LINK' })
    if ($emptyLinks.Count -ne 0) { throw 'Empty static shell must not emit a self-link fallback.' }
    if ('This site did not provide static page content to the Browser.' -notin @($empty.blocks | ForEach-Object { $_.text })) {
        throw 'Empty static shell must explain why no page content was rendered.'
    }

    $search = Invoke-SearchFixture brave-search
    $searchLinks = @($search.blocks | Where-Object { $_.type -eq 'LINK' })
    if ($search.mode -ne 'SEARCH' -or $searchLinks.Count -lt 2) { throw 'Brave search result extraction failed.' }
    if (@($searchLinks | Where-Object { $_.target -match 'brave\.com' }).Count -ne 0) { throw 'Brave provider navigation leaked into search results.' }
    if ('https://example.org/beta-n64' -notin @($searchLinks | ForEach-Object { $_.target })) { throw 'Expected direct search target was not emitted.' }
    Write-Host 'PASS Browser Live generator fixtures'
}
finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
}
