# Browser Live helper. Loaded by Generate-NiceDocument.ps1 after its core helpers.
# It returns $null when readable merging is unavailable so the direct converter remains authoritative fallback.

function Normalize-NiceMergedText {
    param([string]$Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return '' }
    $value = Normalize-Text $Text
    $value = $value.Replace([char]0x2018, "'").Replace([char]0x2019, "'").Replace([char]0x2013, '-').Replace([char]0x2014, '-')
    return [regex]::Replace($value.ToLowerInvariant(), '[^a-z0-9]+', ' ').Trim()
}

function Get-NiceMergedTokens {
    param([string]$Text)
    $stop = @('the','and','for','with','this','that','from','your','you','are','was','new','now','more','home','site','official','page','read','learn','about','into','have','will','our','all','not','one','its')
    return @([regex]::Matches((Normalize-NiceMergedText $Text), '[a-z0-9]{3,}') | ForEach-Object { $_.Value } | Where-Object { $stop -notcontains $_ } | Select-Object -Unique)
}

function Get-NiceMergedOverlap {
    param([string[]]$Left, [string[]]$Right)
    return @($Left | Where-Object { $Right -contains $_ }).Count
}

function Test-NiceReadableResponse {
    param([int]$ExitCode, [string]$HttpStatus, [bool]$BodyExists, [int]$BodyLength)
    return $ExitCode -eq 0 -and $HttpStatus -match '^2\d\d$' -and $BodyExists -and $BodyLength -ge 80
}

function Get-NiceMergedAttribute {
    param([string]$Tag, [string]$Name)
    $pattern = '\b{0}\s*=\s*[''\"](?<value>[^''\"]+)[''\"]' -f [regex]::Escape($Name)
    $match = [regex]::Match($Tag, $pattern, 'IgnoreCase')
    if ($match.Success) { return [System.Net.WebUtility]::HtmlDecode($match.Groups['value'].Value) }
    return ''
}

function Get-NiceMergedSrcSet {
    param([string]$Value)
    if (-not $Value) { return '' }
    return (($Value -split ',')[0].Trim() -split '\s+')[0]
}

function Get-NiceMergedNormalizedUrl {
    param([string]$Url)
    if (-not $Url) { return '' }
    try { $builder = [UriBuilder][Uri]$Url; $builder.Fragment = ''; return $builder.Uri.AbsoluteUri.TrimEnd('/').ToLowerInvariant() } catch { return $Url.TrimEnd('/').ToLowerInvariant() }
}

function ConvertFrom-NiceFrogFindUrl {
    param([string]$Href, [Uri]$BaseUri)
    $absolute = Resolve-Link $Href $BaseUri
    if (-not $absolute) { return '' }
    try {
        $uri = [Uri]$absolute
        if ($uri.Host -match 'frogfind\.com$' -and $uri.AbsolutePath -match '/read\.php$') {
            # Do not depend on System.Web here. Browser Live runs under the
            # Windows PowerShell host where that assembly is not guaranteed to
            # be loaded. This handles both FrogFind's encoded and literal http
            # destination forms.
            $targetMatch = [regex]::Match($uri.Query, '(?:^\?|&)a=(?<target>.*)$', 'IgnoreCase')
            $target = if ($targetMatch.Success) { [Uri]::UnescapeDataString($targetMatch.Groups['target'].Value) } else { '' }
            if ($target) {
                $unwrapped = Resolve-Link $target $BaseUri
                if ($unwrapped) { Write-GeneratorLog "FROGFIND_LINK_UNWRAPPED from=$absolute to=$unwrapped"; return $unwrapped }
            }
        }
    } catch { }
    return $absolute
}

