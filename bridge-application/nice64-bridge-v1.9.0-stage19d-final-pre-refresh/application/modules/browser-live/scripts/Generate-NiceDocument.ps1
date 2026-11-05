param(
    [Parameter(Mandatory = $true)]
    [string]$Url,

    [Parameter(Mandatory = $true)]
    [string]$OutputDirectory,

    [Parameter(Mandatory = $true)]
    [string]$PicoId,

    [Parameter(Mandatory = $true)]
    [string]$SessionId,

    [Parameter(Mandatory = $true)]
    [string]$RequestId,

    [ValidateSet("AUTO", "INDEX", "ARTICLE")]
    [string]$Mode = "AUTO",

    [string]$InputHtmlPath = "",

    [string]$LocalReadableModelPath = "",

    [string]$EnableReadableMerge = "true",

    # Search providers are configured by URL template. Some return a generic
    # landing page to Invoke-WebRequest while their HTTP/1.1 response contains
    # the requested results, so allow the caller to select curl first.
    [string]$PreferCurlFetch = "false",

    # Enables a provider-specific result extraction profile when the configured
    # provider returns its result cards in server-delivered hydration data.
    [string]$SearchResultMode = "false",

    # Temporary local compatibility setting for FrogFind's certificate chain.
    # Logged whenever used; set false to retain curl certificate validation.
    [bool]$AllowInsecureReadableTls = $true,

    [bool]$KeepImages = $true,

    [int]$MaxImageCount = 1,

    [int]$MaxTextChars = 20000,

    [int]$MaxBlocks = 96,

    [int]$TimeoutSeconds = 7,

    [int]$TotalGenerationBudgetSeconds = 15
)

$ErrorActionPreference = "Stop"

$LandingNavLimit = 6
$LandingCardLimit = 5
$LandingLinkLimit = 18
$MergedReadableLinkLimit = 24
$ArticleLinkLimit = 8
$useCurlFetch = $PreferCurlFetch -match '^(?i:1|true|yes)$'
$useSearchResultMode = $SearchResultMode -match '^(?i:1|true|yes)$'

. (Join-Path $PSScriptRoot "NiceMergedPageBuilder.ps1")

function Write-GeneratorLog {
    param([string]$Message)
    $path = Join-Path $OutputDirectory "generator.log"
    "[$((Get-Date).ToString("s"))] $Message" | Add-Content -LiteralPath $path -Encoding UTF8
}

function Get-GenerationRemainingMilliseconds {
    $remaining = ($TotalGenerationBudgetSeconds * 1000) - [int]$generationWatch.ElapsedMilliseconds
    return [Math]::Max(0, $remaining)
}

function Write-GenerationTiming {
    $elapsed = [int]$generationWatch.ElapsedMilliseconds
    Write-GeneratorLog "GENERATION_ELAPSED_MS=$elapsed"
    Write-GeneratorLog "GENERATION_REMAINING_MS=$(Get-GenerationRemainingMilliseconds)"
}

function Write-GeneratorError {
    param(
        [string]$Title,
        [string]$Detail,
        [string]$Code = "CONNECTION_FAILED",
        [string]$HttpStatus = "",
        [string]$ExceptionType = "",
        [int]$ElapsedMs = 0
    )

    Write-GeneratorLog "ERROR_CODE=$Code"
    Write-GeneratorLog "ERROR_TITLE=$Title"
    Write-GeneratorLog "ERROR_DETAIL=$Detail"
    if (-not [string]::IsNullOrWhiteSpace($HttpStatus)) { Write-GeneratorLog "HTTP_STATUS=$HttpStatus" }
    if (-not [string]::IsNullOrWhiteSpace($ExceptionType)) { Write-GeneratorLog "EXCEPTION_TYPE=$ExceptionType" }
    if ($ElapsedMs -gt 0) { Write-GeneratorLog "FETCH_ELAPSED_MS=$ElapsedMs" }

    $errorDocument = [ordered]@{
        format = "NICE-GENERATOR-ERROR"
        version = 1
        title = $Title
        message = $Detail
        httpStatus = if ([string]::IsNullOrWhiteSpace($HttpStatus)) { $null } else { [int]$HttpStatus }
        category = $Code
        sourceUrl = $Url
        finalUrl = $Url
        exitCode = 30
        exceptionType = $ExceptionType
        elapsedMs = $ElapsedMs
    }
    $errorDocument | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutputDirectory "error.json") -Encoding UTF8
}

function Get-HttpErrorTitle {
    param([int]$StatusCode)
    switch ($StatusCode) {
        400 { return "BAD REQUEST" }
        401 { return "AUTHENTICATION REQUIRED" }
        403 { return "SITE REFUSED REQUEST" }
        404 { return "PAGE NOT FOUND" }
        408 { return "SITE REQUEST TIMED OUT" }
        429 { return "TOO MANY REQUESTS" }
        default {
            if ($StatusCode -ge 500 -and $StatusCode -le 599) { return "SITE SERVER ERROR" }
            return "CONNECTION FAILED"
        }
    }
}

function Get-FriendlyFetchError {
    param(
        [System.Management.Automation.ErrorRecord]$ErrorRecord,
        [int]$ElapsedMs
    )

    $exception = $ErrorRecord.Exception
    $exceptionType = $exception.GetType().FullName
    $message = $exception.Message
    $statusCode = $null
    if ($exception.Response -and $exception.Response.StatusCode) {
        $statusCode = [int]$exception.Response.StatusCode
    }

    if ($statusCode) {
        $title = Get-HttpErrorTitle $statusCode
        return [pscustomobject]@{
            Title = $title
            Detail = "The website returned HTTP $statusCode and the Bridge could not download the page."
            Code = "HTTP_$statusCode"
            HttpStatus = "$statusCode"
            ExceptionType = $exceptionType
        }
    }

    $lower = $message.ToLowerInvariant()
    if ($exception.Status -eq [System.Net.WebExceptionStatus]::Timeout -or $lower.Contains("timed out")) {
        return [pscustomobject]@{
            Title = "SITE REQUEST TIMED OUT"
            Detail = "The website did not respond before the Bridge fetch timeout."
            Code = "REMOTE_TIMEOUT"
            HttpStatus = ""
            ExceptionType = $exceptionType
        }
    }
    if ($exception.Status -eq [System.Net.WebExceptionStatus]::NameResolutionFailure -or $lower.Contains("remote name could not be resolved") -or $lower.Contains("name or service not known")) {
        return [pscustomobject]@{
            Title = "SITE NOT FOUND"
            Detail = "The Bridge could not resolve the website address."
            Code = "DNS_FAILURE"
            HttpStatus = ""
            ExceptionType = $exceptionType
        }
    }
    if ($exception.Status -eq [System.Net.WebExceptionStatus]::TrustFailure -or $exception.Status -eq [System.Net.WebExceptionStatus]::SecureChannelFailure -or $lower.Contains("ssl") -or $lower.Contains("tls") -or $lower.Contains("certificate")) {
        return [pscustomobject]@{
            Title = "SECURE CONNECTION FAILED"
            Detail = "The Bridge could not establish a secure connection to the website."
            Code = "TLS_FAILURE"
            HttpStatus = ""
            ExceptionType = $exceptionType
        }
    }

    return [pscustomobject]@{
        Title = "CONNECTION FAILED"
        Detail = "The Bridge could not connect to the website."
        Code = "CONNECTION_FAILED"
        HttpStatus = ""
        ExceptionType = $exceptionType
    }
}

