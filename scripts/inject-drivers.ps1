# Injects every driver found under -Drivers into the Pro image of the Windows ISO, and reports which
# in-box drivers this laptop (Dell Inspiron 15-3567) relies on. Writes the media path to use for the build
# (the original ISO, or an extracted folder when drivers were injected) to -PathFile.
param(
  [Parameter(Mandatory)] [string] $Iso,
  [Parameter(Mandatory)] [string] $Drivers,
  [Parameter(Mandatory)] [string] $Work,
  [Parameter(Mandatory)] [string] $PathFile,
  [string] $Edition = 'Professional'
)
$ErrorActionPreference = 'Stop'
$infs = @(Get-ChildItem -Path $Drivers -Recurse -Filter *.inf -ErrorAction SilentlyContinue)
New-Item -ItemType Directory -Force -Path $Work | Out-Null

$media = Join-Path $Work 'media'
$mount = Join-Path $Work 'mount'
New-Item -ItemType Directory -Force -Path $mount | Out-Null

if ($infs.Count -gt 0) {
  Write-Host "Found $($infs.Count) driver INF file(s); extracting ISO to inject them."
  & 7z x -y "-o$media" $Iso | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "7z extraction failed" }
  $wim = Join-Path $media 'sources\install.wim'
  $esd = Join-Path $media 'sources\install.esd'
} else {
  Write-Host 'No extra drivers in drivers/. Using the ISO as-is (in-box drivers + Windows Update).'
  $disk = Mount-DiskImage -ImagePath $Iso -PassThru
  $letter = ($disk | Get-Volume).DriveLetter
  $wim = "${letter}:\sources\install.wim"
  $esd = "${letter}:\sources\install.esd"
}
$imageFile = if (Test-Path $wim) { $wim } else { $esd }
$info = Get-WindowsImage -ImagePath $imageFile
$info | ForEach-Object { Write-Host ("  #{0} {1}" -f $_.ImageIndex, $_.ImageName) }
$index = ($info | Where-Object { (Get-WindowsImage -ImagePath $imageFile -Index $_.ImageIndex).EditionId -eq $Edition } | Select-Object -First 1).ImageIndex
if (-not $index) { throw "Edition $Edition not found" }

if ($infs.Count -gt 0) {
  if ($imageFile -eq $esd) {
    Write-Host 'Converting install.esd (selected edition) to install.wim'
    Export-WindowsImage -SourceImagePath $esd -SourceIndex $index -DestinationImagePath $wim -CompressionType Maximum | Out-Null
    Remove-Item $esd -Force
    $imageFile = $wim; $index = 1
  }
  Set-ItemProperty -Path $imageFile -Name IsReadOnly -Value $false
  Mount-WindowsImage -ImagePath $imageFile -Index $index -Path $mount | Out-Null
  try {
    $added = 0; $failed = 0
    foreach ($inf in $infs) {
      try {
        Add-WindowsDriver -Path $mount -Driver $inf.FullName -ErrorAction Stop | Out-Null
        $added++; Write-Host "  + $($inf.Directory.Name)\$($inf.Name)"
      } catch {
        $failed++; Write-Host "  ! skipped $($inf.Directory.Name)\$($inf.Name): $($_.Exception.Message)"
      }
    }
    Write-Host "Injected $added INF(s); skipped $failed."
    if ($added -eq 0) { throw 'No drivers could be injected' }
    Write-Host 'Drivers now in the image:'
    Get-WindowsDriver -Path $mount | Sort-Object ClassName, ProviderName |
      ForEach-Object { Write-Host ("  {0,-16} {1,-34} {2}  {3}" -f $_.ClassName, $_.ProviderName, $_.Version, $_.OriginalFileName.Split('\')[-1]) }
  } finally {
    Dismount-WindowsImage -Path $mount -Save | Out-Null
  }
  Set-Content -Path $PathFile -Value $media -NoNewline
} else {
  # Read-only check of in-box support for this laptop's devices.
  Mount-WindowsImage -ImagePath $imageFile -Index $index -Path $mount -ReadOnly | Out-Null
  try {
    $repo = Join-Path $mount 'Windows\System32\DriverStore\FileRepository'
    $checks = [ordered]@{
      'Intel Wireless-AC 3165 (Wi-Fi)' = 'netwtw0*'
      'Intel Bluetooth'                = 'ibtusb*'
      'Realtek RTL810x Fast Ethernet'  = 'rt640x64*'
      'Intel HD Graphics 620'          = 'igdlh*'
      'HD Audio (generic)'             = 'hdaudio.inf*'
      'Precision/I2C HID touchpad'     = 'hidi2c.inf*'
    }
    foreach ($k in $checks.Keys) {
      $hit = Get-ChildItem -Path $repo -Directory -Filter $checks[$k] -ErrorAction SilentlyContinue | Select-Object -First 1
      $state = if ($hit) { "IN-BOX ($($hit.Name))" } else { 'not in-box -> Windows Update / add to drivers/' }
      Write-Host ("  {0,-34} {1}" -f $k, $state)
    }
  } finally {
    Dismount-WindowsImage -Path $mount -Discard | Out-Null
    Dismount-DiskImage -ImagePath $Iso | Out-Null
  }
  Set-Content -Path $PathFile -Value $Iso -NoNewline
}