function Get-NiceOriginalLinks {
    param([string]$Html, [Uri]$BaseUri)
    $items = [System.Collections.Generic.List[object]]::new(); $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($match in [regex]::Matches($Html, '<a\b(?<attrs>[^>]*)>(?<inner>.*?)</a>', 'IgnoreCase,Singleline')) {
        $text = Normalize-Text ([regex]::Replace($match.Groups['inner'].Value, '<[^>]+>', ' ')); $url = Resolve-Link (Get-NiceMergedAttribute $match.Groups['attrs'].Value 'href') $BaseUri
        if ($text.Length -ge 3 -and $text.Length -le 160 -and $url -and $seen.Add((Get-NiceMergedNormalizedUrl $url))) { $items.Add([pscustomobject]@{ Text=$text; Url=$url; Order=$match.Index }) | Out-Null }
    }
    return $items.ToArray()
}

function Get-NiceOriginalImages {
    param([string]$Html, [Uri]$BaseUri)
    $items = [System.Collections.Generic.List[object]]::new(); $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $anchors = [regex]::Matches($Html, '<a\b(?<attrs>[^>]*)>(?<inner>.*?)</a>', 'IgnoreCase,Singleline')
    foreach ($match in [regex]::Matches($Html, '<img\b(?<attrs>[^>]*)/?>|<source\b(?<attrs>[^>]*)/?>', 'IgnoreCase,Singleline')) {
        $attrs = $match.Groups['attrs'].Value; $raw = Get-NiceMergedAttribute $attrs 'src'
        if (-not $raw) { $raw = Get-NiceMergedAttribute $attrs 'data-src' }; if (-not $raw) { $raw = Get-NiceMergedAttribute $attrs 'data-lazy-src' }; if (-not $raw) { $raw = Get-NiceMergedSrcSet (Get-NiceMergedAttribute $attrs 'srcset') }; if (-not $raw) { $raw = Get-NiceMergedSrcSet (Get-NiceMergedAttribute $attrs 'data-srcset') }
        $url = Resolve-Link $raw $BaseUri; if (-not $url -or -not $seen.Add((Get-NiceMergedNormalizedUrl $url))) { continue }
        $terms = "$url $(Get-NiceMergedAttribute $attrs 'alt') $(Get-NiceMergedAttribute $attrs 'class')".ToLowerInvariant()
        if ($url -match '\.svg($|\?)' -or $terms -match 'favicon|icon|spinner|tracking|pixel|social|cookie|avatar|account|rating|star|arrow|close|logo') { Write-GeneratorLog "MERGED_IMAGE_REJECTED reason=UI_OR_UNSUPPORTED url=$url"; continue }
        $parent = ''; foreach ($anchor in $anchors) { if ($match.Index -ge $anchor.Index -and $match.Index -lt ($anchor.Index + $anchor.Length)) { $parent = Resolve-Link (Get-NiceMergedAttribute $anchor.Groups['attrs'].Value 'href') $BaseUri; break } }
        $contextStart = [Math]::Max(0, $match.Index - 1800); $context = $Html.Substring($contextStart, $match.Index - $contextStart); $heads = [regex]::Matches($context, '<h[1-4]\b[^>]*>(?<text>.*?)</h[1-4]>', 'IgnoreCase,Singleline'); $nearby = if ($heads.Count) { Normalize-Text ([regex]::Replace($heads[$heads.Count - 1].Groups['text'].Value, '<[^>]+>', ' ')) } else { '' }
        $score = 0; if ($terms -match 'hero|featured|feature|card|promo|story|article|product') { $score += 8 }
        $items.Add([pscustomobject]@{ Url=$url; Alt=(Get-NiceMergedAttribute $attrs 'alt'); ParentHref=$parent; NearbyHeading=$nearby; SourceOrder=$match.Index; Score=$score; Reasons=@(if ($score) { 'content-class +8' }) }) | Out-Null
    }
    return $items.ToArray()
}