function Normalize-Text {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return "" }
    $decoded = [System.Net.WebUtility]::HtmlDecode($Text)
    $decoded = [regex]::Replace($decoded, "\s+", " ").Trim()
    return $decoded
}

function Add-Block {
    param(
        [System.Collections.Generic.List[object]]$Blocks,
        [string]$Type,
        [string]$Text = "",
        [string]$Target = "",
        [int]$ImageWidth = 0,
        [int]$ImageHeight = 0
    )

    $clean = Normalize-Text $Text
    if (($Type -ne "HR") -and ($Type -ne "END") -and [string]::IsNullOrWhiteSpace($clean)) {
        return
    }

    if ($Blocks.Count -gt 0) {
        $last = $Blocks[$Blocks.Count - 1]
        if ($last.type -eq $Type -and $last.text -eq $clean -and $last.target -eq $Target) {
            return
        }
    }

    $block = [ordered]@{ type = $Type }
    if (-not [string]::IsNullOrWhiteSpace($clean)) { $block.text = $clean }
    if (-not [string]::IsNullOrWhiteSpace($Target)) { $block.target = $Target }
    if ($ImageWidth -gt 0) { $block.imageWidth = $ImageWidth }
    if ($ImageHeight -gt 0) { $block.imageHeight = $ImageHeight }
    $Blocks.Add($block) | Out-Null
}

function Get-RasterImageExtension {
    param([string]$Url)
    if ([string]::IsNullOrWhiteSpace($Url)) { return "" }
    try { return [IO.Path]::GetExtension(([Uri]$Url).AbsolutePath).TrimStart('.').ToLowerInvariant() }
    catch { return "" }
}

function Get-TitleTokens {
    param([string]$Text)
    $stop = @("with", "from", "that", "this", "image", "photo", "banner", "icon", "the", "and", "for", "milk")
    return @(Normalize-Text $Text -split "[^a-z0-9]+" | Where-Object { $_.Length -ge 3 -and $_ -notin $stop } | ForEach-Object { $_.ToLowerInvariant() })
}

function Get-ImageCandidateScore {
    param([object]$Candidate, [string[]]$TitleTokens, [int]$H1Position, [string]$Html)
    $localStart = [Math]::Max(0, $Candidate.Order - 1200)
    $context = if ($Candidate.Order -lt 0) { "" } else { $Html.Substring($localStart, $Candidate.Order - $localStart).ToLowerInvariant() }
    $terms = ("$($Candidate.Url) $($Candidate.Alt) $($Candidate.Title) $($Candidate.Class) $($Candidate.Id)").ToLowerInvariant()
    $score = 0; $positive = [System.Collections.Generic.List[string]]::new(); $negative = [System.Collections.Generic.List[string]]::new()
    $insideMain = $context -match '<(main|article)\b|role\s*=\s*[''\"]main'
    $contentRoot = $context -match "content|entry|post|article|story|recipe|featured|hero"
    $negativeRegion = $context -match "<(nav|header|footer|aside)\b|sidebar|modal|dialog|toolbar|menu|breadcrumb|rating|social|share|comment|advert|cookie|consent|popup|pagination|login|account"
    if ($insideMain) { $score += 80; $positive.Add("main/article") }
    if ($contentRoot) { $score += 60; $positive.Add("content-root") }
    if ($terms -match "open-graph|primary-image") { $score += 100; $positive.Add("open-graph-primary") }
    if ($terms -match "wp-post-image|hero|featured|lead|article-image|recipe-image|banner") { $score += 50; $positive.Add("content-class") }
    if ($H1Position -ge 0 -and $Candidate.Order -ge $H1Position -and ($Candidate.Order - $H1Position) -lt 16000) { $score += 40; $positive.Add("after-h1") }
    $overlap = @($TitleTokens | Where-Object { $terms -match [regex]::Escape($_) }).Count
    if ($overlap -gt 0) { $score += [Math]::Min(30, $overlap * 15); $positive.Add("title-overlap=$overlap") }
    if ($Candidate.Url -match "/(uploads|media|content)/") { $score += 10; $positive.Add("media-path") }
    if ($Candidate.Extension -eq "gif") { $score -= 80; $negative.Add("gif") }
    if ($negativeRegion) { $score -= 100; $negative.Add("ui-region") }
    if ($terms -match "icon|logo|spinner|loading|print|close|star|rating|avatar|badge") { $score -= 90; $negative.Add("ui-name") }
    if ($terms -match "background|texture|decorative") { $score -= 50; $negative.Add("decorative") }
    [PSCustomObject]@{ Candidate=$Candidate; Score=$score; InsideMain=$insideMain; Positive=($positive -join ","); Negative=($negative -join ",") }
}

function Remove-NoiseHtml {
    param([string]$Html)
    $clean = $Html
    $clean = [regex]::Replace($clean, "<!--.*?-->", "", "Singleline")
    foreach ($tag in @("script","style","noscript","iframe","form","canvas","video","audio","embed","object","svg","header","footer","nav")) {
        $clean = [regex]::Replace($clean, "<$tag\b[^>]*>.*?</$tag>", "", "IgnoreCase,Singleline")
    }
    return $clean
}

function Remove-StructuralNoiseHtml {
    param([string]$Html)
    $clean = $Html
    $clean = [regex]::Replace($clean, "<!--.*?-->", "", "Singleline")
    foreach ($tag in @("script","style","noscript","iframe","form","canvas","video","audio","embed","object","svg")) {
        $clean = [regex]::Replace($clean, "<$tag\b[^>]*>.*?</$tag>", "", "IgnoreCase,Singleline")
    }
    return $clean
}

function Get-Attribute {
    param([string]$Tag, [string]$Name)
    $match = [regex]::Match($Tag, "$Name\s*=\s*[""']([^""']+)[""']", "IgnoreCase")
    if ($match.Success) { return $match.Groups[1].Value }
    return ""
}

function Resolve-Link {
    param([string]$Href, [Uri]$BaseUri)
    if ([string]::IsNullOrWhiteSpace($Href)) { return "" }
    $href = [System.Net.WebUtility]::HtmlDecode($Href.Trim())
    if ($href.StartsWith("#")) { return "" }
    if ($href -match "^(javascript|mailto|data):") { return "" }
    try {
        $uri = [Uri]::new($BaseUri, $href)
        if ($uri.Scheme -ne "http" -and $uri.Scheme -ne "https") { return "" }
        return $uri.AbsoluteUri
    } catch {
        return ""
    }
}

