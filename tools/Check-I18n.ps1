#Requires -Version 5.1
<#
.SYNOPSIS
  Lists the UI strings of the tray panel and the installer and compares them with the translation files in ui\i18n.

.DESCRIPTION
  Every string in the code is written once in English at the call site (L.T("English", "Chinese"), L.F(...) and
  {local:Tr ...} in XAML). The files ui\i18n\<code>.json map the English text to the translation.
  This script scans the sources, then reports for each translation file which strings are missing
  (they would show in English) and which keys are no longer used by any source.

  -Dump <file> writes the English source strings as a JSON array (UTF-8) instead of comparing; use it as the
  starting point of a new language file.
#>
param([string]$Dump)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Unescape-CSharp([string]$s) {
    $sb = New-Object Text.StringBuilder
    for ($i = 0; $i -lt $s.Length; $i++) {
        $c = $s[$i]
        if ($c -ne '\' -or $i + 1 -ge $s.Length) { [void]$sb.Append($c); continue }
        $i++
        switch ($s[$i]) {
            'n' { [void]$sb.Append("`n") }
            'r' { [void]$sb.Append("`r") }
            't' { [void]$sb.Append("`t") }
            'u' { [void]$sb.Append([char][Convert]::ToInt32($s.Substring($i + 1, 4), 16)); $i += 4 }
            default { [void]$sb.Append($s[$i]) }
        }
    }
    $sb.ToString()
}

# Splits the text after "L.T(" into top-level arguments (up to the matching parenthesis)
function Split-Args([string]$text, [int]$start) {
    $args2 = New-Object Collections.Generic.List[string]
    $depth = 0; $cur = New-Object Text.StringBuilder
    for ($i = $start; $i -lt $text.Length; $i++) {
        $c = $text[$i]
        if ($c -eq '"') {
            $j = $i + 1
            while ($j -lt $text.Length -and $text[$j] -ne '"') { if ($text[$j] -eq '\') { $j++ }; $j++ }
            [void]$cur.Append($text.Substring($i, $j - $i + 1)); $i = $j; continue
        }
        if ($c -eq '(' -or $c -eq '[' -or $c -eq '{') { $depth++ }
        elseif ($c -eq ')' -or $c -eq ']' -or $c -eq '}') {
            if ($depth -eq 0) { $args2.Add($cur.ToString()); return , $args2 }
            $depth--
        }
        elseif ($c -eq ',' -and $depth -eq 0) { $args2.Add($cur.ToString()); [void]$cur.Clear(); continue }
        [void]$cur.Append($c)
    }
    , $args2
}

$literal = '"(?:[^"\\]|\\.)*"'
$concat = "^\s*$literal(?:\s*\+\s*$literal)*\s*$"

function Get-Literal([string]$expr) {
    if ($expr -notmatch $concat) { return $null }
    $parts = [regex]::Matches($expr, $literal) | ForEach-Object { Unescape-CSharp $_.Value.Substring(1, $_.Value.Length - 2) }
    -join $parts
}

$keys = New-Object Collections.Generic.HashSet[string]
$dynamic = @()

foreach ($file in (Get-ChildItem (Join-Path $root 'ui'), (Join-Path $root 'setup') -Filter '*.cs' -File)) {
    # Comment lines are not code
    $text = (([IO.File]::ReadAllText($file.FullName) -split "`r?`n") | ForEach-Object { if ($_ -match '^\s*//') { '' } else { $_ } }) -join "`n"
    foreach ($m in [regex]::Matches($text, '\bL\.[TF]\(')) {
        $a = Split-Args $text ($m.Index + $m.Length)
        $en = Get-Literal $a[0]
        if ($null -eq $en) { $dynamic += "$($file.Name): $($a[0].Trim())"; continue }
        [void]$keys.Add($en)
    }
    # Tables of string arrays whose entries are passed to L.T later (en, zh, en, zh ...)
    foreach ($m in [regex]::Matches($text, '\{\s*"[a-z]+",\s*new\[\]\s*\{([^}]*)\}\s*\}')) {
        $items = [regex]::Matches($m.Groups[1].Value, $literal) | ForEach-Object { Unescape-CSharp $_.Value.Substring(1, $_.Value.Length - 2) }
        for ($i = 0; $i -lt $items.Count; $i += 2) { [void]$keys.Add($items[$i]) }
    }
}

foreach ($file in (Get-ChildItem (Join-Path $root 'ui'), (Join-Path $root 'setup') -Filter '*.xaml' -File)) {
    $text = [IO.File]::ReadAllText($file.FullName)
    foreach ($m in [regex]::Matches($text, "\{local:Tr\s+'((?:[^'\\]|\\.)*)'\s*,\s*'((?:[^'\\]|\\.)*)'\s*\}")) {
        $en = [Net.WebUtility]::HtmlDecode($m.Groups[1].Value) -replace "\\(.)", '$1'
        [void]$keys.Add($en)
    }
}

Add-Type -AssemblyName System.Web.Extensions
$sorted = @($keys | Sort-Object { $_ }  -CaseSensitive)
if ($Dump) { [IO.File]::WriteAllText($Dump, (ConvertTo-Json $sorted), (New-Object Text.UTF8Encoding($false))); Write-Host "$($sorted.Count) strings written to $Dump"; return }

$bad = 0
foreach ($f in (Get-ChildItem (Join-Path $root 'ui\i18n') -Filter '*.json' | Sort-Object Name)) {
    # JavaScriptSerializer keeps keys that differ only in case apart (None / none); ConvertFrom-Json does not
    $table = (New-Object Web.Script.Serialization.JavaScriptSerializer).DeserializeObject([IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8))
    $have = New-Object Collections.Generic.HashSet[string]
    foreach ($k in $table.Keys) { [void]$have.Add($k) }
    $missing = @($sorted | Where-Object { -not $have.Contains($_) })
    $unused = @($have | Where-Object { -not $keys.Contains($_) })
    $empty = @($table.Keys | Where-Object { [string]::IsNullOrWhiteSpace([string]$table[$_]) })
    Write-Host ("{0}: {1} strings, {2} missing, {3} unused, {4} empty" -f $f.BaseName, $have.Count, $missing.Count, $unused.Count, $empty.Count)
    foreach ($k in $missing) { Write-Host "  missing: $k" }
    foreach ($k in $unused) { Write-Host "  unused:  $k" }
    foreach ($k in $empty) { Write-Host "  empty:   $k" }
    $bad += $missing.Count + $empty.Count
}
if ($dynamic.Count -gt 0) {
    Write-Host "Calls with a computed first argument (not checked):"
    $dynamic | ForEach-Object { Write-Host "  $_" }
}
Write-Host ("{0} source strings" -f $sorted.Count)
if ($bad -gt 0) { exit 1 }