function Get-NiceReadablePage {
    param([string]$Html, [Uri]$BaseUri, [int]$MaxLinks, [int]$MaxBlocks)
    $wrapper = '(?is)<(form|p)\b[^>]*>.*?(Back\s+to\s+.*?Frog.*?Find|Browsing\s+URL|View\s+page\s+images).*?</\1>|<p\b[^>]*>\s*View\s+page\s+images:.*?</p>'
    $clean = [regex]::Replace($Html, $wrapper, ''); $clean = [regex]::Replace($clean, '(?is)<a\b[^>]*href\s*=\s*["'']?[^"''>]*image\.php\?i=[^>]*>.*?</a>', '')
    $titleMatch = [regex]::Match($clean, '<title\b[^>]*>(?<text>.*?)</title>', 'IgnoreCase,Singleline'); $title = if ($titleMatch.Success) { Normalize-Text $titleMatch.Groups['text'].Value } else { '' }
    $headings = [regex]::Matches($clean, '<h[1-4]\b[^>]*>(?<text>.*?)</h[1-4]>', 'IgnoreCase,Singleline'); $primary = ''; foreach ($heading in $headings) { $text = Normalize-Text ([regex]::Replace($heading.Groups['text'].Value, '<[^>]+>', ' ')); if ($text -and (Normalize-NiceMergedText $text) -ne (Normalize-NiceMergedText $title)) { $primary=$text; break } }
    $date = ''; if ($clean -match '\b(\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|[A-Z][a-z]+\s+\d{1,2},\s+\d{4})\b') { $date=$Matches[1] }
    $blocks = [System.Collections.Generic.List[object]]::new(); $links = [System.Collections.Generic.List[object]]::new(); $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($match in [regex]::Matches($clean, '<(?<tag>h1|h2|h3|h4|p|li|blockquote|a)\b(?<attrs>[^>]*)>(?<inner>.*?)</\k<tag>>', 'IgnoreCase,Singleline')) {
        if ($blocks.Count -ge $MaxBlocks) { break }; $tag=$match.Groups['tag'].Value.ToLowerInvariant(); $text=Normalize-Text ([regex]::Replace($match.Groups['inner'].Value,'<[^>]+>',' '))
        if ($tag -eq 'a') { $url=ConvertFrom-NiceFrogFindUrl (Get-NiceMergedAttribute $match.Groups['attrs'].Value 'href') $BaseUri; if ($url -and $text.Length -ge 2 -and $links.Count -lt $MaxLinks -and $seen.Add((Get-NiceMergedNormalizedUrl $url))) { $item=[pscustomobject]@{ Text=$text; Url=$url }; $links.Add($item)|Out-Null; $blocks.Add([pscustomobject]@{ Kind='LINK'; Text=$text; Url=$url })|Out-Null }; continue }
        if ($text.Length -lt 2 -or $text -match '^(Back to|Browsing URL|Go!|View page images)') { continue }
        $kind=if($tag -match '^h'){'H2'}elseif($tag -eq 'li'){'LIST_ITEM'}elseif($tag -eq 'blockquote'){'QUOTE'}else{'PARAGRAPH'}
        if ($kind -eq 'PARAGRAPH' -and $text.Length -lt 25) { continue }; $blocks.Add([pscustomobject]@{ Kind=$kind; Text=$text; Url='' })|Out-Null
    }
    # FrogFind commonly nests meaningful content anchors inside a paragraph. The
    # shallow ordered pass above consumes that paragraph, so preserve those links
    # in one separate pass over the already wrapper-cleaned HTML.
    foreach ($anchor in [regex]::Matches($clean, '<a\b(?<attrs>[^>]*)>(?<inner>.*?)</a>', 'IgnoreCase,Singleline')) {
        if ($links.Count -ge $MaxLinks -or $blocks.Count -ge $MaxBlocks) { break }
        $text = Normalize-Text ([regex]::Replace($anchor.Groups['inner'].Value, '<[^>]+>', ' '))
        $url = ConvertFrom-NiceFrogFindUrl (Get-NiceMergedAttribute $anchor.Groups['attrs'].Value 'href') $BaseUri
        if (-not $url -or $text.Length -lt 2 -or -not $seen.Add((Get-NiceMergedNormalizedUrl $url))) { continue }
        $item = [pscustomobject]@{ Text=$text; Url=$url }
        $links.Add($item) | Out-Null
        $blocks.Add([pscustomobject]@{ Kind='LINK'; Text=$text; Url=$url }) | Out-Null
        Write-GeneratorLog "FROGFIND_CONTENT_LINK text=$text target=$url"
    }
    $summary = ''; foreach($block in $blocks){if($block.Kind -eq 'PARAGRAPH' -and $block.Text.Length -ge 40 -and (Normalize-NiceMergedText $block.Text) -ne (Normalize-NiceMergedText $primary)){ $summary=$block.Text; break }}
    return [pscustomobject]@{ Title=$title; PrimaryHeading=$primary; Date=$date; Summary=$summary; Blocks=$blocks.ToArray(); Links=$links.ToArray() }
}

