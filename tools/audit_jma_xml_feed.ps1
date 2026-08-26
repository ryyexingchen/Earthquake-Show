param(
    [string]$ShortEndpoint = 'https://www.data.jma.go.jp/developer/xml/feed/eqvol.xml',
    [string]$LongEndpoint = 'https://www.data.jma.go.jp/developer/xml/feed/eqvol_l.xml',
    [string]$Since
)

$ErrorActionPreference = 'Stop'
$sinceValue = if ([string]::IsNullOrWhiteSpace($Since)) {
    $null
} else {
    [DateTimeOffset]::Parse($Since, [Globalization.CultureInfo]::InvariantCulture)
}
$namespace = New-Object System.Xml.XmlNamespaceManager((New-Object System.Xml.XmlDocument).NameTable)
$namespace.AddNamespace('a', 'http://www.w3.org/2005/Atom')

function Read-EarthquakeEntries([string]$endpoint) {
    $document = New-Object System.Xml.XmlDocument
    $response = Invoke-WebRequest -Uri $endpoint -UseBasicParsing -TimeoutSec 30
    $document.LoadXml($response.Content)
    @($document.SelectNodes('//a:entry', $namespace) | ForEach-Object {
        $id = $_.SelectSingleNode('a:id', $namespace).InnerText.Trim()
        $fileName = [System.IO.Path]::GetFileName(([Uri]$id).AbsolutePath)
        if ($fileName -match '_(VXSE51|VXSE52|VXSE53)_') {
            $time = [DateTimeOffset]::ParseExact(
                $fileName.Substring(0, 14),
                'yyyyMMddHHmmss',
                [Globalization.CultureInfo]::InvariantCulture,
                [Globalization.DateTimeStyles]::AssumeUniversal)
            [PSCustomObject]@{
                File = $fileName
                Code = $Matches[1]
                IssuedAt = $time
            }
        }
    })
}

$feeds = @(
    [PSCustomObject]@{ Name = 'short'; Endpoint = $ShortEndpoint },
    [PSCustomObject]@{ Name = 'long'; Endpoint = $LongEndpoint }
)

foreach ($feed in $feeds) {
    $entries = @(Read-EarthquakeEntries $feed.Endpoint)
    $ordered = @($entries | Sort-Object IssuedAt)
    $codes = @($entries | Group-Object Code | ForEach-Object { "$($_.Name)=$($_.Count)" })
    Write-Output ("[{0}] count={1} oldest={2} newest={3} codes={4}" -f `
        $feed.Name,
        $entries.Count,
        $(if ($ordered.Count -eq 0) { '--' } else { $ordered[0].IssuedAt.ToString('O') }),
        $(if ($ordered.Count -eq 0) { '--' } else { $ordered[-1].IssuedAt.ToString('O') }),
        $(if ($codes.Count -eq 0) { '--' } else { $codes -join ', ' }))

    if ($null -ne $sinceValue) {
        $matched = @($entries | Where-Object { $_.IssuedAt -ge $sinceValue })
        $oldest = if ($ordered.Count -eq 0) { $null } else { $ordered[0].IssuedAt }
        $coverage = $oldest -ne $null -and $oldest -le $sinceValue
        Write-Output ("[{0}] since={1} matched={2} coverage={3}" -f `
            $feed.Name,
            $sinceValue.ToString('O'),
            $matched.Count,
            $(if ($coverage) { 'complete' } else { 'incomplete' }))
    }
}