function Is-LowValueText {
    param([string]$Text)
    $lower = $Text.ToLowerInvariant()
    foreach ($noise in @("privacy","terms","cookie","sign in","log in","subscribe","advertisement","newsletter","account","cart","skip to","share this","follow us")) {
        if ($lower.Contains($noise)) { return $true }
    }
    return $false
}

function Get-NormalizedUrlKey {
    param([string]$Url)
    if ([string]::IsNullOrWhiteSpace($Url)) { return "" }
    try {
        $uri = [Uri]$Url
        $builder = [UriBuilder]$uri
        $builder.Fragment = ""
        return $builder.Uri.AbsoluteUri.TrimEnd('/').ToLowerInvariant()
    } catch { return $Url.TrimEnd('/').ToLowerInvariant() }
}

function Get-PrimaryNavigationSnapshot {
    param([string]$Html, [Uri]$BaseUri)
    $candidates = [System.Collections.Generic.List[object]]::new()
    $navPattern = "<(nav|div|ul)\b(?<attrs>[^>]*)>(?<inner>.*?)</\1>"
    foreach ($match in [regex]::Matches($Html, $navPattern, "IgnoreCase,Singleline")) {
        $attrs = $match.Groups['attrs'].Value
        $inner = $match.Groups['inner'].Value
        $terms = $attrs.ToLowerInvariant()
        $score = 0
        if ($match.Groups[1].Value -ieq 'nav') { $score += 50 }
        if ($terms -match 'role\s*=\s*["'']navigation|aria-label\s*=\s*["''][^"'']*(main|primary|global|site)') { $score += 60 }
        if ($terms -match 'main-nav|primary-nav|global-nav|site-nav|menu-primary|navigation') { $score += 45 }
        if ($terms -match 'account|login|social|legal|privacy|cookie|consent|language|region|utility|search|cart|rating|footer') { $score -= 120 }
        $links = [regex]::Matches($inner, "<a\b(?<attrs>[^>]*)>(?<text>.*?)</a>", "IgnoreCase,Singleline")
        if ($links.Count -ge 3) { $score += [Math]::Min(24, $links.Count * 3) }
        if ($score -gt 0) { $candidates.Add([pscustomobject]@{ Score=$score; Links=$links; Terms=$terms }) | Out-Null }
    }
    if ($candidates.Count -eq 0) { return @() }
    $selected = $candidates | Sort-Object Score -Descending | Select-Object -First 1
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $result = [System.Collections.Generic.List[object]]::new()
    foreach ($link in $selected.Links) {
        $text = Normalize-Text ([regex]::Replace($link.Groups['text'].Value, '<[^>]+>', ' '))
        $url = Resolve-Link (Get-Attribute $link.Groups['attrs'].Value 'href') $BaseUri
        $linkTerms = "$text $url".ToLowerInvariant()
        if ($text.Length -lt 3 -or $text.Length -gt 50 -or (Is-LowValueText $text) -or $linkTerms -match 'account|login|sign-in|social|privacy|cookie|consent|language|region|utility|search|cart|rating' -or [string]::IsNullOrWhiteSpace($url)) { continue }
        $key = Get-NormalizedUrlKey $url
        if ($seen.Add($key)) { $result.Add([pscustomobject]@{ Text=$text; Url=$url }) | Out-Null }
        if ($result.Count -ge $LandingNavLimit) { break }
    }
    if ($result.Count -gt 0) { Write-GeneratorLog "PRIMARY_NAV_SELECTED count=$($result.Count) score=$($selected.Score)" }
    return @($result)
}

function Get-PageProfile {
    param([string]$Html, [string]$RequestedMode)
    if ($RequestedMode -eq 'ARTICLE') { return [pscustomobject]@{ Name='ARTICLE'; Reason='EXPLICIT_OVERRIDE' } }
    if ($RequestedMode -eq 'INDEX') { return [pscustomobject]@{ Name='INDEX'; Reason='EXPLICIT_INDEX_OVERRIDE' } }
    $paragraphs = [regex]::Matches($Html, '<p\b[^>]*>.*?</p>', 'IgnoreCase,Singleline')
    $paragraphChars = 0
    foreach ($p in $paragraphs) { $paragraphChars += (Normalize-Text ([regex]::Replace($p.Value, '<[^>]+>', ' '))).Length }
    $links = [regex]::Matches($Html, '<a\b[^>]*href\s*=', 'IgnoreCase').Count
    $headings = [regex]::Matches($Html, '<h[1-3]\b', 'IgnoreCase').Count
    $cardSignals = [regex]::Matches($Html, 'card|featured|hero|promo|product|game|story|news|tile', 'IgnoreCase').Count
    $articleRoot = [regex]::IsMatch($Html, '<article\b|class\s*=\s*["''][^"'']*(entry-content|post-content|article-body|recipe)', 'IgnoreCase')
    if (($articleRoot -and $paragraphChars -ge 500) -or ($paragraphChars -ge 1100 -and $links -le 28)) {
        return [pscustomobject]@{ Name='ARTICLE'; Reason="prose=$paragraphChars links=$links articleRoot=$articleRoot" }
    }
    if ($links -ge 14 -or ($headings -ge 4 -and $cardSignals -ge 3) -or ($paragraphChars -lt 700 -and $links -ge 8)) {
        return [pscustomobject]@{ Name='LANDING'; Reason="links=$links headings=$headings cardSignals=$cardSignals prose=$paragraphChars" }
    }
    return [pscustomobject]@{ Name='ARTICLE'; Reason="default prose=$paragraphChars links=$links" }
}

