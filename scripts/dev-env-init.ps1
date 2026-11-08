# S1-03: create the LOCAL DEVELOPMENT .env for compose.yaml from infra/docker/dev.env.template.
# Each __GENERATE__ value gets a fresh random secret. Secrets are never printed.
# Usage: pwsh scripts/dev-env-init.ps1 [-Force]     (-Force overwrites an existing .env)
[CmdletBinding()]
param([switch]$Force)
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$template = Join-Path $root 'infra/docker/dev.env.template'
$target = Join-Path $root '.env'

if ((Test-Path $target) -and -not $Force) {
    Write-Warning '.env already exists; keeping it. Use -Force to regenerate (existing database volumes keep the OLD passwords).'
    exit 0
}

$alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789'
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
function New-Secret {
    # 32 alphanumeric chars from the OS CSPRNG (rejection sampling, no modulo bias), prefixed so SQL Server
    # complexity rules always hold. No '$' or quotes: safe for compose .env parsing.
    $chars = New-Object System.Text.StringBuilder
    $buf = New-Object byte[] 1
    while ($chars.Length -lt 32) {
        $rng.GetBytes($buf)
        if ($buf[0] -lt 248) { [void]$chars.Append($alphabet[$buf[0] % 62]) }
    }
    'Aa1-' + $chars.ToString()
}

$lines = foreach ($line in [System.IO.File]::ReadAllLines($template)) {
    if ($line -match '^([A-Z0-9_]+)=__GENERATE__$') { "$($Matches[1])=$(New-Secret)" } else { $line }
}
$rng.Dispose()

# Write to a temp file, restrict it to the current user (no inherited ACEs such as
# "Authenticated Users"), then rename into place so .env is never readable by others, even briefly.
$tmp = Join-Path $root ('.env.tmp.' + [System.Guid]::NewGuid().ToString('N'))
try {
    [System.IO.File]::WriteAllText($tmp, '')
    $acl = New-Object System.Security.AccessControl.FileSecurity
    $acl.SetAccessRuleProtection($true, $false)
    $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().User
    $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule($me, 'FullControl', 'Allow')))
    Set-Acl -LiteralPath $tmp -AclObject $acl
    # LF line endings, UTF-8 without BOM (compose parses it on every OS).
    [System.IO.File]::WriteAllText($tmp, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $tmp -Destination $target -Force
}
finally {
    if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
}
Write-Host "Wrote $target with generated secrets (readable by the current user only). It is gitignored; do not commit or share it."