function Invoke-NiceMergedReadableDocument {
    param([string]$SourceUrl,[string]$FinalUrl,[string]$OriginalHtml,[string]$PageTitle,[string]$OutputDirectory,[string]$PicoId,[string]$SessionId,[string]$RequestId,[bool]$KeepImages,[int]$MaxImageCount,[int]$MaxLinks,[int]$MaxBlocks,[bool]$AllowInsecureReadableTls,[System.Diagnostics.Stopwatch]$GenerationWatch,[int]$TotalGenerationBudgetSeconds)
    try {
        if ($OriginalHtml.Length -lt 80) { Write-GeneratorLog 'MERGED_FALLBACK reason=ORIGINAL_TINY'; return $null }
        $curl = Join-Path $env:SystemRoot 'System32\curl.exe'; if (-not (Test-Path $curl)) { Write-GeneratorLog 'MERGED_FALLBACK reason=CURL_MISSING'; return $null }
        $remainingMs = [Math]::Max(0, ($TotalGenerationBudgetSeconds * 1000) - [int]$GenerationWatch.ElapsedMilliseconds)
        if ($remainingMs -lt 1500) { Write-GeneratorLog 'READABLE_SKIPPED reason=INSUFFICIENT_BUDGET'; Write-GeneratorLog 'READABLE_FALLBACK_DIRECT'; return $null }
        $readableTimeoutSeconds = [Math]::Max(1, [Math]::Min(7, [Math]::Floor($remainingMs / 1000)))
        $readableConnectTimeoutSeconds = [Math]::Min(3, $readableTimeoutSeconds)
        $frogBody = Join-Path $OutputDirectory 'frogfind-source.html'; $frogHeaders=Join-Path $OutputDirectory 'frogfind-headers.txt'
        $readableEndpoint = [Environment]::GetEnvironmentVariable('NICE64_READABLE_ENDPOINT_OVERRIDE')
        $frogUrl = if ([string]::IsNullOrWhiteSpace($readableEndpoint)) { 'https://frogfind.com/read.php?a=' + [Uri]::EscapeDataString($FinalUrl) } else { $readableEndpoint + $(if ($readableEndpoint.Contains('?')) { '&' } else { '?' }) + 'a=' + [Uri]::EscapeDataString($FinalUrl) }
        if (-not [string]::IsNullOrWhiteSpace($readableEndpoint)) { Write-GeneratorLog "READABLE_ENDPOINT_OVERRIDE=$readableEndpoint" }
        Remove-Item -LiteralPath $frogBody -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $frogHeaders -Force -ErrorAction SilentlyContinue
        $args=@('--location','--silent','--show-error','--max-redirs','10','--connect-timeout',[string]$readableConnectTimeoutSeconds,'--max-time',[string]$readableTimeoutSeconds,'--http1.1','--user-agent','Mozilla/5.0 (Nintendo 3DS; U; en) Version/1.7498.US','--header','Accept: text/html','--dump-header',$frogHeaders,'--output',$frogBody,'--write-out','%{http_code}')
        if ($AllowInsecureReadableTls) { $args += '--insecure'; Write-GeneratorLog 'FROGFIND_TLS_CERTIFICATE_VERIFICATION=DISABLED_EXPLICITLY' }
        $args += $frogUrl
        Write-GeneratorLog "READABLE_STARTED connectTimeoutSeconds=$readableConnectTimeoutSeconds timeoutSeconds=$readableTimeoutSeconds"
        try {
            $status = (& $curl @args).Trim(); $curlExit = $LASTEXITCODE
            Write-GeneratorLog "READABLE_HTTP_STATUS=$status"
            $frogBodyExists = Test-Path -LiteralPath $frogBody
            $frogHtml = if ($frogBodyExists) { [IO.File]::ReadAllText($frogBody,[Text.Encoding]::UTF8) } else { '' }
            if (-not (Test-NiceReadableResponse $curlExit $status $frogBodyExists $frogHtml.Length)) {
                if ($curlExit -ne 0) { Write-GeneratorLog "READABLE_TIMEOUT exit=$curlExit" }
                elseif ($status -notmatch '^2\d\d$') { Write-GeneratorLog "READABLE_FALLBACK_DIRECT reason=HTTP_$status" }
                elseif (-not (Test-Path -LiteralPath $frogBody)) { Write-GeneratorLog 'READABLE_FALLBACK_DIRECT reason=BODY_MISSING' }
                else { Write-GeneratorLog 'READABLE_FALLBACK_DIRECT reason=TINY_BODY' }
                return $null
            }
            Write-GeneratorLog "READABLE_COMPLETED bytes=$($frogHtml.Length)"
        } finally {
            Write-GeneratorLog "GENERATION_ELAPSED_MS=$([int]$GenerationWatch.ElapsedMilliseconds)"
            Write-GeneratorLog "GENERATION_REMAINING_MS=$([Math]::Max(0, ($TotalGenerationBudgetSeconds * 1000) - [int]$GenerationWatch.ElapsedMilliseconds))"
        }
        $originalBase=[Uri]$FinalUrl; $frog=[Uri]'https://frogfind.com/'; $readable=Get-NiceReadablePage $frogHtml $frog $MaxLinks $MaxBlocks; if(-not $readable.Title -or (-not $readable.PrimaryHeading -and -not $readable.Summary)){Write-GeneratorLog 'MERGED_FALLBACK reason=READABLE_WEAK';return $null}
        $originalLinks=Get-NiceOriginalLinks $OriginalHtml $originalBase; $primaryLink=''; $wanted=Get-NiceMergedTokens "$($readable.PrimaryHeading) $($readable.Summary)"; $bestLinkScore=0; foreach($link in $originalLinks){$score=Get-NiceMergedOverlap $wanted (Get-NiceMergedTokens $link.Text);if($score -gt $bestLinkScore){$bestLinkScore=$score;$primaryLink=$link.Url}}; if($bestLinkScore -lt 2 -and $readable.Links.Count){$primaryLink=$readable.Links[0].Url}; if($primaryLink){Write-GeneratorLog "MERGED_PRIMARY_LINK=$primaryLink score=$bestLinkScore"}
        $images=Get-NiceOriginalImages $OriginalHtml $originalBase; $winner=$null;$winnerScore=-999; foreach($image in $images){$score=$image.Score;$reasons=[System.Collections.Generic.List[string]]::new();foreach($r in $image.Reasons){$reasons.Add($r)|Out-Null};if($primaryLink -and (Get-NiceMergedNormalizedUrl $image.ParentHref) -eq (Get-NiceMergedNormalizedUrl $primaryLink)){$score+=100;$reasons.Add('exact destination +100')|Out-Null};$overlap=Get-NiceMergedOverlap $wanted (Get-NiceMergedTokens "$($image.Alt) $($image.NearbyHeading) $($image.Url)");if($overlap -ge 2){$score+=30;$reasons.Add("heading overlap=$overlap +30")|Out-Null};if($score -gt $winnerScore){$winnerScore=$score;$winner=[pscustomobject]@{Image=$image;Score=$score;Reasons=$reasons.ToArray()}}}; if($winner){Write-GeneratorLog "MERGED_SELECTED_IMAGE url=$($winner.Image.Url) score=$winnerScore reasons=$($winner.Reasons -join '; ')"}
        $blocks=[System.Collections.Generic.List[object]]::new(); $finalTitle=if($readable.Title){$readable.Title}else{$PageTitle}; Add-Block $blocks 'TITLE' $finalTitle; if($readable.PrimaryHeading -and (Normalize-NiceMergedText $readable.PrimaryHeading) -ne (Normalize-NiceMergedText $finalTitle)){Add-Block $blocks 'H1' $readable.PrimaryHeading};if($readable.Date){Add-Block $blocks 'PARAGRAPH' $readable.Date};$fallbacks=@();$imageExtension=if($winner){Get-RasterImageExtension $winner.Image.Url}else{''};$imageLabel=if($readable.PrimaryHeading){($readable.PrimaryHeading -replace '(?i)\s+is\s+now.*$','').Trim()}else{'Image'};if($imageLabel.Length -lt 3){$imageLabel='Image'};if($KeepImages -and $MaxImageCount -gt 0 -and $winner -and ($imageExtension -in @('jpg','jpeg','png','gif','bmp') -or [string]::IsNullOrWhiteSpace($imageExtension))){Add-Block $blocks 'IMAGE_PLACEHOLDER' $imageLabel $winner.Image.Url 0 0;$fallbacks=@($winner.Image.Url);Write-GeneratorLog "MERGED_IMAGE_ACCEPTED extension=$(if($imageExtension){$imageExtension}else{'extensionless'}) label=$imageLabel"};if($readable.Summary){Add-Block $blocks 'PARAGRAPH' $readable.Summary};$primaryLinkText='Read more';foreach($link in $readable.Links){if((Get-NiceMergedNormalizedUrl $link.Url) -eq (Get-NiceMergedNormalizedUrl $primaryLink)){$primaryLinkText=$link.Text;break}};if($primaryLink){Add-Block $blocks 'LINK' $primaryLinkText $primaryLink};foreach($block in $readable.Blocks){if($blocks.Count -ge ($MaxBlocks-1)){break};if((Normalize-NiceMergedText $block.Text) -in @((Normalize-NiceMergedText $finalTitle),(Normalize-NiceMergedText $readable.PrimaryHeading),(Normalize-NiceMergedText $readable.Summary))){continue};if($block.Kind -eq 'LINK' -and $block.Url -eq $primaryLink){continue};if($block.Kind -eq 'LINK'){Add-Block $blocks 'LINK' $block.Text $block.Url}else{Add-Block $blocks $block.Kind $block.Text}};Add-Block $blocks 'END'
        $model=[ordered]@{sourceMode='MergedReadable';title=$finalTitle;primaryHeading=$readable.PrimaryHeading;primaryLink=$primaryLink;selectedImage=if($winner){[ordered]@{url=$winner.Image.Url;score=$winner.Score;reasons=$winner.Reasons}}else{$null}};$model|ConvertTo-Json -Depth 6|Set-Content (Join-Path $OutputDirectory 'merged-page-model.json') -Encoding UTF8
        return [ordered]@{format='NICE-DOCUMENT';version=1;requestId=$RequestId;picoId=$PicoId;sessionId=$SessionId;sourceUrl=$SourceUrl;finalUrl=$FinalUrl;pageTitle=$finalTitle;mode='MERGED';truncated=$false;blocks=$blocks;imageFallbacks=$fallbacks}
    } catch { Write-GeneratorLog "MERGED_FALLBACK reason=EXCEPTION detail=$($_.Exception.Message)"; return $null }
}