function Get-CardCandidates {
    param([string]$Html, [Uri]$BaseUri, [string]$PageTitle)
    $candidates = [System.Collections.Generic.List[object]]::new()
    $anchors = [regex]::Matches($Html, '<a\b(?<attrs>[^>]*)>(?<inner>.*?)</a>', 'IgnoreCase,Singleline')
    foreach ($anchor in $anchors) {
        $attrs = $anchor.Groups['attrs'].Value
        $inner = $anchor.Groups['inner'].Value
        if ($inner -notmatch '<img\b' -and $inner -notmatch '<h[1-4]\b') { continue }
        $url = Resolve-Link (Get-Attribute $attrs 'href') $BaseUri
        if ([string]::IsNullOrWhiteSpace($url)) { continue }
        $image = [regex]::Match($inner, '<img\b(?<attrs>[^>]*)/?>', 'IgnoreCase,Singleline')
        $heading = [regex]::Match($inner, '<h[1-4]\b[^>]*>(?<text>.*?)</h[1-4]>', 'IgnoreCase,Singleline')
        $title = if ($heading.Success) { Normalize-Text ([regex]::Replace($heading.Groups['text'].Value, '<[^>]+>', ' ')) } else { Normalize-Text (Get-Attribute $attrs 'aria-label') }
        $summary = Normalize-Text ([regex]::Replace($inner, '<(img|h[1-4])\b[^>]*>.*?</\1>|<img\b[^>]*/?>|<[^>]+>', ' '))
        if ([string]::IsNullOrWhiteSpace($title)) { $title = Normalize-Text (Get-Attribute $attrs 'title') }
        if ($title.Length -lt 3 -or $title.Length -gt 100 -or (Is-LowValueText $title)) { continue }
        $imageUrl = if ($image.Success) { Resolve-Link (Get-Attribute $image.Groups['attrs'].Value 'src') $BaseUri } else { '' }
        $terms = "$attrs $title $summary".ToLowerInvariant()
        $score = 30
        if (-not [string]::IsNullOrWhiteSpace($imageUrl)) { $score += 40 }
        if ($terms -match 'hero|featured|feature|promo|product|game|story|news|card|tile') { $score += 35 }
        if ($anchor.Index -lt 30000) { $score += 15 }
        if ($terms -match 'icon|logo|spinner|loading|close|star|rating|avatar|badge|social|account|login|cookie|advert') { $score -= 120 }
        if ($summary.Length -ge 30) { $score += 10 }
        $candidates.Add([pscustomobject]@{ Url=$url; ImageUrl=$imageUrl; Title=$title; Summary=$summary; Order=$anchor.Index; Score=$score; Signals=$terms }) | Out-Null
    }
    $containerPattern = '<(?<tag>article|li|section|div)\b(?<attrs>[^>]*)>(?<inner>.*?)</\k<tag>>'
    foreach ($container in [regex]::Matches($Html, $containerPattern, 'IgnoreCase,Singleline')) {
        $containerAttrs = $container.Groups['attrs'].Value
        $containerTerms = $containerAttrs.ToLowerInvariant()
        if ($containerTerms -notmatch 'card|featured|feature|hero|promo|product|game|story|news|tile') { continue }
        $inner = $container.Groups['inner'].Value
        $link = [regex]::Match($inner, '<a\b(?<attrs>[^>]*)>(?<text>.*?)</a>', 'IgnoreCase,Singleline')
        if (-not $link.Success) { continue }
        $url = Resolve-Link (Get-Attribute $link.Groups['attrs'].Value 'href') $BaseUri
        $image = [regex]::Match($inner, '<img\b(?<attrs>[^>]*)/?>', 'IgnoreCase,Singleline')
        $heading = [regex]::Match($inner, '<h[1-4]\b[^>]*>(?<text>.*?)</h[1-4]>', 'IgnoreCase,Singleline')
        $title = if ($heading.Success) { Normalize-Text ([regex]::Replace($heading.Groups['text'].Value, '<[^>]+>', ' ')) } else { Normalize-Text ([regex]::Replace($link.Groups['text'].Value, '<[^>]+>', ' ')) }
        if ([string]::IsNullOrWhiteSpace($url) -or $title.Length -lt 3 -or $title.Length -gt 100 -or (Is-LowValueText $title)) { continue }
        $summary = Normalize-Text ([regex]::Replace($inner, '<(img|h[1-4]|a)\b[^>]*>.*?</\1>|<img\b[^>]*/?>|<[^>]+>', ' '))
        $imageUrl = if ($image.Success) { Resolve-Link (Get-Attribute $image.Groups['attrs'].Value 'src') $BaseUri } else { '' }
        $score = 80
        if (-not [string]::IsNullOrWhiteSpace($imageUrl)) { $score += 35 }
        if ($summary.Length -ge 30) { $score += 10 }
        $candidates.Add([pscustomobject]@{ Url=$url; ImageUrl=$imageUrl; Title=$title; Summary=$summary; Order=$container.Index; Score=$score; Signals=$containerTerms }) | Out-Null
    }
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $ranked = [System.Collections.Generic.List[object]]::new()
    foreach ($candidate in ($candidates | Sort-Object @{Expression='Score';Descending=$true}, @{Expression='Order';Ascending=$true})) {
        if ($candidate.Score -lt 0) { Write-GeneratorLog "CARD_REJECTED reason=LOW_SCORE title=$($candidate.Title)"; continue }
        if (-not $seen.Add((Get-NormalizedUrlKey $candidate.Url))) { Write-GeneratorLog "CARD_REJECTED reason=DUPLICATE_URL title=$($candidate.Title)"; continue }
        $ranked.Add($candidate) | Out-Null
        Write-GeneratorLog "CARD_CANDIDATE score=$($candidate.Score) title=$($candidate.Title) url=$($candidate.Url)"
    }
    return @($ranked)
}

function Get-RecoveredParagraphs {
    param([string]$PlainText, [System.Collections.Generic.List[object]]$Blocks)
    $remaining = $PlainText
    foreach ($block in $Blocks) {
        if ($block.text) { $remaining = [regex]::Replace($remaining, [regex]::Escape((Normalize-Text $block.text)), " ", "IgnoreCase") }
    }
    $result = [System.Collections.Generic.List[string]]::new()
    $current = ""
    foreach ($sentence in [regex]::Split($remaining, "(?<=[.!?])\s+")) {
        $clean = Normalize-Text $sentence
        if ($clean.Length -lt 40 -or (Is-LowValueText $clean)) { continue }
        if (($current.Length + $clean.Length + 1) -gt 220 -and $current.Length -ge 80) { $result.Add($current); $current = "" }
        $current = Normalize-Text "$current $clean"
    }
    if ($current.Length -ge 40) { $result.Add($current) }
    return $result
}

function Convert-SearchHydrationText {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) { return "" }
    try { $Value = [regex]::Unescape($Value) } catch { }
    return Normalize-Text ([System.Net.WebUtility]::HtmlDecode($Value))
}

