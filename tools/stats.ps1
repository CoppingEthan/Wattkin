# How many people are getting Wattkin: GitHub's own counts for each release download, plus stars,
# forks and open issues. Read from GitHub's public API; the app itself sends nothing anywhere.
#   powershell -ExecutionPolicy Bypass -File .\tools\stats.ps1
# The updater downloads Wattkin.exe.sig with each update, so the .sig count is roughly the number of
# automatic updates, and Wattkin.exe minus that is roughly the number of manual downloads.
$ErrorActionPreference = 'Stop'
$headers = @{ 'User-Agent' = 'wattkin-stats'; 'Accept' = 'application/vnd.github+json' }
$api = 'https://api.github.com/repos/CoppingEthan/Wattkin'

$repo = Invoke-RestMethod $api -Headers $headers
Write-Host "Stars $($repo.stargazers_count), forks $($repo.forks_count), open issues and pull requests $($repo.open_issues_count)"
Write-Host ''

$total = 0
$releases = Invoke-RestMethod "$api/releases?per_page=100" -Headers $headers
foreach ($release in $releases) {
    $exe = ($release.assets | Where-Object name -eq 'Wattkin.exe').download_count
    $sig = ($release.assets | Where-Object name -eq 'Wattkin.exe.sig').download_count
    $total += $exe
    Write-Host ("{0,-8} {1:yyyy-MM-dd}  Wattkin.exe {2,5}   updates (.sig) {3,4}" -f $release.tag_name, [datetime]$release.published_at, $exe, $sig)
}

Write-Host ''
Write-Host "All releases: $total downloads of Wattkin.exe"
