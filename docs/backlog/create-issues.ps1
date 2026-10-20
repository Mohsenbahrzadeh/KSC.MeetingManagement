param([string]$Repo = "Mohsenbahrzadeh/KSC.MeetingManagement")
# Requires: gh CLI logged in. Creates labels, epics and tasks from backlog.json.
$ErrorActionPreference = "Stop"
$b = Get-Content "$PSScriptRoot\backlog.json" -Raw -Encoding UTF8 | ConvertFrom-Json

$labels = @{ "epic"="5319e7"; "task"="0e8a16"; "P0"="b60205"; "P1"="d93f0b"; "P2"="fbca04" }
foreach ($e in $b.epics) { $labels[$e.id] = "1d76db" }
foreach ($l in $labels.Keys) { gh label create $l --color $labels[$l] --repo $Repo --force | Out-Null }

$epicNo = @{}
foreach ($e in $b.epics) {
  $tasks = $b.tasks | Where-Object { $_.epic -eq $e.id }
  $list = ($tasks | ForEach-Object { "- [ ] $($_.id) - $($_.title) ($($_.p))" }) -join "`n"
  $body = "**فاز:** $($e.phase)`n`n## تسک‌ها`n$list`n`n(پس از ساخت تسک‌ها، شماره Issueها به این فهرست لینک می‌شود.)"
  $url = gh issue create --repo $Repo --title "[$($e.id)] $($e.title)" --body $body --label "epic,$($e.id)"
  $epicNo[$e.id] = ($url -split "/")[-1]
  Write-Host "$($e.id) -> $url"
}

foreach ($t in $b.tasks) {
  $items = ($t.body | ForEach-Object { "- $_" }) -join "`n"
  $body = "Epic: #$($epicNo[$t.epic]) ($($t.epic))`n`n## کارها`n$items`n`n## معیار پذیرش`n$($t.accept)`n`n## تعریف Done`nمطابق بخش «تعریف Done» در docs/backlog/README.md"
  gh issue create --repo $Repo --title "[$($t.id)] $($t.title)" --body $body --label "task,$($t.p),$($t.epic)" | Out-Null
  Write-Host "$($t.id) created"
}