function Get-BraveSearchResultDocument {
    param(
        [string]$Html,
        [Uri]$FinalUri,
        [Uri]$SourceUri,
        [string]$PageTitle,
        [string]$RequestId,
        [string]$PicoId,
        [string]$SessionId,
        [int]$MaximumBlocks
    )

    if ($FinalUri.Host -notin @('search.brave.com', 'www.search.brave.com')) {
        Write-GeneratorLog "SEARCH_RESULTS_UNSUPPORTED host=$($FinalUri.Host) using=DIRECT_COMPATIBILITY"
        return $null
    }

    Write-GeneratorLog "SEARCH_RESULTS_PROFILE provider=BRAVE host=$($FinalUri.Host)"
    $pattern = '(?s)\{title:"(?<title>(?:\\.|[^"\\])*)",url:"(?<url>(?:\\.|[^"\\])*)"(?<body>.*?)description:"(?<description>(?:\\.|[^"\\])*)"'
    $matches = [regex]::Matches($Html, $pattern)
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $blocks = [System.Collections.Generic.List[object]]::new()
    $accepted = 0
    $title = if ([string]::IsNullOrWhiteSpace($PageTitle)) { 'Brave Search' } else { $PageTitle }
    Add-Block $blocks 'TITLE' $title

    foreach ($match in $matches) {
        if ($accepted -ge 10 -or $blocks.Count -ge ($MaximumBlocks - 2)) { break }
        $target = Convert-SearchHydrationText $match.Groups['url'].Value
        $label = Convert-SearchHydrationText $match.Groups['title'].Value
        $description = Convert-SearchHydrationText $match.Groups['description'].Value
        $uri = $null
        if ($label.Length -lt 4 -or -not [Uri]::TryCreate($target, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notin @('http', 'https')) {
            continue
        }
        if ($uri.Host.EndsWith('.brave.com') -or $uri.Host -eq 'brave.com' -or -not $seen.Add($uri.AbsoluteUri)) {
            continue
        }
        Add-Block $blocks 'LINK' $label $uri.AbsoluteUri
        if ($description.Length -ge 28) { Add-Block $blocks 'PARAGRAPH' $description }
        $accepted++
        Write-GeneratorLog "SEARCH_RESULT_ACCEPTED rank=$accepted host=$($uri.Host) title=$label"
    }

    if ($accepted -lt 2) {
        Write-GeneratorLog "SEARCH_RESULTS_UNAVAILABLE reason=INSUFFICIENT_RESULTS matches=$($matches.Count) accepted=$accepted"
        return $null
    }

    Add-Block $blocks 'END'
    Write-GeneratorLog "SEARCH_RESULTS_SELECTED count=$accepted"
    return [ordered]@{
        format = 'NICE-DOCUMENT'
        version = 1
        requestId = $RequestId
        picoId = $PicoId
        sessionId = $SessionId
        sourceUrl = $SourceUri.AbsoluteUri
        finalUrl = $FinalUri.AbsoluteUri
        pageTitle = $title
        mode = 'SEARCH'
        truncated = $false
        blocks = $blocks
        imageFallbacks = @()
    }
}

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
} catch {
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $OutputDirectory "images") | Out-Null
$generationWatch = [System.Diagnostics.Stopwatch]::StartNew()
Write-GeneratorLog "GENERATION_BUDGET_SECONDS=$TotalGenerationBudgetSeconds"

