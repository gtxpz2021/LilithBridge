$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$manifest = Get-Content -LiteralPath (Join-Path $root 'PACKAGE-FILES.json') -Raw | ConvertFrom-Json
$seen = @{}
foreach ($row in $manifest.files) {
    if ($row.path -match '(^|[\\/])\.\.([\\/]|$)' -or [IO.Path]::IsPathRooted($row.path)) { throw 'Manifest path rejected.' }
    $path = [IO.Path]::GetFullPath((Join-Path $root $row.path))
    if (-not $path.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escapes package.' }
    $key = $row.path.Replace('\','/').ToLowerInvariant()
    if ($seen.ContainsKey($key)) { throw 'Duplicate manifest path.' }
    $seen[$key] = $true
    $item = Get-Item -LiteralPath $path
    for ($current=$item; $null -ne $current -and $current.FullName.Length -ge $root.Length; $current=$current.Parent) {
        if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse entry rejected.' }
        if ($current -is [IO.FileInfo]) { $current = $current.Directory; if ($current.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse parent rejected.' } }
    }
    $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($item.Length -ne $row.bytes -or $hash -cne $row.sha256) { throw ('Package file changed: '+$row.path) }
}
$actualFiles = @(Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {$_.FullName -ne (Join-Path $root 'PACKAGE-FILES.json')})
if ($actualFiles.Count -ne $seen.Count) { throw 'Unexpected package file count.' }
foreach ($file in $actualFiles) {
    $key=$file.FullName.Substring($root.Length+1).Replace('\','/').ToLowerInvariant()
    if (-not $seen.ContainsKey($key)) { throw 'Unexpected package file.' }
}
$fixture = Join-Path $root 'setup\optional-recall\probe-fixture'
$identities = Get-Content -LiteralPath (Join-Path $fixture 'identity.json') -Raw | ConvertFrom-Json
foreach ($name in @('semantic_worker.py','model-manifest.json')) {
    $hash = (Get-FileHash -LiteralPath (Join-Path $fixture $name) -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($hash -cne $identities.$name) { throw ('Worker fixture identity mismatch: '+$name) }
}
Write-Output ('Verified '+$seen.Count+' package files and worker fixture identities. This confirms manifest consistency, not publisher authenticity or game compatibility.')
