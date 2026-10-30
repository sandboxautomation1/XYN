$utf8 = [Text.UTF8Encoding]::new($false)
$cp1251 = [Text.Encoding]::GetEncoding(1251)
function Get-BadCount([string]$value) {
    $count = 0
    foreach ($ch in $value.ToCharArray()) {
        $code = [int][char]$ch
        if ($code -in 0x0420,0x0421,0x0412,0x0402,0x00A6,0x045F,0x0459,0x045B,0x045C,0x045E,0x0453,0x00D0,0x00D1) { $count++ }
    }
    return $count
}
$files = Get-ChildItem -Path 'E:\Projects_MT5\XYN' -Recurse -File -Include *.cs,*.xaml | Where-Object { $_.FullName -notmatch '\\(bin|obj|\.work)\\' }
foreach ($file in $files) {
    $lines = [IO.File]::ReadAllLines($file.FullName, $utf8)
    $changed = $false
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        $badBefore = Get-BadCount $line
        if ($badBefore -eq 0) { continue }
        try {
            $candidate = $utf8.GetString($cp1251.GetBytes($line))
            $badAfter = Get-BadCount $candidate
            if ($badAfter -lt $badBefore) {
                $lines[$i] = $candidate
                $changed = $true
            }
        } catch { }
    }
    if ($changed) { [IO.File]::WriteAllLines($file.FullName, $lines, $utf8); Write-Output "Fixed: $($file.FullName)" }
}