try {
    $sourceUri = [Uri]::new($Url)
    if ($sourceUri.Scheme -ne "http" -and $sourceUri.Scheme -ne "https") {
        Write-GeneratorLog "UNSUPPORTED URL SCHEME: $Url"
        exit 20
    }

    Write-GeneratorLog "Fetching $Url mode=$Mode"
    $fetchWatch = $generationWatch
    if (-not [string]::IsNullOrWhiteSpace($InputHtmlPath)) {
        if (-not (Test-Path -LiteralPath $InputHtmlPath -PathType Leaf)) { throw "Fixture HTML not found: $InputHtmlPath" }
        $html = Get-Content -LiteralPath $InputHtmlPath -Raw -Encoding UTF8
        $finalUrl = $sourceUri.AbsoluteUri
        Write-GeneratorLog "Fixture input loaded path=$InputHtmlPath"
    } else {
        $headers = @{ "User-Agent" = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36"; "Accept" = "text/html,application/xhtml+xml" }
        $html = ""
        $finalUrl = $sourceUri.AbsoluteUri
        if ($useCurlFetch) {
            Write-GeneratorLog "ORIGINAL_FETCH_PROFILE=CURL_FIRST"
        } else {
            $iwrTimeoutSeconds = [Math]::Max(1, [Math]::Min($TimeoutSeconds, [Math]::Floor((Get-GenerationRemainingMilliseconds) / 1000)))
            Write-GeneratorLog "ORIGINAL_IWR_STARTED timeoutSeconds=$iwrTimeoutSeconds"
            try {
                $response = Invoke-WebRequest -Uri $sourceUri.AbsoluteUri -Headers $headers -TimeoutSec $iwrTimeoutSeconds -MaximumRedirection 5 -UseBasicParsing
                $finalUrl = if ($response.BaseResponse.ResponseUri) { $response.BaseResponse.ResponseUri.AbsoluteUri } else { $sourceUri.AbsoluteUri }
                $html = [string]$response.Content
                Write-GeneratorLog "ORIGINAL_IWR_COMPLETED bytes=$($html.Length)"
            } catch {
                $reason = if ($_.Exception.Message -match "timed out") { "TIMEOUT" } else { "FAILED" }
                Write-GeneratorLog "ORIGINAL_IWR_$reason detail=$($_.Exception.Message)"
            }
        }
        if ($html.Length -lt 80) {
            # Some CDN responses are empty through Invoke-WebRequest but valid via
            # curl's HTTP/1.1 client profile. Retry once only while the job still
            # has enough budget to make a meaningful request.
            $curl = Join-Path $env:SystemRoot "System32\curl.exe"
            $retryPath = Join-Path $OutputDirectory "original-retry.html"
            $remainingMs = Get-GenerationRemainingMilliseconds
            if (-not (Test-Path -LiteralPath $curl)) {
                Write-GeneratorLog "ORIGINAL_CURL_SKIPPED reason=CURL_MISSING"
            } elseif ($remainingMs -lt 1500) {
                Write-GeneratorLog "ORIGINAL_CURL_SKIPPED reason=INSUFFICIENT_BUDGET"
            } else {
                $retryTimeoutSeconds = [Math]::Max(1, [Math]::Min(7, [Math]::Floor($remainingMs / 1000)))
                # Keep the curl connection phase inside the existing total fetch
                # budget.  Some search providers take more than three seconds
                # to establish a connection even when the page is otherwise fast.
                $connectTimeoutSeconds = $retryTimeoutSeconds
                Remove-Item -LiteralPath $retryPath -Force -ErrorAction SilentlyContinue
                Write-GeneratorLog "ORIGINAL_CURL_STARTED connectTimeoutSeconds=$connectTimeoutSeconds timeoutSeconds=$retryTimeoutSeconds"
                try {
                    $curlArgs = @("--location", "--silent", "--show-error", "--http1.1", "--max-redirs", "10", "--connect-timeout", [string]$connectTimeoutSeconds, "--max-time", [string]$retryTimeoutSeconds, "--user-agent", $headers["User-Agent"], "--header", "Accept: $($headers["Accept"])", "--output", $retryPath, "--write-out", "%{http_code}", $sourceUri.AbsoluteUri)
                    $curlStatus = (& $curl @curlArgs).Trim()
                    $curlExit = $LASTEXITCODE
                    Write-GeneratorLog "ORIGINAL_CURL_STATUS exit=$curlExit http=$curlStatus"
                    if ($curlExit -eq 0 -and $curlStatus -match '^2\d\d$' -and (Test-Path -LiteralPath $retryPath)) {
                        $retryHtml = [IO.File]::ReadAllText($retryPath, [Text.Encoding]::UTF8)
                        if ($retryHtml.Length -ge 80) { $html = $retryHtml; Write-GeneratorLog "ORIGINAL_CURL_COMPLETED bytes=$($html.Length)" }
                        else { Write-GeneratorLog "ORIGINAL_CURL_TINY bytes=$($retryHtml.Length)" }
                    }
                } finally {
                    Write-GenerationTiming
                }
            }
        }
        if ($html.Length -lt 80) { throw "ORIGINAL_FETCH_UNAVAILABLE" }
    }
    Write-GeneratorLog "Fetch completed elapsedMs=$($fetchWatch.ElapsedMilliseconds)"
    Write-GenerationTiming
    $baseUri = [Uri]::new($finalUrl)
    $html = [string]$html
    if ($html.Length -gt 500000) {
        $html = $html.Substring(0, 500000)
    }

    Set-Content -LiteralPath (Join-Path $OutputDirectory "preview.html") -Value $html -Encoding UTF8

    if ($useSearchResultMode) {
        $searchDocument = Get-BraveSearchResultDocument -Html $html -FinalUri $baseUri -SourceUri $sourceUri -PageTitle $baseUri.Host -RequestId $RequestId -PicoId $PicoId -SessionId $SessionId -MaximumBlocks $MaxBlocks
        if ($null -ne $searchDocument) {
            $searchDocument | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory "document.json") -Encoding UTF8
            Write-GeneratorLog "GENERATION_COMPLETED mode=SEARCH"
            Write-GenerationTiming
            exit 0
        }
        Write-GeneratorLog "SEARCH_RESULTS_FALLBACK using=DIRECT_COMPATIBILITY"
    }

    if (-not [string]::IsNullOrWhiteSpace($LocalReadableModelPath)) {
        $localDocument = Invoke-NiceLocalReadableDocument -ModelPath $LocalReadableModelPath -SourceUrl $sourceUri.AbsoluteUri -FinalUrl $finalUrl -OriginalHtml $html -OutputDirectory $OutputDirectory -PicoId $PicoId -SessionId $SessionId -RequestId $RequestId -KeepImages $KeepImages -MaxImageCount $MaxImageCount -MaxBlocks $MaxBlocks
        if ($null -ne $localDocument) {
            $localDocument | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory "document.json") -Encoding UTF8
            Write-GeneratorLog "LOCAL_DOCUMENT_SELECTED blocks=$(@($localDocument.blocks).Count)"
            Write-GeneratorLog "GENERATION_COMPLETED mode=LOCAL"
            Write-GenerationTiming
            exit 0
        }
        Write-GeneratorLog "LOCAL_DOCUMENT_UNAVAILABLE using=DIRECT_COMPATIBILITY"
    }

    if ($EnableReadableMerge -match '^(?i:true|1|yes)$' -and [string]::IsNullOrWhiteSpace($InputHtmlPath)) {
        $mergedDocument = Invoke-NiceMergedReadableDocument -SourceUrl $sourceUri.AbsoluteUri -FinalUrl $finalUrl -OriginalHtml $html -PageTitle $baseUri.Host -OutputDirectory $OutputDirectory -PicoId $PicoId -SessionId $SessionId -RequestId $RequestId -KeepImages $KeepImages -MaxImageCount $MaxImageCount -MaxLinks $MergedReadableLinkLimit -MaxBlocks $MaxBlocks -AllowInsecureReadableTls $AllowInsecureReadableTls -GenerationWatch $generationWatch -TotalGenerationBudgetSeconds $TotalGenerationBudgetSeconds
        if ($null -ne $mergedDocument) {
            $mergedDocument | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory "document.json") -Encoding UTF8
            $mergedBlocks = @($mergedDocument.blocks)
            Write-GeneratorLog "MERGED_DOCUMENT_SELECTED blocks=$($mergedBlocks.Count) links=$(@($mergedBlocks | Where-Object { $_.type -eq 'LINK' }).Count) images=$(@($mergedBlocks | Where-Object { $_.type -eq 'IMAGE_PLACEHOLDER' }).Count)"
            Write-GeneratorLog "GENERATION_COMPLETED mode=MERGED"
            Write-GenerationTiming
            exit 0
        }
        Write-GeneratorLog "MERGED_DOCUMENT_UNAVAILABLE using=DIRECT_COMPATIBILITY"
    }

    $structuralHtml = Remove-StructuralNoiseHtml $html
    $titleMatch = [regex]::Match($structuralHtml, "<title[^>]*>(.*?)</title>", "IgnoreCase,Singleline")
    $pageTitle = if ($titleMatch.Success) { Normalize-Text $titleMatch.Groups[1].Value } else { $baseUri.Host }
    if ([string]::IsNullOrWhiteSpace($pageTitle)) { $pageTitle = $baseUri.Host }

    $baseHrefMatch = [regex]::Match($structuralHtml, "<base\b[^>]*href\s*=\s*[""']([^""']+)[""'][^>]*>", "IgnoreCase")
    if ($baseHrefMatch.Success) {
        try {
            $baseUri = [Uri]::new($baseUri, [System.Net.WebUtility]::HtmlDecode($baseHrefMatch.Groups[1].Value.Trim()))
        } catch {
            Write-GeneratorLog "Ignoring invalid base href"
        }
    }

    $primaryNavigation = Get-PrimaryNavigationSnapshot $structuralHtml $baseUri
    $profile = Get-PageProfile $structuralHtml $Mode
    $effectiveMode = $profile.Name
    Write-GeneratorLog "PAGE_PROFILE $effectiveMode reason=$($profile.Reason)"

    $cleanHtml = Remove-NoiseHtml $structuralHtml
    $contentHtml = $cleanHtml
    if ($effectiveMode -eq "ARTICLE") {
        $articleMatch = [regex]::Match($cleanHtml, "<article\b[^>]*>(.*?)</article>", "IgnoreCase,Singleline")
        $mainMatch = [regex]::Match($cleanHtml, "<main\b[^>]*>(.*?)</main>", "IgnoreCase,Singleline")
        if ($articleMatch.Success) {
            $contentHtml = $articleMatch.Groups[1].Value
        } elseif ($mainMatch.Success) {
            $contentHtml = $mainMatch.Groups[1].Value
        }
    }

    $rankedCards = @()
    if ($effectiveMode -eq "LANDING") {
        $rankedCards = @(Get-CardCandidates $structuralHtml $baseUri $pageTitle | Select-Object -First $LandingCardLimit)
        for ($cardIndex = 0; $cardIndex -lt $rankedCards.Count; $cardIndex++) {
            Write-GeneratorLog "CARD_SELECTED rank=$($cardIndex + 1) title=$($rankedCards[$cardIndex].Title) url=$($rankedCards[$cardIndex].Url)"
        }
    }

    $plainText = Normalize-Text ([regex]::Replace($contentHtml, "<[^>]+>", " "))
    if ($plainText.Length -gt $MaxTextChars) {
        $plainText = $plainText.Substring(0, $MaxTextChars)
    }
    Set-Content -LiteralPath (Join-Path $OutputDirectory "page.txt") -Value $plainText -Encoding UTF8

    $blocks = [System.Collections.Generic.List[object]]::new()
    Add-Block $blocks "TITLE" $pageTitle

    $selectedCardTargets = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $selectedImageCandidates = @()
    $imageCount = 0
    if ($effectiveMode -eq "LANDING") {
        if ($rankedCards.Count -gt 0) {
            $hero = $rankedCards[0]
            Add-Block $blocks "H2" "Featured"
            $heroExtension = Get-RasterImageExtension $hero.ImageUrl
            if ($KeepImages -and $heroExtension -in @("jpg", "jpeg", "png", "gif", "bmp") -and $MaxImageCount -gt 0) {
                Add-Block $blocks "IMAGE_PLACEHOLDER" $hero.Title $hero.ImageUrl 0 0
                $selectedImageCandidates = @($hero.ImageUrl)
                $imageCount = 1
                Write-GeneratorLog "HERO_SELECTED title=$($hero.Title) image=$($hero.ImageUrl)"
            }
            Add-Block $blocks "H1" $hero.Title
            if ($hero.Summary.Length -ge 20) { Add-Block $blocks "PARAGRAPH" $hero.Summary }
            Add-Block $blocks "LINK" "Open" $hero.Url
            $selectedCardTargets.Add((Get-NormalizedUrlKey $hero.Url)) | Out-Null
            foreach ($card in ($rankedCards | Select-Object -Skip 1)) {
                Add-Block $blocks "H2" $card.Title
                if ($card.Summary.Length -ge 20) { Add-Block $blocks "PARAGRAPH" $card.Summary }
                Add-Block $blocks "LINK" "Open" $card.Url
                $selectedCardTargets.Add((Get-NormalizedUrlKey $card.Url)) | Out-Null
            }
        }
        if ($primaryNavigation.Count -gt 0) {
            Add-Block $blocks "HR"
            Add-Block $blocks "H2" "Explore"
            foreach ($navLink in $primaryNavigation) { Add-Block $blocks "LINK" $navLink.Text $navLink.Url }
        }
    }

    $pattern = "<(?<tag>h1|h2|h3|p|li|blockquote|a|img|hr)\b(?<attrs>[^>]*)>(?<inner>.*?)</\k<tag>>|<(?<self>img|hr)\b(?<selfattrs>[^>]*)/?>"
    $matches = [regex]::Matches($contentHtml, $pattern, "IgnoreCase,Singleline")
    $linkCount = 0
    $firstH1Position = -1
    $imageCandidates = [System.Collections.Generic.List[object]]::new()
    $ogImageMatch = [regex]::Match($cleanHtml, '<meta\b(?=[^>]*(?:property|name)\s*=\s*["'']og:image["''])(?=[^>]*content\s*=\s*["''](?<url>[^"'']+)["''])[^>]*>', "IgnoreCase")
    if ($ogImageMatch.Success) {
        $ogUrl = Resolve-Link $ogImageMatch.Groups["url"].Value $baseUri
        $ogExtension = Get-RasterImageExtension $ogUrl
        if ($ogExtension -in @("jpg", "jpeg", "png", "gif", "bmp")) {
            $imageCandidates.Add([PSCustomObject]@{ Url=$ogUrl; Alt=$pageTitle; Title=$pageTitle; Class="open-graph primary-image"; Id=""; HtmlWidth=""; HtmlHeight=""; Extension=$ogExtension; Order=-1 }) | Out-Null
            Write-GeneratorLog "IMAGE CANDIDATE url=$ogUrl tag=meta htmlSize=x contentType=$ogExtension decision=COLLECTED reason=OPEN_GRAPH_PRIMARY"
        }
    }

    foreach ($match in $matches) {
        if ($blocks.Count -ge $MaxBlocks) { break }
        $tag = if ($match.Groups["tag"].Success) { $match.Groups["tag"].Value.ToLowerInvariant() } else { $match.Groups["self"].Value.ToLowerInvariant() }
        $attrs = if ($match.Groups["attrs"].Success) { $match.Groups["attrs"].Value } else { $match.Groups["selfattrs"].Value }
        $inner = if ($match.Groups["inner"].Success) { $match.Groups["inner"].Value } else { "" }
        $text = Normalize-Text ([regex]::Replace($inner, "<[^>]+>", " "))

        if ($tag -ne "img" -and (Is-LowValueText $text)) { continue }

        switch ($tag) {
            "h1" { if ($firstH1Position -lt 0) { $firstH1Position = $match.Index }; Add-Block $blocks "H1" $text }
            "h2" { Add-Block $blocks "H2" $text }
            "h3" { Add-Block $blocks "H2" $text }
            "p"  {
                if ($text.Length -ge 35 -or $effectiveMode -in @("LANDING", "INDEX")) { Add-Block $blocks "PARAGRAPH" $text }
            }
            "li" {
                $innerHrefMatch = [regex]::Match($inner, "href\s*=\s*[""']([^""']+)[""']", "IgnoreCase")
                if ($innerHrefMatch.Success -and $linkCount -lt $(if ($effectiveMode -in @("LANDING", "INDEX")) { $LandingLinkLimit } else { $ArticleLinkLimit })) {
                    $target = Resolve-Link $innerHrefMatch.Groups[1].Value $baseUri
                    if (-not [string]::IsNullOrWhiteSpace($target) -and $text.Length -ge 4 -and $text.Length -le 120) {
                        Add-Block $blocks "LINK" $text $target
                        $linkCount++
                    }
                } elseif ($text.Length -ge 12) {
                    Add-Block $blocks "LIST_ITEM" $text
                }
            }
            "blockquote" { Add-Block $blocks "QUOTE" $text }
            "a" {
                if ($effectiveMode -eq "ARTICLE" -and $linkCount -ge $ArticleLinkLimit) { continue }
                if ($effectiveMode -in @("LANDING", "INDEX") -and $linkCount -ge $LandingLinkLimit) { continue }
                if ($text.Length -lt 4 -or $text.Length -gt 90) { continue }
                $href = Get-Attribute $attrs "href"
                $target = Resolve-Link $href $baseUri
                if ([string]::IsNullOrWhiteSpace($target)) { continue }
                if ($selectedCardTargets.Contains((Get-NormalizedUrlKey $target))) { Write-GeneratorLog "DUPLICATE_ACTION_COLLAPSED target=$target"; continue }
                Add-Block $blocks "LINK" $text $target
                $linkCount++
            }
            "img" {
                $alt = Get-Attribute $attrs "alt"
                $src = Resolve-Link (Get-Attribute $attrs "src") $baseUri
                $htmlWidth = Get-Attribute $attrs "width"
                $htmlHeight = Get-Attribute $attrs "height"
                $extension = Get-RasterImageExtension $src
                if (-not $KeepImages) { Write-GeneratorLog "IMAGE CANDIDATE url=$src tag=img htmlSize=$htmlWidth`x$htmlHeight contentType=$extension decision=REJECTED reason=IMAGES_DISABLED"; continue }
                if ([string]::IsNullOrWhiteSpace($src)) { Write-GeneratorLog "IMAGE CANDIDATE url=(missing) tag=img htmlSize=$htmlWidth`x$htmlHeight contentType=$extension decision=REJECTED reason=MISSING_SOURCE"; continue }
                if ($extension -notin @("jpg", "jpeg", "png", "gif", "bmp")) { Write-GeneratorLog "IMAGE CANDIDATE url=$src tag=img htmlSize=$htmlWidth`x$htmlHeight contentType=$extension decision=REJECTED reason=UNSUPPORTED_IMAGE_FORMAT"; continue }
                if ([string]::IsNullOrWhiteSpace($alt)) { $alt = "Image placeholder" }
                $imageCandidates.Add([PSCustomObject]@{ Url=$src; Alt=$alt; Title=(Get-Attribute $attrs "title"); Class=(Get-Attribute $attrs "class"); Id=(Get-Attribute $attrs "id"); HtmlWidth=$htmlWidth; HtmlHeight=$htmlHeight; Extension=$extension; Order=$match.Index }) | Out-Null
            }
            "hr" { Add-Block $blocks "HR" }
        }
    }

    if ($imageCandidates.Count -gt 0 -and $imageCount -lt $MaxImageCount) {
        $titleTokens = Get-TitleTokens $pageTitle
        $ranked = @($imageCandidates | ForEach-Object { Get-ImageCandidateScore $_ $titleTokens $firstH1Position $contentHtml } | Sort-Object -Property @{Expression={$_.Score};Descending=$true}, @{Expression={$_.Candidate.Order};Ascending=$true})
        foreach ($item in ($ranked | Select-Object -First 3)) {
            Write-GeneratorLog "IMAGE CANDIDATE url=$($item.Candidate.Url) size=$($item.Candidate.HtmlWidth)x$($item.Candidate.HtmlHeight) insideMain=$($item.InsideMain) positive=$($item.Positive) negative=$($item.Negative) score=$($item.Score)"
        }
        $winner = $ranked[0]
        if ($winner.Score -ge -20) {
            # Dimensions are assigned only after the Bridge validates the generated NIMG header.
            Add-Block $blocks "IMAGE_PLACEHOLDER" $winner.Candidate.Alt $winner.Candidate.Url 0 0
            $imageCount = 1
            # Generator metadata for the Bridge's one bounded conversion fallback.
            # It is not emitted into the NICE wire document.
            $selectedImageCandidates = @($ranked | Select-Object -First 2 | ForEach-Object { $_.Candidate.Url } | Select-Object -Unique)
            Write-GeneratorLog "IMAGE SELECTED url=$($winner.Candidate.Url) score=$($winner.Score) reason=HIGHEST_GENERIC_CONTENT_SCORE"
        } else {
            Write-GeneratorLog "IMAGE SELECTED none reason=NO_MEANINGFUL_CANDIDATE score=$($winner.Score)"
        }
    }

    $semanticBodyChars = 0
    $semanticBodyCount = 0
    foreach ($block in $blocks) {
        if ($block.type -in @("PARAGRAPH", "LIST_ITEM", "QUOTE") -and $block.text) { $semanticBodyChars += $block.text.Length; $semanticBodyCount++ }
    }
    $coverage = if ($plainText.Length -gt 0) { $semanticBodyChars / [double]$plainText.Length } else { 1.0 }
    $recovered = [System.Collections.Generic.List[string]]::new()
    if (($plainText.Length -ge 300 -and $coverage -lt 0.35) -or ($semanticBodyCount -eq 0 -and $plainText.Length -ge 180)) {
        $recovered = Get-RecoveredParagraphs $plainText $blocks
        foreach ($paragraph in $recovered) {
            if ($blocks.Count -ge ($MaxBlocks - 1)) { break }
            Add-Block $blocks "PARAGRAPH" $paragraph
        }
    }

    if ($blocks.Count -eq 1) {
        # A JavaScript shell can have a valid HTTP response but no static content
        # for the converter. Never synthesize a link to the current URL here: that
        # produces an endless route loop on the ROM.
        Add-Block $blocks "PARAGRAPH" "This site did not provide static page content to the Browser."
        $paragraphCount++
        Write-GeneratorLog "STATIC_CONTENT_UNAVAILABLE finalUrl=$finalUrl"
    }

    Add-Block $blocks "END"
    $document = [ordered]@{
        format = "NICE-DOCUMENT"
        version = 1
        requestId = $RequestId
        picoId = $PicoId
        sessionId = $SessionId
        sourceUrl = $sourceUri.AbsoluteUri
        finalUrl = $finalUrl
        pageTitle = $pageTitle
        mode = $effectiveMode
        truncated = $false
        blocks = $blocks
        imageFallbacks = $selectedImageCandidates
    }

    $document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory "document.json") -Encoding UTF8
    $recoveredChars = ($recovered | Measure-Object -Property Length -Sum).Sum
    Write-GeneratorLog "Semantic coverage cleanedChars=$($plainText.Length) bodyChars=$semanticBodyChars coverage=$([math]::Round($coverage * 100, 1))% recoveredParagraphs=$($recovered.Count) recoveredChars=$recoveredChars"
    $headingCount = @($blocks | Where-Object { $_.type -in @('H1', 'H2') }).Count
    $paragraphCount = @($blocks | Where-Object { $_.type -eq 'PARAGRAPH' }).Count
    $finalLinkCount = @($blocks | Where-Object { $_.type -eq 'LINK' }).Count
    $finalImageCount = @($blocks | Where-Object { $_.type -eq 'IMAGE_PLACEHOLDER' }).Count
    Write-GeneratorLog "FINAL_DOCUMENT headings=$headingCount paragraphs=$paragraphCount links=$finalLinkCount imageBlocks=$finalImageCount groupedCards=$($rankedCards.Count) primaryNavLinks=$($primaryNavigation.Count)"
    Write-GeneratorLog "Extracted blocks=$($blocks.Count) links=$linkCount images=$imageCount finalUrl=$finalUrl"
    Write-GeneratorLog "GENERATION_COMPLETED mode=DIRECT"
    Write-GenerationTiming
    exit 0
} catch {
    Write-GeneratorLog "FETCH FAILED: $($_.Exception.Message)"
    $elapsedMs = 0
    if ($fetchWatch) {
        $elapsedMs = [int]$fetchWatch.ElapsedMilliseconds
    }
    $friendly = Get-FriendlyFetchError $_ $elapsedMs
    Write-GeneratorError $friendly.Title $friendly.Detail $friendly.Code $friendly.HttpStatus $friendly.ExceptionType $elapsedMs
    Write-GeneratorLog "GENERATION_FAILED reason=$($_.Exception.Message)"
    if ($generationWatch) { Write-GenerationTiming }
    exit 30
}
