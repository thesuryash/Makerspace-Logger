$ErrorActionPreference = 'Stop'

# Resolve common merge conflicts for the LocalWebAdapter by preferring current-branch content.
$files = @(
  'LocalWebAdapter/Program.cs',
  'LocalWebAdapter/LocalWebAdapter.csproj',
  'LocalWebAdapter/wwwroot/index.html',
  'LocalWebAdapter/index.html',
  'scripts/publish-web-adapter.ps1',
  'scripts/run-web-adapter-portable.cmd',
  'scripts/run-web-adapter.ps1',
  'scripts/run-web-adapter.sh',
  'README.md'
)

foreach ($file in $files) {
  git checkout --ours -- $file 2>$null
}

git add $files

Write-Host 'Conflict resolution staged using current branch versions for LocalWebAdapter files.'
Write-Host 'Now run: git commit -m "Resolve LocalWebAdapter merge conflicts"'