function Invoke-NiceLocalReadableDocument {
    param([string]$ModelPath,[string]$SourceUrl,[string]$FinalUrl,[string]$OriginalHtml,[string]$OutputDirectory,[string]$PicoId,[string]$SessionId,[string]$RequestId,[bool]$KeepImages,[int]$MaxImageCount,[int]$MaxBlocks)
    try {
        if (-not (Test-Path -LiteralPath $ModelPath -PathType Leaf)) { Write-GeneratorLog 'LOCAL_FALLBACK reason=MODEL_MISSING'; return $null }
        $result = Get-Content -LiteralPath $ModelPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $page = if ($null -ne $result.Page) { $result.Page } else { $result }
        $quality = if ($null -ne $result.PSObject.Properties['PassesQualityGate']) { [bool]$result.PassesQualityGate } else { $true }
        if ($null -eq $page -or -not $quality -or @($page.Blocks).Count -eq 0) { Write-GeneratorLog 'LOCAL_FALLBACK reason=QUALITY_GATE'; return $null }

        $blocks = [System.Collections.Generic.List[object]]::new()
        $title = if ($page.Title) { $page.Title } else { ([Uri]$FinalUrl).Host }
        Add-Block $blocks 'TITLE' $title
        $primaryLink = [string]$page.PrimaryLink
        $candidateImages = @(Get-NiceOriginalImages $OriginalHtml ([Uri]$FinalUrl) | Where-Object { $_.Url })
        $winner = $null
        if ($primaryLink) {
            $winner = $candidateImages | Where-Object { (Get-NiceMergedNormalizedUrl $_.ParentHref) -eq (Get-NiceMergedNormalizedUrl $primaryLink) } | Select-Object -First 1
        }
        if ($null -eq $winner) { $winner = $candidateImages | Sort-Object -Property Score -Descending | Select-Object -First 1 }
        $imageAdded = $false
        foreach ($block in @($page.Blocks)) {
            if ($blocks.Count -ge ($MaxBlocks - 1)) { break }
            $kind = switch ([string]$block.Type) { '0' { 'H2' } 'Heading' { 'H2' } '1' { 'PARAGRAPH' } 'Paragraph' { 'PARAGRAPH' } '2' { 'LIST_ITEM' } 'ListItem' { 'LIST_ITEM' } '3' { 'QUOTE' } 'Quote' { 'QUOTE' } '4' { 'LINK' } 'Link' { 'LINK' } default { '' } }
            if (-not $kind -or -not $block.Text) { continue }
            if (-not $imageAdded -and $kind -eq 'PARAGRAPH' -and $KeepImages -and $MaxImageCount -gt 0 -and $null -ne $winner) {
                Add-Block $blocks 'IMAGE_PLACEHOLDER' ($(if($page.PrimaryHeading){$page.PrimaryHeading}else{'Image'})) $winner.Url 0 0
                $imageAdded = $true
                Write-GeneratorLog "LOCAL_SELECTED_IMAGE url=$($winner.Url) primaryLink=$primaryLink"
            }
            if ($kind -eq 'LINK') {
                if ($block.Url) { Add-Block $blocks 'LINK' $block.Text $block.Url }
            } else { Add-Block $blocks $kind $block.Text }
        }
        if (-not $imageAdded -and $KeepImages -and $MaxImageCount -gt 0 -and $null -ne $winner -and $blocks.Count -lt ($MaxBlocks - 1)) {
            Add-Block $blocks 'IMAGE_PLACEHOLDER' ($(if($page.PrimaryHeading){$page.PrimaryHeading}else{'Image'})) $winner.Url 0 0
            $imageAdded = $true
            Write-GeneratorLog "LOCAL_SELECTED_IMAGE url=$($winner.Url) primaryLink=$primaryLink"
        }
        Add-Block $blocks 'END'
        $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'local-readable-model-used.json') -Encoding UTF8
        return [ordered]@{format='NICE-DOCUMENT';version=1;requestId=$RequestId;picoId=$PicoId;sessionId=$SessionId;sourceUrl=$SourceUrl;finalUrl=$FinalUrl;pageTitle=$title;mode='LOCAL';truncated=$false;blocks=$blocks;imageFallbacks=$(if($imageAdded){@($winner.Url)}else{@()})}
    } catch { Write-GeneratorLog "LOCAL_FALLBACK reason=EXCEPTION detail=$($_.Exception.Message)"; return $null }
}
